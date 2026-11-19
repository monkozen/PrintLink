using System;
using System.Buffers.Binary;
using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace PrintLink.Receiver.Services;

/// <summary>
/// The exact message format Edrian's Android app already sends.
/// Do not change these numbers without asking him to change the phone app too.
///
/// Phone -> receiver:  "PLK1" (4 ASCII letters) + 1 byte operation number.
///   operation 1 = connection test ("are you there?")
///   operation 3 = send a document
/// Receiver -> phone:  1 byte (1 = success, 0 = failure) + 2 byte length + UTF-8 text.
/// </summary>
public static class PrintLinkProtocol
{
    public const int Port = 5050;

    public static readonly byte[] Magic = "PLK1"u8.ToArray();

    public const byte OperationPing = 1;

    public const byte OperationSendDocument = 3;

    /// <summary>Same decimal 10 MB limit the Android app enforces.</summary>
    public const long MaxFileSize = 10_000_000;

    public const int MaxFileNameLength = 200;

    public const int MaxStatusMessageLength = 512;

    /// <summary>How long a reply may take to reach the phone before we give up.</summary>
    public const int WriteTimeoutMilliseconds = 15_000;

    /// <summary>
    /// The exact wording Edrian asked us to send when a transfer is already running,
    /// so his app shows "Transfer rejected: ..." and nothing is saved.
    /// </summary>
    public const string BusyMessage = "Receiver busy. Please try again later.";

    /// <summary>Writes the small reply the phone reads after every request.</summary>
    public static async Task WriteStatusAsync(
        Stream stream,
        bool success,
        string message,
        CancellationToken token)
    {
        byte[] messageBytes = Encoding.UTF8.GetBytes(message);

        if (messageBytes.Length > MaxStatusMessageLength)
            messageBytes = Encoding.UTF8.GetBytes("Transfer failed");

        byte[] header = new byte[3];
        header[0] = success ? (byte)1 : (byte)0;
        BinaryPrimitives.WriteUInt16BigEndian(header.AsSpan(1), (ushort)messageBytes.Length);

        using var writeCancellation = CancellationTokenSource.CreateLinkedTokenSource(token);
        writeCancellation.CancelAfter(WriteTimeoutMilliseconds);

        try
        {
            await stream.WriteAsync(header, writeCancellation.Token).ConfigureAwait(false);
            await stream.WriteAsync(messageBytes, writeCancellation.Token).ConfigureAwait(false);
            await stream.FlushAsync(writeCancellation.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!token.IsCancellationRequested)
        {
            throw new TimeoutException("The reply could not reach the sender.");
        }
    }

    /// <summary>
    /// Removes anything dangerous from a client-supplied name and keeps it
    /// as a plain file name inside the chosen folder. Returns null when the
    /// name cannot be trusted at all.
    /// </summary>
    public static string? SafeFileName(string? incoming)
    {
        if (string.IsNullOrWhiteSpace(incoming))
            return null;

        // A name with a folder in it (..\..\Windows\system.ini) is never allowed.
        if (incoming != Path.GetFileName(incoming))
            return null;

        if (incoming.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0)
            return null;

        if (incoming.EndsWith('.') || incoming.EndsWith(' '))
            return null;

        string extension = Path.GetExtension(incoming);

        if (!extension.Equals(".pdf", StringComparison.OrdinalIgnoreCase) &&
            !extension.Equals(".docx", StringComparison.OrdinalIgnoreCase))
            return null;

        return incoming;
    }

    /// <summary>
    /// Picks a name that does not overwrite an existing document:
    /// report.pdf becomes report (1).pdf, then report (2).pdf, and so on.
    /// </summary>
    public static string UniqueName(string folder, string fileName)
    {
        string baseName = Path.GetFileNameWithoutExtension(fileName);
        string extension = Path.GetExtension(fileName);
        string candidate = fileName;
        int copyNumber = 1;

        while (File.Exists(Path.Combine(folder, candidate)))
        {
            candidate = $"{baseName} ({copyNumber}){extension}";
            copyNumber++;
        }

        return candidate;
    }

    public static string FormatBytes(long bytes)
    {
        if (bytes >= 1_000_000)
            return $"{bytes / 1_000_000.0:0.0} MB";

        if (bytes >= 1_000)
            return $"{bytes / 1_000.0:0.0} KB";

        return $"{bytes} B";
    }
}
