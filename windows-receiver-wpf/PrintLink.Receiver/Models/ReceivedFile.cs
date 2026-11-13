namespace PrintLink.Receiver.Models;

/// <summary>
/// One row in the received-files table. This is only a display shape,
/// not a record stored anywhere else.
/// </summary>
public class ReceivedFile
{
    public string FileName { get; set; } = string.Empty;

    public string ReceivedAt { get; set; } = string.Empty;

    public string Size { get; set; } = string.Empty;

    public string Status { get; set; } = string.Empty;

    public string SavedPath { get; set; } = string.Empty;
}
