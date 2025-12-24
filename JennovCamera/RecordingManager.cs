using OpenCvSharp;
using System.Diagnostics;

namespace JennovCamera;

public class RecordingManager : IDisposable
{
    private readonly CameraClient _client;
    private bool _isRecording;
    private bool _isStreaming;
    private VideoCapture? _videoCapture;
    private Thread? _captureThread;
    private CancellationTokenSource? _cancellationTokenSource;
    private string _rtspUrl;
    private StreamQuality _currentQuality = StreamQuality.Main;

    // FFmpeg recording
    private Process? _ffmpegProcess;
    private Thread? _ffmpegMonitorThread;
    private Thread? _ffmpegStderrThread;
    private Thread? _ffmpegPreviewThread;
    private string? _currentRecordingPath;
    private long _lastFileSize;
    private DateTime _lastFileSizeCheck;
    private int _stagnantFileSizeCount;
    private bool _ffmpegHasError;
    private string? _ffmpegLastError;
    private bool _useBufferedRecording = false;  // Disabled - Windows pipe issues
    private int _previewWidth = 960;   // Preview resolution (scaled down for performance)
    private int _previewHeight = 540;

    // Segmented recording settings
    private string _recordingFolder = Environment.GetFolderPath(Environment.SpecialFolder.MyVideos);
    private TimeSpan _segmentDuration = TimeSpan.FromMinutes(30);
    private TimeSpan _overlapDuration = TimeSpan.FromSeconds(10);
    private DateTime _segmentStartTime;
    private DateTime _recordingStartTime;
    private int _segmentNumber;

    // Quality settings - Default to Maximum for best quality
    private RecordingQuality _recordingQuality = RecordingQuality.Maximum;

    // Frame info
    private double _fps = 20;
    private int _frameWidth;
    private int _frameHeight;

    // Events
    public event EventHandler<Mat>? FrameCaptured;
    public event EventHandler<string>? SegmentCreated;
    public event EventHandler<RecordingStatus>? StatusChanged;
    public event EventHandler<string>? StreamStatusChanged;

    public bool IsRecording => _isRecording;
    public bool IsStreaming => _isStreaming;
    public StreamQuality CurrentQuality => _currentQuality;
    public string CurrentRtspUrl => _rtspUrl;
    public string RecordingFolder
    {
        get => _recordingFolder;
        set => _recordingFolder = value;
    }
    public TimeSpan SegmentDuration
    {
        get => _segmentDuration;
        set => _segmentDuration = value;
    }
    public TimeSpan OverlapDuration
    {
        get => _overlapDuration;
        set => _overlapDuration = value;
    }
    public int CurrentSegmentNumber => _segmentNumber;
    public TimeSpan CurrentSegmentElapsed => _isRecording ? DateTime.Now - _segmentStartTime : TimeSpan.Zero;
    public bool HasRecordingError => _ffmpegHasError;
    public string? RecordingErrorMessage => _ffmpegLastError;

    /// <summary>
    /// Set recording quality (affects audio encoding)
    /// </summary>
    public RecordingQuality Quality
    {
        get => _recordingQuality;
        set => _recordingQuality = value;
    }

    /// <summary>
    /// Get FFmpeg audio encoding settings based on quality preset
    /// </summary>
    private static string GetAudioSettings(RecordingQuality quality)
    {
        // Keep audio processing simple to maintain A/V sync
        // Complex filters like loudnorm cause latency and desync
        return quality switch
        {
            RecordingQuality.Standard =>
                "-c:a aac -b:a 192k",

            RecordingQuality.High =>
                "-c:a aac -b:a 256k",

            RecordingQuality.Maximum =>
                "-c:a aac -b:a 320k",

            RecordingQuality.Lossless =>
                "-c:a aac -b:a 320k",

            _ => "-c:a aac -b:a 320k"
        };
    }

    /// <summary>
    /// Get description of quality settings
    /// </summary>
    public static string GetQualityDescription(RecordingQuality quality)
    {
        return quality switch
        {
            RecordingQuality.Standard => "192kbps AAC @ 44.1kHz (smaller files)",
            RecordingQuality.High => "320kbps AAC @ 48kHz (recommended)",
            RecordingQuality.Maximum => "320kbps AAC @ 48kHz High Profile (best quality)",
            RecordingQuality.Lossless => "320kbps AAC @ 48kHz High Profile (best quality)",
            _ => "Unknown"
        };
    }

    /// <summary>
    /// Get current recording status with file size info
    /// </summary>
    public RecordingStatus GetRecordingStatus()
    {
        var status = new RecordingStatus
        {
            IsRecording = _isRecording,
            SegmentNumber = _segmentNumber,
            SegmentPath = _currentRecordingPath,
            SegmentStartTime = _segmentStartTime,
            HasError = _ffmpegHasError,
            ErrorMessage = _ffmpegLastError
        };

        if (_isRecording && !string.IsNullOrEmpty(_currentRecordingPath) && File.Exists(_currentRecordingPath))
        {
            try
            {
                var fileInfo = new FileInfo(_currentRecordingPath);
                status.FileSize = fileInfo.Length;
                var elapsed = DateTime.Now - _segmentStartTime;
                if (elapsed.TotalSeconds > 0)
                {
                    status.BitrateMbps = (fileInfo.Length * 8.0 / elapsed.TotalSeconds) / 1_000_000.0;
                }
            }
            catch { }
        }

        // Check disk space
        status.AvailableDiskSpace = GetAvailableDiskSpace(_recordingFolder);
        // Low disk space warning at 2 GB
        status.LowDiskSpace = status.AvailableDiskSpace >= 0 && status.AvailableDiskSpace < 2L * 1024 * 1024 * 1024;

        return status;
    }

