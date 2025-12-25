using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
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

    // Connection overlay animation
    private DispatcherTimer? _spinnerTimer;
    private double _spinnerAngle;

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
        public double ErrorX { get; set; }       // Where target ended up (normalized)
        public double ErrorY { get; set; }
        public double ErrorDistance { get; set; }

        // Separate pan/tilt analysis
        public double PanError { get; set; }      // Error in horizontal direction only
        public double TiltError { get; set; }     // Error in vertical direction only
        public double SuggestedPanMultiplier { get; set; }
        public double SuggestedTiltMultiplier { get; set; }

        public double SuggestedMultiplier { get; set; }
        public bool IsUndershoot { get; set; }
        public bool IsPanUndershoot { get; set; }
        public bool IsTiltUndershoot { get; set; }
    }

    // Enhanced calibration profile
    private CalibrationProfile _calibrationProfile = new();
    private bool _isQuickCalibration;

    public MainWindow()
    {
        InitializeComponent();

        // Load saved settings
        _settings = AppSettings.Load();

        // Apply saved settings to UI
        CameraIpText.Text = _settings.CameraIp;
        UsernameText.Text = _settings.Username;
        PasswordText.Password = _settings.Password;
        RecordingFolderText.Text = _settings.RecordingFolder;
        _clickTravelMultiplier = _settings.ClickTravelMultiplier;
        TravelSlider.Value = _clickTravelMultiplier;
        TravelValueLabel.Text = $"{_clickTravelMultiplier:F0}";

        // Load calibration profile
        _calibrationProfile = _settings.Calibration ?? new CalibrationProfile();

        // Update calibration display
        UpdateCalibrationStatusDisplay();

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

    #region Connection Progress Overlay

    private void ShowConnectionOverlay(string ip)
    {
        // Reset all steps to pending state
        ResetConnectionSteps();

        // Set target IP
        ConnectionTargetText.Text = ip;
        ConnectionActionText.Text = "Initializing connection...";

        // Show overlay
        ConnectionOverlay.Visibility = Visibility.Visible;

        // Start spinner animation
        _spinnerAngle = 0;
        _spinnerTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(30) };
        _spinnerTimer.Tick += (s, e) =>
        {
            _spinnerAngle = (_spinnerAngle + 8) % 360;
            SpinnerRotation.Angle = _spinnerAngle;
        };
        _spinnerTimer.Start();

        // Set progress bar to 0
        AnimateProgressBar(0);
    }

    private void HideConnectionOverlay()
    {
        // Stop spinner
        _spinnerTimer?.Stop();
        _spinnerTimer = null;

        // Hide overlay
        ConnectionOverlay.Visibility = Visibility.Collapsed;
    }

    private void ResetConnectionSteps()
    {
        var pendingColor = new SolidColorBrush(Color.FromRgb(0x55, 0x55, 0x55));
        var textColor = new SolidColorBrush(Color.FromRgb(0x88, 0x88, 0x88));

        Step1Icon.Text = "○"; Step1Icon.Foreground = pendingColor;
        Step1Text.Foreground = textColor; Step1Text.Text = "Authenticating...";

        Step2Icon.Text = "○"; Step2Icon.Foreground = pendingColor;
        Step2Text.Foreground = pendingColor; Step2Text.Text = "Initializing PTZ control...";

        Step3Icon.Text = "○"; Step3Icon.Foreground = pendingColor;
        Step3Text.Foreground = pendingColor; Step3Text.Text = "Detecting video streams...";

        Step4Icon.Text = "○"; Step4Icon.Foreground = pendingColor;
        Step4Text.Foreground = pendingColor; Step4Text.Text = "Starting video stream...";

        Step5Icon.Text = "○"; Step5Icon.Foreground = pendingColor;
        Step5Text.Foreground = pendingColor; Step5Text.Text = "Loading device info...";
    }

    private void SetConnectionStep(int step, string status, bool isActive = true, bool isComplete = false, bool isFailed = false)
    {
        var activeColor = (Brush)Application.Current.Resources["PrimaryBrush"];
        var completeColor = new SolidColorBrush(Color.FromRgb(0x00, 0xD4, 0xAA));
        var failedColor = new SolidColorBrush(Color.FromRgb(0xFF, 0x44, 0x44));
        var textActiveColor = Brushes.White;
        var textCompleteColor = new SolidColorBrush(Color.FromRgb(0xAA, 0xAA, 0xAA));

        TextBlock icon, text;
        switch (step)
        {
            case 1: icon = Step1Icon; text = Step1Text; break;
            case 2: icon = Step2Icon; text = Step2Text; break;
            case 3: icon = Step3Icon; text = Step3Text; break;
            case 4: icon = Step4Icon; text = Step4Text; break;
            case 5: icon = Step5Icon; text = Step5Text; break;
            default: return;
        }

        if (isFailed)
        {
            icon.Text = "✕";
            icon.Foreground = failedColor;
            text.Foreground = failedColor;
        }
        else if (isComplete)
        {
            icon.Text = "✓";
            icon.Foreground = completeColor;
            text.Foreground = textCompleteColor;
        }
        else if (isActive)
        {
            icon.Text = "●";
            icon.Foreground = activeColor;
            text.Foreground = textActiveColor;
        }

        text.Text = status;
        ConnectionActionText.Text = status;

        // Update progress bar
        var progress = isComplete ? step * 20 : (step - 1) * 20 + 10;
        AnimateProgressBar(progress);
    }

    private void AnimateProgressBar(double targetPercent)
    {
        var totalWidth = ConnectionProgressBar.Parent is Grid parent ? parent.ActualWidth : 320;
        if (totalWidth <= 0) totalWidth = 320;

        var targetWidth = totalWidth * (targetPercent / 100.0);

        var animation = new DoubleAnimation
        {
            To = targetWidth,
            Duration = TimeSpan.FromMilliseconds(300),
            EasingFunction = new QuadraticEase { EasingMode = EasingMode.EaseOut }
        };

        ConnectionProgressBar.BeginAnimation(WidthProperty, animation);
    }

    private void ShowConnectionSuccess()
    {
        ConnectionActionText.Text = "Connected successfully!";
        AnimateProgressBar(100);

        // Brief delay then hide
        var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(800) };
        timer.Tick += (s, e) =>
        {
            timer.Stop();
            HideConnectionOverlay();
        };
        timer.Start();
    }

    private void ShowConnectionFailed(string error)
    {
        ConnectionActionText.Text = $"Connection failed: {error}";

        // Keep visible longer for error
        var timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(2) };
        timer.Tick += (s, e) =>
        {
            timer.Stop();
            HideConnectionOverlay();
        };
        timer.Start();
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

            // Show connection overlay
            ShowConnectionOverlay(ip);
            UpdateConnectionStatus("Connecting...", Brushes.Orange);
            ConnectButton.IsEnabled = false;

            // Step 1: Authentication
            SetConnectionStep(1, "Authenticating with camera...");
            await Task.Delay(100); // Allow UI to update

            _client = new CameraClient(ip, 80);

            if (!await _client.LoginAsync(username, password))
            {
                SetConnectionStep(1, "Authentication failed", isFailed: true);
                ShowConnectionFailed("Invalid credentials");
                MessageBox.Show("Failed to connect to camera. Check credentials and IP.",
                    "Connection Error", MessageBoxButton.OK, MessageBoxImage.Error);
                UpdateConnectionStatus("Connection Failed", Brushes.Red);
                ConnectButton.IsEnabled = true;
                return;
            }

            SetConnectionStep(1, "Authenticated", isComplete: true);

            // Step 2: ONVIF/PTZ
            SetConnectionStep(2, "Initializing PTZ control...");
            await Task.Delay(50);

            _ptz = new PTZController(_client);
            _recording = new RecordingManager(_client, StreamQuality.Main);
            _recording.FrameCaptured += OnFrameCaptured;
            _recording.SegmentCreated += OnSegmentCreated;
            _recording.StatusChanged += OnRecordingStatusChanged;
            _recording.StreamStatusChanged += OnStreamStatusChanged;

            // Apply saved recording folder from settings
            _recording.RecordingFolder = _settings.RecordingFolder;
            RecordingFolderText.Text = _settings.RecordingFolder;

            _deviceManager = new DeviceManager(ip, username, password, 80);

            // Warm up ONVIF connection for faster PTZ response
            await _client.Onvif.WarmUpConnectionAsync();
            SetConnectionStep(2, "PTZ initialized", isComplete: true);

            // Step 3: Stream Detection
            SetConnectionStep(3, "Detecting video streams...");
            await Task.Delay(50);

            _recording.AutoDetectBestRtspUrl();
            UpdateStreamQualityDisplay();

            var streamInfo = _recording.CurrentQuality == StreamQuality.Main ? "4K stream detected" : "Stream detected";
            SetConnectionStep(3, streamInfo, isComplete: true);

            // Step 4: Video Streaming
            SetConnectionStep(4, "Starting video stream...");
            await Task.Delay(50);

            if (!_recording.StartStreaming())
            {
                SetConnectionStep(4, "Video stream failed", isFailed: true);
                MessageBox.Show("Connected but failed to start video stream.",
                    "Warning", MessageBoxButton.OK, MessageBoxImage.Warning);
            }
            else
            {
                SetConnectionStep(4, "Video streaming", isComplete: true);
            }

            // Step 5: Device Info
            SetConnectionStep(5, "Loading device info...");

            _isConnected = true;
            NoVideoText.Visibility = Visibility.Collapsed;
            UpdateConnectionStatus("Connected", Brushes.LimeGreen);
            ConnectButton.Content = "Disconnect";
            ConnectButton.IsEnabled = true;

            // Save credentials on successful connection
            _settings.CameraIp = ip;
            _settings.Username = username;
            _settings.Password = password;
            _settings.Save();

            // Enable controls
            PTZControls.IsEnabled = true;
            PresetsGroup.IsEnabled = true;
            RecordingGroup.IsEnabled = true;
            AudioGroup.IsEnabled = true;
            QuickQualityToggle.IsEnabled = true;

            // Enable settings controls
            StreamSettingsGroup.IsEnabled = true;
            ImageSettingsGroup.IsEnabled = true;
            EncoderSettingsGroup.IsEnabled = true;
            OSDSettingsGroup.IsEnabled = true;
            DeviceInfoGroup.IsEnabled = true;

            // Enable Camera Info tab controls
            CameraTimeGroup.IsEnabled = true;
            StorageInfoGroup.IsEnabled = true;
            UsersGroup.IsEnabled = true;
            EventsGroup.IsEnabled = true;
            ServicesGroup.IsEnabled = true;

            // Enable Advanced tab controls
            ToursGroup.IsEnabled = true;
            PatternsGroup.IsEnabled = true;
            ScanGroup.IsEnabled = true;
            WiperGroup.IsEnabled = true;
            TwoWayAudioGroup.IsEnabled = true;
            PrivacyMasksGroup.IsEnabled = true;
            AlarmsGroup.IsEnabled = true;

            // Enable Network tab controls
            NetworkGroup.IsEnabled = true;
            NtpGroup.IsEnabled = true;
            EmailGroup.IsEnabled = true;
            WifiGroup.IsEnabled = true;

            // Enable Firmware tab controls
            FirmwareGroup.IsEnabled = true;
            MaintenanceGroup.IsEnabled = true;
            DeviceActionsGroup.IsEnabled = true;

            // Initialize audio manager
            _audioManager = new AudioManager();
            _audioManager.SetRtspUrl(ip, username, password);
            _audioManager.StatusChanged += (s, status) =>
            {
                Dispatcher.Invoke(() => AudioStatusText.Text = status);
            };

            // Load device info
            await LoadDeviceInfoAsync();
            SetConnectionStep(5, "Device info loaded", isComplete: true);

            // Show success and hide overlay
            ShowConnectionSuccess();
        }
        catch (Exception ex)
        {
            SetConnectionStep(1, "Connection error", isFailed: true);
            ShowConnectionFailed(ex.Message);
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
                _recording.StreamStatusChanged -= OnStreamStatusChanged;
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
            ImageSettingsGroup.IsEnabled = false;
            EncoderSettingsGroup.IsEnabled = false;
            OSDSettingsGroup.IsEnabled = false;
            DeviceInfoGroup.IsEnabled = false;

            // Disable Camera Info tab controls
            CameraTimeGroup.IsEnabled = false;
            StorageInfoGroup.IsEnabled = false;
            UsersGroup.IsEnabled = false;
            EventsGroup.IsEnabled = false;
            ServicesGroup.IsEnabled = false;
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

        var absX = Math.Abs(normalizedX);
        var absY = Math.Abs(normalizedY);
        var distance = Math.Sqrt(normalizedX * normalizedX + normalizedY * normalizedY);

        // Skip if click is very close to center
        if (distance < 0.05) return;

        // Use enhanced calibration profile for separate pan/tilt timing
        var (panDurationMs, tiltDurationMs) = _calibrationProfile.CalculateMoveDuration(normalizedX, normalizedY);

        // Fallback to legacy multiplier if profile not calibrated
        if (_calibrationProfile.SampleCount == 0)
        {
            panDurationMs = (int)(absX * _clickTravelMultiplier);
            tiltDurationMs = (int)(absY * _clickTravelMultiplier);
        }

        // Calculate base direction
        var basePanDir = normalizedX > 0 ? 1.0f : -1.0f;
        var baseTiltDir = normalizedY > 0 ? 1.0f : -1.0f;

        // Zero out small movements
        if (absX < 0.05) basePanDir = 0;
        if (absY < 0.05) baseTiltDir = 0;

        // Calculate which axis takes longer
        var maxDuration = Math.Max(panDurationMs, tiltDurationMs);
        var minDuration = Math.Min(panDurationMs, tiltDurationMs);

        // Skip if durations too short
        if (maxDuration < 30) return;

        // Scale the faster axis to match timing (both start and end together)
        float panSpeedScale = maxDuration > 0 ? (float)panDurationMs / maxDuration : 0;
        float tiltSpeedScale = maxDuration > 0 ? (float)tiltDurationMs / maxDuration : 0;

        // Deceleration curve based on calibration profile strength
        var decelStrength = _calibrationProfile.DecelerationStrength;

        // Enhanced multi-phase deceleration for smoother, more accurate movement
        // Phase timing adapts based on calibration profile
        var phases = new (float speedMultiplier, double durationPercent)[]
        {
            (1.0f, 0.35 + (1.0 - decelStrength) * 0.2),   // Full speed phase
            (0.6f, 0.25),                                  // Medium speed
            (0.3f, 0.20),                                  // Slow approach
            (0.12f, 0.20 * decelStrength),                // Fine positioning (based on profile)
        };

        // Normalize phases
        var totalPercent = phases.Sum(p => p.durationPercent);
        var normalizedPhases = phases.Select(p => (p.speedMultiplier, p.durationPercent / totalPercent)).ToArray();

        // Account for camera response delay at start
        var responseDelay = (int)_calibrationProfile.ResponseDelay;

        foreach (var (speedMult, durationPct) in normalizedPhases)
        {
            var phaseDuration = (int)(maxDuration * durationPct);
            if (phaseDuration < 15) continue; // Skip very short phases

            // Apply speed multiplier with axis scaling
            var panSpeed = basePanDir * speedMult * panSpeedScale;
            var tiltSpeed = baseTiltDir * speedMult * tiltSpeedScale;

            _client.Onvif.ContinuousMoveFast(panSpeed, tiltSpeed, 0);

            // First phase accounts for response delay
            if (responseDelay > 0)
            {
                phaseDuration = Math.Max(phaseDuration - responseDelay, 20);
                responseDelay = 0;
            }

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

    private void QuickCalibration_Click(object sender, RoutedEventArgs e)
    {
        StartCalibration(isQuick: true);
    }

    private void FullCalibration_Click(object sender, RoutedEventArgs e)
    {
        StartCalibration(isQuick: false);
    }

    // Legacy handler - defaults to full calibration
    private void StartCalibration_Click(object sender, RoutedEventArgs e)
    {
        StartCalibration(isQuick: false);
    }

    private void StartCalibration(bool isQuick)
    {
        if (!_isConnected || _client == null)
        {
            MessageBox.Show("Please connect to camera first.", "Calibration", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        _isQuickCalibration = isQuick;
        _isCalibrating = true;
        _calibrationStep = 0;
        _calibrationPhase = 0;
        _calibrationSamples.Clear();
        _currentTarget = null;

        // Show calibration overlay
        CalibrationOverlay.Visibility = Visibility.Visible;

        var modeText = isQuick ? "QUICK CALIBRATION (4 targets)" : "FULL CALIBRATION (16 targets)";
        CalibrationInstructions.Text =
            $"{modeText}\n\n" +
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

    private void UpdateCalibrationStatusDisplay()
    {
        if (_calibrationProfile.SampleCount > 0)
        {
            CalibrationStatusText.Text = _calibrationProfile.GetQualityDescription();
            CalibrationStatusText.Foreground = _calibrationProfile.AverageError switch
            {
                < 0.05 => Brushes.LimeGreen,
                < 0.10 => Brushes.Yellow,
                < 0.20 => Brushes.Orange,
                _ => Brushes.Red
            };
            PanMultiplierText.Text = $"{_calibrationProfile.PanMultiplier:F0}";
            TiltMultiplierText.Text = $"{_calibrationProfile.TiltMultiplier:F0}";
        }
        else
        {
            CalibrationStatusText.Text = "Not calibrated";
            CalibrationStatusText.Foreground = new SolidColorBrush(System.Windows.Media.Color.FromRgb(136, 136, 136));
            PanMultiplierText.Text = $"{_clickTravelMultiplier:F0}";
            TiltMultiplierText.Text = $"{_clickTravelMultiplier:F0}";
        }
    }

    private CalibrationTarget[] GetActiveCalibrationTargets()
    {
        if (_isQuickCalibration)
        {
            // Quick mode: only 4 cardinal directions at mid distance
            return new[]
            {
                new CalibrationTarget(0.55, 0, "Mid-Right", 0.55, "Right"),
                new CalibrationTarget(-0.55, 0, "Mid-Left", 0.55, "Left"),
                new CalibrationTarget(0, 0.55, "Mid-Up", 0.55, "Up"),
                new CalibrationTarget(0, -0.55, "Mid-Down", 0.55, "Down"),
            };
        }
        return _calibrationTargets;
    }

    private void UpdateCalibrationDisplay()
    {
        CalibrationCanvas.Children.Clear();

        var activeTargets = GetActiveCalibrationTargets();

        if (_calibrationStep >= activeTargets.Length)
        {
            FinishCalibration();
            return;
        }

        _currentTarget = activeTargets[_calibrationStep];
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

        // Draw distance rings for reference (only for full calibration)
        if (!_isQuickCalibration)
            DrawDistanceRings(canvas, centerX, centerY);

        // Draw all targets
        for (int i = 0; i < activeTargets.Length; i++)
        {
            var t = activeTargets[i];
            var isActive = i == _calibrationStep;
            var isCompleted = i < _calibrationStep;
            DrawCalibrationTarget(t.X, t.Y, t.Name, isActive, isCompleted, t.Distance);
        }

        // Draw center crosshair
        DrawCenterCrosshair();

        // Draw progress bar
        DrawProgressBar(canvas, activeTargets.Length);

        // Update status text
        var distanceLabel = _currentTarget.Distance switch
        {
            < 0.4 => "NEAR",
            > 0.65 => "FAR",
            _ => "MID"
        };

        if (_calibrationPhase == 0)
        {
            CalibrationStepText.Text = $"[{_calibrationStep + 1}/{activeTargets.Length}] Click the YELLOW {distanceLabel} {_currentTarget.Direction} target";
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

    private void DrawProgressBar(Canvas canvas, int totalTargets)
    {
        var width = canvas.ActualWidth - 40;
        var progress = (double)_calibrationStep / totalTargets;

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
            var targetAbsX = Math.Abs(_currentTarget.X);
            var targetAbsY = Math.Abs(_currentTarget.Y);

            // Calculate separate pan/tilt errors
            // Pan error: how far off in horizontal direction (positive = undershot, negative = overshot)
            var panError = clickNormX * Math.Sign(_currentTarget.X);  // Error in target direction
            var tiltError = clickNormY * Math.Sign(_currentTarget.Y);

            // Determine undershoot/overshoot for each axis
            var isPanUndershoot = panError > 0.02 && Math.Sign(clickNormX) == Math.Sign(_currentTarget.X);
            var isTiltUndershoot = tiltError > 0.02 && Math.Sign(clickNormY) == Math.Sign(_currentTarget.Y);

            // Calculate suggested multipliers for each axis
            var currentPanMult = _calibrationProfile.SampleCount > 0 ? _calibrationProfile.PanMultiplier : _clickTravelMultiplier;
            var currentTiltMult = _calibrationProfile.SampleCount > 0 ? _calibrationProfile.TiltMultiplier : _clickTravelMultiplier;

            double suggestedPanMult = currentPanMult;
            double suggestedTiltMult = currentTiltMult;

            // Pan axis calculation
            if (targetAbsX > 0.05)
            {
                var panTraveled = targetAbsX - Math.Abs(panError);
                if (isPanUndershoot && panTraveled > 0.01)
                    suggestedPanMult = currentPanMult * (targetAbsX / panTraveled);
                else if (!isPanUndershoot && panError < -0.02)
                    suggestedPanMult = currentPanMult * (targetAbsX / (targetAbsX + Math.Abs(panError)));
            }

            // Tilt axis calculation
            if (targetAbsY > 0.05)
            {
                var tiltTraveled = targetAbsY - Math.Abs(tiltError);
                if (isTiltUndershoot && tiltTraveled > 0.01)
                    suggestedTiltMult = currentTiltMult * (targetAbsY / tiltTraveled);
                else if (!isTiltUndershoot && tiltError < -0.02)
                    suggestedTiltMult = currentTiltMult * (targetAbsY / (targetAbsY + Math.Abs(tiltError)));
            }

            // Overall legacy calculation
            var sameDirection = (clickNormX * _currentTarget.X >= 0) || (clickNormY * _currentTarget.Y >= 0);
            var isUndershoot = sameDirection && errorDistance > 0.02;

            double suggestedMultiplier = _clickTravelMultiplier;
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

            // Clamp to reasonable ranges
            suggestedMultiplier = Math.Clamp(suggestedMultiplier, 500, 5000);
            suggestedPanMult = Math.Clamp(suggestedPanMult, 500, 5000);
            suggestedTiltMult = Math.Clamp(suggestedTiltMult, 500, 5000);

            _calibrationSamples.Add(new CalibrationSample
            {
                Target = _currentTarget,
                ErrorX = clickNormX,
                ErrorY = clickNormY,
                ErrorDistance = errorDistance,
                PanError = panError,
                TiltError = tiltError,
                SuggestedPanMultiplier = suggestedPanMult,
                SuggestedTiltMultiplier = suggestedTiltMult,
                SuggestedMultiplier = suggestedMultiplier,
                IsUndershoot = isUndershoot,
                IsPanUndershoot = isPanUndershoot,
                IsTiltUndershoot = isTiltUndershoot
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

        // ===== Enhanced Analysis with Separate Pan/Tilt =====

        // Separate samples by primary axis (only use samples where that axis was dominant)
        var panDominant = _calibrationSamples.Where(s => Math.Abs(s.Target.X) > Math.Abs(s.Target.Y) * 1.5).ToList();
        var tiltDominant = _calibrationSamples.Where(s => Math.Abs(s.Target.Y) > Math.Abs(s.Target.X) * 1.5).ToList();

        // Calculate weighted average pan/tilt multipliers
        double avgPanMult, avgTiltMult;
        if (panDominant.Count > 0)
            avgPanMult = panDominant.Average(s => s.SuggestedPanMultiplier);
        else
            avgPanMult = _calibrationSamples.Average(s => s.SuggestedPanMultiplier);

        if (tiltDominant.Count > 0)
            avgTiltMult = tiltDominant.Average(s => s.SuggestedTiltMultiplier);
        else
            avgTiltMult = _calibrationSamples.Average(s => s.SuggestedTiltMultiplier);

        // ===== Distance Factor Analysis =====
        var nearSamples = _calibrationSamples.Where(s => s.Target.Distance < 0.4).ToList();
        var midSamples = _calibrationSamples.Where(s => s.Target.Distance >= 0.4 && s.Target.Distance < 0.7).ToList();
        var farSamples = _calibrationSamples.Where(s => s.Target.Distance >= 0.7).ToList();

        // Calculate base multiplier (from mid samples as reference)
        var baseMult = midSamples.Any() ? midSamples.Average(s => s.SuggestedMultiplier) : _calibrationSamples.Average(s => s.SuggestedMultiplier);

        // Calculate distance correction factors relative to mid
        double nearFactor = 1.0, midFactor = 1.0, farFactor = 1.0;
        if (nearSamples.Any() && baseMult > 0)
        {
            var nearAvg = nearSamples.Average(s => s.SuggestedMultiplier);
            nearFactor = nearAvg / baseMult;
        }
        if (farSamples.Any() && baseMult > 0)
        {
            var farAvg = farSamples.Average(s => s.SuggestedMultiplier);
            farFactor = farAvg / baseMult;
        }

        // Clamp factors to reasonable range
        nearFactor = Math.Clamp(nearFactor, 0.5, 1.5);
        farFactor = Math.Clamp(farFactor, 0.5, 1.5);

        // ===== Error Analysis =====
        var avgError = _calibrationSamples.Average(s => s.ErrorDistance);
        var perfectCount = _calibrationSamples.Count(s => s.ErrorDistance < 0.05);
        var goodCount = _calibrationSamples.Count(s => s.ErrorDistance >= 0.05 && s.ErrorDistance < 0.15);
        var poorCount = _calibrationSamples.Count(s => s.ErrorDistance >= 0.15);

        var panUndershootCount = _calibrationSamples.Count(s => s.IsPanUndershoot);
        var tiltUndershootCount = _calibrationSamples.Count(s => s.IsTiltUndershoot);

        // ===== Build Report =====
        var qualityDesc = avgError switch
        {
            < 0.05 => "EXCELLENT",
            < 0.10 => "GOOD",
            < 0.20 => "FAIR",
            _ => "NEEDS IMPROVEMENT"
        };

        var report = $"ENHANCED CALIBRATION ANALYSIS\n" +
            $"═══════════════════════════════════\n\n" +
            $"Quality: {qualityDesc}\n" +
            $"Average Error: {avgError * 100:F1}%\n" +
            $"Samples: {_calibrationSamples.Count} ({perfectCount} perfect, {goodCount} good, {poorCount} poor)\n\n" +
            $"AXIS MULTIPLIERS:\n" +
            $"  Pan (horizontal):  {avgPanMult:F0} ms/unit\n" +
            $"  Tilt (vertical):   {avgTiltMult:F0} ms/unit\n" +
            $"  Ratio (Pan/Tilt):  {avgPanMult / avgTiltMult:F2}\n\n" +
            $"DISTANCE FACTORS:\n" +
            $"  Near (< 0.35):  {nearFactor:F2}x\n" +
            $"  Mid (0.35-0.65): {midFactor:F2}x (reference)\n" +
            $"  Far (> 0.65):   {farFactor:F2}x\n\n" +
            $"BIAS:\n" +
            $"  Pan undershoots:  {panUndershootCount}/{_calibrationSamples.Count}\n" +
            $"  Tilt undershoots: {tiltUndershootCount}/{_calibrationSamples.Count}\n\n" +
            $"Apply enhanced calibration profile?";

        var result = MessageBox.Show(report, "Calibration Results", MessageBoxButton.YesNo, MessageBoxImage.Information);

        if (result == MessageBoxResult.Yes)
        {
            // Update enhanced calibration profile
            _calibrationProfile.PanMultiplier = avgPanMult;
            _calibrationProfile.TiltMultiplier = avgTiltMult;
            _calibrationProfile.NearFactor = nearFactor;
            _calibrationProfile.MidFactor = midFactor;
            _calibrationProfile.FarFactor = farFactor;
            _calibrationProfile.SampleCount = _calibrationSamples.Count;
            _calibrationProfile.AverageError = avgError;
            _calibrationProfile.LastCalibrated = DateTime.Now;

            // Also update legacy multiplier for backward compatibility
            var avgMultiplier = (avgPanMult + avgTiltMult) / 2;
            _clickTravelMultiplier = avgMultiplier;
            TravelSlider.Value = Math.Clamp(avgMultiplier, TravelSlider.Minimum, TravelSlider.Maximum);
            TravelValueLabel.Text = $"{avgMultiplier:F0}";

            // Expand slider range if needed
            if (avgMultiplier > TravelSlider.Maximum)
                TravelSlider.Maximum = avgMultiplier + 500;
            if (avgMultiplier < TravelSlider.Minimum)
                TravelSlider.Minimum = avgMultiplier - 200;

            // Save to settings
            _settings.Calibration = _calibrationProfile;
            _settings.ClickTravelMultiplier = _clickTravelMultiplier;
            _settings.Save();

            // Update UI display
            UpdateCalibrationStatusDisplay();

            MessageBox.Show(
                $"Calibration profile saved!\n\n" +
                $"Pan: {avgPanMult:F0} ms/unit\n" +
                $"Tilt: {avgTiltMult:F0} ms/unit\n" +
                $"Quality: {_calibrationProfile.GetQualityDescription()}",
                "Calibration Applied", MessageBoxButton.OK, MessageBoxImage.Information);
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

    private async void SavePreset_Click(object sender, RoutedEventArgs e)
    {
        if (_client == null) return;

        var presetNumStr = SavePresetNumber.Text.Trim();
        var presetName = SavePresetName.Text.Trim();

        if (!int.TryParse(presetNumStr, out int presetNum) || presetNum < 1 || presetNum > 255)
        {
            MessageBox.Show("Please enter a valid preset number (1-255).",
                "Invalid Preset", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        try
        {
            UpdateConnectionStatus($"Saving Preset {presetNum}...", Brushes.Orange);

            // Use preset number as token (common for ONVIF cameras)
            var result = await _client.Onvif.SetPresetAsync(
                string.IsNullOrEmpty(presetName) ? $"Preset{presetNum}" : presetName,
                presetNum.ToString());

            if (result != null)
            {
                UpdateConnectionStatus($"Saved Preset {presetNum}", Brushes.LimeGreen);
                MessageBox.Show($"Current position saved as Preset {presetNum}.",
                    "Preset Saved", MessageBoxButton.OK, MessageBoxImage.Information);
            }
            else
            {
                UpdateConnectionStatus("Save Failed", Brushes.Orange);
                MessageBox.Show("Failed to save preset. The camera may not support this feature.",
                    "Error", MessageBoxButton.OK, MessageBoxImage.Warning);
            }

            // Reset status after delay
            await Task.Delay(2000);
            if (_isConnected)
                UpdateConnectionStatus("Connected", Brushes.LimeGreen);
        }
        catch (Exception ex)
        {
            MessageBox.Show($"Failed to save preset: {ex.Message}",
                "Error", MessageBoxButton.OK, MessageBoxImage.Error);
            UpdateConnectionStatus("Connected", Brushes.LimeGreen);
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

    private void OpenRecordingFolder_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            var folder = _settings.RecordingFolder;
            if (!string.IsNullOrEmpty(folder) && System.IO.Directory.Exists(folder))
            {
                System.Diagnostics.Process.Start("explorer.exe", folder);
            }
            else
            {
                MessageBox.Show("Recording folder does not exist.\nPlease select a valid folder first.",
                    "Folder Not Found", MessageBoxButton.OK, MessageBoxImage.Warning);
            }
        }
        catch (Exception ex)
        {
            MessageBox.Show($"Could not open folder: {ex.Message}",
                "Error", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private void RecordingQualityCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_recording == null) return;

        var selectedItem = RecordingQualityCombo.SelectedItem as ComboBoxItem;
        if (selectedItem?.Tag is string qualityTag)
        {
            _recording.Quality = qualityTag switch
            {
                "Standard" => RecordingQuality.Standard,
                "High" => RecordingQuality.High,
                "Maximum" => RecordingQuality.Maximum,
                _ => RecordingQuality.Maximum
            };
            Console.WriteLine($"Recording quality set to: {_recording.Quality}");
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

            // Stop audio playback since recording captures audio directly
            var wasAudioPlaying = _audioManager?.IsPlaying ?? false;
            if (wasAudioPlaying)
            {
                _audioManager?.Stop();
                AudioButton.Content = "Enable Audio";
                AudioStatusText.Text = "Audio: Captured in recording";
            }

            if (_recording.StartRecording())
            {
                _isRecording = true;
                RecordButton.Content = "Stop Recording";
                RecordButton.Background = (Brush)Application.Current.Resources["AccentRedBrush"];
                UpdateRecordingStatus("Recording...", Brushes.Red);

                // Check if sub-stream preview is available
                if (_recording.IsUsingSubStream)
                {
                    // Dual-stream mode: sub-stream for preview, main stream for recording
                    NoVideoText.Visibility = Visibility.Collapsed;
                    StreamQualityStatus.Text = "Stream: SD (preview)";
                }
                else
                {
                    // Fallback: no preview during recording
                    NoVideoText.Text = "Recording in progress...\n\nPreview paused.";
                    NoVideoText.Visibility = Visibility.Visible;
                }
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

                    // Hide recording message, preview will restart automatically
                    NoVideoText.Text = "No Video Feed\n\nClick 'Connect' to start";
                    NoVideoText.Visibility = Visibility.Collapsed;
                    AudioStatusText.Text = "Audio: Disabled";

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

        var status = _recording.GetRecordingStatus();
        var elapsed = _recording.CurrentSegmentElapsed;

        // Format file size
        var fileSizeMB = status.FileSize / 1024.0 / 1024.0;
        var sizeStr = fileSizeMB > 0 ? $" | {fileSizeMB:F1} MB" : "";

        // Show bitrate if we have it
        var bitrateStr = status.BitrateMbps > 0 ? $" @ {status.BitrateMbps:F1} Mbps" : "";

        RecordingSegmentInfo.Text = $"Seg {status.SegmentNumber} | {elapsed:mm\\:ss}{sizeStr}{bitrateStr}";

        // Show disk space warning
        if (status.LowDiskSpace)
        {
            var spaceGB = status.AvailableDiskSpace / 1024.0 / 1024.0 / 1024.0;
            UpdateRecordingStatus($"LOW DISK: {spaceGB:F1} GB left!", Brushes.Orange);
        }
        // Show error status
        else if (status.HasError)
        {
            UpdateRecordingStatus($"Error: {status.ErrorMessage}", Brushes.Orange);
        }
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

    private void OnStreamStatusChanged(object? sender, string status)
    {
        Dispatcher.Invoke(() =>
        {
            if (status == "Connected")
            {
                UpdateConnectionStatus("Connected", Brushes.LimeGreen);
            }
            else if (status.Contains("Reconnecting"))
            {
                UpdateConnectionStatus(status, Brushes.Orange);
            }
            else if (status.Contains("lost") || status.Contains("exceeded"))
            {
                UpdateConnectionStatus(status, Brushes.Red);
            }
            else
            {
                UpdateConnectionStatus(status, Brushes.Orange);
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

    private async void AudioButton_Click(object sender, RoutedEventArgs e)
    {
        if (_audioManager == null) return;

        if (!_isAudioEnabled)
        {
            AudioButton.IsEnabled = false;
            AudioStatusText.Text = "Audio: Checking...";

            // First check if audio is enabled on the camera
            if (_client?.Rpc != null)
            {
                try
                {
                    var audioConfig = await _client.Rpc.GetAudioEncodeConfigAsync();
                    if (audioConfig != null)
                    {
                        Console.WriteLine($"[Audio] Camera audio enabled: {audioConfig.Enable}, Codec: {audioConfig.Compression}");
                        if (!audioConfig.Enable)
                        {
                            var result = MessageBox.Show(
                                "Audio is disabled on the camera's encoder settings.\n\n" +
                                "Would you like to enable it? (This may require a stream restart)",
                                "Audio Disabled", MessageBoxButton.YesNo, MessageBoxImage.Question);

                            if (result == MessageBoxResult.Yes)
                            {
                                var enabled = await _client.Rpc.SetAudioEnabledAsync(true);
                                if (enabled)
                                {
                                    MessageBox.Show("Audio enabled. Please wait a moment for the stream to update.",
                                        "Audio Enabled", MessageBoxButton.OK, MessageBoxImage.Information);
                                }
                            }
                        }
                    }
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"[Audio] Config check error: {ex.Message}");
                }
            }

            if (_audioManager.Start())
            {
                _isAudioEnabled = true;
                AudioButton.Content = "Disable Audio";
                AudioButton.Background = (Brush)Application.Current.Resources["AccentGreenBrush"];
            }
            else
            {
                AudioStatusText.Text = "Audio: Failed";
                MessageBox.Show(
                    "Failed to start audio stream.\n\n" +
                    "Possible causes:\n" +
                    "• Audio may be disabled in camera settings\n" +
                    "• The RTSP stream may not include audio\n" +
                    "• Check the console for detailed error messages",
                    "Audio Error", MessageBoxButton.OK, MessageBoxImage.Warning);
            }

            AudioButton.IsEnabled = true;
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

    private async void OptimizeMaxQuality_Click(object sender, RoutedEventArgs e)
    {
        if (_client == null)
        {
            MessageBox.Show("Please connect to the camera first.",
                "Not Connected", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        var result = MessageBox.Show(
            "Optimize camera for maximum quality?\n\n" +
            "This will set:\n" +
            "• Resolution: 4K (3840x2160)\n" +
            "• Bitrate: 8 Mbps\n" +
            "• Frame Rate: 25 fps\n" +
            "• H.264 Profile: High\n\n" +
            "The stream will restart after applying.",
            "Optimize Camera", MessageBoxButton.YesNo, MessageBoxImage.Question);

        if (result != MessageBoxResult.Yes) return;

        try
        {
            var success = await _client.Onvif.OptimizeForMaxQualityAsync();

            if (success)
            {
                MessageBox.Show(
                    "Camera optimized for maximum quality!\n\n" +
                    "Settings applied:\n" +
                    "• 4K resolution (3840x2160)\n" +
                    "• 8 Mbps bitrate\n" +
                    "• 25 fps frame rate\n" +
                    "• H.264 High Profile\n\n" +
                    "Reconnect to see the changes.",
                    "Success", MessageBoxButton.OK, MessageBoxImage.Information);

                // Reload encoder settings to reflect changes
                LoadEncoderSettings_Click(sender, e);
            }
            else
            {
                MessageBox.Show("Failed to optimize camera settings.\nThe camera may not support these settings.",
                    "Error", MessageBoxButton.OK, MessageBoxImage.Warning);
            }
        }
        catch (Exception ex)
        {
            MessageBox.Show($"Failed to optimize camera: {ex.Message}",
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

    #region Image Settings (RPC)

    private bool _isLoadingImageSettings;
    private VideoColorConfig? _currentVideoColor;

    private void ImageSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (_isLoadingImageSettings || _isInitializing) return;

        var slider = sender as Slider;
        if (slider == null) return;

        var value = (int)slider.Value;
        var tag = slider.Tag?.ToString();

        // Update the corresponding label
        switch (tag)
        {
            case "Brightness":
                BrightnessValue.Text = value.ToString();
                break;
            case "Contrast":
                ContrastValue.Text = value.ToString();
                break;
            case "Saturation":
                SaturationValue.Text = value.ToString();
                break;
            case "Sharpness":
                SharpnessValue.Text = value.ToString();
                break;
            case "Hue":
                HueValue.Text = value.ToString();
                break;
        }
    }

    private async void LoadImageSettings_Click(object sender, RoutedEventArgs e)
    {
        if (_client?.Rpc == null) return;

        try
        {
            _isLoadingImageSettings = true;
            _currentVideoColor = await _client.Rpc.GetVideoColorAsync();

            if (_currentVideoColor != null)
            {
                BrightnessSlider.Value = _currentVideoColor.Brightness;
                ContrastSlider.Value = _currentVideoColor.Contrast;
                SaturationSlider.Value = _currentVideoColor.Saturation;
                SharpnessSlider.Value = _currentVideoColor.Sharpness;
                HueSlider.Value = _currentVideoColor.Hue;

                BrightnessValue.Text = _currentVideoColor.Brightness.ToString();
                ContrastValue.Text = _currentVideoColor.Contrast.ToString();
                SaturationValue.Text = _currentVideoColor.Saturation.ToString();
                SharpnessValue.Text = _currentVideoColor.Sharpness.ToString();
                HueValue.Text = _currentVideoColor.Hue.ToString();
            }
            else
            {
                MessageBox.Show("Could not load image settings from camera.",
                    "Info", MessageBoxButton.OK, MessageBoxImage.Information);
            }
        }
        catch (Exception ex)
        {
            MessageBox.Show($"Failed to load image settings: {ex.Message}",
                "Error", MessageBoxButton.OK, MessageBoxImage.Error);
        }
        finally
        {
            _isLoadingImageSettings = false;
        }
    }

    private async void ApplyImageSettings_Click(object sender, RoutedEventArgs e)
    {
        if (_client?.Rpc == null) return;

        try
        {
            var config = new VideoColorConfig
            {
                Brightness = (int)BrightnessSlider.Value,
                Contrast = (int)ContrastSlider.Value,
                Saturation = (int)SaturationSlider.Value,
                Sharpness = (int)SharpnessSlider.Value,
                Hue = (int)HueSlider.Value,
                Gamma = _currentVideoColor?.Gamma ?? 50,
                Gain = _currentVideoColor?.Gain ?? 50
            };

            var success = await _client.Rpc.SetVideoColorAsync(config);

            if (success)
            {
                MessageBox.Show("Image settings applied successfully!",
                    "Success", MessageBoxButton.OK, MessageBoxImage.Information);
                _currentVideoColor = config;
            }
            else
            {
                MessageBox.Show("Failed to apply image settings.",
                    "Error", MessageBoxButton.OK, MessageBoxImage.Warning);
            }
        }
        catch (Exception ex)
        {
            MessageBox.Show($"Failed to apply image settings: {ex.Message}",
                "Error", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private async void ResetImageSettings_Click(object sender, RoutedEventArgs e)
    {
        if (_client?.Rpc == null) return;

        var result = MessageBox.Show("Reset image settings to default values (50)?",
            "Confirm Reset", MessageBoxButton.YesNo, MessageBoxImage.Question);

        if (result != MessageBoxResult.Yes) return;

        try
        {
            var config = new VideoColorConfig
            {
                Brightness = 50,
                Contrast = 50,
                Saturation = 50,
                Sharpness = 50,
                Hue = 50,
                Gamma = 50,
                Gain = 50
            };

            var success = await _client.Rpc.SetVideoColorAsync(config);

            if (success)
            {
                _isLoadingImageSettings = true;
                BrightnessSlider.Value = 50;
                ContrastSlider.Value = 50;
                SaturationSlider.Value = 50;
                SharpnessSlider.Value = 50;
                HueSlider.Value = 50;
                _isLoadingImageSettings = false;

                MessageBox.Show("Image settings reset to defaults.",
                    "Success", MessageBoxButton.OK, MessageBoxImage.Information);
                _currentVideoColor = config;
            }
            else
            {
                MessageBox.Show("Failed to reset image settings.",
                    "Error", MessageBoxButton.OK, MessageBoxImage.Warning);
            }
        }
        catch (Exception ex)
        {
            MessageBox.Show($"Failed to reset image settings: {ex.Message}",
                "Error", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    #endregion

    #region Camera Info Tab Handlers

    private async void RefreshCameraTime_Click(object sender, RoutedEventArgs e)
    {
        await LoadCameraTimeAsync();
    }

    private async void SyncCameraTime_Click(object sender, RoutedEventArgs e)
    {
        if (_client?.Rpc == null) return;

        var result = MessageBox.Show(
            $"Set camera time to current PC time?\n\n{DateTime.Now:yyyy-MM-dd HH:mm:ss}",
            "Sync Time", MessageBoxButton.YesNo, MessageBoxImage.Question);

        if (result != MessageBoxResult.Yes) return;

        try
        {
            var success = await _client.Rpc.SetCurrentTimeAsync(DateTime.Now);
            if (success)
            {
                MessageBox.Show("Camera time synchronized!", "Success", MessageBoxButton.OK, MessageBoxImage.Information);
                await LoadCameraTimeAsync();
            }
            else
            {
                MessageBox.Show("Failed to sync camera time.", "Error", MessageBoxButton.OK, MessageBoxImage.Warning);
            }
        }
        catch (Exception ex)
        {
            MessageBox.Show($"Failed to sync time: {ex.Message}", "Error", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private async Task LoadCameraTimeAsync()
    {
        if (_client?.Rpc == null) return;

        try
        {
            var time = await _client.Rpc.GetCurrentTimeAsync();
            if (time.HasValue)
            {
                CameraTimeText.Text = time.Value.ToString("yyyy-MM-dd HH:mm:ss");
            }
            else
            {
                CameraTimeText.Text = "Failed to load";
            }
        }
        catch (Exception ex)
        {
            CameraTimeText.Text = $"Error: {ex.Message}";
        }
    }

    private async void RefreshStorageInfo_Click(object sender, RoutedEventArgs e)
    {
        await LoadStorageInfoAsync();
    }

    private async Task LoadStorageInfoAsync()
    {
        if (_client?.Rpc == null) return;

        try
        {
            var names = await _client.Rpc.GetStorageNamesAsync();
            if (names != null && names.Length > 0)
            {
                var info = await _client.Rpc.GetStorageInfoAsync(names[0]);
                if (info != null)
                {
                    var totalGB = info.TotalBytes / 1024.0 / 1024.0 / 1024.0;
                    var usedGB = info.UsedBytes / 1024.0 / 1024.0 / 1024.0;
                    var freeGB = info.FreeBytes / 1024.0 / 1024.0 / 1024.0;
                    var usedPercent = info.TotalBytes > 0 ? (double)info.UsedBytes / info.TotalBytes * 100 : 0;

                    StorageInfoText.Text = $"Device: {info.Name}\n" +
                        $"Type: {info.Type}\n" +
                        $"State: {info.State}\n" +
                        $"Total: {totalGB:F1} GB\n" +
                        $"Used: {usedGB:F1} GB\n" +
                        $"Free: {freeGB:F1} GB";

                    StorageProgress.Value = usedPercent;
                    StorageUsageText.Text = $"{usedPercent:F1}% used";

                    if (usedPercent > 90)
                        StorageProgress.Foreground = Brushes.Red;
                    else if (usedPercent > 70)
                        StorageProgress.Foreground = Brushes.Orange;
                    else
                        StorageProgress.Foreground = (Brush)Application.Current.Resources["PrimaryBrush"];
                }
                else
                {
                    StorageInfoText.Text = "No storage info available";
                }
            }
            else
            {
                StorageInfoText.Text = "No storage devices found";
            }
        }
        catch (Exception ex)
        {
            StorageInfoText.Text = $"Error: {ex.Message}";
        }
    }

    private async void RefreshUsers_Click(object sender, RoutedEventArgs e)
    {
        await LoadUsersAsync();
    }

    private async Task LoadUsersAsync()
    {
        if (_client?.Rpc == null) return;

        try
        {
            UsersListBox.Items.Clear();
            var users = await _client.Rpc.GetUsersAsync();
            if (users != null)
            {
                foreach (var user in users)
                {
                    var displayText = $"{user.Name} ({user.Group})";
                    if (user.Reserved) displayText += " [Reserved]";
                    UsersListBox.Items.Add(displayText);
                }
            }
            else
            {
                UsersListBox.Items.Add("No users found or API not available");
            }
        }
        catch (Exception ex)
        {
            UsersListBox.Items.Add($"Error: {ex.Message}");
        }
    }

    private async void RefreshEventCaps_Click(object sender, RoutedEventArgs e)
    {
        await LoadEventCapsAsync();
    }

    private async Task LoadEventCapsAsync()
    {
        if (_client?.Rpc == null) return;

        try
        {
            var caps = await _client.Rpc.GetEventCapsAsync();
            if (caps != null && caps.Length > 0)
            {
                EventCapsText.Text = "Supported events:\n" + string.Join("\n", caps.Select(c => $"• {c}"));
            }
            else
            {
                EventCapsText.Text = "No event capabilities found or API not available";
            }
        }
        catch (Exception ex)
        {
            EventCapsText.Text = $"Error: {ex.Message}";
        }
    }

    private async void RefreshServices_Click(object sender, RoutedEventArgs e)
    {
        await LoadServicesAsync();
    }

    private async Task LoadServicesAsync()
    {
        if (_client?.Rpc == null) return;

        try
        {
            ServicesListBox.Items.Clear();
            var services = await _client.Rpc.ListServicesAsync();
            if (services != null)
            {
                foreach (var service in services.OrderBy(s => s))
                {
                    ServicesListBox.Items.Add(service);
                }
            }
            else
            {
                ServicesListBox.Items.Add("No services found or API not available");
            }
        }
        catch (Exception ex)
        {
            ServicesListBox.Items.Add($"Error: {ex.Message}");
        }
    }

    private async Task LoadAllCameraInfoAsync()
    {
        // Load all camera info in parallel
        await Task.WhenAll(
            LoadCameraTimeAsync(),
            LoadStorageInfoAsync(),
            LoadUsersAsync(),
            LoadEventCapsAsync(),
            LoadServicesAsync()
        );
    }

    #endregion

    #region Advanced Tab - PTZ Tours

    private async void RefreshTours_Click(object sender, RoutedEventArgs e)
    {
        if (_client?.Rpc == null) return;

        try
        {
            var tours = await _client.Rpc.GetPtzToursAsync();
            TourCombo.Items.Clear();
            if (tours != null)
            {
                foreach (var tour in tours)
                {
                    TourCombo.Items.Add(tour);
                }
                if (tours.Length > 0)
                    TourCombo.SelectedIndex = 0;
            }
        }
        catch (Exception ex)
        {
            TourStatusText.Text = $"Error: {ex.Message}";
        }
    }

    private async void StartTour_Click(object sender, RoutedEventArgs e)
    {
        if (_client?.Rpc == null || TourCombo.SelectedItem == null) return;

        try
        {
            var tour = TourCombo.SelectedItem as PtzTour;
            if (tour != null)
            {
                var success = await _client.Rpc.StartPtzTourAsync(tour.Id);
                TourStatusText.Text = success ? $"Tour '{tour.Name}' running" : "Failed to start tour";
                TourStatusText.Foreground = success ? Brushes.LimeGreen : Brushes.Orange;
            }
        }
        catch (Exception ex)
        {
            TourStatusText.Text = $"Error: {ex.Message}";
            TourStatusText.Foreground = Brushes.Red;
        }
    }

    private async void StopTour_Click(object sender, RoutedEventArgs e)
    {
        if (_client?.Rpc == null) return;

        try
        {
            var success = await _client.Rpc.StopPtzTourAsync();
            TourStatusText.Text = success ? "Tour stopped" : "Failed to stop tour";
            TourStatusText.Foreground = success ? Brushes.Gray : Brushes.Orange;
        }
        catch (Exception ex)
        {
            TourStatusText.Text = $"Error: {ex.Message}";
        }
    }

    #endregion

    #region Advanced Tab - PTZ Patterns

    private int GetSelectedPatternId()
    {
        if (PatternCombo.SelectedItem is ComboBoxItem item && item.Tag != null)
            return int.Parse(item.Tag.ToString()!);
        return 1;
    }

    private async void StartPatternRecord_Click(object sender, RoutedEventArgs e)
    {
        if (_client?.Rpc == null) return;

        try
        {
            var patternId = GetSelectedPatternId();
            var success = await _client.Rpc.StartPatternRecordAsync(patternId);
            PatternStatusText.Text = success ? $"Recording pattern {patternId}..." : "Failed to start recording";
            PatternStatusText.Foreground = success ? Brushes.Orange : Brushes.Red;
        }
        catch (Exception ex)
        {
            PatternStatusText.Text = $"Error: {ex.Message}";
        }
    }

    private async void StopPatternRecord_Click(object sender, RoutedEventArgs e)
    {
        if (_client?.Rpc == null) return;

        try
        {
            var success = await _client.Rpc.StopPatternRecordAsync();
            PatternStatusText.Text = success ? "Pattern saved" : "Failed to stop recording";
            PatternStatusText.Foreground = Brushes.Gray;
        }
        catch (Exception ex)
        {
            PatternStatusText.Text = $"Error: {ex.Message}";
        }
    }

    private async void StartPatternReplay_Click(object sender, RoutedEventArgs e)
    {
        if (_client?.Rpc == null) return;

        try
        {
            var patternId = GetSelectedPatternId();
            var success = await _client.Rpc.StartPatternReplayAsync(patternId);
            PatternStatusText.Text = success ? $"Playing pattern {patternId}..." : "Failed to start playback";
            PatternStatusText.Foreground = success ? Brushes.LimeGreen : Brushes.Red;
        }
        catch (Exception ex)
        {
            PatternStatusText.Text = $"Error: {ex.Message}";
        }
    }

    private async void StopPatternReplay_Click(object sender, RoutedEventArgs e)
    {
        if (_client?.Rpc == null) return;

        try
        {
            var success = await _client.Rpc.StopPatternReplayAsync();
            PatternStatusText.Text = success ? "Pattern stopped" : "Failed to stop";
            PatternStatusText.Foreground = Brushes.Gray;
        }
        catch (Exception ex)
        {
            PatternStatusText.Text = $"Error: {ex.Message}";
        }
    }

    #endregion

    #region Advanced Tab - Auto Scan

    private async void SetScanLeftLimit_Click(object sender, RoutedEventArgs e)
    {
        if (_client?.Rpc == null) return;

        try
        {
            await _client.Rpc.SetPtzScanLimitAsync("Left");
            MessageBox.Show("Left scan limit set to current position.", "Success", MessageBoxButton.OK, MessageBoxImage.Information);
        }
        catch (Exception ex)
        {
            MessageBox.Show($"Failed to set limit: {ex.Message}", "Error", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private async void SetScanRightLimit_Click(object sender, RoutedEventArgs e)
    {
        if (_client?.Rpc == null) return;

        try
        {
            await _client.Rpc.SetPtzScanLimitAsync("Right");
            MessageBox.Show("Right scan limit set to current position.", "Success", MessageBoxButton.OK, MessageBoxImage.Information);
        }
        catch (Exception ex)
        {
            MessageBox.Show($"Failed to set limit: {ex.Message}", "Error", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private async void StartScan_Click(object sender, RoutedEventArgs e)
    {
        if (_client?.Rpc == null) return;

        try
        {
            await _client.Rpc.StartPtzScanAsync();
        }
        catch (Exception ex)
        {
            MessageBox.Show($"Failed to start scan: {ex.Message}", "Error", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private async void StopScan_Click(object sender, RoutedEventArgs e)
    {
        if (_client?.Rpc == null) return;

        try
        {
            await _client.Rpc.StopPtzScanAsync();
        }
        catch (Exception ex)
        {
            MessageBox.Show($"Failed to stop scan: {ex.Message}", "Error", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    #endregion

    #region Advanced Tab - Wiper Control

    private async void WiperOnce_Click(object sender, RoutedEventArgs e)
    {
        if (_client?.Rpc == null) return;

        try
        {
            await _client.Rpc.WiperOnceAsync();
        }
        catch (Exception ex)
        {
            MessageBox.Show($"Wiper error: {ex.Message}", "Error", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private async void WiperStart_Click(object sender, RoutedEventArgs e)
    {
        if (_client?.Rpc == null) return;

        try
        {
            await _client.Rpc.WiperStartAsync();
        }
        catch (Exception ex)
        {
            MessageBox.Show($"Wiper error: {ex.Message}", "Error", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private async void WiperStop_Click(object sender, RoutedEventArgs e)
    {
        if (_client?.Rpc == null) return;

        try
        {
            await _client.Rpc.WiperStopAsync();
        }
        catch (Exception ex)
        {
            MessageBox.Show($"Wiper error: {ex.Message}", "Error", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    #endregion

    #region Advanced Tab - Two-Way Audio

    private async void StartTalk_Click(object sender, RoutedEventArgs e)
    {
        if (_client?.Rpc == null) return;

        try
        {
            var success = await _client.Rpc.StartSpeakerAsync();
            if (success)
            {
                TalkButton.Content = "Talking...";
                TalkStatusText.Text = "Two-way audio active";
                TalkStatusText.Foreground = Brushes.LimeGreen;
            }
            else
            {
                TalkStatusText.Text = "Failed to start talk";
                TalkStatusText.Foreground = Brushes.Orange;
            }
        }
        catch (Exception ex)
        {
            TalkStatusText.Text = $"Error: {ex.Message}";
            TalkStatusText.Foreground = Brushes.Red;
        }
    }

    private async void StopTalk_Click(object sender, RoutedEventArgs e)
    {
        if (_client?.Rpc == null) return;

        try
        {
            await _client.Rpc.StopSpeakerAsync();
            TalkButton.Content = "Start Talk";
            TalkStatusText.Text = "Two-way audio inactive";
            TalkStatusText.Foreground = Brushes.Gray;
        }
        catch (Exception ex)
        {
            TalkStatusText.Text = $"Error: {ex.Message}";
        }
    }

    #endregion

    #region Advanced Tab - Privacy Masks

    private PrivacyMaskConfig? _currentPrivacyMasks;

    private async void LoadPrivacyMasks_Click(object sender, RoutedEventArgs e)
    {
        if (_client?.Rpc == null) return;

        try
        {
            _currentPrivacyMasks = await _client.Rpc.GetPrivacyMasksAsync();
            PrivacyMasksListBox.Items.Clear();

            if (_currentPrivacyMasks?.Covers != null)
            {
                for (int i = 0; i < _currentPrivacyMasks.Covers.Length; i++)
                {
                    var cover = _currentPrivacyMasks.Covers[i];
                    var status = cover.Enable ? "Enabled" : "Disabled";
                    PrivacyMasksListBox.Items.Add($"Mask {i + 1}: {status}");
                }
            }
            else
            {
                PrivacyMasksListBox.Items.Add("No privacy masks configured");
            }
        }
        catch (Exception ex)
        {
            PrivacyMasksListBox.Items.Clear();
            PrivacyMasksListBox.Items.Add($"Error: {ex.Message}");
        }
    }

    private async void TogglePrivacyMask_Click(object sender, RoutedEventArgs e)
    {
        if (_client?.Rpc == null || _currentPrivacyMasks?.Covers == null) return;

        var selectedIndex = PrivacyMasksListBox.SelectedIndex;
        if (selectedIndex < 0 || selectedIndex >= _currentPrivacyMasks.Covers.Length) return;

        try
        {
            _currentPrivacyMasks.Covers[selectedIndex].Enable = !_currentPrivacyMasks.Covers[selectedIndex].Enable;
            var success = await _client.Rpc.SetPrivacyMasksAsync(_currentPrivacyMasks);

            if (success)
            {
                LoadPrivacyMasks_Click(sender, e); // Refresh
            }
            else
            {
                MessageBox.Show("Failed to toggle privacy mask.", "Error", MessageBoxButton.OK, MessageBoxImage.Warning);
            }
        }
        catch (Exception ex)
        {
            MessageBox.Show($"Error: {ex.Message}", "Error", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    #endregion

    #region Advanced Tab - Alarms

    private async void RefreshAlarms_Click(object sender, RoutedEventArgs e)
    {
        if (_client?.Rpc == null) return;

        try
        {
            var slots = await _client.Rpc.GetAlarmSlotsAsync();
            if (slots != null)
            {
                AlarmInputsText.Text = $"{slots.InputCount} inputs";
                AlarmOutputsText.Text = $"{slots.OutputCount} outputs";
            }

            var inputs = await _client.Rpc.GetAlarmInputStatesAsync();
            if (inputs != null)
            {
                var activeCount = inputs.Count(i => i.State == 1);
                AlarmInputsText.Text = $"{inputs.Length} inputs ({activeCount} active)";
            }

            var outputs = await _client.Rpc.GetAlarmOutputStatesAsync();
            if (outputs != null)
            {
                var activeCount = outputs.Count(o => o.State == 1);
                AlarmOutputsText.Text = $"{outputs.Length} outputs ({activeCount} active)";
            }
        }
        catch (Exception ex)
        {
            AlarmInputsText.Text = $"Error: {ex.Message}";
        }
    }

    private async void TriggerAlarmOutput_Click(object sender, RoutedEventArgs e)
    {
        if (_client?.Rpc == null) return;

        try
        {
            await _client.Rpc.SetAlarmOutputAsync(0, true);
            RefreshAlarms_Click(sender, e);
        }
        catch (Exception ex)
        {
            MessageBox.Show($"Error: {ex.Message}", "Error", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private async void ClearAlarmOutput_Click(object sender, RoutedEventArgs e)
    {
        if (_client?.Rpc == null) return;

        try
        {
            await _client.Rpc.SetAlarmOutputAsync(0, false);
            RefreshAlarms_Click(sender, e);
        }
        catch (Exception ex)
        {
            MessageBox.Show($"Error: {ex.Message}", "Error", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    #endregion

    #region Network Tab

    private async void RefreshNetworkInfo_Click(object sender, RoutedEventArgs e)
    {
        if (_client?.Rpc == null) return;

        try
        {
            var config = await _client.Rpc.GetNetworkConfigAsync();
            if (config != null)
            {
                HostnameText.Text = config.Hostname ?? "--";
                InterfaceText.Text = config.DefaultInterface ?? "--";
            }

            var interfaces = await _client.Rpc.GetNetworkInterfacesAsync();
            if (interfaces != null && interfaces.Length > 0)
            {
                var iface = interfaces[0];
                MacAddressText.Text = iface.MacAddress ?? "--";
                InterfaceText.Text = $"{iface.Name} ({iface.IPAddress})";
            }
        }
        catch (Exception ex)
        {
            HostnameText.Text = $"Error: {ex.Message}";
        }
    }

    private async void LoadNtpSettings_Click(object sender, RoutedEventArgs e)
    {
        if (_client?.Rpc == null) return;

        try
        {
            var ntp = await _client.Rpc.GetNtpConfigAsync();
            if (ntp != null)
            {
                NtpEnabledCheck.IsChecked = ntp.Enable;
                NtpServerText.Text = ntp.Address ?? "pool.ntp.org";
            }
        }
        catch (Exception ex)
        {
            MessageBox.Show($"Failed to load NTP settings: {ex.Message}", "Error", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private async void ApplyNtpSettings_Click(object sender, RoutedEventArgs e)
    {
        if (_client?.Rpc == null) return;

        try
        {
            var config = new NtpConfig
            {
                Enable = NtpEnabledCheck.IsChecked ?? false,
                Address = NtpServerText.Text,
                Port = 123,
                UpdatePeriod = 60
            };

            var success = await _client.Rpc.SetNtpConfigAsync(config);
            MessageBox.Show(success ? "NTP settings applied." : "Failed to apply NTP settings.",
                success ? "Success" : "Error", MessageBoxButton.OK,
                success ? MessageBoxImage.Information : MessageBoxImage.Warning);
        }
        catch (Exception ex)
        {
            MessageBox.Show($"Error: {ex.Message}", "Error", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private async void LoadEmailSettings_Click(object sender, RoutedEventArgs e)
    {
        if (_client?.Rpc == null) return;

        try
        {
            var email = await _client.Rpc.GetEmailConfigAsync();
            if (email != null)
            {
                EmailEnabledCheck.IsChecked = email.Enable;
                EmailServerText.Text = email.Address ?? "";
                EmailPortText.Text = email.Port.ToString();
                EmailUserText.Text = email.UserName ?? "";
                EmailReceiverText.Text = email.Receivers != null && email.Receivers.Length > 0 ? email.Receivers[0] : "";
            }
        }
        catch (Exception ex)
        {
            MessageBox.Show($"Failed to load email settings: {ex.Message}", "Error", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private async void ApplyEmailSettings_Click(object sender, RoutedEventArgs e)
    {
        if (_client?.Rpc == null) return;

        try
        {
            var config = new EmailConfig
            {
                Enable = EmailEnabledCheck.IsChecked ?? false,
                Address = EmailServerText.Text,
                Port = int.TryParse(EmailPortText.Text, out var port) ? port : 587,
                UserName = EmailUserText.Text,
                Receivers = new[] { EmailReceiverText.Text }
            };

            var success = await _client.Rpc.SetEmailConfigAsync(config);
            MessageBox.Show(success ? "Email settings applied." : "Failed to apply email settings.",
                success ? "Success" : "Error", MessageBoxButton.OK,
                success ? MessageBoxImage.Information : MessageBoxImage.Warning);
        }
        catch (Exception ex)
        {
            MessageBox.Show($"Error: {ex.Message}", "Error", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private async void ScanWifi_Click(object sender, RoutedEventArgs e)
    {
        if (_client?.Rpc == null) return;

        try
        {
            WifiListBox.Items.Clear();
            WifiListBox.Items.Add("Scanning...");

            var networks = await _client.Rpc.ScanWifiNetworksAsync();
            WifiListBox.Items.Clear();

            if (networks != null && networks.Length > 0)
            {
                foreach (var network in networks.OrderByDescending(n => n.Signal))
                {
                    WifiListBox.Items.Add($"{network.SSID} (Signal: {network.Signal}%, {network.Security})");
                }
            }
            else
            {
                WifiListBox.Items.Add("No WiFi networks found");
            }
        }
        catch (Exception ex)
        {
            WifiListBox.Items.Clear();
            WifiListBox.Items.Add($"Error: {ex.Message}");
        }
    }

    #endregion

    #region Firmware Tab

    private async void RefreshFirmwareInfo_Click(object sender, RoutedEventArgs e)
    {
        if (_client?.Rpc == null) return;

        try
        {
            var info = await _client.Rpc.GetFirmwareInfoAsync();
            if (info != null)
            {
                FirmwareVersionText.Text = info.Version ?? "--";
                FirmwareBuildDateText.Text = info.BuildDate ?? "--";
                FirmwareWebVersionText.Text = info.WebVersion ?? "--";
            }
        }
        catch (Exception ex)
        {
            FirmwareVersionText.Text = $"Error: {ex.Message}";
        }
    }

    private async void LoadMaintenanceSettings_Click(object sender, RoutedEventArgs e)
    {
        if (_client?.Rpc == null) return;

        try
        {
            var config = await _client.Rpc.GetAutoMaintenanceAsync();
            if (config != null)
            {
                AutoRebootCheck.IsChecked = config.Enable;
                RebootTimeText.Text = config.RebootTime ?? "03:00";

                // Select day in combo
                foreach (ComboBoxItem item in RebootDayCombo.Items)
                {
                    if (item.Content?.ToString() == config.RebootDay)
                    {
                        RebootDayCombo.SelectedItem = item;
                        break;
                    }
                }
            }
        }
        catch (Exception ex)
        {
            MessageBox.Show($"Failed to load maintenance settings: {ex.Message}", "Error", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private async void ApplyMaintenanceSettings_Click(object sender, RoutedEventArgs e)
    {
        if (_client?.Rpc == null) return;

        try
        {
            var selectedDay = (RebootDayCombo.SelectedItem as ComboBoxItem)?.Content?.ToString() ?? "Everyday";

            var config = new AutoMaintenanceConfig
            {
                Enable = AutoRebootCheck.IsChecked ?? false,
                RebootDay = selectedDay,
                RebootTime = RebootTimeText.Text
            };

            var success = await _client.Rpc.SetAutoMaintenanceAsync(config);
            MessageBox.Show(success ? "Maintenance settings applied." : "Failed to apply settings.",
                success ? "Success" : "Error", MessageBoxButton.OK,
                success ? MessageBoxImage.Information : MessageBoxImage.Warning);
        }
        catch (Exception ex)
        {
            MessageBox.Show($"Error: {ex.Message}", "Error", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private async void RebootCamera_Click(object sender, RoutedEventArgs e)
    {
        var result = MessageBox.Show("Are you sure you want to reboot the camera?\n\nThe connection will be lost.",
            "Confirm Reboot", MessageBoxButton.YesNo, MessageBoxImage.Warning);

        if (result != MessageBoxResult.Yes) return;

        if (_deviceManager == null) return;

        try
        {
            await _deviceManager.RebootAsync();
            MessageBox.Show("Reboot command sent. The camera will restart.", "Rebooting", MessageBoxButton.OK, MessageBoxImage.Information);
            Disconnect();
        }
        catch (Exception ex)
        {
            MessageBox.Show($"Failed to reboot: {ex.Message}", "Error", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private async void FactoryResetSoft_Click(object sender, RoutedEventArgs e)
    {
        var result = MessageBox.Show("Soft factory reset will restore settings but keep network configuration.\n\nAre you sure?",
            "Confirm Soft Reset", MessageBoxButton.YesNo, MessageBoxImage.Warning);

        if (result != MessageBoxResult.Yes) return;

        if (_deviceManager == null) return;

        try
        {
            await _deviceManager.FactoryResetAsync(false);
            MessageBox.Show("Soft reset initiated. The camera will restart.", "Resetting", MessageBoxButton.OK, MessageBoxImage.Information);
            Disconnect();
        }
        catch (Exception ex)
        {
            MessageBox.Show($"Failed to reset: {ex.Message}", "Error", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private async void FactoryResetHard_Click(object sender, RoutedEventArgs e)
    {
        var result = MessageBox.Show("HARD FACTORY RESET will erase ALL settings including network configuration!\n\n" +
            "You may need to find the camera on the network again after reset.\n\n" +
            "Are you ABSOLUTELY sure?",
            "Confirm HARD Reset", MessageBoxButton.YesNo, MessageBoxImage.Exclamation);

        if (result != MessageBoxResult.Yes) return;

        // Double confirmation
        result = MessageBox.Show("This is your LAST chance to cancel.\n\nProceed with hard factory reset?",
            "Final Confirmation", MessageBoxButton.YesNo, MessageBoxImage.Stop);

        if (result != MessageBoxResult.Yes) return;

        if (_deviceManager == null) return;

        try
        {
            await _deviceManager.FactoryResetAsync(true);
            MessageBox.Show("Hard reset initiated. The camera will restart with factory defaults.", "Resetting", MessageBoxButton.OK, MessageBoxImage.Information);
            Disconnect();
        }
        catch (Exception ex)
        {
            MessageBox.Show($"Failed to reset: {ex.Message}", "Error", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    #endregion

    protected override void OnClosed(EventArgs e)
    {
        Disconnect();
        base.OnClosed(e);
    }
}
