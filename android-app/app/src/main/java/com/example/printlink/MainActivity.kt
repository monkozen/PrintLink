package com.example.printlink

import android.content.Intent
import android.content.pm.PackageManager
import android.net.Uri
import android.os.Build
import android.os.Bundle
import android.os.SystemClock
import android.provider.OpenableColumns
import androidx.activity.ComponentActivity
import androidx.activity.compose.setContent
import androidx.activity.compose.rememberLauncherForActivityResult
import androidx.activity.result.contract.ActivityResultContracts
import androidx.compose.foundation.background
import androidx.compose.foundation.layout.*
import androidx.compose.foundation.rememberScrollState
import androidx.compose.foundation.shape.RoundedCornerShape
import androidx.compose.foundation.verticalScroll
import androidx.compose.material3.*
import androidx.compose.runtime.*
import androidx.compose.runtime.saveable.rememberSaveable
import androidx.compose.ui.Alignment
import androidx.compose.ui.graphics.Color
import androidx.compose.ui.Modifier
import androidx.compose.ui.text.font.FontWeight
import androidx.compose.ui.text.style.TextOverflow
import com.example.printlink.ui.theme.PrintLinkTheme
import androidx.compose.ui.unit.dp
import java.io.BufferedInputStream
import java.io.BufferedOutputStream
import java.io.DataInputStream
import java.io.DataOutputStream
import java.io.IOException
import java.net.InetSocketAddress
import java.net.Socket
import java.security.MessageDigest
import java.util.Locale

class MainActivity : ComponentActivity() {

    private val localNetworkPermission =
        "android.permission.ACCESS_LOCAL_NETWORK"

    private val maxFileSize = 10_000_000L