    public RecordingManager(CameraClient client, StreamQuality quality = StreamQuality.Main)
    {
        _client = client;
        _currentQuality = quality;
        _rtspUrl = client.Onvif.GetRtspUrl(quality);
        Console.WriteLine($"RecordingManager initialized with RTSP URL: {_rtspUrl}");
    }

    /// <summary>
    /// Try to find the best RTSP URL by testing multiple patterns
    /// </summary>
    public string? AutoDetectBestRtspUrl()
    {
        var urls = _client.Onvif.GetAlternativeRtspUrls(_currentQuality);
        string? bestUrl = null;
        int bestResolution = 0;

        Console.WriteLine("Auto-detecting best RTSP URL...");

        foreach (var url in urls)
        {
            Console.WriteLine($"  Trying: {MaskCredentials(url)}");

            try
            {
                Environment.SetEnvironmentVariable("OPENCV_FFMPEG_CAPTURE_OPTIONS",
                    "rtsp_transport;tcp|timeout;5000000");

                using var capture = new VideoCapture(url, VideoCaptureAPIs.FFMPEG);
                if (capture.IsOpened())
                {
                    using var frame = new Mat();
                    if (capture.Read(frame) && !frame.Empty())
                    {
                        var resolution = frame.Width * frame.Height;
                        Console.WriteLine($"    -> Success! Resolution: {frame.Width}x{frame.Height}");

                        if (resolution > bestResolution)
                        {
                            bestResolution = resolution;
                            bestUrl = url;
                        }
                    }
                    else
                    {
                        var w = (int)capture.Get(VideoCaptureProperties.FrameWidth);
                        var h = (int)capture.Get(VideoCaptureProperties.FrameHeight);
                        if (w > 0 && h > 0)
                        {
                            var resolution = w * h;
                            Console.WriteLine($"    -> Opened, reported: {w}x{h}");
                            if (resolution > bestResolution)
                            {
                                bestResolution = resolution;
                                bestUrl = url;
                            }
                        }
                    }
                }
                else
                {
                    Console.WriteLine($"    -> Failed to open");
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"    -> Error: {ex.Message}");
            }
        }

        if (bestUrl != null)
        {
            Console.WriteLine($"Best URL found: {MaskCredentials(bestUrl)} ({bestResolution} pixels)");
            _rtspUrl = bestUrl;
        }

        return bestUrl;
    }

    private string MaskCredentials(string url)
    {
        try
        {
            var uri = new Uri(url);
            if (!string.IsNullOrEmpty(uri.UserInfo))
            {
                return url.Replace(uri.UserInfo, "***:***");
            }
        }
        catch { }
        return url;
    }

    public void SetStreamQuality(StreamQuality quality)
    {
        if (_isStreaming)
        {
            Console.WriteLine("Cannot change stream quality while streaming. Stop streaming first.");
            return;
        }
        _currentQuality = quality;
        _rtspUrl = _client.Onvif.GetRtspUrl(quality);
        Console.WriteLine($"Stream quality set to: {quality} ({(_currentQuality == StreamQuality.Main ? "4K" : "SD")})");
    }

    public (StreamInfo main, StreamInfo sub) GetAvailableStreams()
    {
        return (_client.Onvif.GetMainStreamInfo(), _client.Onvif.GetSubStreamInfo());
    }

    public void SetRtspUrl(string url)
    {
        _rtspUrl = url;
    }

    public async Task<bool> TakeSnapshotAsync(string? filePath = null)
    {
        try
        {
            var imageData = await _client.Onvif.GetSnapshotAsync();
            if (imageData == null || imageData.Length == 0)
            {
                Console.WriteLine("Failed to capture snapshot from camera");
                return false;
            }

            if (string.IsNullOrEmpty(filePath))
            {
                var timestamp = DateTime.Now.ToString("yyyyMMdd_HHmmss");
                filePath = Path.Combine(_recordingFolder, $"snapshot_{timestamp}.jpg");
            }

            await File.WriteAllBytesAsync(filePath, imageData);
            Console.WriteLine($"Snapshot saved: {filePath}");
            return true;
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Snapshot failed: {ex.Message}");
            return false;
        }
    }

