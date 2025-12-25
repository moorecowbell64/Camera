using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace JennovCamera;

/// <summary>
/// JSON-RPC client for direct camera API access (non-ONVIF features)
/// Uses /IPC endpoint with HTTP Digest authentication
/// </summary>
public class RpcClient : IDisposable
{
    private readonly HttpClient _httpClient;
    private readonly string _baseUrl;
    private int _requestId;

    public RpcClient(string cameraIp, string username, string password, int port = 80)
    {
        _baseUrl = $"http://{cameraIp}:{port}";

        var handler = new HttpClientHandler
        {
            Credentials = new NetworkCredential(username, password),
            PreAuthenticate = true
        };

        _httpClient = new HttpClient(handler)
        {
            Timeout = TimeSpan.FromSeconds(10)
        };
    }

    /// <summary>
    /// Send an RPC request to the camera
    /// </summary>
    public async Task<RpcResponse> SendAsync(string method, object? parameters = null)
    {
        var request = new RpcRequest
        {
            Method = method,
            Params = parameters,
            Session = 0,
            Id = Interlocked.Increment(ref _requestId)
        };

        try
        {
            var response = await _httpClient.PostAsJsonAsync($"{_baseUrl}/IPC", request);
            response.EnsureSuccessStatusCode();

            var json = await response.Content.ReadAsStringAsync();
            var result = JsonSerializer.Deserialize<RpcResponse>(json, new JsonSerializerOptions
            {
                PropertyNameCaseInsensitive = true
            });

            return result ?? new RpcResponse { Error = new RpcError { Code = -1, Message = "Empty response" } };
        }
        catch (Exception ex)
        {
            return new RpcResponse { Error = new RpcError { Code = -1, Message = ex.Message } };
        }
    }

