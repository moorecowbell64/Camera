using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using OpenCvSharp;
using OpenCvSharp.WpfExtensions;

namespace JennovCamera.WPF;

public partial class MainWindow : System.Windows.Window
{
    private CameraClient? _client;
    private PTZController? _ptz;
    private RecordingManager? _recording;
    private bool _isConnected;
    private bool _isRecording;
    private float _ptzSpeed = 0.5f;

    public MainWindow()
    {
        InitializeComponent();
        PasswordText.Password = "hydroLob99";
    }

    private async void ConnectButton_Click(object sender, RoutedEventArgs e)
    {
        if (!_isConnected)
        {
            await ConnectAsync();
        }
        else
        {
            Disconnect();
        }
    }

    private async Task ConnectAsync()
    {
        try
        {
            UpdateConnectionStatus("Connecting...", Brushes.Orange);
            ConnectButton.IsEnabled = false;

            var ip = CameraIpText.Text.Trim();
            var username = UsernameText.Text.Trim();
            var password = PasswordText.Password;

            _client = new CameraClient(ip, 80);

            if (!await _client.LoginAsync(username, password))
            {
                MessageBox.Show("Failed to connect to camera. Check credentials and IP.",
                    "Connection Error", MessageBoxButton.OK, MessageBoxImage.Error);
                UpdateConnectionStatus("Connection Failed", Brushes.Red);
                ConnectButton.IsEnabled = true;
                return;
            }

            _ptz = new PTZController(_client);
            _recording = new RecordingManager(_client);
            _recording.FrameCaptured += OnFrameCaptured;

            // Start video streaming
            if (!_recording.StartStreaming())
            {
                MessageBox.Show("Connected but failed to start video stream.",
                    "Warning", MessageBoxButton.OK, MessageBoxImage.Warning);
            }

            _isConnected = true;
            NoVideoText.Visibility = Visibility.Collapsed;
            UpdateConnectionStatus("Connected", Brushes.LimeGreen);
            ConnectButton.Content = "Disconnect";
            ConnectButton.IsEnabled = true;

            // Enable controls
            PTZControls.IsEnabled = true;
            PresetsGroup.IsEnabled = true;
            RecordingGroup.IsEnabled = true;
        }
        catch (Exception ex)
        {
            MessageBox.Show($"Connection error: {ex.Message}",
                "Error", MessageBoxButton.OK, MessageBoxImage.Error);
            UpdateConnectionStatus("Error", Brushes.Red);
            ConnectButton.IsEnabled = true;
        }
    }