    public bool StartStreaming()
    {
        if (_isStreaming)
        {
            Console.WriteLine("Already streaming");
            return false;
        }

        try
        {
            Environment.SetEnvironmentVariable("OPENCV_FFMPEG_CAPTURE_OPTIONS",
                "rtsp_transport;tcp|buffer_size;8388608|max_delay;500000|analyzeduration;10000000|probesize;10000000|fflags;nobuffer");

            _videoCapture = new VideoCapture(_rtspUrl, VideoCaptureAPIs.FFMPEG);

            if (!_videoCapture.IsOpened())
            {
                Console.WriteLine($"Failed to open RTSP stream: {_rtspUrl}");
                return false;
            }

            _videoCapture.Set(VideoCaptureProperties.BufferSize, 10);

            if (_currentQuality == StreamQuality.Main)
            {
                _videoCapture.Set(VideoCaptureProperties.FrameWidth, 3840);
                _videoCapture.Set(VideoCaptureProperties.FrameHeight, 2160);
                _videoCapture.Set(VideoCaptureProperties.Fps, 20);
            }

            _frameWidth = (int)_videoCapture.Get(VideoCaptureProperties.FrameWidth);
            _frameHeight = (int)_videoCapture.Get(VideoCaptureProperties.FrameHeight);
            _fps = _videoCapture.Get(VideoCaptureProperties.Fps);
            if (_fps <= 0) _fps = 20;

            Console.WriteLine($"Capture initialized: actual={_frameWidth}x{_frameHeight} @ {_fps}fps");

            _isStreaming = true;
            _cancellationTokenSource = new CancellationTokenSource();
            _captureThread = new Thread(() => CaptureLoop(_cancellationTokenSource.Token));
            _captureThread.Start();

            Console.WriteLine($"Started streaming: {MaskCredentials(_rtspUrl)}");
            return true;
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Failed to start streaming: {ex.Message}");
            return false;
        }
    }

    public void StopStreaming()
    {
        if (!_isStreaming)
            return;

        _cancellationTokenSource?.Cancel();
        _captureThread?.Join(TimeSpan.FromSeconds(5));

        _videoCapture?.Release();
        _videoCapture?.Dispose();
        _videoCapture = null;

        _isStreaming = false;
        Console.WriteLine("Stopped streaming");
    }

    // Known FFmpeg installation paths to check
    private static readonly string[] FFmpegSearchPaths = new[]
    {
        "ffmpeg", // In PATH
        @"C:\ffmpeg\ffmpeg-8.0.1-essentials_build\bin\ffmpeg.exe",
        @"C:\ffmpeg\bin\ffmpeg.exe",
        @"C:\Program Files\ffmpeg\bin\ffmpeg.exe",
        @"C:\Program Files (x86)\ffmpeg\bin\ffmpeg.exe",
    };

    private static string? _ffmpegPath;
    private static string? _customFfmpegPath;

    /// <summary>
    /// Set a custom FFmpeg path (takes priority over auto-detection)
    /// </summary>
    public static void SetCustomFFmpegPath(string? path)
    {
        _customFfmpegPath = path;
        _ffmpegPath = null; // Reset cached path to force re-detection
    }

