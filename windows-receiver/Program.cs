
using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Net;
using System.Net.Sockets;
using System.Text;

const int port = 5050;

string saveFolder = Path.Combine(
    Environment.GetFolderPath(
        Environment.SpecialFolder.MyDocuments
    ),
    "PrintLinkReceived"
);

Directory.CreateDirectory(saveFolder);

var listener = new TcpListener(IPAddress.Any, port);
listener.Start();

Console.WriteLine("PrintLink Receiver Started");
Console.WriteLine($"Listening on TCP port {port}");
Console.WriteLine($"Save folder: {saveFolder}");

while (true)
{
    using TcpClient client = listener.AcceptTcpClient();

    client.ReceiveTimeout = 45000;
    client.SendTimeout = 15000;

    using NetworkStream stream = client.GetStream();

    Console.WriteLine(
        $"Client connected: {client.Client.RemoteEndPoint}"
    );

    try
    {
        // Read the PrintLink protocol header.
        byte[] header = new byte[5];
        stream.ReadExactly(header);

        string magic = Encoding.ASCII.GetString(header, 0, 4);
        int operation = header[4];

        if (magic != "PLK1")
            throw new InvalidDataException(
                "Unsupported protocol"
            );

        if (operation == 1)
        {
            SendStatus(
                stream,
                true,
                "Connected to PrintLink"
            );

            Console.WriteLine("Connection test successful");
        }
        else if (operation == 3)
        {
            ReceiveDocument(stream, saveFolder);
        }
        else if (operation == 2)
        {
            throw new InvalidDataException(
                "Old transfer protocol. Update the Android application."
            );
        }
        else
        {
            throw new InvalidDataException(
                "Unknown operation"
            );
        }
    }
    catch (Exception ex)
    {
        Console.WriteLine($"Error: {ex.Message}");

        try
        {
            SendStatus(stream, false, ex.Message);
        }
        catch
        {
            // The connection may already be closed.
        }
    }
}


static void ReceiveDocument(
    NetworkStream stream,
    string folder
)
{
    // 1. Read filename length.
    Span<byte> nameLengthBytes = stackalloc byte[4];
    stream.ReadExactly(nameLengthBytes);

    int nameLength =
        BinaryPrimitives.ReadInt32BigEndian(nameLengthBytes);

    if (nameLength < 1 || nameLength > 200)
        throw new InvalidDataException(
            "Invalid filename length"
        );

    // 2. Read filename.
    byte[] nameBytes = new byte[nameLength];
    stream.ReadExactly(nameBytes);

    string fileName = Encoding.UTF8.GetString(nameBytes);

    if (
        string.IsNullOrWhiteSpace(fileName) ||
        fileName != Path.GetFileName(fileName) ||
        fileName.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0 ||
        fileName.EndsWith('.') ||
        fileName.EndsWith(' ')
    )
    {
        throw new InvalidDataException(
            "Invalid filename"
        );
    }

    string extension = Path.GetExtension(fileName);

    if (
        !extension.Equals(
            ".pdf",
            StringComparison.OrdinalIgnoreCase
        ) &&
        !extension.Equals(
            ".docx",
            StringComparison.OrdinalIgnoreCase
        )
    )
    {
        throw new InvalidDataException(
            "Only PDF and DOCX files are allowed"
        );
    }

    // 3. Read file size.
    Span<byte> sizeBytes = stackalloc byte[8];
    stream.ReadExactly(sizeBytes);

    long fileSize =
        BinaryPrimitives.ReadInt64BigEndian(sizeBytes);

    if (fileSize <= 0 || fileSize > 10_000_000)
        throw new InvalidDataException(
            "Invalid file size or exceeds 10 MB"
        );

    Console.WriteLine($"Receiving: {fileName}");
    Console.WriteLine($"Expected size: {fileSize} bytes");

    // Use a temporary file until verification succeeds.
    string temporaryFile = Path.Combine(
        folder,
        $".upload-{Guid.NewGuid():N}.part"
    );

    try
    {
        byte[] receiverHash;

        // 4. Receive file and calculate SHA-256.
        using (var output = new FileStream(
            temporaryFile,
            FileMode.CreateNew,
            FileAccess.Write,
            FileShare.None
        ))
        using (var hasher = IncrementalHash.CreateHash(
            HashAlgorithmName.SHA256
        ))
        {
            byte[] buffer = new byte[65536];
            long remaining = fileSize;

            while (remaining > 0)
            {
                int toRead = (int)Math.Min(
                    buffer.Length,
                    remaining
                );

                int received = stream.Read(
                    buffer,
                    0,
                    toRead
                );

                if (received == 0)
                    throw new EndOfStreamException(
                        "Transfer ended before completion"
                    );

                output.Write(buffer, 0, received);

                // Hash exactly the bytes received.
                hasher.AppendData(buffer, 0, received);

                remaining -= received;
            }

            output.Flush(flushToDisk: true);

            receiverHash = hasher.GetHashAndReset();
        }

        // 5. Read the 32-byte SHA-256 from Android.
        byte[] senderHash = new byte[32];
        stream.ReadExactly(senderHash);

        // 6. Compare both hashes.
        bool hashesMatch =
            CryptographicOperations.FixedTimeEquals(
                receiverHash,
                senderHash
            );

        if (!hashesMatch)
        {
            Console.WriteLine("SHA-256 mismatch!");

            throw new InvalidDataException(
                "SHA-256 mismatch. Document rejected."
            );
        }

        Console.WriteLine("SHA-256 verification passed!");
        Console.WriteLine(
            $"SHA-256: {Convert.ToHexString(receiverHash)}"
        );

        // 7. Preserve filename without overwriting.
        string baseName =
            Path.GetFileNameWithoutExtension(fileName);

        string savedName = fileName;
        int copyNumber = 1;

        while (File.Exists(Path.Combine(folder, savedName)))
        {
            savedName =
                $"{baseName} ({copyNumber}){extension}";

            copyNumber++;
        }

        string destination = Path.Combine(
            folder,
            savedName
        );

        // 8. Promote verified temporary file.
        File.Move(temporaryFile, destination);

        Console.WriteLine($"Saved: {destination}");

        // 9. Confirm successful transfer.
        SendStatus(
            stream,
            true,
            $"Verified and saved: {savedName}"
        );
    }
    catch
    {
        // Remove failed or corrupted temporary file.
        if (File.Exists(temporaryFile))
        {
            File.Delete(temporaryFile);
        }

        throw;
    }
}


static void SendStatus(
    Stream stream,
    bool success,
    string message
)
{
    byte[] messageBytes = Encoding.UTF8.GetBytes(message);

    if (messageBytes.Length > 512)
        messageBytes = Encoding.UTF8.GetBytes(
            "Transfer failed"
        );

    Span<byte> header = stackalloc byte[3];

    header[0] = success ? (byte)1 : (byte)0;

    BinaryPrimitives.WriteUInt16BigEndian(
        header.Slice(1),
        (ushort)messageBytes.Length
    );

    stream.Write(header);
    stream.Write(messageBytes);
    stream.Flush();
}
