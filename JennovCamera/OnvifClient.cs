using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Xml.Linq;

namespace JennovCamera;

public class OnvifClient : IDisposable
{
    private HttpClient _httpClient;
    private readonly string _baseUrl;
    private readonly string _cameraIp;
    private string _username;
    private string _password;
    private string? _profileToken;

    public OnvifClient(string cameraIp, string username, string password, int port = 80)
    {
        _cameraIp = cameraIp;
        _baseUrl = $"http://{cameraIp}:{port}";
        _username = username;
        _password = password;

        var handler = new HttpClientHandler
        {
            Credentials = new NetworkCredential(username, password),
            PreAuthenticate = true
        };

        _httpClient = new HttpClient(handler)
        {
            Timeout = TimeSpan.FromSeconds(30),
            DefaultRequestHeaders = { ConnectionClose = false } // Keep connection alive
        };
    }

    /// <summary>
    /// Warm up the HTTP connection by sending a lightweight request.
    /// Call this after connecting to reduce latency on first PTZ command.
    /// </summary>
    public async Task WarmUpConnectionAsync()
    {
        try
        {
            // Send a simple request to establish TCP connection and pre-authenticate
            await GetMediaProfileAsync();
        }
        catch
        {
            // Ignore errors - just trying to warm up
        }
    }

    public void SetCredentials(string username, string password)
    {
        _username = username;
        _password = password;

        _httpClient.Dispose();

        var handler = new HttpClientHandler
        {
            Credentials = new NetworkCredential(username, password),
            PreAuthenticate = true
        };

        _httpClient = new HttpClient(handler)
        {
            Timeout = TimeSpan.FromSeconds(30)
        };
    }

    /// <summary>
    /// Generate WS-Security UsernameToken header for ONVIF authentication
    /// </summary>
    private string GenerateSecurityHeader()
    {
        // Generate nonce (random 16 bytes)
        var nonceBytes = new byte[16];
        using (var rng = RandomNumberGenerator.Create())
        {
            rng.GetBytes(nonceBytes);
        }
        var nonce = Convert.ToBase64String(nonceBytes);

        // Generate created timestamp in UTC
        var created = DateTime.UtcNow.ToString("yyyy-MM-ddTHH:mm:ss.fffZ");

        // Generate password digest: Base64(SHA1(nonce + created + password))
        var digestInput = new byte[nonceBytes.Length + Encoding.UTF8.GetByteCount(created) + Encoding.UTF8.GetByteCount(_password)];
        Buffer.BlockCopy(nonceBytes, 0, digestInput, 0, nonceBytes.Length);
        Buffer.BlockCopy(Encoding.UTF8.GetBytes(created), 0, digestInput, nonceBytes.Length, Encoding.UTF8.GetByteCount(created));
        Buffer.BlockCopy(Encoding.UTF8.GetBytes(_password), 0, digestInput, nonceBytes.Length + Encoding.UTF8.GetByteCount(created), Encoding.UTF8.GetByteCount(_password));

        string passwordDigest;
        using (var sha1 = SHA1.Create())
        {
            passwordDigest = Convert.ToBase64String(sha1.ComputeHash(digestInput));
        }

        return $@"<s:Header>
        <Security xmlns=""http://docs.oasis-open.org/wss/2004/01/oasis-200401-wss-wssecurity-secext-1.0.xsd"" s:mustUnderstand=""1"">
            <UsernameToken>
                <Username>{_username}</Username>
                <Password Type=""http://docs.oasis-open.org/wss/2004/01/oasis-200401-wss-username-token-profile-1.0#PasswordDigest"">{passwordDigest}</Password>
                <Nonce EncodingType=""http://docs.oasis-open.org/wss/2004/01/oasis-200401-wss-soap-message-security-1.0#Base64Binary"">{nonce}</Nonce>
                <Created xmlns=""http://docs.oasis-open.org/wss/2004/01/oasis-200401-wss-wssecurity-utility-1.0.xsd"">{created}</Created>
            </UsernameToken>
        </Security>
    </s:Header>";
    }

