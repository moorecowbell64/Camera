using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using OpenCvSharp;
using OpenCvSharp.WpfExtensions;

namespace JennovCamera.WPF;

public partial class MainWindow : System.Windows.Window
{
    private CameraClient? _client;
    private PTZController? _ptz;
    private RecordingManager? _recording;
    private DeviceManager? _deviceManager;
    private bool _isConnected;
    private bool _isRecording;
    private float _ptzSpeed = 0.5f;
    private VideoEncoderConfig? _currentEncoderConfig;

    public MainWindow()
    {
        InitializeComponent();
        PasswordText.Password = "hydroLob99";

        // Set default selections
        StreamQualityCombo.SelectedIndex = 0; // Main 4K
        ResolutionCombo.SelectedIndex = 0; // 4K
        FrameRateCombo.SelectedIndex = 2; // 20 fps
        BitrateCombo.SelectedIndex = 0; // Auto
        ProfileCombo.SelectedIndex = 0; // High
        OsdPositionCombo.SelectedIndex = 0; // Upper Left
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
            _recording = new RecordingManager(_client, StreamQuality.Main);
            _recording.FrameCaptured += OnFrameCaptured;
            _deviceManager = new DeviceManager(ip, username, password, 80);

            // Update stream quality display
            UpdateStreamQualityDisplay();

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

            // Enable settings controls
            StreamSettingsGroup.IsEnabled = true;
            EncoderSettingsGroup.IsEnabled = true;
            OSDSettingsGroup.IsEnabled = true;
            DeviceInfoGroup.IsEnabled = true;

            // Load device info
            await LoadDeviceInfoAsync();
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
            _deviceManager?.Dispose();
            _deviceManager = null;

            _isConnected = false;
            _isRecording = false;

            NoVideoText.Visibility = Visibility.Visible;
            VideoImage.Source = null;
            UpdateConnectionStatus("Disconnected", Brushes.Red);
            UpdateRecordingStatus("Not Recording", Brushes.Gray);
            StreamQualityStatus.Text = "Stream: --";
            ConnectButton.Content = "Connect Camera";

            // Disable controls
            PTZControls.IsEnabled = false;
            PresetsGroup.IsEnabled = false;
            RecordingGroup.IsEnabled = false;
            RecordButton.Content = "Start Recording";

            // Disable settings controls
            StreamSettingsGroup.IsEnabled = false;
            EncoderSettingsGroup.IsEnabled = false;
            OSDSettingsGroup.IsEnabled = false;
            DeviceInfoGroup.IsEnabled = false;
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

    private void UpdateStreamQualityDisplay()
    {
        if (_recording != null)
        {
            var quality = _recording.CurrentQuality;
            var desc = quality == StreamQuality.Main ? "4K" : "SD";
            StreamQualityStatus.Text = $"Stream: {desc}";
            StreamUrlText.Text = $"RTSP URL: {_recording.CurrentRtspUrl}";
        }
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

    // ===== Settings Tab Event Handlers =====

    private void StreamQualityCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_recording == null || StreamQualityCombo.SelectedItem == null) return;

        var item = (ComboBoxItem)StreamQualityCombo.SelectedItem;
        var tag = item.Tag?.ToString();

        if (tag == "Main")
            StreamUrlText.Text = $"RTSP URL: {_client?.Onvif.GetRtspUrl(StreamQuality.Main)}";
        else
            StreamUrlText.Text = $"RTSP URL: {_client?.Onvif.GetRtspUrl(StreamQuality.Sub)}";
    }

