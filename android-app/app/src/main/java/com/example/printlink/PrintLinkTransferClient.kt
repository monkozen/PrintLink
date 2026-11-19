package com.example.printlink

import android.content.ContentResolver
import android.net.Uri
import android.os.SystemClock
import java.io.BufferedInputStream
import java.io.BufferedOutputStream
import java.io.DataInputStream
import java.io.DataOutputStream
import java.io.IOException
import java.net.InetSocketAddress
import java.net.Socket
import java.security.MessageDigest
import java.util.Locale

/**
 * Synchronous PLK1 sender; call only from Dispatchers.IO.
 * No Activity, Compose, or UI callback is retained here.
 */
object PrintLinkTransferClient {
    fun send(
        resolver: ContentResolver,
        host: String,
        uri: Uri,
        fileName: String,
        onProgress: (Int) -> Unit,
        onStage: (String) -> Unit
    ): String {
        val startTime = SystemClock.elapsedRealtimeNanos()
        var awaitingReceiverReceipt = false

        val result = try {
            onStage("Checking document size...")

            require(
                fileName.endsWith(".pdf", ignoreCase = true) ||
                    fileName.endsWith(".docx", ignoreCase = true)
            ) { "Only PDF and DOCX files are allowed" }

            val nameBytes = fileName.toByteArray(Charsets.UTF_8)
            require(nameBytes.size in 1..200) { "Filename is too long" }

            // Validate the actual stream length, not just provider metadata.
            val fileSize = resolver.openInputStream(uri)?.use { input ->
                val buffer = ByteArray(65536)
                var total = 0L
                while (true) {
                    val count = input.read(buffer)
                    if (count == -1) break
                    total += count
                    if (total > 10_000_000L) {
                        throw IOException("File exceeds 10 MB")
                    }
                }
                total
            } ?: throw IOException("Cannot open selected document")

            require(fileSize > 0) { "Cannot send an empty document" }

            Socket().use { socket ->
                socket.connect(InetSocketAddress(host, 5050), 5000)
                socket.soTimeout = 45000

                val output = DataOutputStream(
                    BufferedOutputStream(socket.getOutputStream())
                )

                // PLK1, operation 3, length-prefixed filename, exact file size.
                output.write("PLK1".toByteArray(Charsets.US_ASCII))
                output.writeByte(3)
                output.writeInt(nameBytes.size)
                output.write(nameBytes)
                output.writeLong(fileSize)

                onStage("Sending document...")
                val hasher = MessageDigest.getInstance("SHA-256")
                val input = resolver.openInputStream(uri)
                    ?: throw IOException("Cannot reopen document")

                input.use { source ->
                    val buffer = ByteArray(65536)
                    var sent = 0L
                    var lastReportedPercent = -1

                    while (sent < fileSize) {
                        val count = source.read(
                            buffer, 0,
                            minOf(buffer.size.toLong(), fileSize - sent).toInt()
                        )
                        if (count == -1) {
                            throw IOException("Document ended before expected size")
                        }
                        if (count > 0) {
                            output.write(buffer, 0, count)
                            hasher.update(buffer, 0, count)
                            sent += count
                            val percent = (sent * 100L / fileSize).toInt()
                            if (percent != lastReportedPercent) {
                                lastReportedPercent = percent
                                onProgress(percent)
                            }
                        }
                    }
                }

                val digest = hasher.digest()
                require(digest.size == 32) { "Invalid SHA-256 digest length" }
                output.write(digest)
                output.flush()
                awaitingReceiverReceipt = true
                onStage("File data sent. Waiting for SHA-256 verification...")

                val (success, message) = readReceiverStatus(socket)
                awaitingReceiverReceipt = false

                if (success) {
                    "Transfer successful! $message"
                } else {
                    "Transfer rejected: $message"
                }
            }
        } catch (e: Exception) {
            val reason = e.message?.takeIf { it.isNotBlank() }
                ?: e.javaClass.simpleName
            if (awaitingReceiverReceipt) {
                "Transfer status uncertain. " +
                    "The receiver did not confirm whether the " +
                    "document was saved. Check Windows before retrying. " +
                    "($reason)"
            } else {
                "Transfer failed: $reason"
            }
        }

        val duration = (SystemClock.elapsedRealtimeNanos() - startTime) /
            1_000_000_000.0
        return "$result\nEnd-to-end duration: " +
            String.format(Locale.US, "%.3f", duration) + " seconds"
    }

    private fun readReceiverStatus(socket: Socket): Pair<Boolean, String> {
        val input = DataInputStream(
            BufferedInputStream(socket.getInputStream())
        )
        val statusCode = input.readUnsignedByte()
        val messageLength = input.readUnsignedShort()
        require(statusCode == 0 || statusCode == 1) {
            "Invalid receiver status"
        }
        require(messageLength in 1..512) {
            "Invalid receiver message"
        }
        val messageBytes = ByteArray(messageLength)
        input.readFully(messageBytes)
        return Pair(statusCode == 1, String(messageBytes, Charsets.UTF_8))
    }
}
