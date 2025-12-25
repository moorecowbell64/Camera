using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace JennovCamera;

/// <summary>
/// JSON-RPC client for direct camera API access (non-ONVIF features)
/// Uses /IPC endpoint with Dahua MD5 challenge-response authentication
/// </summary>
public class RpcClient : IDisposable
{
    private readonly HttpClient _httpClient;
    private readonly string _baseUrl;
    private readonly string _username;
    private readonly string _password;
    private string? _sessionId;
    private int _requestId;
    private bool _isAuthenticated;

    public bool IsAuthenticated => _isAuthenticated;

    public RpcClient(string cameraIp, string username, string password, int port = 80)
    {
        _baseUrl = $"http://{cameraIp}:{port}";
        _username = username;
        _password = password;

        // Use cookie container for session management
        var handler = new HttpClientHandler
        {
            CookieContainer = new CookieContainer(),
            UseCookies = true
        };

        _httpClient = new HttpClient(handler)
        {
            Timeout = TimeSpan.FromSeconds(10)
        };
    }

    /// <summary>
    /// Authenticate using Dahua MD5 challenge-response
    /// </summary>
    public async Task<bool> LoginAsync()
    {
        try
        {
            // Step 1: Get challenge
            var step1 = new
            {
                method = "global.login",
                @params = new { userName = _username, password = "", clientType = "Web3.0" },
                id = Interlocked.Increment(ref _requestId)
            };

            var response1 = await _httpClient.PostAsJsonAsync($"{_baseUrl}/RPC2_Login", step1);
            var json1 = await response1.Content.ReadAsStringAsync();
            var result1 = JsonSerializer.Deserialize<DahuaLoginResponse>(json1, new JsonSerializerOptions
            {
                PropertyNameCaseInsensitive = true
            });

            if (result1?.Session == null || result1.Params == null)
            {
                Console.WriteLine("RPC Login Step 1 failed: No session returned");
                return false;
            }

            _sessionId = result1.Session;
            var realm = result1.Params.Realm ?? "Login to " + _baseUrl;
            var random = result1.Params.Random ?? "";

            // Step 2: Calculate MD5 hash
            // hash1 = MD5(username:realm:password)
            // hash2 = MD5(username:random:hash1)
            var hash1 = CalculateMD5($"{_username}:{realm}:{_password}").ToUpper();
            var hash2 = CalculateMD5($"{_username}:{random}:{hash1}").ToUpper();

            // Step 3: Complete login
            var step3 = new
            {
                method = "global.login",
                session = _sessionId,
                @params = new { userName = _username, password = hash2, clientType = "Web3.0" },
                id = Interlocked.Increment(ref _requestId)
            };

            var response3 = await _httpClient.PostAsJsonAsync($"{_baseUrl}/RPC2_Login", step3);
            var json3 = await response3.Content.ReadAsStringAsync();
            var result3 = JsonSerializer.Deserialize<RpcResponse>(json3, new JsonSerializerOptions
            {
                PropertyNameCaseInsensitive = true
            });

            _isAuthenticated = result3?.Result == true;
            if (_isAuthenticated)
            {
                Console.WriteLine($"RPC authenticated successfully (session: {_sessionId})");
            }
            else
            {
                Console.WriteLine($"RPC authentication failed: {result3?.Error?.Message}");
            }

            return _isAuthenticated;
        }
        catch (Exception ex)
        {
            Console.WriteLine($"RPC login error: {ex.Message}");
            return false;
        }
    }

    private static string CalculateMD5(string input)
    {
        var bytes = MD5.HashData(Encoding.UTF8.GetBytes(input));
        return Convert.ToHexString(bytes);
    }

    /// <summary>
    /// Send an RPC request to the camera
    /// </summary>
    public async Task<RpcResponse> SendAsync(string method, object? parameters = null)
    {
        // Auto-login if not authenticated
        if (!_isAuthenticated)
        {
            await LoginAsync();
        }

        var request = new
        {
            method = method,
            @params = parameters,
            session = _sessionId ?? "0",
            id = Interlocked.Increment(ref _requestId)
        };

        try
        {
            var response = await _httpClient.PostAsJsonAsync($"{_baseUrl}/RPC2", request);
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

    [JsonPropertyName("session")]
    public string? Session { get; set; }
}

public class DahuaLoginResponse
{
    [JsonPropertyName("id")]
    public int Id { get; set; }

    [JsonPropertyName("session")]
    public string? Session { get; set; }

    [JsonPropertyName("params")]
    public DahuaLoginParams? Params { get; set; }

    [JsonPropertyName("error")]
    public RpcError? Error { get; set; }
}

public class DahuaLoginParams
{
    [JsonPropertyName("encryption")]
    public string? Encryption { get; set; }

    [JsonPropertyName("realm")]
    public string? Realm { get; set; }

    [JsonPropertyName("random")]
    public string? Random { get; set; }
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
