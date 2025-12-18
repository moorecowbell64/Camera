using OpenCvSharp;

namespace JennovCamera;

public class RecordingManager : IDisposable
{
    private readonly CameraClient _client;
    private bool _isRecording;
    private bool _isStreaming;
    private VideoCapture? _videoCapture;
    private VideoWriter? _videoWriter;
    private Thread? _captureThread;
    private CancellationTokenSource? _cancellationTokenSource;
    private string? _currentRecordingPath;
    private string _rtspUrl;
    private StreamQuality _currentQuality = StreamQuality.Main;

    public bool IsRecording => _isRecording;
    public bool IsStreaming => _isStreaming;
    public StreamQuality CurrentQuality => _currentQuality;
    public string CurrentRtspUrl => _rtspUrl;

    /// <summary>
    /// Event raised when a new frame is captured from the RTSP stream
    /// </summary>
    public event EventHandler<Mat>? FrameCaptured;

    public RecordingManager(CameraClient client, StreamQuality quality = StreamQuality.Main)
    {
        _client = client;
        _currentQuality = quality;
        _rtspUrl = client.Onvif.GetRtspUrl(quality);
    }

    /// <summary>
    /// Set stream quality (Main = 4K, Sub = 480p)
    /// </summary>
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

    /// <summary>
    /// Get information about available streams
    /// </summary>
    public (StreamInfo main, StreamInfo sub) GetAvailableStreams()
    {
        return (_client.Onvif.GetMainStreamInfo(), _client.Onvif.GetSubStreamInfo());
    }

    /// <summary>
    /// Set a custom RTSP URL (if different from default)
    /// </summary>
    public void SetRtspUrl(string url)
    {
        _rtspUrl = url;
    }

    /// <summary>
    /// Take a snapshot using ONVIF protocol
    /// </summary>
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

            // Generate filename if not provided
            if (string.IsNullOrEmpty(filePath))
            {
                var timestamp = DateTime.Now.ToString("yyyyMMdd_HHmmss");
                filePath = $"snapshot_{timestamp}.jpg";
            }

            // Save the image
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

    /// <summary>
    /// Start video streaming from RTSP (without recording)
    /// </summary>
    public bool StartStreaming()
    {
        if (_isStreaming)
        {
            Console.WriteLine("Already streaming");
            return false;
        }

        try
        {
            _videoCapture = new VideoCapture(_rtspUrl, VideoCaptureAPIs.FFMPEG);

            if (!_videoCapture.IsOpened())
            {
                Console.WriteLine($"Failed to open RTSP stream: {_rtspUrl}");
                return false;
            }

            // Configure for low latency
            _videoCapture.Set(VideoCaptureProperties.BufferSize, 3);

            _isStreaming = true;
            _cancellationTokenSource = new CancellationTokenSource();
            _captureThread = new Thread(() => CaptureLoop(_cancellationTokenSource.Token));
            _captureThread.Start();

            Console.WriteLine($"Started streaming: {_rtspUrl}");
            return true;
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Failed to start streaming: {ex.Message}");
            return false;
        }
    }

    /// <summary>
    /// Stop video streaming
    /// </summary>
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

    /// <summary>
    /// Start recording video to file (requires streaming to be active)
    /// </summary>
    public bool StartRecording(string? filePath = null)
    {
        if (_isRecording)
        {
            Console.WriteLine("Already recording");
            return false;
        }

        // Auto-start streaming if not already active
        if (!_isStreaming)
        {
            if (!StartStreaming())
            {
                Console.WriteLine("Cannot start recording without active stream");
                return false;
            }
        }

        try
        {
            // Generate filename if not provided
            if (string.IsNullOrEmpty(filePath))
            {
                var timestamp = DateTime.Now.ToString("yyyyMMdd_HHmmss");
                filePath = $"recording_{timestamp}.mp4";
            }

            _currentRecordingPath = filePath;

            // Get video properties from capture
            var width = (int)_videoCapture!.Get(VideoCaptureProperties.FrameWidth);
            var height = (int)_videoCapture.Get(VideoCaptureProperties.FrameHeight);
            var fps = _videoCapture.Get(VideoCaptureProperties.Fps);

            // Default to 1080p @ 20fps if not detected
            if (width <= 0) width = 1920;
            if (height <= 0) height = 1080;
            if (fps <= 0) fps = 20;

            // Create video writer with MP4V codec
            var fourcc = VideoWriter.FourCC('m', 'p', '4', 'v');
            _videoWriter = new VideoWriter(filePath, fourcc, fps, new OpenCvSharp.Size(width, height));

            if (!_videoWriter.IsOpened())
            {
                Console.WriteLine("Failed to create video writer");
                _videoWriter.Dispose();
                _videoWriter = null;
                return false;
            }

            _isRecording = true;
            Console.WriteLine($"Started recording: {filePath} ({width}x{height} @ {fps}fps)");
            return true;
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Failed to start recording: {ex.Message}");
            return false;
        }
    }

    /// <summary>
    /// Stop recording video
    /// </summary>
    public string? StopRecording()
    {
        if (!_isRecording)
            return null;

        _isRecording = false;

        _videoWriter?.Release();
        _videoWriter?.Dispose();
        _videoWriter = null;

        var recordingPath = _currentRecordingPath;
        _currentRecordingPath = null;

        Console.WriteLine($"Stopped recording: {recordingPath}");
        return recordingPath;
    }

    /// <summary>
    /// Capture a single frame from the current stream
    /// </summary>
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

    /// <summary>
    /// Save a frame to file
    /// </summary>
    public bool SaveFrame(Mat frame, string? filePath = null)
    {
        if (frame == null || frame.Empty())
            return false;

        if (string.IsNullOrEmpty(filePath))
        {
            var timestamp = DateTime.Now.ToString("yyyyMMdd_HHmmss");
            filePath = $"frame_{timestamp}.jpg";
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

                // Write to recording if active
                if (_isRecording && _videoWriter != null && _videoWriter.IsOpened())
                {
                    _videoWriter.Write(frame);
                }

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

                    _videoCapture = new VideoCapture(_rtspUrl, VideoCaptureAPIs.FFMPEG);
                    _videoCapture.Set(VideoCaptureProperties.BufferSize, 3);

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