    override fun onCreate(savedInstanceState: Bundle?) {
        super.onCreate(savedInstanceState)

        setContent {
            var ipAddress by rememberSaveable {
                mutableStateOf("")
            }

            var connectionStatus by rememberSaveable {
                mutableStateOf("Not connected")
            }

            var isConnecting by remember {
                mutableStateOf(false)
            }

            var selectedUri by rememberSaveable {
                mutableStateOf<String?>(null)
            }

            var selectedName by rememberSaveable {
                mutableStateOf<String?>(null)
            }

            var selectedSize by rememberSaveable {
                mutableStateOf<Long?>(null)
            }

            var fileStatus by remember {
                mutableStateOf("No document selected")
            }

            var isSending by remember {
                mutableStateOf(false)
            }

            var transferProgress by remember {
                mutableIntStateOf(0)
            }

            val permissionLauncher =
                rememberLauncherForActivityResult(
                    ActivityResultContracts.RequestPermission()
                ) { granted ->
                    if (granted) {
                        connectToReceiver(ipAddress.trim()) {
                            connectionStatus = it
                            isConnecting = false
                        }
                    } else {
                        connectionStatus =
                            "Local network permission denied"
                        isConnecting = false
                    }
                }

            val documentPicker =
                rememberLauncherForActivityResult(
                    ActivityResultContracts.OpenDocument()
                ) { uri: Uri? ->

                    if (uri != null) {

                        // Clear the previous transferable document.
                        selectedUri = null
                        selectedName = null
                        selectedSize = null
                        transferProgress = 0
                        fileStatus = "Checking document..."

                        try {
                            val (name, size) = getDocumentInfo(uri)

                            // Display the newly selected document,
                            // including when it is rejected.
                            selectedName = name
                            selectedSize = size

                            val allowedType =
                                name.endsWith(".pdf", true) ||
                                        name.endsWith(".docx", true)

                            when {
                                !allowedType -> {
                                    fileStatus =
                                        "Only PDF and DOCX are allowed"
                                }

                                size != null && size > maxFileSize -> {
                                    fileStatus =
                                        "File exceeds the 10 MB limit"
                                }

                                size == 0L -> {
                                    fileStatus = "File is empty"
                                }

                                name.toByteArray(Charsets.UTF_8).size !in 1..200 -> {
                                    fileStatus = "Filename is too long"
                                }

                                else -> {
                                    contentResolver
                                        .takePersistableUriPermission(
                                            uri,
                                            Intent.FLAG_GRANT_READ_URI_PERMISSION
                                        )

                                    // Store only valid documents
                                    // as transferable selections.
                                    selectedUri = uri.toString()

                                    fileStatus =
                                        if (size == null)
                                            "Selected; size will be checked before sending"
                                        else
                                            "Document ready for transfer"
                                }
                            }
                        } catch (e: Exception) {
                            selectedUri = null
                            fileStatus =
                                "Unable to open file: ${e.message}"
                        }
                    }
                }

            PrintLinkTheme {
                Column(
                    modifier = Modifier
                        .fillMaxSize()
                        .background(MaterialTheme.colorScheme.background)
                        .verticalScroll(rememberScrollState())
                        .padding(24.dp),
                    verticalArrangement =
                        Arrangement.spacedBy(16.dp)
                ) {
                    Row(
                        modifier = Modifier.fillMaxWidth(),
                        verticalAlignment = Alignment.CenterVertically,
                        horizontalArrangement = Arrangement.spacedBy(12.dp)
                    ) {
                        Surface(
                            color = MaterialTheme.colorScheme.primary,
                            shape = RoundedCornerShape(12.dp)
                        ) {
                            Text(
                                text = "PL",
                                modifier = Modifier.padding(
                                    horizontal = 12.dp,
                                    vertical = 10.dp
                                ),
                                color = MaterialTheme.colorScheme.onPrimary,
                                style = MaterialTheme.typography.titleMedium,
                                fontWeight = FontWeight.Bold
                            )
                        }

                        Text(
                            text = "PrintLink",
                            modifier = Modifier.weight(1f),
                            style = MaterialTheme.typography.headlineMedium,
                            fontWeight = FontWeight.Bold
                        )

                        Surface(
                            color = MaterialTheme.colorScheme.primaryContainer,
                            shape = RoundedCornerShape(50.dp)
                        ) {
                            Text(
                                text = "SENDER",
                                modifier = Modifier.padding(
                                    horizontal = 10.dp,
                                    vertical = 6.dp
                                ),
                                color = MaterialTheme.colorScheme.onPrimaryContainer,
                                style = MaterialTheme.typography.labelSmall,
                                fontWeight = FontWeight.Bold
                            )
                        }
                    }

                    Text(
                        text = "Offline Android-to-Windows File Sharing",
                        style = MaterialTheme.typography.bodySmall,
                        color = MaterialTheme.colorScheme.onSurfaceVariant
                    )
                    ElevatedCard(
                        modifier = Modifier.fillMaxWidth()
                    ) {
                        Column(
                            modifier = Modifier.padding(20.dp),
                            verticalArrangement = Arrangement.spacedBy(12.dp)
                        ) {
                            Text(
                                text = "Windows Receiver",
                                style = MaterialTheme.typography.titleMedium
                            )

                            Text(
                                text = "Enter the local IP address of the Windows computer.",
                                style = MaterialTheme.typography.bodySmall,
                                color = MaterialTheme.colorScheme.onSurfaceVariant
                            )

                            OutlinedTextField(
                                value = ipAddress,
                                onValueChange = {
                                    ipAddress = it
                                    connectionStatus = "Not checked"
                                },
                                label = {
                                    Text("Windows Receiver IP")
                                },
                                placeholder = {
                                    Text("192.168.1.105")
                                },
                                singleLine = true,
                                modifier = Modifier.fillMaxWidth()
                            )

                            Button(
                                modifier = Modifier.fillMaxWidth(),
                                enabled = !isConnecting,
                                onClick = {
                                    val host = ipAddress.trim()

                                    if (host.isEmpty()) {
                                        connectionStatus =
                                            "Please enter an IP address"
                                    } else {
                                        isConnecting = true
                                        connectionStatus = "Connecting..."

                                        val needsPermission =
                                            Build.VERSION.SDK_INT >= 37 &&
                                                    applicationInfo.targetSdkVersion >= 37

                                        if (
                                            needsPermission &&
                                            checkSelfPermission(
                                                localNetworkPermission
                                            ) != PackageManager.PERMISSION_GRANTED
                                        ) {
                                            permissionLauncher.launch(
                                                localNetworkPermission
                                            )
                                        } else {
                                            connectToReceiver(host) {
                                                connectionStatus = it
                                                isConnecting = false
                                            }
                                        }
                                    }
                                }
                            ) {
                                Text(
                                    if (isConnecting) "Connecting..."
                                    else "Connect to Receiver"
                                )
                            }

                            val connectionSucceeded =
                                connectionStatus.startsWith("Server says:")

                            val connectionFailed =
                                connectionStatus.startsWith("Connection failed:") ||
                                        connectionStatus.startsWith("Receiver error:") ||
                                        connectionStatus == "Local network permission denied"

                            val friendlyConnectionStatus = when {
                                connectionSucceeded ->
                                    "Receiver reachable"

                                connectionFailed ->
                                    "Receiver unreachable"

                                connectionStatus == "Connecting..." ->
                                    "Checking receiver..."

                                connectionStatus == "Not connected" ||
                                        connectionStatus == "Not checked" ->
                                    "Receiver not checked"

                                else -> connectionStatus
                            }

                            val connectionColor = when {
                                connectionSucceeded ->
                                    Color(0xFF237A4B)

                                connectionFailed ->
                                    MaterialTheme.colorScheme.error

                                else ->
                                    MaterialTheme.colorScheme.onSurfaceVariant
                            }

                            Text(
                                text = friendlyConnectionStatus,
                                style = MaterialTheme.typography.titleSmall,
                                color = connectionColor
                            )

                            if (connectionSucceeded) {
                                Text(
                                    text = "Last connection check successful",
                                    style = MaterialTheme.typography.bodySmall,
                                    color = MaterialTheme.colorScheme.onSurfaceVariant
                                )
                            }

                            if (connectionFailed) {
                                Text(
                                    text = "Check the IP address and make sure the Windows receiver is listening.",
                                    style = MaterialTheme.typography.bodySmall
                                )

                                var showConnectionDetails by remember {
                                    mutableStateOf(false)
                                }

                                TextButton(
                                    onClick = {
                                        showConnectionDetails = !showConnectionDetails
                                    }
                                ) {
                                    Text(
                                        if (showConnectionDetails)
                                            "Hide technical details"
                                        else
                                            "Show technical details"
                                    )
                                }

                                if (showConnectionDetails) {
                                    Text(
                                        text = connectionStatus,
                                        style = MaterialTheme.typography.bodySmall,
                                        color = MaterialTheme.colorScheme.onSurfaceVariant
                                    )
                                }
                            }
                        } // Windows Receiver inner Column
                    } // Windows Receiver ElevatedCard

                    // Document Selection card
                    ElevatedCard(
                        modifier = Modifier.fillMaxWidth()
                    ) {
                        Column(
                            modifier = Modifier.padding(20.dp),
                            verticalArrangement = Arrangement.spacedBy(12.dp)
                        ) {
                            Text(
                                text = "Document Selection",
                                style = MaterialTheme.typography.titleMedium
                            )

                            Text(
                                text = "Select a PDF or DOCX document to send to Windows. Maximum size: 10 MB.",
                                style = MaterialTheme.typography.bodySmall,
                                color = MaterialTheme.colorScheme.onSurfaceVariant
                            )

                            OutlinedButton(
                                enabled = !isSending,
                                onClick = {
                                    documentPicker.launch(
                                        arrayOf(
                                            "application/pdf",
                                            "application/vnd.openxmlformats-officedocument.wordprocessingml.document"
                                        )
                                    )
                                },
                                modifier = Modifier.fillMaxWidth()
                            ) {
                                Text("Choose Document")
                            }

                            if (selectedName != null) {
                                val documentName = selectedName!!

                                val documentType = when {
                                    documentName.endsWith(".pdf", true) -> "PDF"
                                    documentName.endsWith(".docx", true) -> "DOCX"
                                    else -> "Unsupported"
                                }

                                val sizeLabel = selectedSize?.let {
                                    String.format(
                                        Locale.US,
                                        "%.2f MB",
                                        it / 1_000_000.0
                                    )
                                } ?: "Unknown size"

                                val documentReady = selectedUri != null

                                val eligibilityText = when {
                                    !documentReady -> "Document not eligible for transfer"
                                    selectedSize == null ->
                                        "Selected - size will be checked before sending"
                                    else -> "Ready for transfer"
                                }

                                Surface(
                                    modifier = Modifier.fillMaxWidth(),
                                    shape = RoundedCornerShape(12.dp),
                                    color = MaterialTheme.colorScheme.surfaceVariant.copy(
                                        alpha = 0.45f
                                    )
                                ) {
                                    Column(
                                        modifier = Modifier.padding(16.dp),
                                        verticalArrangement = Arrangement.spacedBy(8.dp)
                                    ) {
                                        Text(
                                            text = documentName,
                                            style = MaterialTheme.typography.titleSmall,
                                            maxLines = 2,
                                            overflow = TextOverflow.Ellipsis
                                        )

                                        Text(
                                            text = "$documentType document · $sizeLabel",
                                            style = MaterialTheme.typography.bodySmall,
                                            color = MaterialTheme.colorScheme.onSurfaceVariant
                                        )

                                        Text(
                                            text = eligibilityText,
                                            style = MaterialTheme.typography.labelMedium,
                                            color = if (documentReady)
                                                Color(0xFF237A4B)
                                            else
                                                MaterialTheme.colorScheme.error
                                        )
                                    }
                                }
                            }

                            val isSuccess =
                                fileStatus.startsWith("Transfer successful!")

                            val isRejected =
                                fileStatus.startsWith("Transfer rejected:")

                            val isFailed =
                                fileStatus.startsWith("Transfer failed:")

                            val isUncertain =
                                fileStatus.startsWith("Transfer status uncertain.")

                            val hasFinalResult =
                                isSuccess || isRejected || isFailed || isUncertain

                            val isVerifying =
                                isSending &&
                                        fileStatus.startsWith("File data sent.")

                            val statusTitle = when {
                                isSuccess -> "Document received"
                                isRejected -> "Document rejected"
                                isFailed -> "Transfer failed"
                                isUncertain -> "Confirmation not received"
                                isVerifying -> "Waiting for Windows verification"
                                else -> fileStatus
                            }

                            val statusColor = when {
                                isSuccess -> Color(0xFF237A4B)
                                isRejected || isFailed ->
                                    MaterialTheme.colorScheme.error
                                isUncertain -> Color(0xFF946200)
                                else -> MaterialTheme.colorScheme.onSurfaceVariant
                            }

                            if (hasFinalResult) {
                                val details = when {
                                    isSuccess ->
                                        fileStatus.removePrefix("Transfer successful!")
                                    isRejected ->
                                        fileStatus.removePrefix("Transfer rejected:")
                                    isFailed ->
                                        fileStatus.removePrefix("Transfer failed:")
                                    else ->
                                        fileStatus.removePrefix("Transfer status uncertain.")
                                }.trim()

                                Surface(
                                    modifier = Modifier.fillMaxWidth(),
                                    shape = RoundedCornerShape(12.dp),
                                    color = statusColor.copy(alpha = 0.10f)
                                ) {
                                    Column(
                                        modifier = Modifier.padding(16.dp),
                                        verticalArrangement =
                                            Arrangement.spacedBy(8.dp)
                                    ) {
                                        Text(
                                            text = statusTitle,
                                            style = MaterialTheme.typography.titleMedium,
                                            color = statusColor
                                        )

                                        Text(
                                            text = details,
                                            style = MaterialTheme.typography.bodySmall
                                        )

                                        if (isUncertain) {
                                            Text(
                                                text = "Check the Windows receiver before retrying.",
                                                style = MaterialTheme.typography.bodySmall,
                                                color = statusColor
                                            )
                                        }
                                    }
                                }
                            } else {
                                Text(
                                    text = statusTitle,
                                    style = MaterialTheme.typography.bodyMedium,
                                    color = statusColor
                                )
                            }

                            if (isSending) {
                                if (isVerifying) {
                                    LinearProgressIndicator(
                                        modifier = Modifier.fillMaxWidth()
                                    )

                                    Text(
                                        text = "File bytes written. Waiting for receiver confirmation...",
                                        style = MaterialTheme.typography.bodySmall
                                    )
                                } else {
                                    LinearProgressIndicator(
                                        progress = { transferProgress / 100f },
                                        modifier = Modifier.fillMaxWidth()
                                    )

                                    Text(
                                        text = "$transferProgress% of file bytes written",
                                        style = MaterialTheme.typography.bodySmall
                                    )
                                }
                            }

                            Button(
                                modifier = Modifier.fillMaxWidth(),
                                enabled = selectedUri != null &&
                                        selectedName != null &&
                                        !isSending,
                                onClick = {
                                    val host = ipAddress.trim()
                                    val uriText = selectedUri
                                    val name = selectedName

                                    val needsPermission =
                                        Build.VERSION.SDK_INT >= 37 &&
                                                applicationInfo.targetSdkVersion >= 37

                                    when {
                                        host.isEmpty() -> {
                                            fileStatus = "Enter the receiver IP address"
                                        }

                                        uriText == null || name == null -> {
                                            fileStatus = "Choose a document first"
                                        }

                                        needsPermission &&
                                                checkSelfPermission(
                                                    localNetworkPermission
                                                ) != PackageManager.PERMISSION_GRANTED -> {
                                            fileStatus =
                                                "Tap Connect first to grant network permission"
                                        }

                                        else -> {
                                            isSending = true
                                            transferProgress = 0
                                            fileStatus = "Preparing document..."

                                            sendDocument(
                                                host = host,
                                                uri = Uri.parse(uriText),
                                                fileName = name,

                                                onProgress = { percent ->
                                                    transferProgress = percent
                                                },

                                                onStage = { stage ->
                                                    fileStatus = stage
                                                },

                                                onResult = { result ->
                                                    fileStatus = result
                                                    isSending = false
                                                }
                                            )
                                        }
                                    }
                                }
                            ) {
                                Text(
                                    if (isSending) "Sending..."
                                    else "Send Document"
                                )
                            }
                        } // Closes Document Selection Column
                    } // Closes Document Selection ElevatedCard

                }
            }
        }
    }

