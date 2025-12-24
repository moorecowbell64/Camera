using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using OpenCvSharp;
using OpenCvSharp.WpfExtensions;

namespace JennovCamera.WPF;

public partial class MainWindow : System.Windows.Window
{
    private CameraClient? _client;
    private PTZController? _ptz;
    private RecordingManager? _recording;
    private DeviceManager? _deviceManager;
    private AudioManager? _audioManager;
    private bool _isConnected;
    private bool _isRecording;
    private bool _isAudioEnabled;
    private float _ptzSpeed = 1.0f;
    private VideoEncoderConfig? _currentEncoderConfig;

    // App settings (persisted)
    private AppSettings _settings = new();
    private bool _isInitializing = true;

    // PTZ movement tracking
    private bool _isPtzActive;

    // Click-to-center tracking
    private DateTime _lastClickTime = DateTime.MinValue;
    private bool _isClickZooming;
    private double _clickTravelMultiplier = 1700; // ms per unit distance

    // Calibration mode
    private bool _isCalibrating;
    private int _calibrationStep;
    private int _calibrationPhase; // 0=click target, 1=click result
    private List<CalibrationSample> _calibrationSamples = new();
    private CalibrationTarget? _currentTarget;

    // Comprehensive calibration targets - multiple distances and directions
    private readonly CalibrationTarget[] _calibrationTargets = GenerateCalibrationTargets();

    private static CalibrationTarget[] GenerateCalibrationTargets()
    {
        var targets = new List<CalibrationTarget>();

        // Test 3 distances: near (0.3), mid (0.55), far (0.8)
        var distances = new[] { 0.3, 0.55, 0.8 };
        var distanceNames = new[] { "Near", "Mid", "Far" };

        // 8 directions
        var directions = new (double x, double y, string name)[]
        {
            (1, 0, "Right"),
            (-1, 0, "Left"),
            (0, 1, "Up"),
            (0, -1, "Down"),
            (0.707, 0.707, "UpRight"),
            (-0.707, 0.707, "UpLeft"),
            (0.707, -0.707, "DownRight"),
            (-0.707, -0.707, "DownLeft"),
        };

        // Generate targets for each distance ring, but only select a subset for reasonable calibration time
        // Far ring: all 8 directions
        foreach (var dir in directions)
        {
            targets.Add(new CalibrationTarget(
                dir.x * 0.8, dir.y * 0.8,
                $"Far-{dir.name}", 0.8, dir.name));
        }

        // Mid ring: 4 cardinal directions
        foreach (var dir in directions.Take(4))
        {
            targets.Add(new CalibrationTarget(
                dir.x * 0.55, dir.y * 0.55,
                $"Mid-{dir.name}", 0.55, dir.name));
        }

        // Near ring: 4 cardinal directions
        foreach (var dir in directions.Take(4))
        {
            targets.Add(new CalibrationTarget(
                dir.x * 0.3, dir.y * 0.3,
                $"Near-{dir.name}", 0.3, dir.name));
        }

        return targets.ToArray();
    }

    private class CalibrationTarget
    {
        public double X { get; }
        public double Y { get; }
        public string Name { get; }
        public double Distance { get; }
        public string Direction { get; }

        public CalibrationTarget(double x, double y, string name, double distance, string direction)
        {
            X = x; Y = y; Name = name; Distance = distance; Direction = direction;
        }
    }

    private class CalibrationSample
    {
        public CalibrationTarget Target { get; set; } = null!;
        public double ErrorX { get; set; }
        public double ErrorY { get; set; }
        public double ErrorDistance { get; set; }
        public double SuggestedMultiplier { get; set; }
        public bool IsUndershoot { get; set; }
    }

    public MainWindow()
    {
        InitializeComponent();

        // Load saved settings
        _settings = AppSettings.Load();

        // Apply saved settings to UI
        CameraIpText.Text = _settings.CameraIp;
        UsernameText.Text = _settings.Username;
        // Password is not saved for security - user must enter each session
        RecordingFolderText.Text = _settings.RecordingFolder;
        _clickTravelMultiplier = _settings.ClickTravelMultiplier;
        TravelSlider.Value = _clickTravelMultiplier;
        TravelValueLabel.Text = $"{_clickTravelMultiplier:F0}";

        // Set default selections
        StreamQualityCombo.SelectedIndex = 0; // Main 4K
        ResolutionCombo.SelectedIndex = 0; // 4K
        FrameRateCombo.SelectedIndex = 2; // 20 fps
        BitrateCombo.SelectedIndex = 0; // Auto
        ProfileCombo.SelectedIndex = 0; // High
        OsdPositionCombo.SelectedIndex = 0; // Upper Left

        // Initialize FFmpeg settings
        InitializeFFmpegSettings();

        // Done initializing
        _isInitializing = false;

        // Auto-connect when window loads
        Loaded += MainWindow_Loaded;
        Closing += MainWindow_Closing;
    }

    private void MainWindow_Closing(object? sender, System.ComponentModel.CancelEventArgs e)
    {
        // Confirm if recording is active
        if (_isRecording)
        {
            var result = MessageBox.Show(
                "Recording is currently active. Closing will stop the recording.\n\nDo you want to close?",
                "Confirm Close",
                MessageBoxButton.YesNo,
                MessageBoxImage.Warning);

            if (result != MessageBoxResult.Yes)
            {
                e.Cancel = true;
                return;
            }
        }

        // Save settings on close
        _settings.Save();
    }

    private async void MainWindow_Loaded(object sender, RoutedEventArgs e)
    {
        // Auto-connect to camera at startup only if credentials are provided
        if (!string.IsNullOrEmpty(PasswordText.Password))
        {
            await ConnectAsync();
        }
    }

    private async void ConnectButton_Click(object sender, RoutedEventArgs e)
    {
        if (!_isConnected)
        {
            await ConnectAsync();
        }
        else
        {
            // Confirm disconnect if recording is active
            if (_isRecording)
            {
                var result = MessageBox.Show(
                    "Recording is currently active. Disconnecting will stop the recording.\n\nDo you want to disconnect?",
                    "Confirm Disconnect",
                    MessageBoxButton.YesNo,
                    MessageBoxImage.Warning);

                if (result != MessageBoxResult.Yes)
                    return;
            }

            Disconnect();
        }
    }

    private async void PasswordText_KeyDown(object sender, KeyEventArgs e)
    {
        // Connect on Enter key press in password field
        if (e.Key == Key.Enter && !_isConnected)
        {
            e.Handled = true;
            await ConnectAsync();
        }
    }

    #region Keyboard Shortcuts

    private HashSet<Key> _pressedKeys = new HashSet<Key>();