    public async Task<bool> ConnectAsync()
    {
        try
        {
            // Get device information to test connection
            var response = await GetDeviceInformationAsync();
            if (response != null)
            {
                Console.WriteLine($"Connected to: {response.Manufacturer} {response.Model}");
                Console.WriteLine($"Firmware: {response.FirmwareVersion}");

                // Get media profile
                await GetMediaProfileAsync();
                return true;
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Connection failed: {ex.Message}");
        }

        return false;
    }

    private async Task GetMediaProfileAsync()
    {
        var securityHeader = GenerateSecurityHeader();
        var soapEnvelope = $@"<?xml version=""1.0"" encoding=""UTF-8""?>
<s:Envelope xmlns:s=""http://www.w3.org/2003/05/soap-envelope"">
    {securityHeader}
    <s:Body xmlns:xsi=""http://www.w3.org/2001/XMLSchema-instance"" xmlns:xsd=""http://www.w3.org/2001/XMLSchema"">
        <GetProfiles xmlns=""http://www.onvif.org/ver10/media/wsdl""/>
    </s:Body>
</s:Envelope>";

        var response = await SendOnvifRequestAsync("/onvif/Media", soapEnvelope);

        if (response != null)
        {
            var ns = XNamespace.Get("http://www.onvif.org/ver10/media/wsdl");
            var profile = response.Descendants(ns + "Profiles").FirstOrDefault();
            _profileToken = profile?.Attribute("token")?.Value ?? "MainStream";
            Console.WriteLine($"Using profile: {_profileToken}");
        }
    }

    public async Task<DeviceInfo?> GetDeviceInformationAsync()
    {
        var securityHeader = GenerateSecurityHeader();
        var soapEnvelope = $@"<?xml version=""1.0"" encoding=""UTF-8""?>
<s:Envelope xmlns:s=""http://www.w3.org/2003/05/soap-envelope"">
    {securityHeader}
    <s:Body xmlns:xsi=""http://www.w3.org/2001/XMLSchema-instance"" xmlns:xsd=""http://www.w3.org/2001/XMLSchema"">
        <GetDeviceInformation xmlns=""http://www.onvif.org/ver10/device/wsdl""/>
    </s:Body>
</s:Envelope>";

        var response = await SendOnvifRequestAsync("/onvif/device_service", soapEnvelope);

        if (response != null)
        {
            var ns = XNamespace.Get("http://www.onvif.org/ver10/device/wsdl");
            return new DeviceInfo
            {
                Manufacturer = response.Descendants(ns + "Manufacturer").FirstOrDefault()?.Value ?? "",
                Model = response.Descendants(ns + "Model").FirstOrDefault()?.Value ?? "",
                FirmwareVersion = response.Descendants(ns + "FirmwareVersion").FirstOrDefault()?.Value ?? ""
            };
        }

        return null;
    }

    public async Task<bool> ContinuousMoveAsync(float panSpeed, float tiltSpeed, float zoomSpeed = 0)
    {
        if (string.IsNullOrEmpty(_profileToken))
            await GetMediaProfileAsync();

        var securityHeader = GenerateSecurityHeader();
        // Use invariant culture for float formatting to ensure decimal point
        var panStr = panSpeed.ToString(System.Globalization.CultureInfo.InvariantCulture);
        var tiltStr = tiltSpeed.ToString(System.Globalization.CultureInfo.InvariantCulture);
        var zoomStr = zoomSpeed.ToString(System.Globalization.CultureInfo.InvariantCulture);

        var soapEnvelope = $@"<?xml version=""1.0"" encoding=""UTF-8""?>
<s:Envelope xmlns:s=""http://www.w3.org/2003/05/soap-envelope""
            xmlns:tptz=""http://www.onvif.org/ver20/ptz/wsdl""
            xmlns:tt=""http://www.onvif.org/ver10/schema"">
    {securityHeader}
    <s:Body>
        <tptz:ContinuousMove>
            <tptz:ProfileToken>{_profileToken}</tptz:ProfileToken>
            <tptz:Velocity>
                <tt:PanTilt x=""{panStr}"" y=""{tiltStr}""/>
                <tt:Zoom x=""{zoomStr}""/>
            </tptz:Velocity>
        </tptz:ContinuousMove>
    </s:Body>
</s:Envelope>";

        var response = await SendOnvifRequestAsync("/onvif/PTZ", soapEnvelope);
        return response != null;
    }

    /// <summary>
    /// Fast continuous move - true fire and forget, returns immediately.
    /// Use this for real-time PTZ control where responsiveness matters.
    /// </summary>
    public void ContinuousMoveFast(float panSpeed, float tiltSpeed, float zoomSpeed = 0)
    {
        if (string.IsNullOrEmpty(_profileToken))
            _profileToken = "MainStream"; // Use default if not set

        var securityHeader = GenerateSecurityHeader();
        var panStr = panSpeed.ToString(System.Globalization.CultureInfo.InvariantCulture);
        var tiltStr = tiltSpeed.ToString(System.Globalization.CultureInfo.InvariantCulture);
        var zoomStr = zoomSpeed.ToString(System.Globalization.CultureInfo.InvariantCulture);

        var soapEnvelope = $@"<?xml version=""1.0"" encoding=""UTF-8""?>
<s:Envelope xmlns:s=""http://www.w3.org/2003/05/soap-envelope""
            xmlns:tptz=""http://www.onvif.org/ver20/ptz/wsdl""
            xmlns:tt=""http://www.onvif.org/ver10/schema"">
    {securityHeader}
    <s:Body>
        <tptz:ContinuousMove>
            <tptz:ProfileToken>{_profileToken}</tptz:ProfileToken>
            <tptz:Velocity>
                <tt:PanTilt x=""{panStr}"" y=""{tiltStr}""/>
                <tt:Zoom x=""{zoomStr}""/>
            </tptz:Velocity>
        </tptz:ContinuousMove>
    </s:Body>
</s:Envelope>";

        SendOnvifRequestFireAndForget("/onvif/PTZ", soapEnvelope);
    }

    public async Task<bool> StopAsync()
    {
        if (string.IsNullOrEmpty(_profileToken))
            await GetMediaProfileAsync();

        var securityHeader = GenerateSecurityHeader();
        var soapEnvelope = $@"<?xml version=""1.0"" encoding=""UTF-8""?>
<s:Envelope xmlns:s=""http://www.w3.org/2003/05/soap-envelope""
            xmlns:tptz=""http://www.onvif.org/ver20/ptz/wsdl"">
    {securityHeader}
    <s:Body>
        <tptz:Stop>
            <tptz:ProfileToken>{_profileToken}</tptz:ProfileToken>
            <tptz:PanTilt>true</tptz:PanTilt>
            <tptz:Zoom>true</tptz:Zoom>
        </tptz:Stop>
    </s:Body>
</s:Envelope>";

        var response = await SendOnvifRequestAsync("/onvif/PTZ", soapEnvelope);
        return response != null;
    }

    /// <summary>
    /// Fast stop - true fire and forget, returns immediately.
    /// Use this for real-time PTZ control where responsiveness matters.
    /// </summary>
    public void StopFast()
    {
        if (string.IsNullOrEmpty(_profileToken))
            _profileToken = "MainStream"; // Use default if not set

        var securityHeader = GenerateSecurityHeader();
        var soapEnvelope = $@"<?xml version=""1.0"" encoding=""UTF-8""?>
<s:Envelope xmlns:s=""http://www.w3.org/2003/05/soap-envelope""
            xmlns:tptz=""http://www.onvif.org/ver20/ptz/wsdl"">
    {securityHeader}
    <s:Body>
        <tptz:Stop>
            <tptz:ProfileToken>{_profileToken}</tptz:ProfileToken>
            <tptz:PanTilt>true</tptz:PanTilt>
            <tptz:Zoom>true</tptz:Zoom>
        </tptz:Stop>
    </s:Body>
</s:Envelope>";

        SendOnvifRequestFireAndForget("/onvif/PTZ", soapEnvelope);
    }

    // ===== Focus Control Methods =====

    /// <summary>
    /// Move focus toward near (close objects) - continuous movement
    /// </summary>
    public void FocusNearFast(float speed = 1.0f)
    {
        var securityHeader = GenerateSecurityHeader();
        var speedStr = (-speed).ToString(System.Globalization.CultureInfo.InvariantCulture); // Negative = Near

        var soapEnvelope = $@"<?xml version=""1.0"" encoding=""UTF-8""?>
<s:Envelope xmlns:s=""http://www.w3.org/2003/05/soap-envelope""
            xmlns:timg=""http://www.onvif.org/ver20/imaging/wsdl""
            xmlns:tt=""http://www.onvif.org/ver10/schema"">
    {securityHeader}
    <s:Body>
        <timg:Move>
            <timg:VideoSourceToken>VideoSource_0</timg:VideoSourceToken>
            <timg:Focus>
                <tt:Continuous>
                    <tt:Speed>{speedStr}</tt:Speed>
                </tt:Continuous>
            </timg:Focus>
        </timg:Move>
    </s:Body>
</s:Envelope>";

        SendOnvifRequestFireAndForget("/onvif/Imaging", soapEnvelope);
    }

    /// <summary>
    /// Move focus toward far (distant objects) - continuous movement
    /// </summary>
    public void FocusFarFast(float speed = 1.0f)
    {
        var securityHeader = GenerateSecurityHeader();
        var speedStr = speed.ToString(System.Globalization.CultureInfo.InvariantCulture); // Positive = Far

        var soapEnvelope = $@"<?xml version=""1.0"" encoding=""UTF-8""?>
<s:Envelope xmlns:s=""http://www.w3.org/2003/05/soap-envelope""
            xmlns:timg=""http://www.onvif.org/ver20/imaging/wsdl""
            xmlns:tt=""http://www.onvif.org/ver10/schema"">
    {securityHeader}
    <s:Body>
        <timg:Move>
            <timg:VideoSourceToken>VideoSource_0</timg:VideoSourceToken>
            <timg:Focus>
                <tt:Continuous>
                    <tt:Speed>{speedStr}</tt:Speed>
                </tt:Continuous>
            </timg:Focus>
        </timg:Move>
    </s:Body>
</s:Envelope>";

        SendOnvifRequestFireAndForget("/onvif/Imaging", soapEnvelope);
    }

    /// <summary>
    /// Stop focus movement
    /// </summary>
    public void FocusStopFast()
    {
        var securityHeader = GenerateSecurityHeader();
        var soapEnvelope = $@"<?xml version=""1.0"" encoding=""UTF-8""?>
<s:Envelope xmlns:s=""http://www.w3.org/2003/05/soap-envelope""
            xmlns:timg=""http://www.onvif.org/ver20/imaging/wsdl"">
    {securityHeader}
    <s:Body>
        <timg:Stop>
            <timg:VideoSourceToken>VideoSource_0</timg:VideoSourceToken>
        </timg:Stop>
    </s:Body>
</s:Envelope>";

        SendOnvifRequestFireAndForget("/onvif/Imaging", soapEnvelope);
    }

    /// <summary>
    /// Trigger one-shot auto focus
    /// </summary>
    public void AutoFocusFast()
    {
        var securityHeader = GenerateSecurityHeader();
        // Use Move with no parameters to trigger auto-focus on many cameras
        // Some cameras use SetImagingSettings with FocusMode=AUTO instead
        var soapEnvelope = $@"<?xml version=""1.0"" encoding=""UTF-8""?>
<s:Envelope xmlns:s=""http://www.w3.org/2003/05/soap-envelope""
            xmlns:timg=""http://www.onvif.org/ver20/imaging/wsdl""
            xmlns:tt=""http://www.onvif.org/ver10/schema"">
    {securityHeader}
    <s:Body>
        <timg:SetImagingSettings>
            <timg:VideoSourceToken>VideoSource_0</timg:VideoSourceToken>
            <timg:ImagingSettings>
                <tt:Focus>
                    <tt:AutoFocusMode>AUTO</tt:AutoFocusMode>
                </tt:Focus>
            </timg:ImagingSettings>
        </timg:SetImagingSettings>
    </s:Body>
</s:Envelope>";

        SendOnvifRequestFireAndForget("/onvif/Imaging", soapEnvelope);
    }

    /// <summary>
    /// Set focus to manual mode
    /// </summary>
    public void SetManualFocusFast()
    {
        var securityHeader = GenerateSecurityHeader();
        var soapEnvelope = $@"<?xml version=""1.0"" encoding=""UTF-8""?>
<s:Envelope xmlns:s=""http://www.w3.org/2003/05/soap-envelope""
            xmlns:timg=""http://www.onvif.org/ver20/imaging/wsdl""
            xmlns:tt=""http://www.onvif.org/ver10/schema"">
    {securityHeader}
    <s:Body>
        <timg:SetImagingSettings>
            <timg:VideoSourceToken>VideoSource_0</timg:VideoSourceToken>
            <timg:ImagingSettings>
                <tt:Focus>
                    <tt:AutoFocusMode>MANUAL</tt:AutoFocusMode>
                </tt:Focus>
            </timg:ImagingSettings>
        </timg:SetImagingSettings>
    </s:Body>
</s:Envelope>";

        SendOnvifRequestFireAndForget("/onvif/Imaging", soapEnvelope);
    }

    public async Task<bool> GotoPresetAsync(string presetToken, float speed = 1.0f)
    {
        if (string.IsNullOrEmpty(_profileToken))
            await GetMediaProfileAsync();

        var securityHeader = GenerateSecurityHeader();
        var speedStr = speed.ToString(System.Globalization.CultureInfo.InvariantCulture);

        var soapEnvelope = $@"<?xml version=""1.0"" encoding=""UTF-8""?>
<s:Envelope xmlns:s=""http://www.w3.org/2003/05/soap-envelope""
            xmlns:tptz=""http://www.onvif.org/ver20/ptz/wsdl""
            xmlns:tt=""http://www.onvif.org/ver10/schema"">
    {securityHeader}
    <s:Body>
        <tptz:GotoPreset>
            <tptz:ProfileToken>{_profileToken}</tptz:ProfileToken>
            <tptz:PresetToken>{presetToken}</tptz:PresetToken>
            <tptz:Speed>
                <tt:PanTilt x=""{speedStr}"" y=""{speedStr}""/>
                <tt:Zoom x=""{speedStr}""/>
            </tptz:Speed>
        </tptz:GotoPreset>
    </s:Body>
</s:Envelope>";

        var response = await SendOnvifRequestAsync("/onvif/PTZ", soapEnvelope);
        return response != null;
    }

    /// <summary>
    /// Save current position as a preset
    /// </summary>
    public async Task<string?> SetPresetAsync(string presetName, string? presetToken = null)
    {
        if (string.IsNullOrEmpty(_profileToken))
            await GetMediaProfileAsync();

        var securityHeader = GenerateSecurityHeader();
        var tokenElement = string.IsNullOrEmpty(presetToken) ? "" : $"<tptz:PresetToken>{presetToken}</tptz:PresetToken>";

        var soapEnvelope = $@"<?xml version=""1.0"" encoding=""UTF-8""?>
<s:Envelope xmlns:s=""http://www.w3.org/2003/05/soap-envelope""
            xmlns:tptz=""http://www.onvif.org/ver20/ptz/wsdl"">
    {securityHeader}
    <s:Body>
        <tptz:SetPreset>
            <tptz:ProfileToken>{_profileToken}</tptz:ProfileToken>
            <tptz:PresetName>{presetName}</tptz:PresetName>
            {tokenElement}
        </tptz:SetPreset>
    </s:Body>
</s:Envelope>";

        var response = await SendOnvifRequestAsync("/onvif/PTZ", soapEnvelope);
        if (response != null)
        {
            var ns = XNamespace.Get("http://www.onvif.org/ver20/ptz/wsdl");
            return response.Descendants(ns + "PresetToken").FirstOrDefault()?.Value;
        }
        return null;
    }

    /// <summary>
    /// Remove a preset
    /// </summary>
    public async Task<bool> RemovePresetAsync(string presetToken)
    {
        if (string.IsNullOrEmpty(_profileToken))
            await GetMediaProfileAsync();

        var securityHeader = GenerateSecurityHeader();
        var soapEnvelope = $@"<?xml version=""1.0"" encoding=""UTF-8""?>
<s:Envelope xmlns:s=""http://www.w3.org/2003/05/soap-envelope""
            xmlns:tptz=""http://www.onvif.org/ver20/ptz/wsdl"">
    {securityHeader}
    <s:Body>
        <tptz:RemovePreset>
            <tptz:ProfileToken>{_profileToken}</tptz:ProfileToken>
            <tptz:PresetToken>{presetToken}</tptz:PresetToken>
        </tptz:RemovePreset>
    </s:Body>
</s:Envelope>";

        var response = await SendOnvifRequestAsync("/onvif/PTZ", soapEnvelope);
        return response != null;
    }

    /// <summary>
    /// Get all presets
    /// </summary>
    public async Task<List<PtzPreset>> GetPresetsAsync()
    {
        if (string.IsNullOrEmpty(_profileToken))
            await GetMediaProfileAsync();

        var securityHeader = GenerateSecurityHeader();
        var soapEnvelope = $@"<?xml version=""1.0"" encoding=""UTF-8""?>
<s:Envelope xmlns:s=""http://www.w3.org/2003/05/soap-envelope""
            xmlns:tptz=""http://www.onvif.org/ver20/ptz/wsdl"">
    {securityHeader}
    <s:Body>
        <tptz:GetPresets>
            <tptz:ProfileToken>{_profileToken}</tptz:ProfileToken>
        </tptz:GetPresets>
    </s:Body>
</s:Envelope>";

        var presets = new List<PtzPreset>();
        var response = await SendOnvifRequestAsync("/onvif/PTZ", soapEnvelope);

        if (response != null)
        {
            var ns = XNamespace.Get("http://www.onvif.org/ver20/ptz/wsdl");
            var tt = XNamespace.Get("http://www.onvif.org/ver10/schema");

            foreach (var preset in response.Descendants(ns + "Preset"))
            {
                presets.Add(new PtzPreset
                {
                    Token = preset.Attribute("token")?.Value ?? "",
                    Name = preset.Element(tt + "Name")?.Value ?? ""
                });
            }
        }

        return presets;
    }

    /// <summary>
    /// Move to absolute position
    /// </summary>
    public async Task<bool> AbsoluteMoveAsync(float pan, float tilt, float zoom = 0)
    {
        if (string.IsNullOrEmpty(_profileToken))
            await GetMediaProfileAsync();

        var securityHeader = GenerateSecurityHeader();
        var panStr = pan.ToString(System.Globalization.CultureInfo.InvariantCulture);
        var tiltStr = tilt.ToString(System.Globalization.CultureInfo.InvariantCulture);
        var zoomStr = zoom.ToString(System.Globalization.CultureInfo.InvariantCulture);

        var soapEnvelope = $@"<?xml version=""1.0"" encoding=""UTF-8""?>
<s:Envelope xmlns:s=""http://www.w3.org/2003/05/soap-envelope""
            xmlns:tptz=""http://www.onvif.org/ver20/ptz/wsdl""
            xmlns:tt=""http://www.onvif.org/ver10/schema"">
    {securityHeader}
    <s:Body>
        <tptz:AbsoluteMove>
            <tptz:ProfileToken>{_profileToken}</tptz:ProfileToken>
            <tptz:Position>
                <tt:PanTilt x=""{panStr}"" y=""{tiltStr}""/>
                <tt:Zoom x=""{zoomStr}""/>
            </tptz:Position>
        </tptz:AbsoluteMove>
    </s:Body>
</s:Envelope>";

        var response = await SendOnvifRequestAsync("/onvif/PTZ", soapEnvelope);
        return response != null;
    }

    /// <summary>
    /// Move relative to current position
    /// </summary>
    public async Task<bool> RelativeMoveAsync(float pan, float tilt, float zoom = 0)
    {
        if (string.IsNullOrEmpty(_profileToken))
            await GetMediaProfileAsync();

        var securityHeader = GenerateSecurityHeader();
        var panStr = pan.ToString(System.Globalization.CultureInfo.InvariantCulture);
        var tiltStr = tilt.ToString(System.Globalization.CultureInfo.InvariantCulture);
        var zoomStr = zoom.ToString(System.Globalization.CultureInfo.InvariantCulture);

        var soapEnvelope = $@"<?xml version=""1.0"" encoding=""UTF-8""?>
<s:Envelope xmlns:s=""http://www.w3.org/2003/05/soap-envelope""
            xmlns:tptz=""http://www.onvif.org/ver20/ptz/wsdl""
            xmlns:tt=""http://www.onvif.org/ver10/schema"">
    {securityHeader}
    <s:Body>
        <tptz:RelativeMove>
            <tptz:ProfileToken>{_profileToken}</tptz:ProfileToken>
            <tptz:Translation>
                <tt:PanTilt x=""{panStr}"" y=""{tiltStr}""/>
                <tt:Zoom x=""{zoomStr}""/>
            </tptz:Translation>
        </tptz:RelativeMove>
    </s:Body>
</s:Envelope>";

        var response = await SendOnvifRequestAsync("/onvif/PTZ", soapEnvelope);
        return response != null;
    }

    public async Task<string?> GetSnapshotUriAsync()
    {
        if (string.IsNullOrEmpty(_profileToken))
            await GetMediaProfileAsync();

        var securityHeader = GenerateSecurityHeader();
        var soapEnvelope = $@"<?xml version=""1.0"" encoding=""UTF-8""?>
<s:Envelope xmlns:s=""http://www.w3.org/2003/05/soap-envelope"">
    {securityHeader}
    <s:Body xmlns:xsi=""http://www.w3.org/2001/XMLSchema-instance"" xmlns:xsd=""http://www.w3.org/2001/XMLSchema"">
        <GetSnapshotUri xmlns=""http://www.onvif.org/ver10/media/wsdl"">
            <ProfileToken>{_profileToken}</ProfileToken>
        </GetSnapshotUri>
    </s:Body>
</s:Envelope>";

        var response = await SendOnvifRequestAsync("/onvif/Media", soapEnvelope);

        if (response != null)
        {
            var ns = XNamespace.Get("http://www.onvif.org/ver10/media/wsdl");
            var tt = XNamespace.Get("http://www.onvif.org/ver10/schema");

            var mediaUri = response.Descendants(ns + "MediaUri").FirstOrDefault();
            var uri = mediaUri?.Element(tt + "Uri")?.Value;

            return uri;
        }

        return null;
    }

    public async Task<byte[]?> GetSnapshotAsync()
    {
        var snapshotUri = await GetSnapshotUriAsync();
        if (string.IsNullOrEmpty(snapshotUri))
        {
            Console.WriteLine("Failed to get snapshot URI from camera");
            return null;
        }

        try
        {
            var response = await _httpClient.GetAsync(snapshotUri);
            if (response.IsSuccessStatusCode)
            {
                return await response.Content.ReadAsByteArrayAsync();
            }
            else
            {
                Console.WriteLine($"Snapshot request failed: {response.StatusCode}");
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Failed to get snapshot: {ex.Message}");
        }

        return null;
    }

    public async Task<string?> GetStreamUriAsync()
    {
        if (string.IsNullOrEmpty(_profileToken))
            await GetMediaProfileAsync();

        var securityHeader = GenerateSecurityHeader();
        var soapEnvelope = $@"<?xml version=""1.0"" encoding=""UTF-8""?>
<s:Envelope xmlns:s=""http://www.w3.org/2003/05/soap-envelope"">
    {securityHeader}
    <s:Body xmlns:xsi=""http://www.w3.org/2001/XMLSchema-instance"" xmlns:xsd=""http://www.w3.org/2001/XMLSchema"">
        <GetStreamUri xmlns=""http://www.onvif.org/ver10/media/wsdl"">
            <StreamSetup>
                <Stream xmlns=""http://www.onvif.org/ver10/schema"">RTP-Unicast</Stream>
                <Transport xmlns=""http://www.onvif.org/ver10/schema"">
                    <Protocol>RTSP</Protocol>
                </Transport>
            </StreamSetup>
            <ProfileToken>{_profileToken}</ProfileToken>
        </GetStreamUri>
    </s:Body>
</s:Envelope>";

        var response = await SendOnvifRequestAsync("/onvif/Media", soapEnvelope);

        if (response != null)
        {
            var ns = XNamespace.Get("http://www.onvif.org/ver10/media/wsdl");
            var tt = XNamespace.Get("http://www.onvif.org/ver10/schema");

            var mediaUri = response.Descendants(ns + "MediaUri").FirstOrDefault();
            var uri = mediaUri?.Element(tt + "Uri")?.Value;

            return uri;
        }

        return null;
    }

    /// <summary>
    /// Get RTSP URL for the specified stream quality
    /// Uses Dahua format: /cam/realmonitor?channel=1&subtype=0|1
    /// </summary>
    /// <param name="quality">Main (4K) or Sub (480p) stream</param>
    public string GetRtspUrl(StreamQuality quality = StreamQuality.Main)
    {
        var subtype = quality == StreamQuality.Main ? "0" : "1";
        return $"rtsp://{_username}:{_password}@{_cameraIp}:554/cam/realmonitor?channel=1&subtype={subtype}";
    }

    /// <summary>
    /// Get the legacy RTSP URL format (/stream1, /stream2)
    /// </summary>
    public string GetLegacyRtspUrl(StreamQuality quality = StreamQuality.Main)
    {
        var streamNum = quality == StreamQuality.Main ? "1" : "2";
        return $"rtsp://{_username}:{_password}@{_cameraIp}:554/stream{streamNum}";
    }

    #region CGI-based PTZ Control (Dahua compatible)

    /// <summary>
    /// PTZ control via CGI endpoint (alternative to ONVIF, may be more responsive)
    /// Direction codes: Up, Down, Left, Right, LeftUp, LeftDown, RightUp, RightDown,
    ///                  ZoomTele, ZoomWide, FocusNear, FocusFar
    /// </summary>
    public async Task<bool> CgiPtzStartAsync(string direction, int speed = 1)
    {
        try
        {
            var url = $"{_baseUrl}/cgi-bin/ptz.cgi?action=start&channel=1&code={direction}&arg1=0&arg2={speed}&arg3=0";
            var response = await _httpClient.GetAsync(url);
            return response.IsSuccessStatusCode;
        }
        catch (Exception ex)
        {
            Console.WriteLine($"CGI PTZ start error: {ex.Message}");
            return false;
        }
    }

    /// <summary>
    /// Stop CGI PTZ movement
    /// </summary>
    public async Task<bool> CgiPtzStopAsync(string direction)
    {
        try
        {
            var url = $"{_baseUrl}/cgi-bin/ptz.cgi?action=stop&channel=1&code={direction}";
            var response = await _httpClient.GetAsync(url);
            return response.IsSuccessStatusCode;
        }
        catch (Exception ex)
        {
            Console.WriteLine($"CGI PTZ stop error: {ex.Message}");
            return false;
        }
    }

    /// <summary>
    /// Go to preset via CGI
    /// </summary>
    public async Task<bool> CgiGotoPresetAsync(int presetNumber)
    {
        try
        {
            var url = $"{_baseUrl}/cgi-bin/ptz.cgi?action=start&channel=1&code=GotoPreset&arg1={presetNumber}&arg2=0&arg3=0";
            var response = await _httpClient.GetAsync(url);
            return response.IsSuccessStatusCode;
        }
        catch (Exception ex)
        {
            Console.WriteLine($"CGI GotoPreset error: {ex.Message}");
            return false;
        }
    }

    /// <summary>
    /// Take snapshot via CGI endpoint
    /// </summary>
    public async Task<byte[]?> CgiSnapshotAsync()
    {
        try
        {
            var url = $"{_baseUrl}/cgi-bin/snapshot.cgi?channel=1";
            var response = await _httpClient.GetAsync(url);
            if (response.IsSuccessStatusCode)
            {
                return await response.Content.ReadAsByteArrayAsync();
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"CGI Snapshot error: {ex.Message}");
        }
        return null;
    }

    #endregion

    /// <summary>
    /// Get alternative RTSP URLs to try if the default doesn't work
    /// </summary>
    public string[] GetAlternativeRtspUrls(StreamQuality quality = StreamQuality.Main)
    {
        var streamNum = quality == StreamQuality.Main ? "1" : "2";
        var subtype = quality == StreamQuality.Main ? "0" : "1";
        var channel = quality == StreamQuality.Main ? "101" : "102";

        return new[]
        {
            $"rtsp://{_username}:{_password}@{_cameraIp}:554/{streamNum}",
            $"rtsp://{_username}:{_password}@{_cameraIp}:554/stream{streamNum}",
            $"rtsp://{_username}:{_password}@{_cameraIp}:554/Streaming/Channels/{channel}",
            $"rtsp://{_username}:{_password}@{_cameraIp}:554/cam/realmonitor?channel=1&subtype={subtype}",
            $"rtsp://{_username}:{_password}@{_cameraIp}:554/h264/ch1/{(quality == StreamQuality.Main ? "main" : "sub")}/av_stream",
            $"rtsp://{_username}:{_password}@{_cameraIp}:554/live/ch00_{subtype}",
        };
    }

    /// <summary>
    /// Get stream information for both streams
    /// </summary>
    public StreamInfo GetMainStreamInfo() => new StreamInfo
    {
        Quality = StreamQuality.Main,
        RtspUrl = GetRtspUrl(StreamQuality.Main),
        Width = 3840,
        Height = 2160,
        Description = "4K Ultra HD (3840x2160)"
    };

    public StreamInfo GetSubStreamInfo() => new StreamInfo
    {
        Quality = StreamQuality.Sub,
        RtspUrl = GetRtspUrl(StreamQuality.Sub),
        Width = 720,
        Height = 480,
        Description = "SD (720x480)"
    };

    /// <summary>
    /// Get video encoder configurations
    /// </summary>
    public async Task<List<VideoEncoderConfig>> GetVideoEncoderConfigurationsAsync()
    {
        var securityHeader = GenerateSecurityHeader();
        var soapEnvelope = $@"<?xml version=""1.0"" encoding=""UTF-8""?>
<s:Envelope xmlns:s=""http://www.w3.org/2003/05/soap-envelope""
            xmlns:trt=""http://www.onvif.org/ver10/media/wsdl"">
    {securityHeader}
    <s:Body>
        <trt:GetVideoEncoderConfigurations/>
    </s:Body>
</s:Envelope>";

        var configs = new List<VideoEncoderConfig>();
        var response = await SendOnvifRequestAsync("/onvif/Media", soapEnvelope);

        if (response != null)
        {
            var ns = XNamespace.Get("http://www.onvif.org/ver10/media/wsdl");
            var tt = XNamespace.Get("http://www.onvif.org/ver10/schema");

            foreach (var config in response.Descendants(ns + "Configurations"))
            {
                var rateControl = config.Element(tt + "RateControl");
                var resolution = config.Element(tt + "Resolution");
                var h264 = config.Element(tt + "H264");

                configs.Add(new VideoEncoderConfig
                {
                    Token = config.Attribute("token")?.Value ?? "",
                    Name = config.Element(tt + "Name")?.Value ?? "",
                    Encoding = config.Element(tt + "Encoding")?.Value ?? "",
                    Width = int.TryParse(resolution?.Element(tt + "Width")?.Value, out var w) ? w : 0,
                    Height = int.TryParse(resolution?.Element(tt + "Height")?.Value, out var h) ? h : 0,
                    Quality = float.TryParse(config.Element(tt + "Quality")?.Value, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var q) ? q : 0,
                    FrameRateLimit = int.TryParse(rateControl?.Element(tt + "FrameRateLimit")?.Value, out var fr) ? fr : 0,
                    BitrateLimit = int.TryParse(rateControl?.Element(tt + "BitrateLimit")?.Value, out var br) ? br : 0,
                    GovLength = int.TryParse(h264?.Element(tt + "GovLength")?.Value, out var gov) ? gov : 0,
                    H264Profile = h264?.Element(tt + "H264Profile")?.Value ?? ""
                });
            }
        }

        return configs;
    }

    /// <summary>
    /// Optimize video encoder for maximum quality
    /// Sets: 4K resolution, 8Mbps bitrate, 25fps, High profile
    /// </summary>
    public async Task<bool> OptimizeForMaxQualityAsync()
    {
        var configs = await GetVideoEncoderConfigurationsAsync();
        if (configs.Count == 0)
        {
            Console.WriteLine("No video encoder configurations found");
            return false;
        }

        // Find the main stream configuration (usually the first one or highest resolution)
        var mainConfig = configs.OrderByDescending(c => c.Width * c.Height).First();

        Console.WriteLine($"Optimizing encoder: {mainConfig.Token} ({mainConfig.Width}x{mainConfig.Height})");

        // Set maximum quality parameters
        mainConfig.Width = 3840;           // 4K
        mainConfig.Height = 2160;
        mainConfig.BitrateLimit = 8000;    // 8 Mbps
        mainConfig.FrameRateLimit = 25;    // 25 fps (smooth)
        mainConfig.Quality = 5;            // Maximum quality (1-5 scale)
        mainConfig.H264Profile = "High";   // H.264 High Profile
        mainConfig.GovLength = 50;         // GOP = 2 seconds at 25fps

        var success = await SetVideoEncoderConfigurationAsync(mainConfig);

        if (success)
        {
            Console.WriteLine("Camera encoder optimized for maximum quality:");
            Console.WriteLine($"  Resolution: {mainConfig.Width}x{mainConfig.Height}");
            Console.WriteLine($"  Bitrate: {mainConfig.BitrateLimit} kbps");
            Console.WriteLine($"  Frame Rate: {mainConfig.FrameRateLimit} fps");
            Console.WriteLine($"  Profile: {mainConfig.H264Profile}");
        }

        return success;
    }

    /// <summary>
    /// Set video encoder configuration (resolution, bitrate, framerate, etc.)
    /// </summary>
    public async Task<bool> SetVideoEncoderConfigurationAsync(VideoEncoderConfig config)
    {
        var securityHeader = GenerateSecurityHeader();
        var qualityStr = config.Quality.ToString(System.Globalization.CultureInfo.InvariantCulture);

        var soapEnvelope = $@"<?xml version=""1.0"" encoding=""UTF-8""?>
<s:Envelope xmlns:s=""http://www.w3.org/2003/05/soap-envelope""
            xmlns:trt=""http://www.onvif.org/ver10/media/wsdl""
            xmlns:tt=""http://www.onvif.org/ver10/schema"">
    {securityHeader}
    <s:Body>
        <trt:SetVideoEncoderConfiguration>
            <trt:Configuration token=""{config.Token}"">
                <tt:Name>{config.Name}</tt:Name>
                <tt:UseCount>1</tt:UseCount>
                <tt:Encoding>{config.Encoding}</tt:Encoding>
                <tt:Resolution>
                    <tt:Width>{config.Width}</tt:Width>
                    <tt:Height>{config.Height}</tt:Height>
                </tt:Resolution>
                <tt:Quality>{qualityStr}</tt:Quality>
                <tt:RateControl>
                    <tt:FrameRateLimit>{config.FrameRateLimit}</tt:FrameRateLimit>
                    <tt:EncodingInterval>1</tt:EncodingInterval>
                    <tt:BitrateLimit>{config.BitrateLimit}</tt:BitrateLimit>
                </tt:RateControl>
                <tt:H264>
                    <tt:GovLength>{config.GovLength}</tt:GovLength>
                    <tt:H264Profile>{config.H264Profile}</tt:H264Profile>
                </tt:H264>
                <tt:Multicast>
                    <tt:Address>
                        <tt:Type>IPv4</tt:Type>
                        <tt:IPv4Address>0.0.0.0</tt:IPv4Address>
                    </tt:Address>
                    <tt:Port>0</tt:Port>
                    <tt:TTL>0</tt:TTL>
                    <tt:AutoStart>false</tt:AutoStart>
                </tt:Multicast>
                <tt:SessionTimeout>PT60S</tt:SessionTimeout>
            </trt:Configuration>
            <trt:ForcePersistence>true</trt:ForcePersistence>
        </trt:SetVideoEncoderConfiguration>
    </s:Body>
</s:Envelope>";

        var response = await SendOnvifRequestAsync("/onvif/Media", soapEnvelope);
        return response != null;
    }

    /// <summary>
    /// Get OSD (On-Screen Display) configurations
    /// </summary>
    public async Task<List<OsdConfig>> GetOSDsAsync(string videoSourceToken = "VideoSource_0")
    {
        var securityHeader = GenerateSecurityHeader();
        var soapEnvelope = $@"<?xml version=""1.0"" encoding=""UTF-8""?>
<s:Envelope xmlns:s=""http://www.w3.org/2003/05/soap-envelope""
            xmlns:trt=""http://www.onvif.org/ver10/media/wsdl"">
    {securityHeader}
    <s:Body>
        <trt:GetOSDs>
            <trt:ConfigurationToken>{videoSourceToken}</trt:ConfigurationToken>
        </trt:GetOSDs>
    </s:Body>
</s:Envelope>";

        var osds = new List<OsdConfig>();
        var response = await SendOnvifRequestAsync("/onvif/Media", soapEnvelope);

        if (response != null)
        {
            var ns = XNamespace.Get("http://www.onvif.org/ver10/media/wsdl");
            var tt = XNamespace.Get("http://www.onvif.org/ver10/schema");

            foreach (var osd in response.Descendants(ns + "OSDs"))
            {
                var position = osd.Element(tt + "Position");
                var textString = osd.Element(tt + "TextString");

                osds.Add(new OsdConfig
                {
                    Token = osd.Attribute("token")?.Value ?? "",
                    VideoSourceToken = osd.Element(tt + "VideoSourceConfigurationToken")?.Value ?? "",
                    Type = osd.Element(tt + "Type")?.Value ?? "",
                    PositionType = position?.Element(tt + "Type")?.Value ?? "",
                    TextType = textString?.Element(tt + "Type")?.Value ?? "",
                    PlainText = textString?.Element(tt + "PlainText")?.Value ?? ""
                });
            }
        }

        return osds;
    }

    /// <summary>
    /// Set OSD text (e.g., camera title)
    /// </summary>
    public async Task<bool> SetOSDAsync(string osdToken, string text, string positionType = "UpperLeft", string videoSourceToken = "VideoSource_0")
    {
        var securityHeader = GenerateSecurityHeader();
        var soapEnvelope = $@"<?xml version=""1.0"" encoding=""UTF-8""?>
<s:Envelope xmlns:s=""http://www.w3.org/2003/05/soap-envelope""
            xmlns:trt=""http://www.onvif.org/ver10/media/wsdl""
            xmlns:tt=""http://www.onvif.org/ver10/schema"">
    {securityHeader}
    <s:Body>
        <trt:SetOSD>
            <trt:OSD token=""{osdToken}"">
                <tt:VideoSourceConfigurationToken>{videoSourceToken}</tt:VideoSourceConfigurationToken>
                <tt:Type>Text</tt:Type>
                <tt:Position>
                    <tt:Type>{positionType}</tt:Type>
                </tt:Position>
                <tt:TextString>
                    <tt:Type>Plain</tt:Type>
                    <tt:PlainText>{text}</tt:PlainText>
                </tt:TextString>
            </trt:OSD>
        </trt:SetOSD>
    </s:Body>
</s:Envelope>";

        var response = await SendOnvifRequestAsync("/onvif/Media", soapEnvelope);
        return response != null;
    }

    /// <summary>
    /// Send ONVIF request with very short timeout, don't wait for response.
    /// Used for PTZ commands where we only need to send, not receive.
    /// </summary>
    private void SendOnvifRequestFireAndForget(string endpoint, string soapEnvelope)
    {
        // True fire-and-forget: start the request and don't wait at all
        _ = Task.Run(async () =>
        {
            try
            {
                using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(100));
                var content = new StringContent(soapEnvelope, Encoding.UTF8, "application/soap+xml");
                await _httpClient.PostAsync(_baseUrl + endpoint, content, cts.Token);
            }
            catch
            {
                // Ignore all errors - command was sent
            }
        });
    }

    private async Task<XDocument?> SendOnvifRequestAsync(string endpoint, string soapEnvelope)
    {
        try
        {
            var content = new StringContent(soapEnvelope, Encoding.UTF8, "application/soap+xml");
            var response = await _httpClient.PostAsync(_baseUrl + endpoint, content);

            var responseBody = await response.Content.ReadAsStringAsync();

            if (response.IsSuccessStatusCode)
            {
                return XDocument.Parse(responseBody);
            }
            else
            {
                // Check for SOAP Fault
                if (responseBody.Contains("Fault"))
                {
                    Console.WriteLine($"ONVIF SOAP Fault [{response.StatusCode}]:");
                    try
                    {
                        var doc = XDocument.Parse(responseBody);
                        var faultString = doc.Descendants().FirstOrDefault(e => e.Name.LocalName == "Text")?.Value
                            ?? doc.Descendants().FirstOrDefault(e => e.Name.LocalName == "faultstring")?.Value
                            ?? "Unknown fault";
                        Console.WriteLine($"  Fault: {faultString}");
                    }
                    catch
                    {
                        Console.WriteLine($"  Raw response: {responseBody.Substring(0, Math.Min(500, responseBody.Length))}");
                    }
                }
                else
                {
                    Console.WriteLine($"ONVIF Error [{response.StatusCode}]: {responseBody.Substring(0, Math.Min(500, responseBody.Length))}");
                }
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Request failed: {ex.Message}");
        }

        return null;
    }

    public void Dispose()
    {
        _httpClient?.Dispose();
    }
}

public class DeviceInfo
{
    public string Manufacturer { get; set; } = "";
    public string Model { get; set; } = "";
    public string FirmwareVersion { get; set; } = "";
}

public class PtzPreset
{
    public string Token { get; set; } = "";
    public string Name { get; set; } = "";

    public override string ToString() => $"[{Token}] {Name}";
}

/// <summary>
/// Stream quality selection
/// </summary>
public enum StreamQuality
{
    /// <summary>Main stream - 4K (3840x2160)</summary>
    Main,
    /// <summary>Sub stream - SD (720x480)</summary>
    Sub
}

/// <summary>
/// Stream information
/// </summary>
public class StreamInfo
{
    public StreamQuality Quality { get; set; }
    public string RtspUrl { get; set; } = "";
    public int Width { get; set; }
    public int Height { get; set; }
    public string Description { get; set; } = "";

    public override string ToString() => $"{Quality}: {Description} - {RtspUrl}";
}
