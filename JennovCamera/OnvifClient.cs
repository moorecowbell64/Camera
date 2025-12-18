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
            Timeout = TimeSpan.FromSeconds(30)
        };
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

    public string GetRtspUrl()
    {
        return $"rtsp://{_username}:{_password}@{_cameraIp}:554/stream1";
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