    private void Window_KeyDown(object sender, KeyEventArgs e)
    {
        // Ignore if focused on a text input
        if (e.OriginalSource is TextBox || e.OriginalSource is PasswordBox)
            return;

        // Ignore if not connected
        if (!_isConnected || _ptz == null)
            return;

        // Prevent auto-repeat from causing multiple commands
        if (_pressedKeys.Contains(e.Key))
            return;

        _pressedKeys.Add(e.Key);
        e.Handled = true;

        var speed = (float)SpeedSlider.Value;

        switch (e.Key)
        {
            // Arrow keys for PTZ
            case Key.Up:
                _ptz.ContinuousMoveFast(0, speed, 0);
                break;
            case Key.Down:
                _ptz.ContinuousMoveFast(0, -speed, 0);
                break;
            case Key.Left:
                _ptz.ContinuousMoveFast(-speed, 0, 0);
                break;
            case Key.Right:
                _ptz.ContinuousMoveFast(speed, 0, 0);
                break;

            // Zoom with + and -
            case Key.Add:
            case Key.OemPlus:
                _ptz.ContinuousMoveFast(0, 0, speed);
                break;
            case Key.Subtract:
            case Key.OemMinus:
                _ptz.ContinuousMoveFast(0, 0, -speed);
                break;

            // Focus with W (near) and S (far)
            case Key.W:
                _ptz.FocusNearFast(speed);
                break;
            case Key.S:
                _ptz.FocusFarFast(speed);
                break;

            // Auto focus with F
            case Key.F:
                _ptz.AutoFocusFast();
                break;

            // Stop all movement with Space
            case Key.Space:
                _ptz.StopFast();
                _ptz.FocusStopFast();
                break;

            // Number keys for quick presets (1-9)
            case Key.D1:
            case Key.NumPad1:
                _ = GoToPresetAsync("1");
                break;
            case Key.D2:
            case Key.NumPad2:
                _ = GoToPresetAsync("2");
                break;
            case Key.D3:
            case Key.NumPad3:
                _ = GoToPresetAsync("3");
                break;
            case Key.D4:
            case Key.NumPad4:
                _ = GoToPresetAsync("4");
                break;
            case Key.D5:
            case Key.NumPad5:
                _ = GoToPresetAsync("5");
                break;
            case Key.D6:
            case Key.NumPad6:
                _ = GoToPresetAsync("6");
                break;
            case Key.D7:
            case Key.NumPad7:
                _ = GoToPresetAsync("7");
                break;
            case Key.D8:
            case Key.NumPad8:
                _ = GoToPresetAsync("8");
                break;
            case Key.D9:
            case Key.NumPad9:
                _ = GoToPresetAsync("9");
                break;

            default:
                e.Handled = false;
                break;
        }
    }

    private void Window_KeyUp(object sender, KeyEventArgs e)
    {
        if (!_pressedKeys.Contains(e.Key))
            return;

        _pressedKeys.Remove(e.Key);

        // Ignore if not connected
        if (!_isConnected || _ptz == null)
            return;

        // Stop movement when key is released
        switch (e.Key)
        {
            case Key.Up:
            case Key.Down:
            case Key.Left:
            case Key.Right:
            case Key.Add:
            case Key.OemPlus:
            case Key.Subtract:
            case Key.OemMinus:
                _ptz.StopFast();
                e.Handled = true;
                break;

            case Key.W:
            case Key.S:
                _ptz.FocusStopFast();
                e.Handled = true;
                break;
        }
    }

    #endregion