    private fun getDocumentInfo(
        uri: Uri
    ): Pair<String, Long?> {

        var name = uri.lastPathSegment ?: "Unknown"
        var size: Long? = null

        contentResolver.query(
            uri,
            arrayOf(
                OpenableColumns.DISPLAY_NAME,
                OpenableColumns.SIZE
            ),
            null,
            null,
            null
        )?.use { cursor ->

            if (cursor.moveToFirst()) {
                val nameIndex = cursor.getColumnIndex(
                    OpenableColumns.DISPLAY_NAME
                )

                val sizeIndex = cursor.getColumnIndex(
                    OpenableColumns.SIZE
                )

                if (
                    nameIndex >= 0 &&
                    !cursor.isNull(nameIndex)
                ) {
                    name = cursor.getString(nameIndex)
                }

                if (
                    sizeIndex >= 0 &&
                    !cursor.isNull(sizeIndex)
                ) {
                    size = cursor.getLong(sizeIndex)
                }
            }
        }

        return Pair(name, size)
    }


    private fun connectToReceiver(
        host: String,
        onResult: (String) -> Unit
    ) {
        Thread {
            val result = try {
                Socket().use { socket ->
                    socket.connect(
                        InetSocketAddress(host, 5050),
                        5000
                    )

                    socket.soTimeout = 15000

                    val output = DataOutputStream(
                        BufferedOutputStream(
                            socket.getOutputStream()
                        )
                    )

                    // PrintLink protocol + PING operation.
                    output.write(
                        "PLK1".toByteArray(Charsets.US_ASCII)
                    )
                    output.writeByte(1)
                    output.flush()

                    val (success, message) =
                        readReceiverStatus(socket)

                    if (success)
                        "Server says: $message"
                    else
                        "Receiver error: $message"
                }
            } catch (e: Exception) {
                "Connection failed: ${e.message}"
            }

            runOnUiThread {
                onResult(result)
            }
        }.start()
    }


