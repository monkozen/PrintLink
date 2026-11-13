using Microsoft.Win32;
using PrintLink.Receiver.Models;
using PrintLink.Receiver.Services;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;

namespace PrintLink.Receiver;

/// <summary>
/// The glass dashboard. It displays what the receiver service reports
/// and never touches the network itself.
/// </summary>
public partial class MainWindow : Window
{
    private readonly DispatcherTimer _clockTimer;
    private readonly ReceiverService _receiver = new();
    private readonly ObservableCollection<ReceivedFile> _receivedFiles = new();

    private int _verifiedCount;
    private int _failedCount;

    public MainWindow()
    {
        InitializeComponent();

        _clockTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        _clockTimer.Tick += (_, _) => UpdateClock();
        _clockTimer.Start();
        UpdateClock();

        _receiver.ProgressChanged += OnProgressChanged;
        _receiver.DocumentReceived += OnDocumentReceived;
        _receiver.TransferFailed += OnTransferFailed;
        _receiver.TransferRejected += OnTransferRejected;

        ReceivedFilesGrid.ItemsSource = _receivedFiles;

        LocalIpText.Text = FindLocalIpv4();

        string defaultFolder = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments),
            "PrintLinkReceived");

        SetSaveFolder(defaultFolder);

        // The window starts in the "not listening" state so the operator chooses when to begin.
        ApplyListeningState(false);
    }

    private void UpdateClock()
    {
        var now = DateTime.Now;
        ClockTimeText.Text = now.ToString("h:mm:ss tt");
        ClockDateText.Text = now.ToString("ddd, d MMM yyyy");
    }

    private void ToggleListeningButton_Click(object sender, RoutedEventArgs e)
    {
        if (_receiver.IsListening)
        {
            StopListening();
            return;
        }

        StartListening();
    }

    private void StartListening()
    {
        try
        {
            Directory.CreateDirectory(_receiver.SaveFolder);
            _receiver.Start();
        }
        catch (SocketException ex)
        {
            FooterStatusText.Text = "Port 5050 is busy or blocked";
            MessageBox.Show(
                this,
                $"Could not start listening on port {PrintLinkProtocol.Port}.\n\n{ex.Message}\n\n" +
                "Another program may be using the port, or Windows Firewall is blocking it. " +
                "Allow PrintLink Receiver in Windows Firewall for private networks, then try again.",
                "PrintLink Receiver",
                MessageBoxButton.OK,
                MessageBoxImage.Warning);
            return;
        }
        catch (Exception ex)
        {
            FooterStatusText.Text = "Could not start listening";
            MessageBox.Show(this, ex.Message, "PrintLink Receiver",
                MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        ApplyListeningState(true);
        FooterStatusText.Text = $"Listening on port {PrintLinkProtocol.Port}";
    }

    private void StopListening()
    {
        _receiver.Stop();
        ApplyListeningState(false);
        ResetTransferPanel();
        FooterStatusText.Text = $"Stopped · port {PrintLinkProtocol.Port} is closed";
    }

    /// <summary>Keeps the badges, button and folder controls in step with the listener.</summary>
    private void ApplyListeningState(bool isListening)
    {
        if (isListening)
        {
            ListenerStateText.Text = "Listening";
            ListenerDot.Fill = (Brush)FindResource("SuccessBrush");
            ToggleListeningButton.Style = (Style)FindResource("DangerActionButton");
            ToggleLabelText.Text = "Stop Listening";
            ToggleIconText.Text = "\uE71A";
        }
        else
        {
            ListenerStateText.Text = "Idle";
            ListenerDot.Fill = (Brush)FindResource("TextDimBrush");
            ToggleListeningButton.Style = (Style)FindResource("PrimaryActionButton");
            ToggleLabelText.Text = "Start Listening";
            ToggleIconText.Text = "\uE768";
        }

        // The folder is locked while files could arrive, as the design notes say.
        BrowseButton.IsEnabled = !isListening;
    }

    private void BrowseButton_Click(object sender, RoutedEventArgs e)
    {
        if (_receiver.IsListening)
            return;

        var dialog = new OpenFolderDialog
        {
            Title = "Choose the folder where received documents are saved",
            InitialDirectory = Directory.Exists(_receiver.SaveFolder)
                ? _receiver.SaveFolder
                : Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments)
        };

        if (dialog.ShowDialog(this) == true)
            SetSaveFolder(dialog.FolderName);
    }

    private void SetSaveFolder(string folder)
    {
        _receiver.SaveFolder = folder;
        SavePathBox.Text = folder;
    }

    private void OnProgressChanged(object? sender, TransferProgressEventArgs e)
    {
        Dispatcher.BeginInvoke(() =>
        {
            TransferIdlePanel.Visibility = Visibility.Collapsed;
            TransferActivePanel.Visibility = Visibility.Visible;
            LivePill.Visibility = Visibility.Visible;

            TransferFileNameText.Text = e.FileName;
            TransferSenderText.Text = $"Android sender · {e.SenderLabel}";
            TransferPercentText.Text = $"{e.Percent}%";
            TransferBytesText.Text =
                $"{PrintLinkProtocol.FormatBytes(e.BytesReceived)} / {PrintLinkProtocol.FormatBytes(e.TotalBytes)}";

            TransferVerifyText.Text = "SHA-256 · Receiving...";
            TransferSpeedText.Text = $"{FormatSpeed(e.BytesPerSecond)} · about {RemainingSeconds(e)} sec left";

            UpdateProgressWidth(e.Percent);
        });
    }

    private void OnDocumentReceived(object? sender, DocumentReceivedEventArgs e)
    {
        Dispatcher.BeginInvoke(() =>
        {
            UpdateProgressWidth(100);

            TransferVerifyText.Text = "SHA-256 · Verified";
            TransferSpeedText.Text = $"Saved in {e.DurationSeconds:0.0} sec";

            _verifiedCount++;
            VerifiedLegendText.Text = $"{_verifiedCount} verified";

            _receivedFiles.Insert(0, new ReceivedFile
            {
                FileName = e.FileName,
                ReceivedAt = e.ReceivedAt.ToString("h:mm:ss tt"),
                Size = PrintLinkProtocol.FormatBytes(e.SizeBytes),
                Status = "Verified",
                SavedPath = e.SavedPath
            });

            ReceivedCountText.Text = _receivedFiles.Count.ToString();

            FooterStatusText.Text = $"Saved {e.FileName} · SHA-256 {e.Sha256Hex[..12]}...";

            // Let the finished transfer stay visible for a moment, then go back to waiting.
            var timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(3) };
            timer.Tick += (_, _) =>
            {
                timer.Stop();
                ResetTransferPanel();
            };
            timer.Start();
        });
    }

    /// <summary>
    /// A second phone tried to send while another document was arriving. Nothing was
    /// saved, and the current transfer keeps running, so only the footer changes.
    /// </summary>
    private void OnTransferRejected(object? sender, TransferFailedEventArgs e)
    {
        Dispatcher.BeginInvoke(() => { FooterStatusText.Text = $"Sender refused · {e.Message}"; });
    }

    private void OnTransferFailed(object? sender, TransferFailedEventArgs e)
    {
        Dispatcher.BeginInvoke(() =>
        {
            _failedCount++;
            FailedLegendText.Text = $"{_failedCount} failed";
            FooterStatusText.Text = $"Transfer failed · {e.Message}";

            ResetTransferPanel();
        });
    }

    private void ResetTransferPanel()
    {
        TransferActivePanel.Visibility = Visibility.Collapsed;
        TransferIdlePanel.Visibility = Visibility.Visible;
        LivePill.Visibility = Visibility.Collapsed;
        UpdateProgressWidth(0);
    }

    /// <summary>The progress bar is a Border, so its width is a share of the track.</summary>
    private void UpdateProgressWidth(int percent)
    {
        double trackWidth = TransferProgressBar.Parent is FrameworkElement track
            ? track.ActualWidth - 2
            : 0;

        if (trackWidth <= 0)
            return;

        TransferProgressBar.Width =
            Math.Max(0, Math.Min(trackWidth, trackWidth * percent / 100.0));
    }

    private static string FormatSpeed(double bytesPerSecond)
    {
        if (bytesPerSecond >= 1_000_000)
            return $"{bytesPerSecond / 1_000_000.0:0.0} MB/s";

        if (bytesPerSecond >= 1_000)
            return $"{bytesPerSecond / 1_000.0:0} KB/s";

        return $"{bytesPerSecond:0} B/s";
    }

    private static int RemainingSeconds(TransferProgressEventArgs e)
    {
        if (e.BytesPerSecond <= 0)
            return 0;

        long left = e.TotalBytes - e.BytesReceived;
        return (int)Math.Max(0, Math.Round(left / e.BytesPerSecond));
    }

    /// <summary>
    /// Finds a local IPv4 address so the operator can tell students what to type.
    /// </summary>
    private static string FindLocalIpv4()
    {
        var addresses = new List<string>();

        foreach (var nic in System.Net.NetworkInformation.NetworkInterface.GetAllNetworkInterfaces())
        {
            if (nic.OperationalStatus != System.Net.NetworkInformation.OperationalStatus.Up)
                continue;

            if (nic.NetworkInterfaceType == System.Net.NetworkInformation.NetworkInterfaceType.Loopback)
                continue;

            foreach (var info in nic.GetIPProperties().UnicastAddresses)
            {
                if (info.Address.AddressFamily == AddressFamily.InterNetwork &&
                    !IPAddress.IsLoopback(info.Address))
                {
                    addresses.Add(info.Address.ToString());
                }
            }
        }

        return addresses.Count > 0 ? addresses[0] : "Not connected";
    }

    protected override void OnClosed(EventArgs e)
    {
        _clockTimer.Stop();
        _receiver.Dispose();
        base.OnClosed(e);
    }
}