    private void Disconnect()
    {
        try
        {
            if (_recording != null)
            {
                _recording.FrameCaptured -= OnFrameCaptured;
                _recording.StopRecording();
                _recording.StopStreaming();
                _recording.Dispose();
                _recording = null;
            }

            _ptz = null;
            _client?.Dispose();
            _client = null;

            _isConnected = false;
            _isRecording = false;

            NoVideoText.Visibility = Visibility.Visible;
            VideoImage.Source = null;
            UpdateConnectionStatus("Disconnected", Brushes.Red);
            UpdateRecordingStatus("Not Recording", Brushes.Gray);
            ConnectButton.Content = "Connect Camera";

            // Disable controls
            PTZControls.IsEnabled = false;
            PresetsGroup.IsEnabled = false;
            RecordingGroup.IsEnabled = false;
            RecordButton.Content = "Start Recording";
        }
        catch (Exception ex)
        {
            MessageBox.Show($"Disconnect error: {ex.Message}",
                "Error", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private void OnFrameCaptured(object? sender, Mat frame)
    {
        try
        {
            // Convert Mat to BitmapSource on UI thread
            Dispatcher.Invoke(() =>
            {
                try
                {
                    var bitmap = BitmapSourceConverter.ToBitmapSource(frame);
                    bitmap.Freeze(); // Allow cross-thread access
                    VideoImage.Source = bitmap;
                }
                catch
                {
                    // Ignore frame conversion errors
                }
            });
        }
        finally
        {
            frame.Dispose();
        }
    }

    private void UpdateConnectionStatus(string text, Brush color)
    {
        ConnectionStatus.Text = text;
        ConnectionStatus.Foreground = color;
    }

    private void UpdateRecordingStatus(string text, Brush color)
    {
        RecordingStatus.Text = text;
        RecordingStatus.Foreground = color;
    }

    private void SpeedSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        _ptzSpeed = (float)e.NewValue;
        if (SpeedLabel != null)
            SpeedLabel.Content = _ptzSpeed.ToString("F1");
    }

    // PTZ Button Handlers
    private async void UpButton_MouseDown(object sender, MouseButtonEventArgs e)
    {
        if (_ptz != null)
            await _ptz.TiltUpAsync(_ptzSpeed);
    }

    private async void DownButton_MouseDown(object sender, MouseButtonEventArgs e)
    {
        if (_ptz != null)
            await _ptz.TiltDownAsync(_ptzSpeed);
    }

    private async void LeftButton_MouseDown(object sender, MouseButtonEventArgs e)
    {
        if (_ptz != null)
            await _ptz.PanLeftAsync(_ptzSpeed);
    }

    private async void RightButton_MouseDown(object sender, MouseButtonEventArgs e)
    {
        if (_ptz != null)
            await _ptz.PanRightAsync(_ptzSpeed);
    }

    private async void ZoomInButton_MouseDown(object sender, MouseButtonEventArgs e)
    {
        if (_ptz != null)
            await _ptz.ZoomInAsync(0.3f);
    }

    private async void ZoomOutButton_MouseDown(object sender, MouseButtonEventArgs e)
    {
        if (_ptz != null)
            await _ptz.ZoomOutAsync(0.3f);
    }

    private async void PTZButton_MouseUp(object sender, MouseButtonEventArgs e)
    {
        if (_ptz != null)
            await _ptz.StopMoveAsync();
    }

    private async void StopButton_Click(object sender, RoutedEventArgs e)
    {
        if (_ptz != null)
            await _ptz.StopMoveAsync();
    }

    private async void GotoPresetButton_Click(object sender, RoutedEventArgs e)
    {
        if (_client == null) return;

        var preset = (PresetCombo.SelectedIndex + 1).ToString();
        try
        {
            await _client.Onvif.GotoPresetAsync(preset, 1.0f);
            UpdateConnectionStatus($"Moving to Preset {preset}", Brushes.Orange);

            // Reset status after delay
            await Task.Delay(2000);
            if (_isConnected)
                UpdateConnectionStatus("Connected", Brushes.LimeGreen);
        }
        catch (Exception ex)
        {
            MessageBox.Show($"Failed to go to preset: {ex.Message}",
                "Error", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    private void RecordButton_Click(object sender, RoutedEventArgs e)
    {
        if (_recording == null) return;

        if (!_isRecording)
        {
            if (_recording.StartRecording())
            {
                _isRecording = true;
                RecordButton.Content = "Stop Recording";
                RecordButton.Background = (Brush)Application.Current.Resources["AccentRedBrush"];
                UpdateRecordingStatus("Recording...", Brushes.Red);
            }
            else
            {
                MessageBox.Show("Failed to start recording.",
                    "Error", MessageBoxButton.OK, MessageBoxImage.Warning);
            }
        }
        else
        {
            var filename = _recording.StopRecording();
            _isRecording = false;
            RecordButton.Content = "Start Recording";
            RecordButton.Background = (Brush)Application.Current.Resources["PrimaryBrush"];
            UpdateRecordingStatus("Not Recording", Brushes.Gray);

            if (!string.IsNullOrEmpty(filename))
            {
                MessageBox.Show($"Recording saved:\n{filename}",
                    "Recording Saved", MessageBoxButton.OK, MessageBoxImage.Information);
            }
        }
    }

    private async void SnapshotButton_Click(object sender, RoutedEventArgs e)
    {
        if (_recording == null) return;

        SnapshotButton.IsEnabled = false;
        try
        {
            if (await _recording.TakeSnapshotAsync())
            {
                MessageBox.Show("Snapshot saved successfully!",
                    "Snapshot", MessageBoxButton.OK, MessageBoxImage.Information);
            }
            else
            {
                MessageBox.Show("Failed to capture snapshot.",
                    "Error", MessageBoxButton.OK, MessageBoxImage.Warning);
            }
        }
        finally
        {
            SnapshotButton.IsEnabled = true;
        }
    }

    protected override void OnClosed(EventArgs e)
    {
        Disconnect();
        base.OnClosed(e);
    }
}
