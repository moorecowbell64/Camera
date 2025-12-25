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

    #region Event Management

    /// <summary>
    /// Subscribe to camera events (motion, video loss, etc.)
    /// Event codes: VideoMotion, VideoLoss, VideoBlind, AlarmLocal, StorageNotExist, StorageLowSpace, StorageFailure
    /// </summary>
    public async Task<bool> SubscribeEventsAsync(string[] eventCodes)
    {
        var response = await SendAsync("eventManager.attach", new { codes = eventCodes });
        return response.Result == true;
    }

    /// <summary>
    /// Unsubscribe from events
    /// </summary>
    public async Task<bool> UnsubscribeEventsAsync()
    {
        var response = await SendAsync("eventManager.detach");
        return response.Result == true;
    }

    /// <summary>
    /// Get event capabilities
    /// </summary>
    public async Task<string[]?> GetEventCapsAsync()
    {
        var response = await SendAsync("eventManager.getCaps");
        if (response.Params == null) return null;

        try
        {
            var json = JsonSerializer.Serialize(response.Params);
            var caps = JsonSerializer.Deserialize<EventCaps>(json, new JsonSerializerOptions
            {
                PropertyNameCaseInsensitive = true
            });
            return caps?.Codes;
        }
        catch
        {
            return null;
        }
    }

    #endregion

    #region Storage & Recording

    /// <summary>
    /// Get all storage device names
    /// </summary>
    public async Task<string[]?> GetStorageNamesAsync()
    {
        var response = await SendAsync("devStorage.getAllNames");
        if (response.Params == null) return null;

        try
        {
            var json = JsonSerializer.Serialize(response.Params);
            var result = JsonSerializer.Deserialize<StorageNamesResult>(json, new JsonSerializerOptions
            {
                PropertyNameCaseInsensitive = true
            });
            return result?.Names;
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// Get storage device information
    /// </summary>
    public async Task<StorageDeviceInfo?> GetStorageInfoAsync(string deviceName = "/dev/sda")
    {
        var response = await SendAsync("devStorage.getDeviceInfo", new { name = deviceName });
        if (response.Params == null) return null;

        try
        {
            var json = JsonSerializer.Serialize(response.Params);
            return JsonSerializer.Deserialize<StorageDeviceInfo>(json, new JsonSerializerOptions
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
    /// Search for recordings
    /// </summary>
    public async Task<RecordingSearchResult?> SearchRecordingsAsync(DateTime startTime, DateTime endTime, int channel = 1)
    {
        // Create finder
        var createResponse = await SendAsync("RecordFinder.factory.create");
        if (createResponse.Params == null) return null;

        try
        {
            var finderJson = JsonSerializer.Serialize(createResponse.Params);
            var finder = JsonSerializer.Deserialize<RecordFinderCreate>(finderJson, new JsonSerializerOptions
            {
                PropertyNameCaseInsensitive = true
            });

            if (finder?.Object == null) return null;

            // Start search
            var startResponse = await SendAsync("RecordFinder.startFind", new
            {
                @object = finder.Object,
                condition = new
                {
                    Channel = channel,
                    StartTime = startTime.ToString("yyyy-MM-dd HH:mm:ss"),
                    EndTime = endTime.ToString("yyyy-MM-dd HH:mm:ss"),
                    Types = new[] { "dav", "mp4" }
                }
            });

            if (startResponse.Result != true) return null;

            // Get results
            var doFindResponse = await SendAsync("RecordFinder.doFind", new
            {
                @object = finder.Object,
                count = 100
            });

            if (doFindResponse.Params == null) return null;

            var resultJson = JsonSerializer.Serialize(doFindResponse.Params);
            return JsonSerializer.Deserialize<RecordingSearchResult>(resultJson, new JsonSerializerOptions
            {
                PropertyNameCaseInsensitive = true
            });
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Recording search error: {ex.Message}");
            return null;
        }
    }

    #endregion

    #region User Management

    /// <summary>
    /// Get all users
    /// </summary>
    public async Task<UserInfo[]?> GetUsersAsync()
    {
        var response = await SendAsync("userManager.getUserInfoAll");
        if (response.Params == null) return null;

        try
        {
            var json = JsonSerializer.Serialize(response.Params);
            var result = JsonSerializer.Deserialize<UserListResult>(json, new JsonSerializerOptions
            {
                PropertyNameCaseInsensitive = true
            });
            return result?.Users;
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// Get all groups
    /// </summary>
    public async Task<GroupInfo[]?> GetGroupsAsync()
    {
        var response = await SendAsync("userManager.getGroupInfoAll");
        if (response.Params == null) return null;

        try
        {
            var json = JsonSerializer.Serialize(response.Params);
            var result = JsonSerializer.Deserialize<GroupListResult>(json, new JsonSerializerOptions
            {
                PropertyNameCaseInsensitive = true
            });
            return result?.Groups;
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// Change user password
    /// </summary>
    public async Task<bool> ChangePasswordAsync(string username, string oldPassword, string newPassword)
    {
        var response = await SendAsync("userManager.modifyPassword", new
        {
            name = username,
            pwd = oldPassword,
            pwdNew = newPassword
        });
        return response.Result == true;
    }

    #endregion

    #region Additional Configuration

    /// <summary>
    /// Get any configuration by name
    /// Common names: Network, NTP, Email, Encode, VideoColor, MotionDetect,
    /// VideoInOptions, Record, Snap, Alarm, Storage, VideoWidget, Audio
    /// </summary>
    public async Task<JsonElement?> GetConfigRawAsync(string configName)
    {
        var response = await SendAsync("configManager.getConfig", new { name = configName });
        if (response.Params == null) return null;

        try
        {
            var json = JsonSerializer.Serialize(response.Params);
            return JsonSerializer.Deserialize<JsonElement>(json);
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// Restore configuration to factory default
    /// </summary>
    public async Task<bool> RestoreConfigAsync(string[] configNames)
    {
        var response = await SendAsync("configManager.restore", configNames);
        return response.Result == true;
    }

    /// <summary>
    /// Restore all configurations except specified ones
    /// </summary>
    public async Task<bool> RestoreAllExceptAsync(string[] exceptNames)
    {
        var response = await SendAsync("configManager.restoreExcept", exceptNames);
        return response.Result == true;
    }

    /// <summary>
    /// Get current camera time
    /// </summary>
    public async Task<DateTime?> GetCurrentTimeAsync()
    {
        var response = await SendAsync("global.getCurrentTime");
        if (response.Params == null) return null;

        try
        {
            var json = JsonSerializer.Serialize(response.Params);
            var timeResult = JsonSerializer.Deserialize<TimeResult>(json, new JsonSerializerOptions
            {
                PropertyNameCaseInsensitive = true
            });
            return timeResult?.Time;
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// Set camera time
    /// </summary>
    public async Task<bool> SetCurrentTimeAsync(DateTime time)
    {
        var response = await SendAsync("global.setCurrentTime", new
        {
            time = time.ToString("yyyy-MM-dd HH:mm:ss")
        });
        return response.Result == true;
    }

    /// <summary>
    /// Logout from RPC session
    /// </summary>
    public async Task<bool> LogoutAsync()
    {
        var response = await SendAsync("global.logout");
        _isAuthenticated = false;
        _sessionId = null;
        return response.Result == true;
    }

    /// <summary>
    /// List available RPC services
    /// </summary>
    public async Task<string[]?> ListServicesAsync()
    {
        var response = await SendAsync("system.listService");
        if (response.Params == null) return null;

        try
        {
            var json = JsonSerializer.Serialize(response.Params);
            var result = JsonSerializer.Deserialize<ServiceListResult>(json, new JsonSerializerOptions
            {
                PropertyNameCaseInsensitive = true
            });
            return result?.Services;
        }
        catch
        {
            return null;
        }
    }

    #endregion

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

#region Event Classes

public class EventCaps
{
    [JsonPropertyName("codes")]
    public string[]? Codes { get; set; }
}

#endregion

#region Storage Classes

public class StorageNamesResult
{
    [JsonPropertyName("names")]
    public string[]? Names { get; set; }
}

public class StorageDeviceInfo
{
    [JsonPropertyName("Name")]
    public string? Name { get; set; }

    [JsonPropertyName("State")]
    public string? State { get; set; }

    [JsonPropertyName("TotalBytes")]
    public long TotalBytes { get; set; }

    [JsonPropertyName("UsedBytes")]
    public long UsedBytes { get; set; }

    [JsonPropertyName("FreeBytes")]
    public long FreeBytes { get; set; }

    [JsonPropertyName("IsError")]
    public bool IsError { get; set; }

    [JsonPropertyName("Type")]
    public string? Type { get; set; }

    [JsonPropertyName("Partition")]
    public StoragePartition[]? Partitions { get; set; }
}

public class StoragePartition
{
    [JsonPropertyName("Name")]
    public string? Name { get; set; }

    [JsonPropertyName("TotalBytes")]
    public long TotalBytes { get; set; }

    [JsonPropertyName("UsedBytes")]
    public long UsedBytes { get; set; }

    [JsonPropertyName("FreeBytes")]
    public long FreeBytes { get; set; }

    [JsonPropertyName("IsCurrent")]
    public bool IsCurrent { get; set; }

    [JsonPropertyName("Status")]
    public string? Status { get; set; }
}

public class RecordFinderCreate
{
    [JsonPropertyName("object")]
    public int Object { get; set; }
}

public class RecordingSearchResult
{
    [JsonPropertyName("found")]
    public int Found { get; set; }

    [JsonPropertyName("infos")]
    public RecordingInfo[]? Infos { get; set; }
}

public class RecordingInfo
{
    [JsonPropertyName("Channel")]
    public int Channel { get; set; }

    [JsonPropertyName("StartTime")]
    public string? StartTime { get; set; }

    [JsonPropertyName("EndTime")]
    public string? EndTime { get; set; }

    [JsonPropertyName("Type")]
    public string? Type { get; set; }

    [JsonPropertyName("FilePath")]
    public string? FilePath { get; set; }

    [JsonPropertyName("Length")]
    public long Length { get; set; }

    [JsonPropertyName("Flags")]
    public string[]? Flags { get; set; }

    [JsonPropertyName("Events")]
    public string[]? Events { get; set; }
}

#endregion

#region User Management Classes

public class UserListResult
{
    [JsonPropertyName("users")]
    public UserInfo[]? Users { get; set; }
}

public class GroupListResult
{
    [JsonPropertyName("groups")]
    public GroupInfo[]? Groups { get; set; }
}

public class UserInfo
{
    [JsonPropertyName("Name")]
    public string? Name { get; set; }

    [JsonPropertyName("Id")]
    public int Id { get; set; }

    [JsonPropertyName("Group")]
    public string? Group { get; set; }

    [JsonPropertyName("Memo")]
    public string? Memo { get; set; }

    [JsonPropertyName("Reserved")]
    public bool Reserved { get; set; }

    [JsonPropertyName("Sharable")]
    public bool Sharable { get; set; }
}

public class GroupInfo
{
    [JsonPropertyName("Name")]
    public string? Name { get; set; }

    [JsonPropertyName("Id")]
    public int Id { get; set; }

    [JsonPropertyName("Memo")]
    public string? Memo { get; set; }

    [JsonPropertyName("Authorities")]
    public string[]? Authorities { get; set; }
}

#endregion

#region Time & Service Classes

public class TimeResult
{
    [JsonPropertyName("time")]
    public DateTime Time { get; set; }
}

public class ServiceListResult
{
    [JsonPropertyName("services")]
    public string[]? Services { get; set; }
}

#endregion