    private void ApplyStreamQuality_Click(object sender, RoutedEventArgs e)
    {
        if (_recording == null || StreamQualityCombo.SelectedItem == null) return;

        var item = (ComboBoxItem)StreamQualityCombo.SelectedItem;
        var tag = item.Tag?.ToString();
        var newQuality = tag == "Main" ? StreamQuality.Main : StreamQuality.Sub;

        if (_recording.IsStreaming)
        {
            var result = MessageBox.Show(
                "Changing stream quality requires restarting the stream. Continue?",
                "Confirm", MessageBoxButton.YesNo, MessageBoxImage.Question);

            if (result != MessageBoxResult.Yes) return;

            _recording.StopStreaming();
            _recording.SetStreamQuality(newQuality);
            _recording.StartStreaming();
        }
        else
        {
            _recording.SetStreamQuality(newQuality);
        }

        UpdateStreamQualityDisplay();
        MessageBox.Show($"Stream quality changed to {newQuality}",
            "Success", MessageBoxButton.OK, MessageBoxImage.Information);
    }

    private async void LoadEncoderSettings_Click(object sender, RoutedEventArgs e)
    {
        if (_deviceManager == null) return;

        try
        {
            var configs = await _deviceManager.GetVideoEncoderConfigsAsync();
            if (configs.Count > 0)
            {
                _currentEncoderConfig = configs[0]; // Main stream

                // Update UI
                SelectComboByTag(ResolutionCombo, $"{_currentEncoderConfig.Width}x{_currentEncoderConfig.Height}");
                SelectComboByTag(FrameRateCombo, _currentEncoderConfig.FrameRateLimit.ToString());
                SelectComboByTag(BitrateCombo, _currentEncoderConfig.BitrateLimit.ToString());
                SelectComboByTag(ProfileCombo, _currentEncoderConfig.H264Profile);

                MessageBox.Show($"Loaded encoder settings:\n" +
                    $"Resolution: {_currentEncoderConfig.Width}x{_currentEncoderConfig.Height}\n" +
                    $"Frame Rate: {_currentEncoderConfig.FrameRateLimit} fps\n" +
                    $"Bitrate: {_currentEncoderConfig.BitrateLimit} kbps\n" +
                    $"Profile: {_currentEncoderConfig.H264Profile}",
                    "Encoder Settings", MessageBoxButton.OK, MessageBoxImage.Information);
            }
        }
        catch (Exception ex)
        {
            MessageBox.Show($"Failed to load encoder settings: {ex.Message}",
                "Error", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private async void ApplyEncoderSettings_Click(object sender, RoutedEventArgs e)
    {
        if (_deviceManager == null || _currentEncoderConfig == null)
        {
            MessageBox.Show("Please load current settings first.",
                "Info", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        try
        {
            // Parse resolution
            var resTag = ((ComboBoxItem)ResolutionCombo.SelectedItem)?.Tag?.ToString() ?? "3840x2160";
            var resParts = resTag.Split('x');
            _currentEncoderConfig.Width = int.Parse(resParts[0]);
            _currentEncoderConfig.Height = int.Parse(resParts[1]);

            // Parse other settings
            _currentEncoderConfig.FrameRateLimit = int.Parse(((ComboBoxItem)FrameRateCombo.SelectedItem)?.Tag?.ToString() ?? "20");
            _currentEncoderConfig.BitrateLimit = int.Parse(((ComboBoxItem)BitrateCombo.SelectedItem)?.Tag?.ToString() ?? "0");
            _currentEncoderConfig.H264Profile = ((ComboBoxItem)ProfileCombo.SelectedItem)?.Tag?.ToString() ?? "High";

            var result = MessageBox.Show(
                $"Apply these encoder settings?\n\n" +
                $"Resolution: {_currentEncoderConfig.Width}x{_currentEncoderConfig.Height}\n" +
                $"Frame Rate: {_currentEncoderConfig.FrameRateLimit} fps\n" +
                $"Bitrate: {_currentEncoderConfig.BitrateLimit} kbps\n" +
                $"Profile: {_currentEncoderConfig.H264Profile}",
                "Confirm", MessageBoxButton.YesNo, MessageBoxImage.Question);

            if (result != MessageBoxResult.Yes) return;

            var success = await _deviceManager.SetVideoEncoderConfigAsync(_currentEncoderConfig);

            if (success)
            {
                MessageBox.Show("Encoder settings applied successfully!",
                    "Success", MessageBoxButton.OK, MessageBoxImage.Information);
            }
            else
            {
                MessageBox.Show("Failed to apply encoder settings.",
                    "Error", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }
        catch (Exception ex)
        {
            MessageBox.Show($"Failed to apply encoder settings: {ex.Message}",
                "Error", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private async void LoadOsdSettings_Click(object sender, RoutedEventArgs e)
    {
        if (_deviceManager == null) return;

        try
        {
            var osds = await _deviceManager.GetOSDsAsync();
            var titleOsd = osds.FirstOrDefault(o => o.Token == "osd_title");

            if (titleOsd != null)
            {
                OsdTitleText.Text = titleOsd.PlainText;
                SelectComboByTag(OsdPositionCombo, titleOsd.PositionType);

                MessageBox.Show($"Loaded OSD settings:\n" +
                    $"Title: {titleOsd.PlainText}\n" +
                    $"Position: {titleOsd.PositionType}",
                    "OSD Settings", MessageBoxButton.OK, MessageBoxImage.Information);
            }
            else
            {
                MessageBox.Show("No OSD configuration found.",
                    "Info", MessageBoxButton.OK, MessageBoxImage.Information);
            }
        }
        catch (Exception ex)
        {
            MessageBox.Show($"Failed to load OSD settings: {ex.Message}",
                "Error", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private async void ApplyOsdSettings_Click(object sender, RoutedEventArgs e)
    {
        if (_deviceManager == null) return;

        try
        {
            var title = OsdTitleText.Text.Trim();
            var position = ((ComboBoxItem)OsdPositionCombo.SelectedItem)?.Tag?.ToString() ?? "UpperLeft";

            if (string.IsNullOrEmpty(title))
            {
                MessageBox.Show("Please enter a camera title.",
                    "Validation", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            var result = MessageBox.Show(
                $"Apply these OSD settings?\n\n" +
                $"Title: {title}\n" +
                $"Position: {position}",
                "Confirm", MessageBoxButton.YesNo, MessageBoxImage.Question);

            if (result != MessageBoxResult.Yes) return;

            var success = await _deviceManager.SetOSDAsync("osd_title", title, position);

            if (success)
            {
                MessageBox.Show("OSD settings applied successfully!",
                    "Success", MessageBoxButton.OK, MessageBoxImage.Information);
            }
            else
            {
                MessageBox.Show("Failed to apply OSD settings.",
                    "Error", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }
        catch (Exception ex)
        {
            MessageBox.Show($"Failed to apply OSD settings: {ex.Message}",
                "Error", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private async void RefreshDeviceInfo_Click(object sender, RoutedEventArgs e)
    {
        await LoadDeviceInfoAsync();
    }

    private async Task LoadDeviceInfoAsync()
    {
        if (_deviceManager == null) return;

        try
        {
            var info = await _deviceManager.GetDeviceInformationAsync();
            var network = await _deviceManager.GetNetworkInfoAsync();

            DeviceInfoText.Text =
                $"Manufacturer: {info.Manufacturer}\n" +
                $"Model: {info.Model}\n" +
                $"Firmware: {info.FirmwareVersion}\n" +
                $"Serial: {info.SerialNumber}\n" +
                $"Hardware: {info.HardwareId}\n" +
                $"\nNetwork:\n" +
                $"IP: {network.IpAddress}\n" +
                $"MAC: {network.MacAddress}";
        }
        catch (Exception ex)
        {
            DeviceInfoText.Text = $"Error loading device info:\n{ex.Message}";
        }
    }

    private void SelectComboByTag(ComboBox combo, string tag)
    {
        foreach (ComboBoxItem item in combo.Items)
        {
            if (item.Tag?.ToString() == tag)
            {
                combo.SelectedItem = item;
                return;
            }
        }
    }

    protected override void OnClosed(EventArgs e)
    {
        Disconnect();
        base.OnClosed(e);
    }
}
