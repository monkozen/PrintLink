using System;
using System.Buffers.Binary;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace PrintLink.Receiver.Services;

/// <summary>Progress of one incoming document, raised on a background thread.</summary>
public sealed class TransferProgressEventArgs : EventArgs
{
    public string FileName { get; init; } = string.Empty;
    public string SenderLabel { get; init; } = string.Empty;
    public long BytesReceived { get; init; }
    public long TotalBytes { get; init; }
    public double BytesPerSecond { get; init; }

    public int Percent =>
        TotalBytes <= 0 ? 0 : (int)Math.Min(100, BytesReceived * 100 / TotalBytes);
}

/// <summary>A document that has been saved and verified.</summary>
public sealed class DocumentReceivedEventArgs : EventArgs
{
    public string FileName { get; init; } = string.Empty;
    public string SavedPath { get; init; } = string.Empty;
    public long SizeBytes { get; init; }
    public string Sha256Hex { get; init; } = string.Empty;
    public DateTime ReceivedAt { get; init; }
    public string SenderLabel { get; init; } = string.Empty;
    public double DurationSeconds { get; init; }
}

/// <summary>Anything that went wrong while receiving.</summary>
public sealed class TransferFailedEventArgs : EventArgs
{
    public string FileName { get; init; } = string.Empty;
    public string Message { get; init; } = string.Empty;
}

/// <summary>
/// Listens on TCP port 5050 and speaks the same format as Edrian's Android app.
/// One document is handled at a time. A second phone that tries to send while a
/// transfer is running is told the receiver is busy - after its bytes are read
/// and thrown away, so its own socket does not fail with a reset.
/// Every wait has a timeout, so a stuck phone cannot freeze the listener.
/// </summary>
public sealed class ReceiverService : IDisposable
{
    /// <summary>The phone may pause for this long mid-transfer before we give up.</summary>
    private const int ReadTimeoutMilliseconds = 45_000;

    private TcpListener? _listener;
    private CancellationTokenSource? _cancellation;
    private Task? _loop;

    /// <summary>1 while a document is being received, 0 when the receiver is free.</summary>
    private int _busy;

    public event EventHandler<TransferProgressEventArgs>? ProgressChanged;
    public event EventHandler<DocumentReceivedEventArgs>? DocumentReceived;
    public event EventHandler<TransferFailedEventArgs>? TransferFailed;
    public event EventHandler<TransferFailedEventArgs>? TransferRejected;

    public bool IsListening => _listener is not null;

