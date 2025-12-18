using LibVLCSharp.Shared;

namespace JennovCamera.WPF;

public class AudioManager : IDisposable
{
    private LibVLC? _libVLC;
    private MediaPlayer? _mediaPlayer;
    private Media? _media;
    private string _rtspUrl = "";
    private bool _isPlaying;
    private int _volume = 80;

    public bool IsPlaying => _isPlaying;
    public int Volume
    {
        get => _volume;
        set
        {
            _volume = Math.Clamp(value, 0, 100);
            if (_mediaPlayer != null)
                _mediaPlayer.Volume = _volume;
        }
    }

    public event EventHandler<string>? StatusChanged;

    public AudioManager()
    {
        // Initialize LibVLC
        Core.Initialize();
        _libVLC = new LibVLC(
            "--no-video",           // Audio only
            "--rtsp-tcp",           // Use TCP for RTSP (more reliable)
            "--network-caching=300" // Low latency
        );
    }

    public void SetRtspUrl(string url)
    {
        _rtspUrl = url;
    }

    public void SetRtspUrl(string ip, string username, string password, int channel = 1)
    {
        // Standard RTSP URL for audio (same stream as video typically contains audio)
        _rtspUrl = $"rtsp://{username}:{password}@{ip}:554/stream1";
    }

    public bool Start()
    {
        if (string.IsNullOrEmpty(_rtspUrl))
        {
            StatusChanged?.Invoke(this, "No RTSP URL configured");
            return false;
        }

        if (_isPlaying)
        {
            Stop();
        }

        try
        {
            _media = new Media(_libVLC!, _rtspUrl, FromType.FromLocation);
            _mediaPlayer = new MediaPlayer(_media);
            _mediaPlayer.Volume = _volume;

            // Subscribe to events
            _mediaPlayer.Playing += (s, e) =>
            {
                _isPlaying = true;
                StatusChanged?.Invoke(this, "Audio Playing");
            };

            _mediaPlayer.Stopped += (s, e) =>
            {
                _isPlaying = false;
                StatusChanged?.Invoke(this, "Audio Stopped");
            };

            _mediaPlayer.EncounteredError += (s, e) =>
            {
                _isPlaying = false;
                StatusChanged?.Invoke(this, "Audio Error");
            };

            _mediaPlayer.Play();
            return true;
        }
        catch (Exception ex)
        {
            StatusChanged?.Invoke(this, $"Audio Error: {ex.Message}");
            return false;
        }
    }

    public void Stop()
    {
        try
        {
            _mediaPlayer?.Stop();
            _media?.Dispose();
            _media = null;
            _mediaPlayer?.Dispose();
            _mediaPlayer = null;
            _isPlaying = false;
            StatusChanged?.Invoke(this, "Audio Stopped");
        }
        catch
        {
            // Ignore cleanup errors
        }
    }

    public void Mute()
    {
        if (_mediaPlayer != null)
            _mediaPlayer.Mute = true;
    }

    public void Unmute()
    {
        if (_mediaPlayer != null)
            _mediaPlayer.Mute = false;
    }

    public void ToggleMute()
    {
        if (_mediaPlayer != null)
            _mediaPlayer.Mute = !_mediaPlayer.Mute;
    }

    public bool IsMuted => _mediaPlayer?.Mute ?? false;

    public void Dispose()
    {
        Stop();
        _libVLC?.Dispose();
        _libVLC = null;
    }
}