    /// <summary>
    /// Validate that an FFmpeg path is executable
    /// </summary>
    private static bool ValidateFFmpegPath(string path)
    {
        try
        {
            var process = new Process
            {
                StartInfo = new ProcessStartInfo
                {
                    FileName = path,
                    Arguments = "-version",
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true
                }
            };
            process.Start();
            process.WaitForExit(3000);
            return process.ExitCode == 0;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// Get the current FFmpeg path (after detection)
    /// </summary>
    public static string? GetFFmpegPath() => _ffmpegPath ?? FindFFmpegPath();

    /// <summary>
    /// Find FFmpeg executable path
    /// </summary>
    public static string? FindFFmpegPath()
    {
        if (_ffmpegPath != null)
            return _ffmpegPath;

        // Check custom path first
        if (!string.IsNullOrEmpty(_customFfmpegPath))
        {
            if (ValidateFFmpegPath(_customFfmpegPath))
            {
                _ffmpegPath = _customFfmpegPath;
                Console.WriteLine($"Using custom FFmpeg path: {_customFfmpegPath}");
                return _ffmpegPath;
            }
            Console.WriteLine($"Custom FFmpeg path invalid: {_customFfmpegPath}");
        }

        foreach (var path in FFmpegSearchPaths)
        {
            try
            {
                var process = new Process
                {
                    StartInfo = new ProcessStartInfo
                    {
                        FileName = path,
                        Arguments = "-version",
                        UseShellExecute = false,
                        CreateNoWindow = true,
                        RedirectStandardOutput = true,
                        RedirectStandardError = true
                    }
                };
                process.Start();
                process.WaitForExit(3000);
                if (process.ExitCode == 0)
                {
                    _ffmpegPath = path;
                    Console.WriteLine($"Found FFmpeg at: {path}");
                    return _ffmpegPath;
                }
            }
            catch
            {
                // Try next path
            }
        }

        return null;
    }

    /// <summary>
    /// Check if FFmpeg is available
    /// </summary>
    public static bool IsFFmpegAvailable()
    {
        return FindFFmpegPath() != null;
    }

    /// <summary>
    /// Get the last error message for UI display
    /// </summary>
    public string? LastError { get; private set; }

    /// <summary>
    /// Get available disk space in bytes for the recording folder
    /// </summary>
    public static long GetAvailableDiskSpace(string folder)
    {
        try
        {
            var driveInfo = new DriveInfo(Path.GetPathRoot(folder) ?? "C:");
            return driveInfo.AvailableFreeSpace;
        }
        catch
        {
            return -1; // Unknown
        }
    }

    /// <summary>
    /// Check if there's enough disk space for recording (minimum 5 GB recommended)
    /// </summary>
    public static bool HasSufficientDiskSpace(string folder, long minimumBytes = 5L * 1024 * 1024 * 1024)
    {
        var available = GetAvailableDiskSpace(folder);
        return available < 0 || available >= minimumBytes; // -1 means unknown, allow recording
    }

    /// <summary>
    /// Format bytes as human-readable string
    /// </summary>
    public static string FormatBytes(long bytes)
    {
        if (bytes < 1024) return $"{bytes} B";
        if (bytes < 1024 * 1024) return $"{bytes / 1024.0:F1} KB";
        if (bytes < 1024 * 1024 * 1024) return $"{bytes / 1024.0 / 1024.0:F1} MB";
        return $"{bytes / 1024.0 / 1024.0 / 1024.0:F1} GB";
    }

    /// <summary>
    /// Start recording with FFmpeg (includes audio)
    /// </summary>
    public bool StartRecording(string? baseName = null)
    {
        LastError = null;

        if (_isRecording)
        {
            LastError = "Already recording";
            Console.WriteLine(LastError);
            return false;
        }

        // Check if FFmpeg is available
        if (!IsFFmpegAvailable())
        {
            LastError = "FFmpeg is not installed or not in PATH.\n\nInstall with: winget install ffmpeg\n\nOr download from: https://ffmpeg.org/download.html";
            Console.WriteLine(LastError);
            return false;
        }

        // Stop the preview stream to free up the RTSP connection for FFmpeg
        // Many cameras only support 2-3 concurrent connections
        if (_isStreaming)
        {
            Console.WriteLine("Stopping preview stream to free connection for recording...");
            StopStreaming();
            // Give camera time to fully close the connection
            Console.WriteLine("Waiting for camera to release connection...");
            Thread.Sleep(2000);
        }

        try
        {
            if (!Directory.Exists(_recordingFolder))
            {
                Directory.CreateDirectory(_recordingFolder);
            }

            // Check disk space (minimum 5 GB for 4K recording)
            var availableSpace = GetAvailableDiskSpace(_recordingFolder);
            var minimumSpace = 5L * 1024 * 1024 * 1024; // 5 GB
            if (availableSpace >= 0 && availableSpace < minimumSpace)
            {
                LastError = $"Insufficient disk space.\n\nAvailable: {FormatBytes(availableSpace)}\nRequired: {FormatBytes(minimumSpace)} minimum\n\nFree up space or choose a different recording folder.";
                Console.WriteLine(LastError);
                return false;
            }

            Console.WriteLine($"Disk space available: {FormatBytes(availableSpace)}");

            _segmentNumber = 1;
            _recordingStartTime = DateTime.Now;

            if (!StartNewFFmpegSegment())
            {
                LastError ??= "Failed to start FFmpeg process";
                return false;
            }

            _isRecording = true;
            Console.WriteLine($"Started recording with audio: {_segmentDuration.TotalMinutes}min segments");
            return true;
        }
        catch (Exception ex)
        {
            LastError = $"Failed to start recording: {ex.Message}";
            Console.WriteLine(LastError);
            return false;
        }
    }

    private bool StartNewFFmpegSegment()
    {
        // Stop previous segment if running
        StopFFmpegSegment();

        var ffmpegPath = FindFFmpegPath();
        if (ffmpegPath == null)
        {
            LastError = "FFmpeg not found";
            return false;
        }

        var timestamp = DateTime.Now;
        var segmentName = $"recording_{timestamp:yyyy-MM-dd}_{timestamp:HH-mm-ss}.mp4";
        _currentRecordingPath = Path.Combine(_recordingFolder, segmentName);
        _segmentStartTime = timestamp;

        // Reset monitoring state
        _lastFileSize = 0;
        _lastFileSizeCheck = DateTime.Now;
        _stagnantFileSizeCount = 0;
        _ffmpegHasError = false;
        _ffmpegLastError = null;

        // Build FFmpeg command with robust options:
        // -rtsp_transport tcp: Use TCP for reliable streaming
        // -i: Input RTSP URL
        // -c:v copy: Copy video without re-encoding (fast, preserves quality)
        // -c:a aac: Re-encode audio to AAC (pcm_alaw from camera isn't supported in MP4)
        // -movflags: Fragmented MP4 for robustness (allows playback even if recording interrupted)
        // -t: Duration limit for this segment
        var segmentSeconds = (int)_segmentDuration.TotalSeconds;

        // Get quality-based audio settings
        var audioSettings = GetAudioSettings(_recordingQuality);

        // FFmpeg arguments with dual output:
        // Output 1: Full quality recording to file (video copy, AAC audio)
        // Output 2: Scaled preview frames to stdout (for live preview during recording)
        string ffmpegArgs;

        if (_useBufferedRecording)
        {
            // Dual output: file + preview frames using tee muxer
            // The tee muxer allows multiple outputs from a single encode pass
            ffmpegArgs = $"-hide_banner -loglevel warning " +
                $"-rtsp_transport tcp " +
                $"-i \"{_rtspUrl}\" " +
                // Use filter_complex to split video stream
                $"-filter_complex \"[0:v]split=2[rec][prev];[prev]scale={_previewWidth}:{_previewHeight}[preview]\" " +
                // Output 1: Recording file (re-encoded since we're using filters)
                $"-map \"[rec]\" -map 0:a -c:v libx264 -preset ultrafast -crf 18 {audioSettings} " +
                $"-movflags frag_keyframe+empty_moov " +
                $"-t {segmentSeconds} " +
                $"-y \"{_currentRecordingPath}\" " +
                // Output 2: Preview frames to stdout (scaled down, MJPEG for better pipe handling)
                $"-map \"[preview]\" -c:v mjpeg -q:v 5 -f mjpeg pipe:1";
        }
        else
        {
            // Simple recording only (no preview)
            ffmpegArgs = $"-hide_banner -loglevel warning " +
                $"-rtsp_transport tcp " +
                $"-i \"{_rtspUrl}\" " +
                $"-c:v copy " +
                $"{audioSettings} " +
                $"-movflags frag_keyframe+empty_moov " +
                $"-t {segmentSeconds} " +
                $"-y \"{_currentRecordingPath}\"";
        }

        Console.WriteLine($"Starting FFmpeg segment {_segmentNumber}: {segmentName}");
        Console.WriteLine($"FFmpeg: {ffmpegPath}");
        Console.WriteLine($"Args: {ffmpegArgs.Replace(_rtspUrl, MaskCredentials(_rtspUrl))}");

        try
        {
            _ffmpegProcess = new Process
            {
                StartInfo = new ProcessStartInfo
                {
                    FileName = ffmpegPath,
                    Arguments = ffmpegArgs,
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    RedirectStandardInput = true,  // Need this to send 'q' for graceful stop
                    RedirectStandardOutput = true,
                    RedirectStandardError = true
                },
                EnableRaisingEvents = true
            };

            _ffmpegProcess.Exited += OnFFmpegExited;
            _ffmpegProcess.Start();

            // Start stderr monitoring thread
            _ffmpegStderrThread = new Thread(MonitorFFmpegStderr);
            _ffmpegStderrThread.Start();

            // Start preview frame reader thread if using buffered recording
            if (_useBufferedRecording)
            {
                _ffmpegPreviewThread = new Thread(ReadPreviewFrames);
                _ffmpegPreviewThread.Start();
            }

            // Start monitoring thread for segment timing and file size
            _ffmpegMonitorThread = new Thread(MonitorFFmpegSegment);
            _ffmpegMonitorThread.Start();

            StatusChanged?.Invoke(this, new RecordingStatus
            {
                IsRecording = true,
                SegmentNumber = _segmentNumber,
                SegmentPath = _currentRecordingPath,
                SegmentStartTime = _segmentStartTime
            });

            _segmentNumber++;
            return true;
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Failed to start FFmpeg: {ex.Message}");
            Console.WriteLine("Make sure FFmpeg is installed and in your PATH.");
            LastError = ex.Message;
            return false;
        }
    }

    /// <summary>
    /// Read preview frames from FFmpeg stdout (MJPEG stream)
    /// </summary>
    private void ReadPreviewFrames()
    {
        if (_ffmpegProcess == null) return;

        Console.WriteLine($"Starting MJPEG preview frame reader: {_previewWidth}x{_previewHeight}");

        try
        {
            using var stream = _ffmpegProcess.StandardOutput.BaseStream;
            using var memStream = new MemoryStream();
            byte[] buffer = new byte[65536]; // 64KB read buffer

            // JPEG markers
            const byte SOI_MARKER_1 = 0xFF;
            const byte SOI_MARKER_2 = 0xD8;
            const byte EOI_MARKER_1 = 0xFF;
            const byte EOI_MARKER_2 = 0xD9;

            bool inFrame = false;
            int frameCount = 0;

            while (_isRecording && _ffmpegProcess != null && !_ffmpegProcess.HasExited)
            {
                int bytesRead = stream.Read(buffer, 0, buffer.Length);
                if (bytesRead <= 0)
                {
                    Console.WriteLine("Preview stream ended");
                    break;
                }

                // Process bytes looking for JPEG frames
                for (int i = 0; i < bytesRead; i++)
                {
                    byte b = buffer[i];

                    // Look for Start Of Image (SOI) marker: FF D8
                    if (!inFrame && i < bytesRead - 1 && b == SOI_MARKER_1 && buffer[i + 1] == SOI_MARKER_2)
                    {
                        inFrame = true;
                        memStream.SetLength(0);
                        memStream.WriteByte(b);
                    }
                    else if (inFrame)
                    {
                        memStream.WriteByte(b);

                        // Look for End Of Image (EOI) marker: FF D9
                        if (memStream.Length >= 2)
                        {
                            var pos = memStream.Position;
                            memStream.Position = pos - 2;
                            int prev = memStream.ReadByte();
                            int curr = memStream.ReadByte();

                            if (prev == EOI_MARKER_1 && curr == EOI_MARKER_2)
                            {
                                // Complete JPEG frame received
                                inFrame = false;
                                frameCount++;

                                try
                                {
                                    // Decode JPEG to Mat
                                    var jpegData = memStream.ToArray();
                                    using var mat = Cv2.ImDecode(jpegData, ImreadModes.Color);

                                    if (mat != null && !mat.Empty())
                                    {
                                        // Invoke event with cloned frame
                                        FrameCaptured?.Invoke(this, mat.Clone());
                                    }
                                }
                                catch (Exception ex)
                                {
                                    Console.WriteLine($"Error decoding preview frame: {ex.Message}");
                                }

                                memStream.SetLength(0);
                            }
                        }
                    }
                }
            }

            Console.WriteLine($"Preview frame reader stopped after {frameCount} frames");
        }
        catch (Exception ex)
        {
            if (_isRecording) // Only log if we're supposed to be recording
            {
                Console.WriteLine($"Preview frame reader error: {ex.Message}");
            }
        }
    }

    private void MonitorFFmpegStderr()
    {
        try
        {
            if (_ffmpegProcess == null) return;

            using var reader = _ffmpegProcess.StandardError;
            string? line;
            while ((line = reader.ReadLine()) != null)
            {
                Console.WriteLine($"[FFmpeg] {line}");

                // Check for errors
                if (line.Contains("error", StringComparison.OrdinalIgnoreCase) ||
                    line.Contains("failed", StringComparison.OrdinalIgnoreCase) ||
                    line.Contains("connection refused", StringComparison.OrdinalIgnoreCase) ||
                    line.Contains("Connection timed out", StringComparison.OrdinalIgnoreCase))
                {
                    _ffmpegHasError = true;
                    _ffmpegLastError = line;
                    Console.WriteLine($"[FFmpeg ERROR] {line}");
                }
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"FFmpeg stderr monitor error: {ex.Message}");
        }
    }

    private void MonitorFFmpegSegment()
    {
        // Wait a bit for the file to be created
        Thread.Sleep(3000);

        while (_isRecording && _ffmpegProcess != null && !_ffmpegProcess.HasExited)
        {
            Thread.Sleep(2000);

            // Check file size is growing
            if (!string.IsNullOrEmpty(_currentRecordingPath) && File.Exists(_currentRecordingPath))
            {
                try
                {
                    var fileInfo = new FileInfo(_currentRecordingPath);
                    var currentSize = fileInfo.Length;
                    var elapsedTime = DateTime.Now - _segmentStartTime;

                    Console.WriteLine($"[Recording] {elapsedTime:mm\\:ss} - File size: {currentSize / 1024.0 / 1024.0:F2} MB");

                    if (currentSize == _lastFileSize)
                    {
                        _stagnantFileSizeCount++;
                        Console.WriteLine($"[Warning] File size not growing (count: {_stagnantFileSizeCount})");

                        // If file hasn't grown in 10 seconds (5 checks), there's a problem
                        if (_stagnantFileSizeCount >= 5)
                        {
                            Console.WriteLine("[ERROR] Recording appears stalled - file not growing");
                            _ffmpegHasError = true;
                            _ffmpegLastError = "Recording stalled - file not growing";

                            // Try to restart the recording
                            Console.WriteLine("Attempting to restart recording...");
                            StopFFmpegSegment();
                            Thread.Sleep(1000);

                            if (!StartNewFFmpegSegment())
                            {
                                Console.WriteLine("Failed to restart recording");
                                _isRecording = false;
                            }
                            break;
                        }
                    }
                    else
                    {
                        _stagnantFileSizeCount = 0;
                        _lastFileSize = currentSize;

                        // Calculate bitrate
                        var bitrateMbps = (currentSize * 8.0 / elapsedTime.TotalSeconds) / 1_000_000.0;
                        Console.WriteLine($"[Recording] Bitrate: {bitrateMbps:F2} Mbps");
                    }
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"Error checking file size: {ex.Message}");
                }
            }
            else
            {
                var elapsedSinceStart = DateTime.Now - _segmentStartTime;
                if (elapsedSinceStart.TotalSeconds > 10)
                {
                    Console.WriteLine("[ERROR] Recording file not created after 10 seconds");
                    _ffmpegHasError = true;
                    _ffmpegLastError = "Recording file not created";
                }
            }

            // Check for FFmpeg errors
            if (_ffmpegHasError)
            {
                Console.WriteLine($"[FFmpeg Error] {_ffmpegLastError}");
            }

            // Check if segment duration exceeded
            if (DateTime.Now - _segmentStartTime >= _segmentDuration)
            {
                Console.WriteLine($"Segment duration reached ({_segmentDuration.TotalMinutes} min), starting new segment...");

                // Start new segment (this will stop the current one)
                StartNewFFmpegSegment();
                break;
            }
        }
    }

    private void OnFFmpegExited(object? sender, EventArgs e)
    {
        if (_ffmpegProcess != null && !string.IsNullOrEmpty(_currentRecordingPath))
        {
            var exitCode = _ffmpegProcess.ExitCode;
            Console.WriteLine($"FFmpeg segment completed (exit code: {exitCode}): {Path.GetFileName(_currentRecordingPath)}");

            if (exitCode == 0 || File.Exists(_currentRecordingPath))
            {
                SegmentCreated?.Invoke(this, _currentRecordingPath);
            }
        }
    }

    private void StopFFmpegSegment()
    {
        if (_ffmpegProcess != null)
        {
            try
            {
                if (!_ffmpegProcess.HasExited)
                {
                    // Send 'q' to gracefully stop FFmpeg (allows it to finalize the MP4 file)
                    try
                    {
                        // Write 'q' and close stdin - both signals FFmpeg to stop
                        _ffmpegProcess.StandardInput.Write("q");
                        _ffmpegProcess.StandardInput.Flush();
                        _ffmpegProcess.StandardInput.Close();
                        Console.WriteLine("Sent quit signal to FFmpeg...");
                    }
                    catch (Exception ex)
                    {
                        Console.WriteLine($"Error sending quit to FFmpeg: {ex.Message}");
                    }

                    // Wait for graceful exit (give it more time to finalize)
                    if (!_ffmpegProcess.WaitForExit(15000))
                    {
                        // If it didn't exit gracefully, force kill
                        Console.WriteLine("FFmpeg didn't exit gracefully after 15s, forcing...");
                        try { _ffmpegProcess.Kill(); } catch { }
                        _ffmpegProcess.WaitForExit(2000);
                    }
                    else
                    {
                        Console.WriteLine($"FFmpeg exited gracefully with code: {_ffmpegProcess.ExitCode}");
                    }
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Error stopping FFmpeg: {ex.Message}");
            }
            finally
            {
                _ffmpegProcess.Dispose();
                _ffmpegProcess = null;
            }
        }

        // Wait for monitoring threads to finish
        try
        {
            _ffmpegStderrThread?.Join(2000);
            _ffmpegStderrThread = null;
        }
        catch { }

        try
        {
            _ffmpegPreviewThread?.Join(2000);
            _ffmpegPreviewThread = null;
        }
        catch { }

        // Log final file size
        if (!string.IsNullOrEmpty(_currentRecordingPath) && File.Exists(_currentRecordingPath))
        {
            var fileInfo = new FileInfo(_currentRecordingPath);
            Console.WriteLine($"Recording segment saved: {fileInfo.Name} ({fileInfo.Length / 1024.0 / 1024.0:F2} MB)");
        }
    }

    public string? StopRecording()
    {
        if (!_isRecording)
            return null;

        _isRecording = false;

        StopFFmpegSegment();

        var recordingPath = _currentRecordingPath;
        _currentRecordingPath = null;

        Console.WriteLine($"Stopped recording: {recordingPath}");

        if (!string.IsNullOrEmpty(recordingPath) && File.Exists(recordingPath))
        {
            SegmentCreated?.Invoke(this, recordingPath);
        }

        StatusChanged?.Invoke(this, new RecordingStatus
        {
            IsRecording = false,
            SegmentNumber = _segmentNumber - 1,
            SegmentPath = recordingPath,
            SegmentStartTime = _segmentStartTime
        });

        // Restart the preview stream after recording stops
        Console.WriteLine("Restarting preview stream...");
        Thread.Sleep(500); // Give FFmpeg time to release the connection
        StartStreaming();

        return recordingPath;
    }

    public Mat? CaptureFrame()
    {
        if (_videoCapture == null || !_videoCapture.IsOpened())
            return null;

        var frame = new Mat();
        if (_videoCapture.Read(frame) && !frame.Empty())
            return frame;

        frame.Dispose();
        return null;
    }

    public bool SaveFrame(Mat frame, string? filePath = null)
    {
        if (frame == null || frame.Empty())
            return false;

        if (string.IsNullOrEmpty(filePath))
        {
            var timestamp = DateTime.Now.ToString("yyyyMMdd_HHmmss");
            filePath = Path.Combine(_recordingFolder, $"frame_{timestamp}.jpg");
        }

        try
        {
            Cv2.ImWrite(filePath, frame);
            Console.WriteLine($"Frame saved: {filePath}");
            return true;
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Failed to save frame: {ex.Message}");
            return false;
        }
    }

    private void CaptureLoop(CancellationToken cancellationToken)
    {
        int consecutiveFailures = 0;
        const int maxFailures = 30;
        int reconnectAttempts = 0;
        const int maxReconnectAttempts = 10;
        DateTime lastSuccessfulFrame = DateTime.Now;

        while (!cancellationToken.IsCancellationRequested)
        {
            if (_videoCapture == null || !_videoCapture.IsOpened())
            {
                // Try to reconnect
                if (reconnectAttempts < maxReconnectAttempts)
                {
                    reconnectAttempts++;
                    var waitSeconds = Math.Min(reconnectAttempts * 2, 30); // Exponential backoff up to 30s
                    Console.WriteLine($"Stream disconnected. Reconnect attempt {reconnectAttempts}/{maxReconnectAttempts} in {waitSeconds}s...");
                    StreamStatusChanged?.Invoke(this, $"Reconnecting ({reconnectAttempts}/{maxReconnectAttempts})...");

                    Thread.Sleep(waitSeconds * 1000);
                    if (cancellationToken.IsCancellationRequested) break;

                    if (!TryReconnectStream())
                    {
                        continue;
                    }
                    Console.WriteLine("Stream reconnected successfully!");
                    StreamStatusChanged?.Invoke(this, "Connected");
                    reconnectAttempts = 0;
                }
                else
                {
                    Console.WriteLine($"Max reconnect attempts ({maxReconnectAttempts}) reached. Stopping stream.");
                    StreamStatusChanged?.Invoke(this, "Connection lost - max retries exceeded");
                    break;
                }
            }

            using var frame = new Mat();
            if (_videoCapture!.Read(frame) && !frame.Empty())
            {
                consecutiveFailures = 0;
                reconnectAttempts = 0;
                lastSuccessfulFrame = DateTime.Now;

                // Raise event for GUI updates (clone frame to prevent disposal issues)
                FrameCaptured?.Invoke(this, frame.Clone());
            }
            else
            {
                consecutiveFailures++;

                // Check if stream seems dead (no frames for 10+ seconds)
                var timeSinceLastFrame = DateTime.Now - lastSuccessfulFrame;
                if (timeSinceLastFrame.TotalSeconds > 10)
                {
                    Console.WriteLine($"No frames received for {timeSinceLastFrame.TotalSeconds:F0}s, forcing reconnect...");
                    StreamStatusChanged?.Invoke(this, "Stream stalled - reconnecting...");
                    _videoCapture?.Release();
                    _videoCapture = null;
                    consecutiveFailures = 0;
                    continue;
                }

                if (consecutiveFailures >= maxFailures)
                {
                    Console.WriteLine("Too many capture failures, attempting reconnect...");
                    StreamStatusChanged?.Invoke(this, "Stream error - reconnecting...");

                    _videoCapture?.Release();
                    _videoCapture = null;
                    consecutiveFailures = 0;
                }
                else
                {
                    Thread.Sleep(100);
                }
            }
        }
    }

    private bool TryReconnectStream()
    {
        try
        {
            _videoCapture?.Release();
            _videoCapture?.Dispose();

            Environment.SetEnvironmentVariable("OPENCV_FFMPEG_CAPTURE_OPTIONS",
                "rtsp_transport;tcp|buffer_size;8388608|max_delay;500000|analyzeduration;10000000|probesize;10000000|fflags;nobuffer");

            _videoCapture = new VideoCapture(_rtspUrl, VideoCaptureAPIs.FFMPEG);

            if (!_videoCapture.IsOpened())
            {
                Console.WriteLine("Failed to open stream during reconnect");
                return false;
            }

            _videoCapture.Set(VideoCaptureProperties.BufferSize, 10);

            if (_currentQuality == StreamQuality.Main)
            {
                _videoCapture.Set(VideoCaptureProperties.FrameWidth, 3840);
                _videoCapture.Set(VideoCaptureProperties.FrameHeight, 2160);
            }

            // Verify we can read a frame
            using var testFrame = new Mat();
            if (_videoCapture.Read(testFrame) && !testFrame.Empty())
            {
                return true;
            }

            Console.WriteLine("Reconnected but couldn't read frame");
            return false;
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Reconnect failed: {ex.Message}");
            return false;
        }
    }

    public void Dispose()
    {
        StopRecording();
        StopStreaming();
        _cancellationTokenSource?.Dispose();
    }
}

public class RecordingStatus
{
    public bool IsRecording { get; set; }
    public int SegmentNumber { get; set; }
    public string? SegmentPath { get; set; }
    public DateTime SegmentStartTime { get; set; }
    public long FileSize { get; set; }
    public double BitrateMbps { get; set; }
    public bool HasError { get; set; }
    public string? ErrorMessage { get; set; }
    public long AvailableDiskSpace { get; set; }
    public bool LowDiskSpace { get; set; }
}

/// <summary>
/// Recording quality presets
/// </summary>
public enum RecordingQuality
{
    /// <summary>
    /// Standard quality - 192kbps AAC stereo @ 44.1kHz
    /// Good for general use, smaller file sizes
    /// </summary>
    Standard,

    /// <summary>
    /// High quality - 320kbps AAC stereo @ 48kHz
    /// Excellent audio quality, recommended for most uses
    /// </summary>
    High,

    /// <summary>
    /// Maximum quality - 320kbps AAC stereo @ 48kHz with high profile
    /// Best possible AAC quality
    /// </summary>
    Maximum,

    /// <summary>
    /// Lossless audio - FLAC encoding
    /// Perfect audio quality, larger file sizes
    /// </summary>
    Lossless
}