    private async Task ConnectAsync()
    {
        try
        {
            var ip = CameraIpText.Text.Trim();
            var username = UsernameText.Text.Trim();
            var password = PasswordText.Password;

            // Validate inputs
            if (string.IsNullOrEmpty(ip))
            {
                MessageBox.Show("Please enter a camera IP address.",
                    "Validation Error", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            if (!System.Net.IPAddress.TryParse(ip, out _))
            {
                MessageBox.Show("Please enter a valid IP address (e.g., 192.168.1.100).",
                    "Validation Error", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            if (string.IsNullOrEmpty(username))
            {
                MessageBox.Show("Please enter a username.",
                    "Validation Error", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            if (string.IsNullOrEmpty(password))
            {
                MessageBox.Show("Please enter a password.",
                    "Validation Error", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            UpdateConnectionStatus("Connecting...", Brushes.Orange);
            ConnectButton.IsEnabled = false;

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
            _recording.SegmentCreated += OnSegmentCreated;
            _recording.StatusChanged += OnRecordingStatusChanged;

            // Apply saved recording folder from settings
            _recording.RecordingFolder = _settings.RecordingFolder;
            RecordingFolderText.Text = _settings.RecordingFolder;

            _deviceManager = new DeviceManager(ip, username, password, 80);

            // Warm up ONVIF connection for faster PTZ response
            UpdateConnectionStatus("Warming up PTZ...", Brushes.Orange);
            await _client.Onvif.WarmUpConnectionAsync();

            // Auto-detect best RTSP URL for highest resolution
            UpdateConnectionStatus("Detecting streams...", Brushes.Orange);
            _recording.AutoDetectBestRtspUrl();

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

            // Save IP and username on successful connection (password not saved for security)
            _settings.CameraIp = ip;
            _settings.Username = username;
            _settings.Save();

            // Enable controls
            PTZControls.IsEnabled = true;
            PresetsGroup.IsEnabled = true;
            RecordingGroup.IsEnabled = true;
            AudioGroup.IsEnabled = true;
            QuickQualityToggle.IsEnabled = true;

            // Enable settings controls
            StreamSettingsGroup.IsEnabled = true;
            EncoderSettingsGroup.IsEnabled = true;
            OSDSettingsGroup.IsEnabled = true;
            DeviceInfoGroup.IsEnabled = true;

            // Initialize audio manager
            _audioManager = new AudioManager();
            _audioManager.SetRtspUrl(ip, username, password);
            _audioManager.StatusChanged += (s, status) =>
            {
                Dispatcher.Invoke(() => AudioStatusText.Text = status);
            };

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
            // Stop audio
            _audioManager?.Stop();
            _audioManager?.Dispose();
            _audioManager = null;
            _isAudioEnabled = false;

            // Stop recording timer
            StopRecordingTimer();

            if (_recording != null)
            {
                _recording.FrameCaptured -= OnFrameCaptured;
                _recording.SegmentCreated -= OnSegmentCreated;
                _recording.StatusChanged -= OnRecordingStatusChanged;
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
            FrameResolutionStatus.Text = "Resolution: --";
            _lastFrameWidth = 0;
            _lastFrameHeight = 0;
            ConnectButton.Content = "Connect Camera";

            // Disable controls
            PTZControls.IsEnabled = false;
            PresetsGroup.IsEnabled = false;
            RecordingGroup.IsEnabled = false;
            RecordButton.Content = "Start Recording";
            RecordButton.Background = (Brush)Application.Current.Resources["PrimaryBrush"];
            RecordingSegmentInfo.Text = "30min segments, 10s overlap";
            AudioGroup.IsEnabled = false;
            QuickQualityToggle.IsEnabled = false;
            AudioButton.Content = "Enable Audio";
            AudioStatusText.Text = "Audio: Disabled";

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

    private int _lastFrameWidth = 0;
    private int _lastFrameHeight = 0;

    private void OnFrameCaptured(object? sender, Mat frame)
    {
        try
        {
            var width = frame.Width;
            var height = frame.Height;

            // Convert Mat to BitmapSource on UI thread
            Dispatcher.Invoke(() =>
            {
                try
                {
                    var bitmap = BitmapSourceConverter.ToBitmapSource(frame);
                    bitmap.Freeze(); // Allow cross-thread access
                    VideoImage.Source = bitmap;

                    // Update resolution display if changed
                    if (width != _lastFrameWidth || height != _lastFrameHeight)
                    {
                        _lastFrameWidth = width;
                        _lastFrameHeight = height;
                        FrameResolutionStatus.Text = $"Resolution: {width}x{height}";
                    }
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

    // PTZ Button Handlers - Single command on press, stop on release
    // ONVIF ContinuousMove keeps moving until Stop is called
    // Using synchronous fire-and-forget for maximum responsiveness

    private void UpButton_MouseDown(object sender, MouseButtonEventArgs e)
    {
        if (_client == null || _isPtzActive) return;
        _isPtzActive = true;
        ((UIElement)sender).CaptureMouse();
        _client.Onvif.ContinuousMoveFast(0, _ptzSpeed, 0);
    }

    private void DownButton_MouseDown(object sender, MouseButtonEventArgs e)
    {
        if (_client == null || _isPtzActive) return;
        _isPtzActive = true;
        ((UIElement)sender).CaptureMouse();
        _client.Onvif.ContinuousMoveFast(0, -_ptzSpeed, 0);
    }

    private void LeftButton_MouseDown(object sender, MouseButtonEventArgs e)
    {
        if (_client == null || _isPtzActive) return;
        _isPtzActive = true;
        ((UIElement)sender).CaptureMouse();
        _client.Onvif.ContinuousMoveFast(-_ptzSpeed, 0, 0);
    }

    private void RightButton_MouseDown(object sender, MouseButtonEventArgs e)
    {
        if (_client == null || _isPtzActive) return;
        _isPtzActive = true;
        ((UIElement)sender).CaptureMouse();
        _client.Onvif.ContinuousMoveFast(_ptzSpeed, 0, 0);
    }

    private void ZoomInButton_MouseDown(object sender, MouseButtonEventArgs e)
    {
        if (_client == null || _isPtzActive) return;
        _isPtzActive = true;
        ((UIElement)sender).CaptureMouse();
        _client.Onvif.ContinuousMoveFast(0, 0, 0.5f);
    }

    private void ZoomOutButton_MouseDown(object sender, MouseButtonEventArgs e)
    {
        if (_client == null || _isPtzActive) return;
        _isPtzActive = true;
        ((UIElement)sender).CaptureMouse();
        _client.Onvif.ContinuousMoveFast(0, 0, -0.5f);
    }

    private void PTZButton_MouseUp(object sender, MouseButtonEventArgs e)
    {
        if (!_isPtzActive) return;
        _isPtzActive = false;
        ((UIElement)sender).ReleaseMouseCapture();
        _client?.Onvif.StopFast();
    }

    private void StopButton_Click(object sender, RoutedEventArgs e)
    {
        _isPtzActive = false;
        _isClickZooming = false;
        _isFocusActive = false;
        _client?.Onvif.StopFast();
        _client?.Onvif.FocusStopFast();
    }

    // ===== Focus Controls =====
    private bool _isFocusActive;

    private void FocusNearButton_MouseDown(object sender, MouseButtonEventArgs e)
    {
        if (_client == null || _isFocusActive) return;
        _isFocusActive = true;
        ((UIElement)sender).CaptureMouse();
        _client.Onvif.FocusNearFast();
    }

    private void FocusFarButton_MouseDown(object sender, MouseButtonEventArgs e)
    {
        if (_client == null || _isFocusActive) return;
        _isFocusActive = true;
        ((UIElement)sender).CaptureMouse();
        _client.Onvif.FocusFarFast();
    }

    private void FocusButton_MouseUp(object sender, MouseButtonEventArgs e)
    {
        if (!_isFocusActive) return;
        _isFocusActive = false;
        ((UIElement)sender).ReleaseMouseCapture();
        _client?.Onvif.FocusStopFast();
    }

    private void AutoFocusButton_Click(object sender, RoutedEventArgs e)
    {
        _client?.Onvif.AutoFocusFast();
    }

    // Click-to-center: Click on video to pan/tilt camera to center on that point
    // Double-click and hold: Move to point and zoom in while held
    private void VideoImage_MouseDown(object sender, MouseButtonEventArgs e)
    {
        if (_client == null || !_isConnected) return;
        if (e.LeftButton != MouseButtonState.Pressed) return;

        var image = (Image)sender;
        var clickPoint = e.GetPosition(image);

        // Calculate normalized offset from center (-1 to 1)
        var centerX = image.ActualWidth / 2;
        var centerY = image.ActualHeight / 2;

        // Normalize: -1 (left/top) to +1 (right/bottom)
        var normalizedX = (clickPoint.X - centerX) / centerX;
        var normalizedY = (centerY - clickPoint.Y) / centerY; // Invert Y (up is positive)

        // Clamp to valid range
        normalizedX = Math.Clamp(normalizedX, -1.0, 1.0);
        normalizedY = Math.Clamp(normalizedY, -1.0, 1.0);

        // Check for double-click (within 300ms)
        var now = DateTime.Now;
        var isDoubleClick = (now - _lastClickTime).TotalMilliseconds < 300;
        _lastClickTime = now;

        if (isDoubleClick)
        {
            // Double-click and hold: move to point AND zoom in
            _isClickZooming = true;
            image.CaptureMouse();

            // Move toward clicked point at max speed while zooming in
            var panDirection = normalizedX > 0 ? 1.0f : (normalizedX < 0 ? -1.0f : 0f);
            var tiltDirection = normalizedY > 0 ? 1.0f : (normalizedY < 0 ? -1.0f : 0f);
            _client.Onvif.ContinuousMoveFast(panDirection, tiltDirection, 0.5f);
        }
        else
        {
            // Single click: move at max speed for duration proportional to distance from center
            // Then stop - this should approximately center the clicked point
            _ = MoveToClickedPointAsync(normalizedX, normalizedY);
        }
    }

    private async Task MoveToClickedPointAsync(double normalizedX, double normalizedY)
    {
        if (_client == null) return;

        // Calculate distance from center (0 to 1.414 for corners)
        var distance = Math.Sqrt(normalizedX * normalizedX + normalizedY * normalizedY);

        // Skip if click is very close to center
        if (distance < 0.05) return;

        // Calculate total movement duration based on distance
        var totalDurationMs = (int)(distance * _clickTravelMultiplier);

        // Calculate base direction
        var basePanDir = normalizedX > 0 ? 1.0f : -1.0f;
        var baseTiltDir = normalizedY > 0 ? 1.0f : -1.0f;

        // Scale direction by component magnitude (so diagonal moves are balanced)
        var absX = Math.Abs(normalizedX);
        var absY = Math.Abs(normalizedY);
        if (absX > 0.01) basePanDir *= (float)(absX / Math.Max(absX, absY));
        if (absY > 0.01) baseTiltDir *= (float)(absY / Math.Max(absX, absY));

        // If one axis is very small, zero it out
        if (Math.Abs(normalizedX) < 0.05) basePanDir = 0;
        if (Math.Abs(normalizedY) < 0.05) baseTiltDir = 0;

        // Deceleration curve: fast start, slow finish for accuracy
        // Phase 1: 50% of duration at 100% speed
        // Phase 2: 30% of duration at 50% speed
        // Phase 3: 20% of duration at 20% speed
        var phases = new (float speedMultiplier, double durationPercent)[]
        {
            (1.0f, 0.50),   // Full speed for first 50%
            (0.5f, 0.30),   // Half speed for next 30%
            (0.2f, 0.20),   // Slow creep for final 20%
        };

        foreach (var (speedMult, durationPct) in phases)
        {
            var phaseDuration = (int)(totalDurationMs * durationPct);
            if (phaseDuration < 20) continue; // Skip very short phases

            var panSpeed = basePanDir * speedMult;
            var tiltSpeed = baseTiltDir * speedMult;

            _client.Onvif.ContinuousMoveFast(panSpeed, tiltSpeed, 0);
            await Task.Delay(phaseDuration);
        }

        // Stop movement
        _client.Onvif.StopFast();
    }

    private void VideoImage_MouseUp(object sender, MouseButtonEventArgs e)
    {
        if (_isClickZooming)
        {
            _isClickZooming = false;
            ((Image)sender).ReleaseMouseCapture();
            _client?.Onvif.StopFast();
        }
    }

    private void VideoImage_MouseLeave(object sender, MouseEventArgs e)
    {
        // Stop zoom if mouse leaves while double-click zooming
        if (_isClickZooming)
        {
            _isClickZooming = false;
            _client?.Onvif.StopFast();
        }
    }

    private void TravelSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        _clickTravelMultiplier = e.NewValue;
        if (TravelValueLabel != null)
            TravelValueLabel.Text = $"{_clickTravelMultiplier:F0}";

        // Save to settings (skip during initialization)
        if (!_isInitializing)
        {
            _settings.ClickTravelMultiplier = _clickTravelMultiplier;
            _settings.Save();
        }
    }

    // ===== Calibration System =====

    private void StartCalibration_Click(object sender, RoutedEventArgs e)
    {
        if (!_isConnected || _client == null)
        {
            MessageBox.Show("Please connect to camera first.", "Calibration", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        _isCalibrating = true;
        _calibrationStep = 0;
        _calibrationPhase = 0;
        _calibrationSamples.Clear();
        _currentTarget = null;

        // Show calibration overlay
        CalibrationOverlay.Visibility = Visibility.Visible;
        CalibrationInstructions.Text =
            "CALIBRATION\n\n" +
            "1. Click YELLOW target\n" +
            "2. Camera moves\n" +
            "3. Click where that spot\n" +
            "   ended up in the video\n" +
            "   (CENTER = perfect)\n\n" +
            "ESC=cancel  SPACE=skip";

        // Wait for layout to complete before drawing targets
        Dispatcher.BeginInvoke(DispatcherPriority.Loaded, new Action(() =>
        {
            UpdateCalibrationDisplay();
        }));
    }

    private void UpdateCalibrationDisplay()
    {
        CalibrationCanvas.Children.Clear();

        if (_calibrationStep >= _calibrationTargets.Length)
        {
            FinishCalibration();
            return;
        }

        _currentTarget = _calibrationTargets[_calibrationStep];
        var canvas = CalibrationCanvas;
        var centerX = canvas.ActualWidth / 2;
        var centerY = canvas.ActualHeight / 2;

        // Safety check - if canvas not yet laid out, defer
        if (centerX < 10 || centerY < 10)
        {
            Dispatcher.BeginInvoke(DispatcherPriority.Loaded, new Action(() =>
            {
                UpdateCalibrationDisplay();
            }));
            return;
        }

        // Draw distance rings for reference
        DrawDistanceRings(canvas, centerX, centerY);

        // Draw all targets
        for (int i = 0; i < _calibrationTargets.Length; i++)
        {
            var t = _calibrationTargets[i];
            var isActive = i == _calibrationStep;
            var isCompleted = i < _calibrationStep;
            DrawCalibrationTarget(t.X, t.Y, t.Name, isActive, isCompleted, t.Distance);
        }

        // Draw center crosshair
        DrawCenterCrosshair();

        // Draw progress bar
        DrawProgressBar(canvas);

        // Update status text
        var distanceLabel = _currentTarget.Distance switch
        {
            0.3 => "NEAR",
            0.55 => "MID",
            0.8 => "FAR",
            _ => ""
        };

        if (_calibrationPhase == 0)
        {
            CalibrationStepText.Text = $"[{_calibrationStep + 1}/{_calibrationTargets.Length}] Click the YELLOW {distanceLabel} {_currentTarget.Direction} target";
        }
        else
        {
            CalibrationStepText.Text = $"Where did that spot end up in the video?\n(Click CENTER crosshair if it's perfectly centered)";
        }
    }

    private void DrawDistanceRings(Canvas canvas, double centerX, double centerY)
    {
        var distances = new[] { 0.3, 0.55, 0.8 };
        var colors = new[] { Brushes.DarkGreen, Brushes.DarkOrange, Brushes.DarkRed };
        var labels = new[] { "Near", "Mid", "Far" };

        for (int i = 0; i < distances.Length; i++)
        {
            var radius = distances[i] * Math.Min(centerX, centerY);
            var ring = new System.Windows.Shapes.Ellipse
            {
                Width = radius * 2,
                Height = radius * 2,
                Stroke = colors[i],
                StrokeThickness = 1,
                StrokeDashArray = new DoubleCollection { 4, 4 },
                Fill = Brushes.Transparent
            };
            Canvas.SetLeft(ring, centerX - radius);
            Canvas.SetTop(ring, centerY - radius);
            canvas.Children.Add(ring);

            // Ring label
            var label = new TextBlock
            {
                Text = labels[i],
                Foreground = colors[i],
                FontSize = 9
            };
            Canvas.SetLeft(label, centerX + radius + 5);
            Canvas.SetTop(label, centerY - 6);
            canvas.Children.Add(label);
        }
    }

    private void DrawProgressBar(Canvas canvas)
    {
        var width = canvas.ActualWidth - 40;
        var progress = (double)_calibrationStep / _calibrationTargets.Length;

        // Background
        var bg = new System.Windows.Shapes.Rectangle
        {
            Width = width,
            Height = 8,
            Fill = new SolidColorBrush(System.Windows.Media.Color.FromRgb(60, 60, 60)),
            RadiusX = 4,
            RadiusY = 4
        };
        Canvas.SetLeft(bg, 20);
        Canvas.SetTop(bg, 10);
        canvas.Children.Add(bg);

        // Progress
        var fg = new System.Windows.Shapes.Rectangle
        {
            Width = Math.Max(0, width * progress),
            Height = 8,
            Fill = Brushes.LimeGreen,
            RadiusX = 4,
            RadiusY = 4
        };
        Canvas.SetLeft(fg, 20);
        Canvas.SetTop(fg, 10);
        canvas.Children.Add(fg);

        // Percentage text
        var pct = new TextBlock
        {
            Text = $"{progress * 100:F0}%",
            Foreground = Brushes.White,
            FontSize = 10
        };
        Canvas.SetLeft(pct, 20 + width + 10);
        Canvas.SetTop(pct, 6);
        canvas.Children.Add(pct);
    }

    private void DrawCalibrationTarget(double normX, double normY, string name, bool isActive, bool isCompleted, double distance)
    {
        var canvas = CalibrationCanvas;
        var centerX = canvas.ActualWidth / 2;
        var centerY = canvas.ActualHeight / 2;

        var x = centerX + (normX * centerX);
        var y = centerY - (normY * centerY);

        var size = isActive ? 35.0 : (isCompleted ? 20.0 : 25.0);

        // Color based on distance ring and state
        Brush color;
        if (isCompleted)
            color = Brushes.Green;
        else if (isActive)
            color = Brushes.Yellow;
        else
        {
            color = distance switch
            {
                0.3 => new SolidColorBrush(System.Windows.Media.Color.FromRgb(100, 100, 100)),
                0.55 => new SolidColorBrush(System.Windows.Media.Color.FromRgb(80, 80, 80)),
                _ => new SolidColorBrush(System.Windows.Media.Color.FromRgb(60, 60, 60))
            };
        }

        // Target circle
        var circle = new System.Windows.Shapes.Ellipse
        {
            Width = size,
            Height = size,
            Stroke = color,
            StrokeThickness = isActive ? 3 : 2,
            Fill = isActive ? new SolidColorBrush(System.Windows.Media.Color.FromArgb(60, 255, 255, 0)) : Brushes.Transparent
        };
        Canvas.SetLeft(circle, x - size / 2);
        Canvas.SetTop(circle, y - size / 2);
        canvas.Children.Add(circle);

        // Crosshair for active target
        if (isActive)
        {
            var h = new System.Windows.Shapes.Line { X1 = x - size / 2 + 5, Y1 = y, X2 = x + size / 2 - 5, Y2 = y, Stroke = Brushes.Yellow, StrokeThickness = 1 };
            var v = new System.Windows.Shapes.Line { X1 = x, Y1 = y - size / 2 + 5, X2 = x, Y2 = y + size / 2 - 5, Stroke = Brushes.Yellow, StrokeThickness = 1 };
            canvas.Children.Add(h);
            canvas.Children.Add(v);
        }

        // Center dot
        var dot = new System.Windows.Shapes.Ellipse
        {
            Width = isActive ? 6 : 4,
            Height = isActive ? 6 : 4,
            Fill = color
        };
        Canvas.SetLeft(dot, x - (isActive ? 3 : 2));
        Canvas.SetTop(dot, y - (isActive ? 3 : 2));
        canvas.Children.Add(dot);

        // Checkmark for completed
        if (isCompleted)
        {
            var check = new TextBlock
            {
                Text = "✓",
                Foreground = Brushes.Green,
                FontSize = 12,
                FontWeight = FontWeights.Bold
            };
            Canvas.SetLeft(check, x - 5);
            Canvas.SetTop(check, y - 8);
            canvas.Children.Add(check);
        }
    }

    private void DrawCenterCrosshair()
    {
        var canvas = CalibrationCanvas;
        var centerX = canvas.ActualWidth / 2;
        var centerY = canvas.ActualHeight / 2;

        // Large crosshair
        var hLine = new System.Windows.Shapes.Line
        {
            X1 = centerX - 40, Y1 = centerY,
            X2 = centerX + 40, Y2 = centerY,
            Stroke = Brushes.Cyan,
            StrokeThickness = 2
        };
        canvas.Children.Add(hLine);

        var vLine = new System.Windows.Shapes.Line
        {
            X1 = centerX, Y1 = centerY - 40,
            X2 = centerX, Y2 = centerY + 40,
            Stroke = Brushes.Cyan,
            StrokeThickness = 2
        };
        canvas.Children.Add(vLine);

        // Center circle
        var centerCircle = new System.Windows.Shapes.Ellipse
        {
            Width = 20,
            Height = 20,
            Stroke = Brushes.Cyan,
            StrokeThickness = 2,
            Fill = new SolidColorBrush(System.Windows.Media.Color.FromArgb(40, 0, 255, 255))
        };
        Canvas.SetLeft(centerCircle, centerX - 10);
        Canvas.SetTop(centerCircle, centerY - 10);
        canvas.Children.Add(centerCircle);

        var label = new TextBlock
        {
            Text = "CENTER\n(click if perfect)",
            Foreground = Brushes.Cyan,
            FontSize = 9,
            TextAlignment = TextAlignment.Center
        };
        Canvas.SetLeft(label, centerX - 35);
        Canvas.SetTop(label, centerY + 42);
        canvas.Children.Add(label);
    }

    private async void CalibrationCanvas_MouseDown(object sender, MouseButtonEventArgs e)
    {
        if (!_isCalibrating || _client == null || _currentTarget == null) return;
        if (e.LeftButton != MouseButtonState.Pressed) return;

        var canvas = (Canvas)sender;
        var clickPoint = e.GetPosition(canvas);
        var centerX = canvas.ActualWidth / 2;
        var centerY = canvas.ActualHeight / 2;

        if (_calibrationPhase == 0)
        {
            // Phase 0: User should click the target to start movement
            var targetCanvasX = centerX + (_currentTarget.X * centerX);
            var targetCanvasY = centerY - (_currentTarget.Y * centerY);
            var distToTarget = Math.Sqrt(Math.Pow(clickPoint.X - targetCanvasX, 2) + Math.Pow(clickPoint.Y - targetCanvasY, 2));

            if (distToTarget < 50)
            {
                CalibrationStepText.Text = $"Moving camera to {_currentTarget.Name}...";

                // Perform movement
                await MoveToClickedPointAsync(_currentTarget.X, _currentTarget.Y);
                await Task.Delay(300);

                _calibrationPhase = 1;
                UpdateCalibrationDisplay();
            }
        }
        else
        {
            // Phase 1: User indicates where target ended up
            var clickNormX = (clickPoint.X - centerX) / centerX;
            var clickNormY = (centerY - clickPoint.Y) / centerY;

            var errorDistance = Math.Sqrt(clickNormX * clickNormX + clickNormY * clickNormY);
            var targetDistance = Math.Sqrt(_currentTarget.X * _currentTarget.X + _currentTarget.Y * _currentTarget.Y);

            // Determine if undershoot or overshoot
            var sameDirection = (clickNormX * _currentTarget.X >= 0) || (clickNormY * _currentTarget.Y >= 0);
            var isUndershoot = sameDirection && errorDistance > 0.02;

            double suggestedMultiplier;
            if (errorDistance < 0.05)
            {
                suggestedMultiplier = _clickTravelMultiplier;
            }
            else if (isUndershoot)
            {
                var traveledDistance = targetDistance - errorDistance;
                if (traveledDistance > 0.01)
                    suggestedMultiplier = _clickTravelMultiplier * (targetDistance / traveledDistance);
                else
                    suggestedMultiplier = _clickTravelMultiplier * 2;
            }
            else
            {
                var traveledDistance = targetDistance + errorDistance;
                suggestedMultiplier = _clickTravelMultiplier * (targetDistance / traveledDistance);
            }

            // Clamp to reasonable range
            suggestedMultiplier = Math.Clamp(suggestedMultiplier, 500, 5000);

            _calibrationSamples.Add(new CalibrationSample
            {
                Target = _currentTarget,
                ErrorX = clickNormX,
                ErrorY = clickNormY,
                ErrorDistance = errorDistance,
                SuggestedMultiplier = suggestedMultiplier,
                IsUndershoot = isUndershoot
            });

            // Next target
            _calibrationStep++;
            _calibrationPhase = 0;
            UpdateCalibrationDisplay();
        }
    }

    private void FinishCalibration()
    {
        _isCalibrating = false;
        CalibrationOverlay.Visibility = Visibility.Collapsed;

        if (_calibrationSamples.Count == 0)
        {
            MessageBox.Show("No calibration data collected.", "Calibration", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        // Comprehensive analysis
        var avgMultiplier = _calibrationSamples.Average(s => s.SuggestedMultiplier);
        var stdDev = Math.Sqrt(_calibrationSamples.Average(s => Math.Pow(s.SuggestedMultiplier - avgMultiplier, 2)));

        // Analyze by distance
        var nearSamples = _calibrationSamples.Where(s => s.Target.Distance < 0.4).ToList();
        var midSamples = _calibrationSamples.Where(s => s.Target.Distance >= 0.4 && s.Target.Distance < 0.7).ToList();
        var farSamples = _calibrationSamples.Where(s => s.Target.Distance >= 0.7).ToList();

        var nearAvg = nearSamples.Any() ? nearSamples.Average(s => s.SuggestedMultiplier) : avgMultiplier;
        var midAvg = midSamples.Any() ? midSamples.Average(s => s.SuggestedMultiplier) : avgMultiplier;
        var farAvg = farSamples.Any() ? farSamples.Average(s => s.SuggestedMultiplier) : avgMultiplier;

        // Analyze by axis
        var panSamples = _calibrationSamples.Where(s => Math.Abs(s.Target.X) > Math.Abs(s.Target.Y)).ToList();
        var tiltSamples = _calibrationSamples.Where(s => Math.Abs(s.Target.Y) > Math.Abs(s.Target.X)).ToList();

        var panAvg = panSamples.Any() ? panSamples.Average(s => s.SuggestedMultiplier) : avgMultiplier;
        var tiltAvg = tiltSamples.Any() ? tiltSamples.Average(s => s.SuggestedMultiplier) : avgMultiplier;

        // Count accuracy
        var perfectCount = _calibrationSamples.Count(s => s.ErrorDistance < 0.05);
        var undershootCount = _calibrationSamples.Count(s => s.IsUndershoot);
        var overshootCount = _calibrationSamples.Count - perfectCount - undershootCount;

        var avgError = _calibrationSamples.Average(s => s.ErrorDistance);

        var report = $"CALIBRATION ANALYSIS\n" +
            $"═══════════════════════════════\n\n" +
            $"Samples: {_calibrationSamples.Count}\n" +
            $"Perfect (< 5% error): {perfectCount}\n" +
            $"Undershoot: {undershootCount}  |  Overshoot: {overshootCount}\n" +
            $"Average error: {avgError * 100:F1}%\n\n" +
            $"BY DISTANCE:\n" +
            $"  Near (0.3):  {nearAvg:F0}\n" +
            $"  Mid (0.55):  {midAvg:F0}\n" +
            $"  Far (0.8):   {farAvg:F0}\n\n" +
            $"BY AXIS:\n" +
            $"  Pan (H):  {panAvg:F0}\n" +
            $"  Tilt (V): {tiltAvg:F0}\n\n" +
            $"RECOMMENDED:\n" +
            $"  Overall: {avgMultiplier:F0} (±{stdDev:F0})\n" +
            $"  Current: {_clickTravelMultiplier:F0}\n\n" +
            $"Apply recommended value?";

        var result = MessageBox.Show(report, "Calibration Results", MessageBoxButton.YesNo, MessageBoxImage.Information);

        if (result == MessageBoxResult.Yes)
        {
            _clickTravelMultiplier = avgMultiplier;
            TravelSlider.Value = Math.Clamp(avgMultiplier, TravelSlider.Minimum, TravelSlider.Maximum);
            TravelValueLabel.Text = $"{avgMultiplier:F0}";

            // Expand slider range if needed
            if (avgMultiplier > TravelSlider.Maximum)
            {
                TravelSlider.Maximum = avgMultiplier + 500;
                TravelSlider.Value = avgMultiplier;
            }
            else if (avgMultiplier < TravelSlider.Minimum)
            {
                TravelSlider.Minimum = avgMultiplier - 200;
                TravelSlider.Value = avgMultiplier;
            }
        }
    }

    private void CancelCalibration()
    {
        _isCalibrating = false;
        CalibrationOverlay.Visibility = Visibility.Collapsed;
        CalibrationCanvas.Children.Clear();
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e);

        if (_isCalibrating)
        {
            if (e.Key == Key.Escape)
            {
                CancelCalibration();
                e.Handled = true;
            }
            else if (e.Key == Key.Space)
            {
                // Skip current target
                _calibrationStep++;
                _calibrationPhase = 0;
                UpdateCalibrationDisplay();
                e.Handled = true;
            }
        }
    }

    private async void GotoPresetButton_Click(object sender, RoutedEventArgs e)
    {
        if (_client == null) return;

        // Presets 5-8 in dropdown (index 0 = preset 5)
        var preset = (PresetCombo.SelectedIndex + 5).ToString();
        await GoToPresetAsync(preset);
    }

    private async void QuickPreset_Click(object sender, RoutedEventArgs e)
    {
        if (_client == null) return;

        if (sender is Button btn && btn.Tag is string preset)
        {
            await GoToPresetAsync(preset);
        }
    }

    private async void GotoPresetNumber_Click(object sender, RoutedEventArgs e)
    {
        await GoToPresetFromInput();
    }

    private async void PresetNumberInput_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter)
        {
            e.Handled = true;
            await GoToPresetFromInput();
        }
    }

    private async Task GoToPresetFromInput()
    {
        if (_client == null) return;

        var input = PresetNumberInput.Text.Trim();
        if (int.TryParse(input, out int presetNum) && presetNum >= 1 && presetNum <= 255)
        {
            await GoToPresetAsync(presetNum.ToString());
        }
        else
        {
            MessageBox.Show("Please enter a valid preset number (1-255).",
                "Invalid Preset", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    private async Task GoToPresetAsync(string preset)
    {
        try
        {
            await _client!.Onvif.GotoPresetAsync(preset, 1.0f);
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

    private void BrowseRecordingFolder_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new Microsoft.Win32.OpenFolderDialog
        {
            Title = "Select Recording Folder",
            InitialDirectory = _settings.RecordingFolder
        };

        if (dialog.ShowDialog() == true)
        {
            var folder = dialog.FolderName;
            RecordingFolderText.Text = folder;

            // Save to settings
            _settings.RecordingFolder = folder;
            _settings.Save();

            if (_recording != null)
            {
                _recording.RecordingFolder = folder;
            }
        }
    }

    private void RecordButton_Click(object sender, RoutedEventArgs e)
    {
        if (_recording == null) return;

        if (!_isRecording)
        {
            // Check if folder is selected
            if (string.IsNullOrEmpty(_recording.RecordingFolder) ||
                RecordingFolderText.Text == "(Click Browse)")
            {
                MessageBox.Show("Please select a recording folder first.",
                    "Recording", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            // Validate folder exists and is writable
            try
            {
                if (!System.IO.Directory.Exists(_recording.RecordingFolder))
                {
                    var result = MessageBox.Show(
                        $"Recording folder does not exist:\n{_recording.RecordingFolder}\n\nCreate it now?",
                        "Recording Folder", MessageBoxButton.YesNo, MessageBoxImage.Question);

                    if (result == MessageBoxResult.Yes)
                    {
                        System.IO.Directory.CreateDirectory(_recording.RecordingFolder);
                    }
                    else
                    {
                        return;
                    }
                }
            }
            catch (Exception ex)
            {
                MessageBox.Show($"Cannot access recording folder:\n{ex.Message}",
                    "Recording Error", MessageBoxButton.OK, MessageBoxImage.Error);
                return;
            }

            // Check FFmpeg is available
            if (RecordingManager.GetFFmpegPath() == null)
            {
                MessageBox.Show("FFmpeg is not installed or configured.\n\nPlease configure FFmpeg in the Settings tab.",
                    "Recording Error", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            if (_recording.StartRecording())
            {
                _isRecording = true;
                RecordButton.Content = "Stop Recording";
                RecordButton.Background = (Brush)Application.Current.Resources["AccentRedBrush"];
                UpdateRecordingStatus("Recording...", Brushes.Red);
                StartRecordingTimer();
            }
            else
            {
                var errorMsg = _recording.LastError ?? "Failed to start recording.";
                MessageBox.Show(errorMsg, "Recording Error", MessageBoxButton.OK, MessageBoxImage.Warning);
            }
        }
        else
        {
            StopRecordingTimer();

            // Show stopping indicator with animation
            RecordButton.IsEnabled = false;
            StoppingIndicator.Visibility = Visibility.Visible;
            StartStoppingAnimation();

            // Stop recording in background so UI stays responsive
            _ = Task.Run(() =>
            {
                var filename = _recording.StopRecording();

                Dispatcher.Invoke(() =>
                {
                    // Hide stopping indicator
                    StopStoppingAnimation();
                    StoppingIndicator.Visibility = Visibility.Collapsed;

                    _isRecording = false;
                    RecordButton.Content = "Start Recording";
                    RecordButton.Background = (Brush)Application.Current.Resources["PrimaryBrush"];
                    RecordButton.IsEnabled = true;
                    UpdateRecordingStatus("Not Recording", Brushes.Gray);
                    RecordingSegmentInfo.Text = "30min segments, 10s overlap";

                    if (!string.IsNullOrEmpty(filename))
                    {
                        // Show saved filename below button for 3 seconds
                        RecordingSavedText.Text = $"Saved: {System.IO.Path.GetFileName(filename)}";
                        var clearTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(3) };
                        clearTimer.Tick += (s, args) =>
                        {
                            clearTimer.Stop();
                            RecordingSavedText.Text = "";
                        };
                        clearTimer.Start();
                    }
                });
            });
        }
    }

    private DispatcherTimer? _stoppingAnimationTimer;
    private int _stoppingDotCount;

    private void StartStoppingAnimation()
    {
        _stoppingDotCount = 0;
        _stoppingAnimationTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(300) };
        _stoppingAnimationTimer.Tick += (s, e) =>
        {
            _stoppingDotCount = (_stoppingDotCount + 1) % 4;
            StoppingDots.Text = new string('.', _stoppingDotCount + 1);
        };
        _stoppingAnimationTimer.Start();
    }

    private void StopStoppingAnimation()
    {
        _stoppingAnimationTimer?.Stop();
        _stoppingAnimationTimer = null;
    }

    private DispatcherTimer? _recordingTimer;

    private void StartRecordingTimer()
    {
        _recordingTimer = new DispatcherTimer
        {
            Interval = TimeSpan.FromSeconds(1)
        };
        _recordingTimer.Tick += (s, e) => UpdateRecordingSegmentInfo();
        _recordingTimer.Start();
    }

    private void StopRecordingTimer()
    {
        _recordingTimer?.Stop();
        _recordingTimer = null;
    }

    private void UpdateRecordingSegmentInfo()
    {
        if (_recording == null || !_recording.IsRecording) return;

        var elapsed = _recording.CurrentSegmentElapsed;
        var remaining = _recording.SegmentDuration - elapsed;
        var segment = _recording.CurrentSegmentNumber;

        RecordingSegmentInfo.Text = $"Segment {segment} | {elapsed:mm\\:ss} / {_recording.SegmentDuration:mm\\:ss}";
    }

    private void OnSegmentCreated(object? sender, string segmentPath)
    {
        Dispatcher.Invoke(() =>
        {
            var filename = System.IO.Path.GetFileName(segmentPath);
            UpdateRecordingStatus($"Saved: {filename}", Brushes.LimeGreen);

            // Reset to "Recording..." after a brief delay
            var timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(3) };
            timer.Tick += (s, e) =>
            {
                timer.Stop();
                if (_isRecording)
                    UpdateRecordingStatus("Recording...", Brushes.Red);
            };
            timer.Start();
        });
    }

    private void OnRecordingStatusChanged(object? sender, RecordingStatus status)
    {
        Dispatcher.Invoke(() =>
        {
            if (status.IsRecording)
            {
                RecordingSegmentInfo.Text = $"Segment {status.SegmentNumber} starting...";
            }
        });
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

    // ===== Audio Controls =====

    private void AudioButton_Click(object sender, RoutedEventArgs e)
    {
        if (_audioManager == null) return;

        if (!_isAudioEnabled)
        {
            if (_audioManager.Start())
            {
                _isAudioEnabled = true;
                AudioButton.Content = "Disable Audio";
                AudioButton.Background = (Brush)Application.Current.Resources["AccentGreenBrush"];
            }
            else
            {
                MessageBox.Show("Failed to start audio stream. The camera may not have audio support.",
                    "Audio Error", MessageBoxButton.OK, MessageBoxImage.Warning);
            }
        }
        else
        {
            _audioManager.Stop();
            _isAudioEnabled = false;
            AudioButton.Content = "Enable Audio";
            AudioButton.Background = (Brush)Application.Current.Resources["PrimaryBrush"];
        }
    }

    private void VolumeSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (_audioManager != null)
            _audioManager.Volume = (int)e.NewValue;

        if (VolumeLabel != null)
            VolumeLabel.Content = $"{(int)e.NewValue}%";
    }

    private void MuteButton_Click(object sender, RoutedEventArgs e)
    {
        if (_audioManager == null) return;

        _audioManager.ToggleMute();

        if (_audioManager.IsMuted)
        {
            MuteButton.Content = "Unmute";
            MuteButton.Background = (Brush)Application.Current.Resources["AccentOrangeBrush"];
        }
        else
        {
            MuteButton.Content = "Mute";
            MuteButton.Background = (Brush)Application.Current.Resources["PrimaryBrush"];
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

    private void QuickQualityToggle_Click(object sender, RoutedEventArgs e)
    {
        if (_recording == null) return;

        // Toggle between Main (4K) and Sub (SD)
        var currentQuality = _recording.CurrentQuality;
        var newQuality = currentQuality == StreamQuality.Main ? StreamQuality.Sub : StreamQuality.Main;

        if (_recording.IsStreaming)
        {
            _recording.StopStreaming();
            _recording.SetStreamQuality(newQuality);
            _recording.StartStreaming();
        }
        else
        {
            _recording.SetStreamQuality(newQuality);
        }

        // Update UI
        StreamQualityCombo.SelectedIndex = newQuality == StreamQuality.Main ? 0 : 1;
        UpdateStreamQualityDisplay();
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

    #region FFmpeg Settings

    private void InitializeFFmpegSettings()
    {
        // Load custom path from settings
        FFmpegPathText.Text = _settings.FFmpegPath;
        if (!string.IsNullOrEmpty(_settings.FFmpegPath))
        {
            RecordingManager.SetCustomFFmpegPath(_settings.FFmpegPath);
        }

        // Detect FFmpeg
        UpdateFFmpegStatus();
    }

    private void UpdateFFmpegStatus()
    {
        var path = RecordingManager.GetFFmpegPath();
        if (path != null)
        {
            FFmpegStatusText.Text = $"FFmpeg: Found\n{path}";
            FFmpegStatusText.Foreground = Brushes.LimeGreen;
        }
        else
        {
            FFmpegStatusText.Text = "FFmpeg: Not found\nRecording requires FFmpeg";
            FFmpegStatusText.Foreground = Brushes.Orange;
        }
    }

    private void BrowseFFmpegPath_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new Microsoft.Win32.OpenFileDialog
        {
            Title = "Select FFmpeg Executable",
            Filter = "FFmpeg|ffmpeg.exe|All Executables|*.exe",
            InitialDirectory = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles)
        };

        if (dialog.ShowDialog() == true)
        {
            FFmpegPathText.Text = dialog.FileName;
            _settings.FFmpegPath = dialog.FileName;
            _settings.Save();
            RecordingManager.SetCustomFFmpegPath(dialog.FileName);
            UpdateFFmpegStatus();
        }
    }

    private void DetectFFmpeg_Click(object sender, RoutedEventArgs e)
    {
        // Clear custom path to force auto-detection
        var customPath = FFmpegPathText.Text.Trim();
        if (!string.IsNullOrEmpty(customPath))
        {
            _settings.FFmpegPath = customPath;
            _settings.Save();
            RecordingManager.SetCustomFFmpegPath(customPath);
        }
        else
        {
            _settings.FFmpegPath = "";
            _settings.Save();
            RecordingManager.SetCustomFFmpegPath(null);
        }

        UpdateFFmpegStatus();
    }

    #endregion

    protected override void OnClosed(EventArgs e)
    {
        Disconnect();
        base.OnClosed(e);
    }
}
