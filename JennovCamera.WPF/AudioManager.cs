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
            "--no-video",              // Audio only - don't decode video
            "--rtsp-tcp",              // Use TCP for RTSP (more reliable)
            "--network-caching=500",   // Slightly higher cache for stability
            "--live-caching=300",      // Live stream caching
            "--sout-mux-caching=300",  // Mux caching
            "--aout=directsound",      // Use DirectSound on Windows
            "--verbose=2"              // Enable verbose logging for debugging
        );
    }

    public void SetRtspUrl(string url)
    {
        _rtspUrl = url;
        Console.WriteLine($"[AudioManager] RTSP URL set to: {url}");
    }

    public void SetRtspUrl(string ip, string username, string password, int channel = 1)
    {
        // Standard RTSP URL for main stream (contains audio)
        _rtspUrl = $"rtsp://{username}:{password}@{ip}:554/stream1";
        Console.WriteLine($"[AudioManager] RTSP URL configured for {ip}");
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
            Console.WriteLine($"[AudioManager] Starting audio playback...");
            Console.WriteLine($"[AudioManager] URL: {_rtspUrl}");

            // Create media with RTSP-specific options
            _media = new Media(_libVLC!, _rtspUrl, FromType.FromLocation);

            // Add options for better audio handling
            _media.AddOption(":rtsp-tcp");
            _media.AddOption(":network-caching=500");
            _media.AddOption(":no-video");  // Ensure no video processing

            _mediaPlayer = new MediaPlayer(_media);
            _mediaPlayer.Volume = _volume;

            // Subscribe to events
            _mediaPlayer.Playing += (s, e) =>
            {
                _isPlaying = true;
                Console.WriteLine("[AudioManager] Audio stream playing");
                StatusChanged?.Invoke(this, "Audio: Playing");
            };

            _mediaPlayer.Stopped += (s, e) =>
            {
                _isPlaying = false;
                Console.WriteLine("[AudioManager] Audio stream stopped");
                StatusChanged?.Invoke(this, "Audio: Stopped");
            };

            _mediaPlayer.EncounteredError += (s, e) =>
            {
                _isPlaying = false;
                Console.WriteLine("[AudioManager] Audio error encountered");
                StatusChanged?.Invoke(this, "Audio: Error - check console");
            };

            _mediaPlayer.Buffering += (s, e) =>
            {
                Console.WriteLine($"[AudioManager] Buffering: {e.Cache}%");
                if (e.Cache < 100)
                    StatusChanged?.Invoke(this, $"Audio: Buffering {e.Cache:F0}%");
            };

            _mediaPlayer.Opening += (s, e) =>
            {
                Console.WriteLine("[AudioManager] Opening audio stream...");
                StatusChanged?.Invoke(this, "Audio: Connecting...");
            };

            _mediaPlayer.EndReached += (s, e) =>
            {
                Console.WriteLine("[AudioManager] Stream ended");
                _isPlaying = false;
                StatusChanged?.Invoke(this, "Audio: Stream ended");
            };

            var result = _mediaPlayer.Play();
            Console.WriteLine($"[AudioManager] Play() returned: {result}");

            if (!result)
            {
                StatusChanged?.Invoke(this, "Audio: Failed to start");
                return false;
            }

            return true;
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[AudioManager] Exception: {ex.Message}");
            StatusChanged?.Invoke(this, $"Audio Error: {ex.Message}");
            return false;
        }
    }

    public void Stop()
    {
        try
        {
            Console.WriteLine("[AudioManager] Stopping audio...");
            _mediaPlayer?.Stop();
            _media?.Dispose();
            _media = null;
            _mediaPlayer?.Dispose();
            _mediaPlayer = null;
            _isPlaying = false;
            StatusChanged?.Invoke(this, "Audio: Disabled");
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[AudioManager] Stop error: {ex.Message}");
        }
    }

    public void Mute()
    {
        if (_mediaPlayer != null)
        {
            _mediaPlayer.Mute = true;
            StatusChanged?.Invoke(this, "Audio: Muted");
        }
    }

    public void Unmute()
    {
        if (_mediaPlayer != null)
        {
            _mediaPlayer.Mute = false;
            StatusChanged?.Invoke(this, "Audio: Unmuted");
        }
    }

    public void ToggleMute()
    {
        if (_mediaPlayer != null)
        {
            _mediaPlayer.Mute = !_mediaPlayer.Mute;
            StatusChanged?.Invoke(this, _mediaPlayer.Mute ? "Audio: Muted" : "Audio: Playing");
        }
    }

    public bool IsMuted => _mediaPlayer?.Mute ?? false;

    public void Dispose()
    {
        Stop();
        _libVLC?.Dispose();
        _libVLC = null;
    }
}