    public string SaveFolder { get; set; } =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "PrintLinkReceived");

    /// <summary>Starts the listener. Throws when the port is busy or not permitted.</summary>
    public void Start()
    {
        if (IsListening)
            return;

        Directory.CreateDirectory(SaveFolder);

        var listener = new TcpListener(IPAddress.Any, PrintLinkProtocol.Port);
        listener.Start();

        _listener = listener;
        _cancellation = new CancellationTokenSource();
        _loop = Task.Run(() => AcceptLoopAsync(listener, SaveFolder, _cancellation.Token));
    }

    public void Stop()
    {
        if (!IsListening)
            return;

        _cancellation?.Cancel();
        _listener?.Stop();
        _listener = null;
        _cancellation = null;
        _loop = null;
    }

    private async Task AcceptLoopAsync(TcpListener listener, string folder, CancellationToken token)
    {
        while (!token.IsCancellationRequested)
        {
            TcpClient client;

            try
            {
                client = await listener.AcceptTcpClientAsync(token).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                return;
            }
            catch (Exception ex) when (ex is SocketException or ObjectDisposedException)
            {
                return;
            }

            // Each phone gets its own task, so a second phone is answered (with the
            // busy message) while a document is still arriving from the first.
            _ = Task.Run(() => HandleConnectionAsync(client, folder, token), token);
        }
    }

    private async Task HandleConnectionAsync(TcpClient client, string folder, CancellationToken token)
    {
        using (client)
        {
            client.ReceiveTimeout = ReadTimeoutMilliseconds;
            client.SendTimeout = PrintLinkProtocol.WriteTimeoutMilliseconds;

            string sender = client.Client.RemoteEndPoint?.ToString() ?? "unknown sender";

            // One stream for the whole conversation. It must stay open until the
            // failure reply is written: disposing it closes the socket.
            NetworkStream stream = client.GetStream();

            try
            {
                await HandleClientAsync(stream, folder, sender, token).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                // A dropped phone or a bad request must never crash the app.
                ReportFailure(sender, ex.Message);

                // Tell the phone why, while the socket is still open.
                try
                {
                    await PrintLinkProtocol
                        .WriteStatusAsync(stream, false, ex.Message, token)
                        .ConfigureAwait(false);
                }
                catch
                {
                    // The phone may already be gone, which is fine.
                }
            }
        }
    }

    private async Task HandleClientAsync(
        NetworkStream stream,
        string folder,
        string sender,
        CancellationToken token)
    {
        // 1. Four letters "PLK1" and one operation number.
        byte[] header = new byte[5];
        await ReadExactlyAsync(stream, header, token).ConfigureAwait(false);

        for (int i = 0; i < 4; i++)
        {
            if (header[i] != PrintLinkProtocol.Magic[i])
                throw new InvalidDataException("Unsupported protocol");
        }

        byte operation = header[4];

        switch (operation)
        {
            case PrintLinkProtocol.OperationPing:
                // The connection test always answers; it is not a document transfer.
                await PrintLinkProtocol
                    .WriteStatusAsync(stream, true, "Connected to PrintLink", token)
                    .ConfigureAwait(false);
                break;

            case PrintLinkProtocol.OperationSendDocument:
                await HandleDocumentOperationAsync(stream, folder, sender, token).ConfigureAwait(false);
                break;

            case 2:
                throw new InvalidDataException(
                    "Old transfer protocol. Update the Android application.");

            default:
                throw new InvalidDataException("Unknown operation");
        }
    }

    private async Task HandleDocumentOperationAsync(
        NetworkStream stream,
        string folder,
        string sender,
        CancellationToken token)
    {
        // Only one document at a time, as the team decided.
        if (Interlocked.CompareExchange(ref _busy, 1, 0) != 0)
        {
            await RejectBusyAsync(stream, sender, token).ConfigureAwait(false);
            return;
        }

        try
        {
            await ReceiveDocumentAsync(stream, folder, sender, token).ConfigureAwait(false);
        }
        finally
        {
            Interlocked.Exchange(ref _busy, 0);
        }
    }

    /// <summary>
    /// Reads the whole upload, throws the bytes away, and only then says the
    /// receiver is busy. Reading first matters: if we closed the connection while
    /// the phone was still writing, the phone would show a socket error instead of
    /// Edrian's "Transfer rejected: Receiver busy. Please try again later." message.
    /// </summary>
    private async Task RejectBusyAsync(NetworkStream stream, string sender, CancellationToken token)
    {
        TransferRejected?.Invoke(this, new TransferFailedEventArgs
        {
            FileName = sender,
            Message = PrintLinkProtocol.BusyMessage
        });

        try
        {
            await DiscardUploadAsync(stream, token).ConfigureAwait(false);
        }
        catch
        {
            // If the phone stops sending, we still try to give it the busy message.
        }

        await PrintLinkProtocol
            .WriteStatusAsync(stream, false, PrintLinkProtocol.BusyMessage, token)
            .ConfigureAwait(false);
    }

    /// <summary>Reads the metadata and the file body of a rejected upload without saving anything.</summary>
    private static async Task DiscardUploadAsync(NetworkStream stream, CancellationToken token)
    {
        byte[] nameLengthBytes = new byte[4];
        await ReadExactlyAsync(stream, nameLengthBytes, token).ConfigureAwait(false);

        int nameLength = BinaryPrimitives.ReadInt32BigEndian(nameLengthBytes);

        if (nameLength < 1 || nameLength > PrintLinkProtocol.MaxFileNameLength)
            throw new InvalidDataException("Invalid filename length");

        byte[] nameBytes = new byte[nameLength];
        await ReadExactlyAsync(stream, nameBytes, token).ConfigureAwait(false);

        byte[] sizeBytes = new byte[8];
        await ReadExactlyAsync(stream, sizeBytes, token).ConfigureAwait(false);

        long fileSize = BinaryPrimitives.ReadInt64BigEndian(sizeBytes);

        if (fileSize <= 0 || fileSize > PrintLinkProtocol.MaxFileSize)
            throw new InvalidDataException("Invalid file size or exceeds 10 MB");

        byte[] discardBuffer = new byte[65536];
        long remaining = fileSize;

        while (remaining > 0)
        {
            int toRead = (int)Math.Min(discardBuffer.Length, remaining);
            int received = await ReadWithTimeoutAsync(stream, discardBuffer.AsMemory(0, toRead), token)
                .ConfigureAwait(false);

            if (received == 0)
                throw new EndOfStreamException("Transfer ended before completion");

            remaining -= received;
        }

        // The 32-byte SHA-256 the phone sends last.
        byte[] hash = new byte[32];
        await ReadExactlyAsync(stream, hash, token).ConfigureAwait(false);
    }

    private async Task ReceiveDocumentAsync(
        NetworkStream stream,
        string folder,
        string sender,
        CancellationToken token)
    {
        // 2. File name length, then the name itself.
        byte[] nameLengthBytes = new byte[4];
        await ReadExactlyAsync(stream, nameLengthBytes, token).ConfigureAwait(false);

        int nameLength = BinaryPrimitives.ReadInt32BigEndian(nameLengthBytes);

        if (nameLength < 1 || nameLength > PrintLinkProtocol.MaxFileNameLength)
            throw new InvalidDataException("Invalid filename length");

        byte[] nameBytes = new byte[nameLength];
        await ReadExactlyAsync(stream, nameBytes, token).ConfigureAwait(false);

        string rawName = Encoding.UTF8.GetString(nameBytes);
        string? fileName = PrintLinkProtocol.SafeFileName(rawName);

        if (fileName is null)
            throw new InvalidDataException("Invalid filename or unsupported file type");

        // 3. Exact file size.
        byte[] sizeBytes = new byte[8];
        await ReadExactlyAsync(stream, sizeBytes, token).ConfigureAwait(false);

        long fileSize = BinaryPrimitives.ReadInt64BigEndian(sizeBytes);

        if (fileSize <= 0 || fileSize > PrintLinkProtocol.MaxFileSize)
            throw new InvalidDataException("Invalid file size or exceeds 10 MB");

        // 4. Write into a temporary file while hashing the exact bytes received.
        string temporaryFile = Path.Combine(
            folder,
            $".upload-{Guid.NewGuid():N}.part");

        DateTime startedAt = DateTime.Now;
        var stopwatch = System.Diagnostics.Stopwatch.StartNew();

        byte[] receiverHash;

        try
        {
            using (var output = new FileStream(
                       temporaryFile,
                       FileMode.CreateNew,
                       FileAccess.Write,
                       FileShare.None,
                       bufferSize: 4096,
                       useAsync: true))
            using (var hasher = IncrementalHash.CreateHash(HashAlgorithmName.SHA256))
            {
                byte[] buffer = new byte[65536];
                long remaining = fileSize;
                long totalReceived = 0;
                var reportTimer = System.Diagnostics.Stopwatch.StartNew();

                while (remaining > 0)
                {
                    token.ThrowIfCancellationRequested();

                    int toRead = (int)Math.Min(buffer.Length, remaining);
                    int received = await ReadWithTimeoutAsync(stream, buffer.AsMemory(0, toRead), token)
                        .ConfigureAwait(false);

                    if (received == 0)
                        throw new EndOfStreamException("Transfer ended before completion");

                    await output.WriteAsync(buffer.AsMemory(0, received), token).ConfigureAwait(false);
                    hasher.AppendData(buffer, 0, received);

                    remaining -= received;
                    totalReceived += received;

                    // Report a few times per second so the screen stays smooth.
                    if (reportTimer.ElapsedMilliseconds >= 120)
                    {
                        reportTimer.Restart();

                        ProgressChanged?.Invoke(this, new TransferProgressEventArgs
                        {
                            FileName = fileName,
                            SenderLabel = sender,
                            BytesReceived = totalReceived,
                            TotalBytes = fileSize,
                            BytesPerSecond = stopwatch.Elapsed.TotalSeconds > 0
                                ? totalReceived / stopwatch.Elapsed.TotalSeconds
                                : 0
                        });
                    }
                }

                output.Flush(flushToDisk: true);
                receiverHash = hasher.GetHashAndReset();
            }

            ProgressChanged?.Invoke(this, new TransferProgressEventArgs
            {
                FileName = fileName,
                SenderLabel = sender,
                BytesReceived = fileSize,
                TotalBytes = fileSize,
                BytesPerSecond = stopwatch.Elapsed.TotalSeconds > 0
                    ? fileSize / stopwatch.Elapsed.TotalSeconds
                    : 0
            });

            // 5. The phone appends its own 32-byte SHA-256 right after the file bytes.
            byte[] senderHash = new byte[32];
            await ReadExactlyAsync(stream, senderHash, token).ConfigureAwait(false);

            // 6. Compare. A mismatch means the file is deleted, never saved.
            if (!CryptographicOperations.FixedTimeEquals(receiverHash, senderHash))
            {
                throw new InvalidDataException(
                    "SHA-256 mismatch. Document rejected.");
            }

            // 7. Give the document a name that cannot overwrite an existing file.
            string savedName = PrintLinkProtocol.UniqueName(folder, fileName);
            string destination = Path.Combine(folder, savedName);

            // 8. Only now does the temporary file become a real document.
            File.Move(temporaryFile, destination);

            // 9. Tell the phone only after the file is safely on disk.
            await PrintLinkProtocol
                .WriteStatusAsync(stream, true, $"Verified and saved: {savedName}", token)
                .ConfigureAwait(false);

            DocumentReceived?.Invoke(this, new DocumentReceivedEventArgs
            {
                FileName = savedName,
                SavedPath = destination,
                SizeBytes = fileSize,
                Sha256Hex = Convert.ToHexString(receiverHash),
                ReceivedAt = startedAt,
                SenderLabel = sender,
                DurationSeconds = stopwatch.Elapsed.TotalSeconds
            });
        }
        catch
        {
            // A dropped connection, bad hash or full disk leaves no half document behind.
            if (File.Exists(temporaryFile))
            {
                try
                {
                    File.Delete(temporaryFile);
                }
                catch
                {
                    // If the file is still locked, the next run can clean it up.
                }
            }

            throw;
        }
    }

    private void ReportFailure(string sender, string message)
    {
        TransferFailed?.Invoke(this, new TransferFailedEventArgs
        {
            FileName = sender,
            Message = message
        });
    }

    private static async Task ReadExactlyAsync(Stream stream, byte[] buffer, CancellationToken token)
    {
        int offset = 0;

        while (offset < buffer.Length)
        {
            int read = await ReadWithTimeoutAsync(stream, buffer.AsMemory(offset), token).ConfigureAwait(false);

            if (read == 0)
                throw new EndOfStreamException("The sender closed the connection early.");

            offset += read;
        }
    }

    /// <summary>
    /// One read that gives up after <see cref="ReadTimeoutMilliseconds"/> of silence.
    /// The timeout restarts on every read, so a slow but steady transfer is never cut off.
    /// </summary>
    private static async Task<int> ReadWithTimeoutAsync(
        Stream stream,
        Memory<byte> buffer,
        CancellationToken token)
    {
        using var readCancellation = CancellationTokenSource.CreateLinkedTokenSource(token);
        readCancellation.CancelAfter(ReadTimeoutMilliseconds);

        try
        {
            return await stream.ReadAsync(buffer, readCancellation.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!token.IsCancellationRequested)
        {
            throw new TimeoutException(
                $"The sender stopped sending for {ReadTimeoutMilliseconds / 1000} seconds.");
        }
    }

    public void Dispose()
    {
        Stop();
        _cancellation?.Dispose();
        _cancellation = null;
    }
}
