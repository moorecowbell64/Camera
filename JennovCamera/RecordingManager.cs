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
    private string? _currentRecordingPath;

    // Segmented recording settings
    private string _recordingFolder = Environment.GetFolderPath(Environment.SpecialFolder.MyVideos);
    private TimeSpan _segmentDuration = TimeSpan.FromMinutes(30);
    private TimeSpan _overlapDuration = TimeSpan.FromSeconds(10);
    private DateTime _segmentStartTime;
    private DateTime _recordingStartTime;
    private int _segmentNumber;

    // Frame info
    private double _fps = 20;
    private int _frameWidth;
    private int _frameHeight;

    // Events
    public event EventHandler<Mat>? FrameCaptured;
    public event EventHandler<string>? SegmentCreated;
    public event EventHandler<RecordingStatus>? StatusChanged;

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
    private static readonly string[] FFmpegPaths = new[]
    {
        "ffmpeg", // In PATH
        @"C:\ffmpeg\ffmpeg-8.0.1-essentials_build\bin\ffmpeg.exe",
        @"C:\ffmpeg\bin\ffmpeg.exe",
        @"C:\Program Files\ffmpeg\bin\ffmpeg.exe",
        @"C:\Program Files (x86)\ffmpeg\bin\ffmpeg.exe",
    };

    private static string? _ffmpegPath;

    /// <summary>
    /// Find FFmpeg executable path
    /// </summary>
    public static string? FindFFmpegPath()
    {
        if (_ffmpegPath != null)
            return _ffmpegPath;

        foreach (var path in FFmpegPaths)
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

        if (!_isStreaming)
        {
            if (!StartStreaming())
            {
                LastError = "Cannot start recording without active stream";
                Console.WriteLine(LastError);
                return false;
            }
        }

        try
        {
            if (!Directory.Exists(_recordingFolder))
            {
                Directory.CreateDirectory(_recordingFolder);
            }

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

        // Build FFmpeg command
        // -rtsp_transport tcp: Use TCP for reliable streaming
        // -i: Input RTSP URL
        // -c:v copy: Copy video without re-encoding (fast, preserves quality)
        // -c:a aac: Re-encode audio to AAC (pcm_alaw from camera isn't supported in MP4)
        // -movflags frag_keyframe+empty_moov: Fragmented MP4, playable even if interrupted
        // -t: Duration limit for this segment
        var segmentSeconds = (int)_segmentDuration.TotalSeconds;

        var ffmpegArgs = $"-rtsp_transport tcp -i \"{_rtspUrl}\" -c:v copy -c:a aac -movflags frag_keyframe+empty_moov -t {segmentSeconds} -y \"{_currentRecordingPath}\"";

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

            // Start monitoring thread for segment timing
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
            return false;
        }
    }

    private void MonitorFFmpegSegment()
    {
        while (_isRecording && _ffmpegProcess != null && !_ffmpegProcess.HasExited)
        {
            Thread.Sleep(1000);

            // Check if segment duration exceeded
            if (DateTime.Now - _segmentStartTime >= _segmentDuration)
            {
                Console.WriteLine($"Segment duration reached, starting new segment...");

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
                        _ffmpegProcess.StandardInput.Close();
                        Console.WriteLine("Sent quit signal to FFmpeg...");
                    }
                    catch (Exception ex)
                    {
                        Console.WriteLine($"Error sending quit to FFmpeg: {ex.Message}");
                    }

                    // Wait for graceful exit (give it more time to finalize)
                    if (!_ffmpegProcess.WaitForExit(10000))
                    {
                        // If it didn't exit gracefully, force kill
                        Console.WriteLine("FFmpeg didn't exit gracefully, forcing...");
                        try { _ffmpegProcess.Kill(); } catch { }
                        _ffmpegProcess.WaitForExit(2000);
                    }
                    else
                    {
                        Console.WriteLine($"FFmpeg exited with code: {_ffmpegProcess.ExitCode}");
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

        while (!cancellationToken.IsCancellationRequested)
        {
            if (_videoCapture == null || !_videoCapture.IsOpened())
                break;

            using var frame = new Mat();
            if (_videoCapture.Read(frame) && !frame.Empty())
            {
                consecutiveFailures = 0;

                // Raise event for GUI updates (clone frame to prevent disposal issues)
                FrameCaptured?.Invoke(this, frame.Clone());
            }
            else
            {
                consecutiveFailures++;
                if (consecutiveFailures >= maxFailures)
                {
                    Console.WriteLine("Too many capture failures, attempting reconnect...");

                    _videoCapture?.Release();
                    Thread.Sleep(1000);

                    Environment.SetEnvironmentVariable("OPENCV_FFMPEG_CAPTURE_OPTIONS",
                        "rtsp_transport;tcp|buffer_size;8388608|max_delay;500000|analyzeduration;10000000|probesize;10000000|fflags;nobuffer");

                    _videoCapture = new VideoCapture(_rtspUrl, VideoCaptureAPIs.FFMPEG);
                    _videoCapture.Set(VideoCaptureProperties.BufferSize, 10);

                    if (_currentQuality == StreamQuality.Main)
                    {
                        _videoCapture.Set(VideoCaptureProperties.FrameWidth, 3840);
                        _videoCapture.Set(VideoCaptureProperties.FrameHeight, 2160);
                    }

                    consecutiveFailures = 0;
                }
                else
                {
                    Thread.Sleep(100);
                }
            }
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
}