    private fun sendDocument(
        host: String,
        uri: Uri,
        fileName: String,
        onProgress: (Int) -> Unit,
        onStage: (String) -> Unit,
        onResult: (String) -> Unit
    ) {
        Thread {
            // Includes size validation, TCP transfer and receiver verification.
            val startTime = SystemClock.elapsedRealtimeNanos()

            var awaitingReceiverReceipt = false

            val result = try {
                runOnUiThread {
                    onStage("Checking document size...")
                }

                require(
                    fileName.endsWith(".pdf", true) ||
                            fileName.endsWith(".docx", true)
                ) {
                    "Only PDF and DOCX files are allowed"
                }

                val nameBytes =
                    fileName.toByteArray(Charsets.UTF_8)

                require(nameBytes.size in 1..200) {
                    "Filename is too long"
                }

                // 1. Count the actual file bytes.
                val fileSize =
                    contentResolver.openInputStream(uri)?.use { input ->

                        val buffer = ByteArray(65536)
                        var total = 0L

                        while (true) {
                            val count = input.read(buffer)

                            if (count == -1) break

                            total += count

                            if (total > 10_000_000L) {
                                throw IOException(
                                    "File exceeds 10 MB"
                                )
                            }
                        }

                        total

                    } ?: throw IOException(
                        "Cannot open selected document"
                    )

                require(fileSize > 0) {
                    "Cannot send an empty document"
                }

                // 2. Establish TCP connection.
                Socket().use { socket ->

                    socket.connect(
                        InetSocketAddress(host, 5050),
                        5000
                    )

                    socket.soTimeout = 45000

                    val output = DataOutputStream(
                        BufferedOutputStream(
                            socket.getOutputStream()
                        )
                    )

                    // 3. Send PLK1 with operation 3.
                    output.write(
                        "PLK1".toByteArray(Charsets.US_ASCII)
                    )

                    output.writeByte(3)

                    output.writeInt(nameBytes.size)
                    output.write(nameBytes)
                    output.writeLong(fileSize)

                    runOnUiThread {
                        onStage("Sending document...")
                    }

                    // 5. Initialize SHA-256.
                    val hasher =
                        MessageDigest.getInstance("SHA-256")

                    // 6. Stream file bytes and update hash.
                    val input =
                        contentResolver.openInputStream(uri)
                            ?: throw IOException(
                                "Cannot reopen document"
                            )

                    input.use { source ->
                        val buffer = ByteArray(65536)
                        var sent = 0L
                        var lastReportedPercent = -1

                        while (sent < fileSize) {

                            val toRead = minOf(
                                buffer.size.toLong(),
                                fileSize - sent
                            ).toInt()

                            val count = source.read(
                                buffer,
                                0,
                                toRead
                            )

                            if (count == -1) {
                                throw IOException(
                                    "Document ended before expected size"
                                )
                            }


                            if (count > 0) {
                                // Write document bytes to the TCP stream.
                                output.write(
                                    buffer,
                                    0,
                                    count
                                )

                                // Include the same bytes in SHA-256.
                                hasher.update(
                                    buffer,
                                    0,
                                    count
                                )

                                sent += count

                                // Calculate progress from bytes written.
                                val percent = (
                                        sent * 100L / fileSize
                                        ).toInt()

                                // Only update the UI when the percentage changes.
                                if (percent != lastReportedPercent) {
                                    lastReportedPercent = percent

                                    runOnUiThread {
                                        onProgress(percent)
                                    }
                                }
                            }
                        }
                    }

                    // 7. Finalize the 32-byte SHA-256 digest.
                    val digest = hasher.digest()
                    require(digest.size == 32) {
                        "Invalid SHA-256 digest length"
                    }

                    // 8. Append checksum after file contents.
                    output.write(digest)
                    output.flush()
                    awaitingReceiverReceipt = true

                    runOnUiThread {
                        onStage(
                            "File data sent. Waiting for SHA-256 verification..."
                        )
                    }

                    val (success, message) =
                        readReceiverStatus(socket)
                    awaitingReceiverReceipt = false

                    if (success) {
                        "Transfer successful! $message"
                    } else {
                        "Transfer rejected: $message"
                    }
                }

            } catch (e: Exception) {
                val reason = e.message
                    ?.takeIf { it.isNotBlank() }
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

            val endTime = SystemClock.elapsedRealtimeNanos()
            val durationSeconds =
                (endTime - startTime) / 1_000_000_000.0

            val formattedTime = String.format(
                Locale.US,
                "%.3f",
                durationSeconds
            )

            runOnUiThread {
                onResult(
                    "$result\nEnd-to-end duration: $formattedTime seconds"
                )
            }
        }.start()
    }

    private fun readReceiverStatus(
        socket: Socket
    ): Pair<Boolean, String> {
        val input = DataInputStream(
            BufferedInputStream(
                socket.getInputStream()
            )
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

        return Pair(
            statusCode == 1,
            String(messageBytes, Charsets.UTF_8)
        )
    }
}
