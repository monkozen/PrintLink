# PrintLink Receiver (Windows / WPF)

WPF receiver for the PrintLink offline Android-to-Windows document transfer.
It speaks the same compact binary protocol as Edrian's Android sender.

## Build and run

1. Install the .NET 10 SDK.
2. Open `PrintLink.Receiver\PrintLink.Receiver.slnx` (or the `.csproj` inside it).
3. Press Run. The window opens at 1280 wide.

## Using it

1. Pick a save folder with **Browse** (default `Documents\PrintLinkReceived`). You cannot change it while listening.
2. Click **Start Listening**. The card shows this PC's local IP and port 5050.
3. On the phone, enter that IP and port, press **Connect to Receiver**, then send a PDF or DOCX.

Windows Firewall may ask for permission the first time — allow it on private networks, otherwise the phone cannot reach port 5050.

## Protocol (as implemented by the Android sender)

Each operation opens its own TCP connection.

- Connection test: `PLK1` + operation byte `1`.
- Document transfer: `PLK1` + operation byte `3`, 4-byte big-endian filename length,
  UTF-8 filename, 8-byte big-endian file size, file bytes, 32-byte raw SHA-256 digest.
- Reply: 1 byte status (1 = success, 0 = failure), 2-byte big-endian message length,
  then UTF-8 message text (1-512 bytes).

Receiver rules:

- Only `.pdf` and `.docx`, maximum 10,000,000 bytes, filename up to 200 characters.
- Filenames are sanitised (no paths, no invalid Windows characters).
- The file is streamed to a hidden `.part` file. The SHA-256 must match before it is renamed.
- Duplicates become `name (1).pdf`; nothing is ever overwritten.
- A dropped connection, bad name, wrong type, oversize file or hash mismatch leaves no file behind.
- One transfer at a time. A second sender gets `Receiver busy. Please try again later.` after its
  upload is drained, so its app shows our message instead of a socket error.
- Reads give up after 45 seconds of silence (45 s is reset per read, not a whole-transfer deadline);
  writes give up after 15 seconds.
- No pairing or sender authorization in this prototype.
