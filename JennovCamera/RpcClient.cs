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

    #region PTZ Tours & Patterns

    /// <summary>
    /// Get list of PTZ tours
    /// </summary>
    public async Task<PtzTour[]?> GetPtzToursAsync()
    {
        var response = await SendAsync("ptz.getTours");
        if (response.Params == null) return null;

        try
        {
            var json = JsonSerializer.Serialize(response.Params);
            var result = JsonSerializer.Deserialize<PtzToursResult>(json, new JsonSerializerOptions
            {
                PropertyNameCaseInsensitive = true
            });
            return result?.Tours;
        }
        catch { return null; }
    }

    /// <summary>
    /// Start a PTZ tour
    /// </summary>
    public async Task<bool> StartPtzTourAsync(int tourId)
    {
        var response = await SendAsync("ptz.startTour", new { id = tourId, channel = 0 });
        return response.Result == true;
    }

    /// <summary>
    /// Stop current PTZ tour
    /// </summary>
    public async Task<bool> StopPtzTourAsync()
    {
        var response = await SendAsync("ptz.stopTour", new { channel = 0 });
        return response.Result == true;
    }

    /// <summary>
    /// Create or update a PTZ tour
    /// </summary>
    public async Task<bool> SetPtzTourAsync(PtzTour tour)
    {
        var response = await SendAsync("ptz.setTour", tour);
        return response.Result == true;
    }

    /// <summary>
    /// Delete a PTZ tour
    /// </summary>
    public async Task<bool> DeletePtzTourAsync(int tourId)
    {
        var response = await SendAsync("ptz.deleteTour", new { id = tourId });
        return response.Result == true;
    }

    /// <summary>
    /// Get tour points for a specific tour
    /// </summary>
    public async Task<PtzTourPoint[]?> GetTourPointsAsync(int tourId)
    {
        var response = await SendAsync("ptz.getTourPoints", new { id = tourId });
        if (response.Params == null) return null;

        try
        {
            var json = JsonSerializer.Serialize(response.Params);
            var result = JsonSerializer.Deserialize<PtzTourPointsResult>(json, new JsonSerializerOptions
            {
                PropertyNameCaseInsensitive = true
            });
            return result?.Points;
        }
        catch { return null; }
    }

    /// <summary>
    /// Start recording a PTZ pattern
    /// </summary>
    public async Task<bool> StartPatternRecordAsync(int patternId)
    {
        var response = await SendAsync("ptz.startPatternRecord", new { id = patternId, channel = 0 });
        return response.Result == true;
    }

    /// <summary>
    /// Stop recording a PTZ pattern
    /// </summary>
    public async Task<bool> StopPatternRecordAsync()
    {
        var response = await SendAsync("ptz.stopPatternRecord", new { channel = 0 });
        return response.Result == true;
    }

    /// <summary>
    /// Start replaying a PTZ pattern
    /// </summary>
    public async Task<bool> StartPatternReplayAsync(int patternId)
    {
        var response = await SendAsync("ptz.startPatternReplay", new { id = patternId, channel = 0 });
        return response.Result == true;
    }

    /// <summary>
    /// Stop replaying a PTZ pattern
    /// </summary>
    public async Task<bool> StopPatternReplayAsync()
    {
        var response = await SendAsync("ptz.stopPatternReplay", new { channel = 0 });
        return response.Result == true;
    }

    /// <summary>
    /// Get available PTZ patterns
    /// </summary>
    public async Task<int[]?> GetPtzPatternsAsync()
    {
        var response = await SendAsync("ptz.getPatterns", new { channel = 0 });
        if (response.Params == null) return null;

        try
        {
            var json = JsonSerializer.Serialize(response.Params);
            var result = JsonSerializer.Deserialize<PtzPatternsResult>(json, new JsonSerializerOptions
            {
                PropertyNameCaseInsensitive = true
            });
            return result?.Patterns;
        }
        catch { return null; }
    }

    /// <summary>
    /// Start PTZ auto-scan between limits
    /// </summary>
    public async Task<bool> StartPtzScanAsync()
    {
        var response = await SendAsync("ptz.startScan", new { channel = 0 });
        return response.Result == true;
    }

    /// <summary>
    /// Stop PTZ auto-scan
    /// </summary>
    public async Task<bool> StopPtzScanAsync()
    {
        var response = await SendAsync("ptz.stopScan", new { channel = 0 });
        return response.Result == true;
    }

    /// <summary>
    /// Set PTZ scan limits
    /// </summary>
    public async Task<bool> SetPtzScanLimitAsync(string side) // "Left" or "Right"
    {
        var response = await SendAsync("ptz.setScanLimit", new { side, channel = 0 });
        return response.Result == true;
    }

    #endregion

    #region Wiper/Rain Brush Control

    /// <summary>
    /// Start wiper continuous movement
    /// </summary>
    public async Task<bool> WiperStartAsync()
    {
        var response = await SendAsync("ptz.start", new
        {
            code = "RainBrush",
            arg1 = 0,
            arg2 = 1,
            arg3 = 0,
            channel = 0
        });
        return response.Result == true;
    }

    /// <summary>
    /// Stop wiper movement
    /// </summary>
    public async Task<bool> WiperStopAsync()
    {
        var response = await SendAsync("ptz.stop", new
        {
            code = "RainBrush",
            channel = 0
        });
        return response.Result == true;
    }

    /// <summary>
    /// Wiper single wipe
    /// </summary>
    public async Task<bool> WiperOnceAsync()
    {
        var response = await SendAsync("ptz.start", new
        {
            code = "RainBrushOnce",
            arg1 = 0,
            arg2 = 1,
            arg3 = 0,
            channel = 0
        });
        return response.Result == true;
    }

    #endregion

    #region Two-Way Audio

    /// <summary>
    /// Start audio output (speaker)
    /// </summary>
    public async Task<bool> StartSpeakerAsync()
    {
        var response = await SendAsync("speak.startPlay");
        return response.Result == true;
    }

    /// <summary>
    /// Stop audio output
    /// </summary>
    public async Task<bool> StopSpeakerAsync()
    {
        var response = await SendAsync("speak.stopPlay");
        return response.Result == true;
    }

    /// <summary>
    /// Start audio recording from camera
    /// </summary>
    public async Task<bool> StartAudioRecordingAsync(int channel = 0)
    {
        var response = await SendAsync("audioRecordManager.startChannel", new { channel });
        return response.Result == true;
    }

    /// <summary>
    /// Stop audio recording
    /// </summary>
    public async Task<bool> StopAudioRecordingAsync(int channel = 0)
    {
        var response = await SendAsync("audioRecordManager.stopChannel", new { channel });
        return response.Result == true;
    }

    /// <summary>
    /// Get audio capabilities
    /// </summary>
    public async Task<AudioCaps?> GetAudioCapsAsync()
    {
        var response = await SendAsync("devAudioInput.getCaps", new { channel = 0 });
        if (response.Params == null) return null;

        try
        {
            var json = JsonSerializer.Serialize(response.Params);
            return JsonSerializer.Deserialize<AudioCaps>(json, new JsonSerializerOptions
            {
                PropertyNameCaseInsensitive = true
            });
        }
        catch { return null; }
    }

    /// <summary>
    /// Get audio encode configuration
    /// </summary>
    public async Task<AudioEncodeConfig?> GetAudioEncodeConfigAsync(int channel = 0)
    {
        var response = await SendAsync("configManager.getConfig", new { name = "Encode" });
        if (response.Params == null) return null;

        try
        {
            var json = JsonSerializer.Serialize(response.Params);
            Console.WriteLine($"[RPC] Audio encode config: {json}");

            // Parse the encode config which contains audio settings
            using var doc = JsonDocument.Parse(json);
            if (doc.RootElement.TryGetProperty("table", out var table) && table.GetArrayLength() > channel)
            {
                var channelConfig = table[channel];
                if (channelConfig.TryGetProperty("Audio", out var audio))
                {
                    return JsonSerializer.Deserialize<AudioEncodeConfig>(audio.GetRawText(), new JsonSerializerOptions
                    {
                        PropertyNameCaseInsensitive = true
                    });
                }
            }
            return null;
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[RPC] Audio config error: {ex.Message}");
            return null;
        }
    }

    /// <summary>
    /// Enable or disable audio on the encode stream
    /// </summary>
    public async Task<bool> SetAudioEnabledAsync(bool enabled, int channel = 0)
    {
        // Get current config first
        var response = await SendAsync("configManager.getConfig", new { name = "Encode" });
        if (response.Params == null) return false;

        try
        {
            var json = JsonSerializer.Serialize(response.Params);
            using var doc = JsonDocument.Parse(json);

            if (doc.RootElement.TryGetProperty("table", out var table) && table.GetArrayLength() > channel)
            {
                // Modify the audio enable setting
                var tableArray = JsonSerializer.Deserialize<JsonElement[]>(table.GetRawText());
                if (tableArray == null || tableArray.Length <= channel) return false;

                // Create modified config with audio enabled/disabled
                var modifiedConfig = new
                {
                    name = "Encode",
                    table = new[]
                    {
                        new
                        {
                            Audio = new
                            {
                                Enable = enabled
                            }
                        }
                    }
                };

                var setResponse = await SendAsync("configManager.setConfig", modifiedConfig);
                return setResponse.Result == true;
            }
            return false;
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[RPC] Set audio error: {ex.Message}");
            return false;
        }
    }

    #endregion

    #region Privacy Masks

    /// <summary>
    /// Get privacy mask configuration
    /// </summary>
    public async Task<PrivacyMaskConfig?> GetPrivacyMasksAsync()
    {
        var response = await SendAsync("configManager.getConfig", new { name = "VideoWidget" });
        if (response.Params == null) return null;

        try
        {
            var json = JsonSerializer.Serialize(response.Params);
            var wrapper = JsonSerializer.Deserialize<PrivacyMaskWrapper>(json, new JsonSerializerOptions
            {
                PropertyNameCaseInsensitive = true
            });
            return wrapper?.VideoWidget?.FirstOrDefault();
        }
        catch { return null; }
    }

    /// <summary>
    /// Set privacy mask configuration
    /// </summary>
    public async Task<bool> SetPrivacyMasksAsync(PrivacyMaskConfig config)
    {
        var response = await SendAsync("configManager.setConfig", new
        {
            name = "VideoWidget",
            table = new[] { config }
        });
        return response.Result == true;
    }

    #endregion

    #region Alarm I/O

    /// <summary>
    /// Get alarm input states
    /// </summary>
    public async Task<AlarmState[]?> GetAlarmInputStatesAsync()
    {
        var response = await SendAsync("alarm.getInState");
        if (response.Params == null) return null;

        try
        {
            var json = JsonSerializer.Serialize(response.Params);
            var result = JsonSerializer.Deserialize<AlarmStatesResult>(json, new JsonSerializerOptions
            {
                PropertyNameCaseInsensitive = true
            });
            return result?.States;
        }
        catch { return null; }
    }

    /// <summary>
    /// Get alarm output states
    /// </summary>
    public async Task<AlarmState[]?> GetAlarmOutputStatesAsync()
    {
        var response = await SendAsync("alarm.getOutState");
        if (response.Params == null) return null;

        try
        {
            var json = JsonSerializer.Serialize(response.Params);
            var result = JsonSerializer.Deserialize<AlarmStatesResult>(json, new JsonSerializerOptions
            {
                PropertyNameCaseInsensitive = true
            });
            return result?.States;
        }
        catch { return null; }
    }

    /// <summary>
    /// Set alarm output state
    /// </summary>
    public async Task<bool> SetAlarmOutputAsync(int channel, bool active)
    {
        var response = await SendAsync("alarm.setOutState", new
        {
            channel,
            state = active ? 1 : 0
        });
        return response.Result == true;
    }

    /// <summary>
    /// Get alarm input/output slot counts
    /// </summary>
    public async Task<AlarmSlots?> GetAlarmSlotsAsync()
    {
        var inResponse = await SendAsync("alarm.getInSlots");
        var outResponse = await SendAsync("alarm.getOutSlots");

        return new AlarmSlots
        {
            InputCount = inResponse.Params is JsonElement inEl ? inEl.GetProperty("count").GetInt32() : 0,
            OutputCount = outResponse.Params is JsonElement outEl ? outEl.GetProperty("count").GetInt32() : 0
        };
    }

    /// <summary>
    /// Get alarm configuration
    /// </summary>
    public async Task<AlarmConfig?> GetAlarmConfigAsync()
    {
        var response = await SendAsync("configManager.getConfig", new { name = "Alarm" });
        if (response.Params == null) return null;

        try
        {
            var json = JsonSerializer.Serialize(response.Params);
            var wrapper = JsonSerializer.Deserialize<AlarmConfigWrapper>(json, new JsonSerializerOptions
            {
                PropertyNameCaseInsensitive = true
            });
            return wrapper?.Alarm?.FirstOrDefault();
        }
        catch { return null; }
    }

    /// <summary>
    /// Set alarm configuration
    /// </summary>
    public async Task<bool> SetAlarmConfigAsync(AlarmConfig config)
    {
        var response = await SendAsync("configManager.setConfig", new
        {
            name = "Alarm",
            table = new[] { config }
        });
        return response.Result == true;
    }

    #endregion

    #region Network Configuration

    /// <summary>
    /// Get network configuration
    /// </summary>
    public async Task<NetworkConfig?> GetNetworkConfigAsync()
    {
        var response = await SendAsync("configManager.getConfig", new { name = "Network" });
        if (response.Params == null) return null;

        try
        {
            var json = JsonSerializer.Serialize(response.Params);
            var wrapper = JsonSerializer.Deserialize<NetworkConfigWrapper>(json, new JsonSerializerOptions
            {
                PropertyNameCaseInsensitive = true
            });
            return wrapper?.Network?.FirstOrDefault();
        }
        catch { return null; }
    }

    /// <summary>
    /// Get NTP configuration
    /// </summary>
    public async Task<NtpConfig?> GetNtpConfigAsync()
    {
        var response = await SendAsync("configManager.getConfig", new { name = "NTP" });
        if (response.Params == null) return null;

        try
        {
            var json = JsonSerializer.Serialize(response.Params);
            var wrapper = JsonSerializer.Deserialize<NtpConfigWrapper>(json, new JsonSerializerOptions
            {
                PropertyNameCaseInsensitive = true
            });
            return wrapper?.NTP?.FirstOrDefault();
        }
        catch { return null; }
    }

    /// <summary>
    /// Set NTP configuration
    /// </summary>
    public async Task<bool> SetNtpConfigAsync(NtpConfig config)
    {
        var response = await SendAsync("configManager.setConfig", new
        {
            name = "NTP",
            table = new[] { config }
        });
        return response.Result == true;
    }

    /// <summary>
    /// Get email configuration
    /// </summary>
    public async Task<EmailConfig?> GetEmailConfigAsync()
    {
        var response = await SendAsync("configManager.getConfig", new { name = "Email" });
        if (response.Params == null) return null;

        try
        {
            var json = JsonSerializer.Serialize(response.Params);
            var wrapper = JsonSerializer.Deserialize<EmailConfigWrapper>(json, new JsonSerializerOptions
            {
                PropertyNameCaseInsensitive = true
            });
            return wrapper?.Email?.FirstOrDefault();
        }
        catch { return null; }
    }

    /// <summary>
    /// Set email configuration
    /// </summary>
    public async Task<bool> SetEmailConfigAsync(EmailConfig config)
    {
        var response = await SendAsync("configManager.setConfig", new
        {
            name = "Email",
            table = new[] { config }
        });
        return response.Result == true;
    }

    /// <summary>
    /// Scan for WiFi networks
    /// </summary>
    public async Task<WifiNetwork[]?> ScanWifiNetworksAsync()
    {
        var response = await SendAsync("netApp.scanWLanDevices");
        if (response.Params == null) return null;

        try
        {
            var json = JsonSerializer.Serialize(response.Params);
            var result = JsonSerializer.Deserialize<WifiScanResult>(json, new JsonSerializerOptions
            {
                PropertyNameCaseInsensitive = true
            });
            return result?.Networks;
        }
        catch { return null; }
    }

    /// <summary>
    /// Get network interfaces
    /// </summary>
    public async Task<NetworkInterface[]?> GetNetworkInterfacesAsync()
    {
        var response = await SendAsync("netApp.getNetInterfaces");
        if (response.Params == null) return null;

        try
        {
            var json = JsonSerializer.Serialize(response.Params);
            var result = JsonSerializer.Deserialize<NetworkInterfacesResult>(json, new JsonSerializerOptions
            {
                PropertyNameCaseInsensitive = true
            });
            return result?.Interfaces;
        }
        catch { return null; }
    }

    #endregion

    #region Firmware Management

    /// <summary>
    /// Get firmware version information
    /// </summary>
    public async Task<FirmwareInfo?> GetFirmwareInfoAsync()
    {
        var response = await SendAsync("magicBox.getSoftwareVersion");
        if (response.Params == null) return null;

        try
        {
            var json = JsonSerializer.Serialize(response.Params);
            return JsonSerializer.Deserialize<FirmwareInfo>(json, new JsonSerializerOptions
            {
                PropertyNameCaseInsensitive = true
            });
        }
        catch { return null; }
    }

    /// <summary>
    /// Prepare for firmware upgrade
    /// </summary>
    public async Task<bool> PrepareUpgradeAsync()
    {
        var response = await SendAsync("upgrader.prepare");
        return response.Result == true;
    }

    /// <summary>
    /// Get upgrade status
    /// </summary>
    public async Task<UpgradeStatus?> GetUpgradeStatusAsync()
    {
        var response = await SendAsync("upgrader.getStatus");
        if (response.Params == null) return null;

        try
        {
            var json = JsonSerializer.Serialize(response.Params);
            return JsonSerializer.Deserialize<UpgradeStatus>(json, new JsonSerializerOptions
            {
                PropertyNameCaseInsensitive = true
            });
        }
        catch { return null; }
    }

    /// <summary>
    /// Cancel firmware upgrade
    /// </summary>
    public async Task<bool> CancelUpgradeAsync()
    {
        var response = await SendAsync("upgrader.cancel");
        return response.Result == true;
    }

    /// <summary>
    /// Get auto maintenance configuration
    /// </summary>
    public async Task<AutoMaintenanceConfig?> GetAutoMaintenanceAsync()
    {
        var response = await SendAsync("configManager.getConfig", new { name = "AutoMaintain" });
        if (response.Params == null) return null;

        try
        {
            var json = JsonSerializer.Serialize(response.Params);
            var wrapper = JsonSerializer.Deserialize<AutoMaintenanceWrapper>(json, new JsonSerializerOptions
            {
                PropertyNameCaseInsensitive = true
            });
            return wrapper?.AutoMaintain?.FirstOrDefault();
        }
        catch { return null; }
    }

    /// <summary>
    /// Set auto maintenance configuration
    /// </summary>
    public async Task<bool> SetAutoMaintenanceAsync(AutoMaintenanceConfig config)
    {
        var response = await SendAsync("configManager.setConfig", new
        {
            name = "AutoMaintain",
            table = new[] { config }
        });
        return response.Result == true;
    }

    #endregion

    #region Recording & Playback Control

    /// <summary>
    /// Start recording
    /// </summary>
    public async Task<bool> StartRecordingAsync(int channel = 0)
    {
        var response = await SendAsync("recordManager.start", new { channel });
        return response.Result == true;
    }

    /// <summary>
    /// Stop recording
    /// </summary>
    public async Task<bool> StopRecordingAsync(int channel = 0)
    {
        var response = await SendAsync("recordManager.stop", new { channel });
        return response.Result == true;
    }

    /// <summary>
    /// Get recording configuration
    /// </summary>
    public async Task<RecordConfig?> GetRecordConfigAsync()
    {
        var response = await SendAsync("configManager.getConfig", new { name = "Record" });
        if (response.Params == null) return null;

        try
        {
            var json = JsonSerializer.Serialize(response.Params);
            var wrapper = JsonSerializer.Deserialize<RecordConfigWrapper>(json, new JsonSerializerOptions
            {
                PropertyNameCaseInsensitive = true
            });
            return wrapper?.Record?.FirstOrDefault();
        }
        catch { return null; }
    }

    /// <summary>
    /// Set recording configuration
    /// </summary>
    public async Task<bool> SetRecordConfigAsync(RecordConfig config)
    {
        var response = await SendAsync("configManager.setConfig", new
        {
            name = "Record",
            table = new[] { config }
        });
        return response.Result == true;
    }

    /// <summary>
    /// Take a snapshot
    /// </summary>
    public async Task<bool> TakeSnapshotAsync(int channel = 0)
    {
        var response = await SendAsync("snapManager.start", new { channel });
        return response.Result == true;
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

#region PTZ Tour Classes

public class PtzToursResult
{
    [JsonPropertyName("tours")]
    public PtzTour[]? Tours { get; set; }
}

public class PtzTour
{
    [JsonPropertyName("Id")]
    public int Id { get; set; }

    [JsonPropertyName("Name")]
    public string? Name { get; set; }

    [JsonPropertyName("Enable")]
    public bool Enable { get; set; }

    [JsonPropertyName("Points")]
    public PtzTourPoint[]? Points { get; set; }
}

public class PtzTourPointsResult
{
    [JsonPropertyName("points")]
    public PtzTourPoint[]? Points { get; set; }
}

public class PtzTourPoint
{
    [JsonPropertyName("PresetId")]
    public int PresetId { get; set; }

    [JsonPropertyName("Duration")]
    public int Duration { get; set; } // Seconds to stay at preset

    [JsonPropertyName("Speed")]
    public int Speed { get; set; } // 1-8
}

public class PtzPatternsResult
{
    [JsonPropertyName("patterns")]
    public int[]? Patterns { get; set; }
}

#endregion

#region Audio Classes

public class AudioCaps
{
    [JsonPropertyName("Channels")]
    public int Channels { get; set; }

    [JsonPropertyName("SampleRate")]
    public int SampleRate { get; set; }

    [JsonPropertyName("BitDepth")]
    public int BitDepth { get; set; }

    [JsonPropertyName("Compression")]
    public string[]? Compression { get; set; }
}

public class AudioEncodeConfig
{
    [JsonPropertyName("Enable")]
    public bool Enable { get; set; }

    [JsonPropertyName("Compression")]
    public string? Compression { get; set; }  // "G.711A", "G.711Mu", "AAC", etc.

    [JsonPropertyName("Depth")]
    public int Depth { get; set; }

    [JsonPropertyName("Frequency")]
    public int Frequency { get; set; }

    [JsonPropertyName("Mode")]
    public int Mode { get; set; }

    [JsonPropertyName("Pack")]
    public string? Pack { get; set; }  // "DHAV", etc.
}

#endregion

#region Privacy Mask Classes

public class PrivacyMaskWrapper
{
    [JsonPropertyName("VideoWidget")]
    public PrivacyMaskConfig[]? VideoWidget { get; set; }
}

public class PrivacyMaskConfig
{
    [JsonPropertyName("Covers")]
    public CoverRegion[]? Covers { get; set; }

    [JsonPropertyName("ChannelTitle")]
    public ChannelTitle? ChannelTitle { get; set; }
}

public class CoverRegion
{
    [JsonPropertyName("Enable")]
    public bool Enable { get; set; }

    [JsonPropertyName("Rect")]
    public int[]? Rect { get; set; } // [left, top, right, bottom] in 0-8191 range

    [JsonPropertyName("Color")]
    public string? Color { get; set; } // Hex color like "0x000000"
}

public class ChannelTitle
{
    [JsonPropertyName("Name")]
    public string? Name { get; set; }

    [JsonPropertyName("Rect")]
    public int[]? Rect { get; set; }
}

#endregion

#region Alarm Classes

public class AlarmStatesResult
{
    [JsonPropertyName("states")]
    public AlarmState[]? States { get; set; }
}

public class AlarmState
{
    [JsonPropertyName("Channel")]
    public int Channel { get; set; }

    [JsonPropertyName("State")]
    public int State { get; set; } // 0 = inactive, 1 = active
}

public class AlarmSlots
{
    public int InputCount { get; set; }
    public int OutputCount { get; set; }
}

public class AlarmConfigWrapper
{
    [JsonPropertyName("Alarm")]
    public AlarmConfig[]? Alarm { get; set; }
}

public class AlarmConfig
{
    [JsonPropertyName("Enable")]
    public bool Enable { get; set; }

    [JsonPropertyName("SensorType")]
    public string? SensorType { get; set; } // "NO" or "NC"

    [JsonPropertyName("EventHandler")]
    public AlarmEventHandler? EventHandler { get; set; }
}

public class AlarmEventHandler
{
    [JsonPropertyName("RecordEnable")]
    public bool RecordEnable { get; set; }

    [JsonPropertyName("RecordLatch")]
    public int RecordLatch { get; set; } // Seconds

    [JsonPropertyName("AlarmOutEnable")]
    public bool AlarmOutEnable { get; set; }

    [JsonPropertyName("AlarmOutLatch")]
    public int AlarmOutLatch { get; set; }

    [JsonPropertyName("MailEnable")]
    public bool MailEnable { get; set; }

    [JsonPropertyName("SnapshotEnable")]
    public bool SnapshotEnable { get; set; }
}

#endregion

#region Network Configuration Classes

public class NetworkConfigWrapper
{
    [JsonPropertyName("Network")]
    public NetworkConfig[]? Network { get; set; }
}

public class NetworkConfig
{
    [JsonPropertyName("DefaultInterface")]
    public string? DefaultInterface { get; set; }

    [JsonPropertyName("Hostname")]
    public string? Hostname { get; set; }

    [JsonPropertyName("Domain")]
    public string? Domain { get; set; }
}

public class NtpConfigWrapper
{
    [JsonPropertyName("NTP")]
    public NtpConfig[]? NTP { get; set; }
}

public class NtpConfig
{
    [JsonPropertyName("Enable")]
    public bool Enable { get; set; }

    [JsonPropertyName("Address")]
    public string? Address { get; set; }

    [JsonPropertyName("Port")]
    public int Port { get; set; }

    [JsonPropertyName("UpdatePeriod")]
    public int UpdatePeriod { get; set; } // Minutes

    [JsonPropertyName("TimeZone")]
    public int TimeZone { get; set; }
}

public class EmailConfigWrapper
{
    [JsonPropertyName("Email")]
    public EmailConfig[]? Email { get; set; }
}

public class EmailConfig
{
    [JsonPropertyName("Enable")]
    public bool Enable { get; set; }

    [JsonPropertyName("Address")]
    public string? Address { get; set; }

    [JsonPropertyName("Port")]
    public int Port { get; set; }

    [JsonPropertyName("Encryption")]
    public string? Encryption { get; set; } // "None", "SSL", "TLS"

    [JsonPropertyName("UserName")]
    public string? UserName { get; set; }

    [JsonPropertyName("Password")]
    public string? Password { get; set; }

    [JsonPropertyName("Sender")]
    public string? Sender { get; set; }

    [JsonPropertyName("Receivers")]
    public string[]? Receivers { get; set; }

    [JsonPropertyName("Subject")]
    public string? Subject { get; set; }

    [JsonPropertyName("AttachSnapshot")]
    public bool AttachSnapshot { get; set; }
}

public class WifiScanResult
{
    [JsonPropertyName("networks")]
    public WifiNetwork[]? Networks { get; set; }
}

public class WifiNetwork
{
    [JsonPropertyName("SSID")]
    public string? SSID { get; set; }

    [JsonPropertyName("Signal")]
    public int Signal { get; set; } // Percentage

    [JsonPropertyName("Security")]
    public string? Security { get; set; } // "None", "WEP", "WPA", "WPA2"

    [JsonPropertyName("Channel")]
    public int Channel { get; set; }
}

public class NetworkInterfacesResult
{
    [JsonPropertyName("interfaces")]
    public NetworkInterface[]? Interfaces { get; set; }
}

public class NetworkInterface
{
    [JsonPropertyName("Name")]
    public string? Name { get; set; }

    [JsonPropertyName("Type")]
    public string? Type { get; set; }

    [JsonPropertyName("IPAddress")]
    public string? IPAddress { get; set; }

    [JsonPropertyName("SubnetMask")]
    public string? SubnetMask { get; set; }

    [JsonPropertyName("DefaultGateway")]
    public string? DefaultGateway { get; set; }

    [JsonPropertyName("MacAddress")]
    public string? MacAddress { get; set; }

    [JsonPropertyName("DHCP")]
    public bool DHCP { get; set; }
}

#endregion

#region Firmware Classes

public class FirmwareInfo
{
    [JsonPropertyName("version")]
    public string? Version { get; set; }

    [JsonPropertyName("buildDate")]
    public string? BuildDate { get; set; }

    [JsonPropertyName("webVersion")]
    public string? WebVersion { get; set; }
}

public class UpgradeStatus
{
    [JsonPropertyName("status")]
    public string? Status { get; set; } // "idle", "uploading", "installing", "complete", "error"

    [JsonPropertyName("progress")]
    public int Progress { get; set; } // 0-100

    [JsonPropertyName("message")]
    public string? Message { get; set; }
}

public class AutoMaintenanceWrapper
{
    [JsonPropertyName("AutoMaintain")]
    public AutoMaintenanceConfig[]? AutoMaintain { get; set; }
}

public class AutoMaintenanceConfig
{
    [JsonPropertyName("Enable")]
    public bool Enable { get; set; }

    [JsonPropertyName("RebootDay")]
    public string? RebootDay { get; set; } // "Sunday", "Monday", etc. or "Everyday"

    [JsonPropertyName("RebootTime")]
    public string? RebootTime { get; set; } // "02:00:00"
}

#endregion

#region Recording Configuration Classes

public class RecordConfigWrapper
{
    [JsonPropertyName("Record")]
    public RecordConfig[]? Record { get; set; }
}

public class RecordConfig
{
    [JsonPropertyName("Enable")]
    public bool Enable { get; set; }

    [JsonPropertyName("PreRecord")]
    public int PreRecord { get; set; } // Seconds

    [JsonPropertyName("Redundancy")]
    public bool Redundancy { get; set; }

    [JsonPropertyName("PacketLength")]
    public int PacketLength { get; set; } // Minutes per file

    [JsonPropertyName("TimeSection")]
    public string[][]? TimeSection { get; set; } // Schedule
}

#endregion