    /// <summary>
    /// Get a configuration by name
    /// </summary>
    public async Task<T?> GetConfigAsync<T>(string configName) where T : class
    {
        var response = await SendAsync("configManager.getConfig", new { name = configName });
        if (response.Error != null || response.Params == null)
            return null;

        try
        {
            var json = JsonSerializer.Serialize(response.Params);
            return JsonSerializer.Deserialize<T>(json, new JsonSerializerOptions
            {
                PropertyNameCaseInsensitive = true
            });
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// Set a configuration
    /// </summary>
    public async Task<bool> SetConfigAsync(string configName, object config)
    {
        var response = await SendAsync("configManager.setConfig", new { name = configName, table = config });
        return response.Result == true;
    }

    /// <summary>
    /// Get video color settings (brightness, contrast, saturation, etc.)
    /// </summary>
    public async Task<VideoColorConfig?> GetVideoColorAsync()
    {
        var response = await SendAsync("configManager.getConfig", new { name = "VideoColor" });
        if (response.Error != null || response.Params == null)
            return null;

        try
        {
            // VideoColor returns an array with one element per channel
            var json = JsonSerializer.Serialize(response.Params);
            var wrapper = JsonSerializer.Deserialize<VideoColorWrapper>(json, new JsonSerializerOptions
            {
                PropertyNameCaseInsensitive = true
            });
            return wrapper?.VideoColor?.FirstOrDefault();
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Error parsing VideoColor: {ex.Message}");
            return null;
        }
    }

    /// <summary>
    /// Set video color settings
    /// </summary>
    public async Task<bool> SetVideoColorAsync(VideoColorConfig config)
    {
        var response = await SendAsync("configManager.setConfig", new
        {
            name = "VideoColor",
            table = new[] { config }
        });
        return response.Result == true;
    }

    /// <summary>
    /// Get motion detection configuration
    /// </summary>
    public async Task<MotionDetectConfig?> GetMotionDetectAsync()
    {
        var response = await SendAsync("configManager.getConfig", new { name = "MotionDetect" });
        if (response.Error != null || response.Params == null)
            return null;

        try
        {
            var json = JsonSerializer.Serialize(response.Params);
            var wrapper = JsonSerializer.Deserialize<MotionDetectWrapper>(json, new JsonSerializerOptions
            {
                PropertyNameCaseInsensitive = true
            });
            return wrapper?.MotionDetect?.FirstOrDefault();
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// Set motion detection configuration
    /// </summary>
    public async Task<bool> SetMotionDetectAsync(MotionDetectConfig config)
    {
        var response = await SendAsync("configManager.setConfig", new
        {
            name = "MotionDetect",
            table = new[] { config }
        });
        return response.Result == true;
    }

    /// <summary>
    /// Get system information
    /// </summary>
    public async Task<SystemInfo?> GetSystemInfoAsync()
    {
        var response = await SendAsync("magicBox.getSystemInfo");
        if (response.Error != null || response.Params == null)
            return null;

        try
        {
            var json = JsonSerializer.Serialize(response.Params);
            return JsonSerializer.Deserialize<SystemInfo>(json, new JsonSerializerOptions
            {
                PropertyNameCaseInsensitive = true
            });
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// Reboot the camera
    /// </summary>
    public async Task<bool> RebootAsync()
    {
        var response = await SendAsync("magicBox.reboot");
        return response.Result == true;
    }

    public void Dispose()
    {
        _httpClient.Dispose();
    }
}

#region RPC Request/Response Classes

public class RpcRequest
{
    [JsonPropertyName("method")]
    public string Method { get; set; } = "";

    [JsonPropertyName("params")]
    public object? Params { get; set; }

    [JsonPropertyName("session")]
    public int Session { get; set; }

    [JsonPropertyName("id")]
    public int Id { get; set; }
}

public class RpcResponse
{
    [JsonPropertyName("id")]
    public int Id { get; set; }

    [JsonPropertyName("result")]
    public bool? Result { get; set; }

    [JsonPropertyName("params")]
    public object? Params { get; set; }

    [JsonPropertyName("error")]
    public RpcError? Error { get; set; }
}

public class RpcError
{
    [JsonPropertyName("code")]
    public int Code { get; set; }

    [JsonPropertyName("message")]
    public string Message { get; set; } = "";
}

#endregion

#region Configuration Classes

public class VideoColorWrapper
{
    [JsonPropertyName("VideoColor")]
    public VideoColorConfig[]? VideoColor { get; set; }
}

public class VideoColorConfig
{
    [JsonPropertyName("Brightness")]
    public int Brightness { get; set; } = 50;

    [JsonPropertyName("Contrast")]
    public int Contrast { get; set; } = 50;

    [JsonPropertyName("Saturation")]
    public int Saturation { get; set; } = 50;

    [JsonPropertyName("Hue")]
    public int Hue { get; set; } = 50;

    [JsonPropertyName("Sharpness")]
    public int Sharpness { get; set; } = 50;

    [JsonPropertyName("Gamma")]
    public int Gamma { get; set; } = 50;

    [JsonPropertyName("Gain")]
    public int Gain { get; set; } = 50;

    // Time range settings (optional)
    [JsonPropertyName("TimeSection")]
    public string? TimeSection { get; set; }
}

public class MotionDetectWrapper
{
    [JsonPropertyName("MotionDetect")]
    public MotionDetectConfig[]? MotionDetect { get; set; }
}

public class MotionDetectConfig
{
    [JsonPropertyName("Enable")]
    public bool Enable { get; set; }

    [JsonPropertyName("Level")]
    public int Level { get; set; } = 3; // Sensitivity 1-6

    [JsonPropertyName("Region")]
    public int[][]? Region { get; set; } // Detection grid
}

public class SystemInfo
{
    [JsonPropertyName("deviceType")]
    public string? DeviceType { get; set; }

    [JsonPropertyName("processor")]
    public string? Processor { get; set; }

    [JsonPropertyName("serialNo")]
    public string? SerialNo { get; set; }

    [JsonPropertyName("updateSerial")]
    public string? UpdateSerial { get; set; }

    [JsonPropertyName("hardwareVersion")]
    public string? HardwareVersion { get; set; }
}

#endregion
