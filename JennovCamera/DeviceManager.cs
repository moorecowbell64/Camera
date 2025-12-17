using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Xml.Linq;

namespace JennovCamera;

/// <summary>
/// Manages device settings like factory reset, reboot, network and encoder configuration
/// </summary>
public class DeviceManager : IDisposable
{
    private HttpClient _httpClient;
    private readonly string _baseUrl;
    private readonly string _cameraIp;
    private string _username;
    private string _password;

    public DeviceManager(string cameraIp, string username, string password, int port = 80)
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

    private string GenerateSecurityHeader()
    {
        var nonceBytes = new byte[16];
        using (var rng = RandomNumberGenerator.Create())
        {
            rng.GetBytes(nonceBytes);
        }
        var nonce = Convert.ToBase64String(nonceBytes);
        var created = DateTime.UtcNow.ToString("yyyy-MM-ddTHH:mm:ss.fffZ");

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
                if (responseBody.Contains("Fault"))
                {
                    var doc = XDocument.Parse(responseBody);
                    var faultString = doc.Descendants().FirstOrDefault(e => e.Name.LocalName == "Text")?.Value
                        ?? doc.Descendants().FirstOrDefault(e => e.Name.LocalName == "faultstring")?.Value
                        ?? "Unknown fault";
                    throw new InvalidOperationException($"ONVIF Fault: {faultString}");
                }
                throw new HttpRequestException($"Request failed with status {response.StatusCode}");
            }
        }
        catch (Exception ex) when (ex is not InvalidOperationException)
        {
            throw new InvalidOperationException($"ONVIF request failed: {ex.Message}", ex);
        }
    }

    /// <summary>
    /// Get comprehensive device information
    /// </summary>
    public async Task<DeviceInformation> GetDeviceInformationAsync()
    {
        var securityHeader = GenerateSecurityHeader();
        var soapEnvelope = $@"<?xml version=""1.0"" encoding=""UTF-8""?>
<s:Envelope xmlns:s=""http://www.w3.org/2003/05/soap-envelope"">
    {securityHeader}
    <s:Body>
        <GetDeviceInformation xmlns=""http://www.onvif.org/ver10/device/wsdl""/>
    </s:Body>
</s:Envelope>";

        var response = await SendOnvifRequestAsync("/onvif/device_service", soapEnvelope);
        if (response == null) throw new InvalidOperationException("Failed to get device information");

        var ns = XNamespace.Get("http://www.onvif.org/ver10/device/wsdl");
        return new DeviceInformation
        {
            Manufacturer = response.Descendants(ns + "Manufacturer").FirstOrDefault()?.Value ?? "",
            Model = response.Descendants(ns + "Model").FirstOrDefault()?.Value ?? "",
            FirmwareVersion = response.Descendants(ns + "FirmwareVersion").FirstOrDefault()?.Value ?? "",
            SerialNumber = response.Descendants(ns + "SerialNumber").FirstOrDefault()?.Value ?? "",
            HardwareId = response.Descendants(ns + "HardwareId").FirstOrDefault()?.Value ?? ""
        };
    }

    /// <summary>
    /// Get network interface information
    /// </summary>
    public async Task<NetworkInfo> GetNetworkInfoAsync()
    {
        var securityHeader = GenerateSecurityHeader();
        var soapEnvelope = $@"<?xml version=""1.0"" encoding=""UTF-8""?>
<s:Envelope xmlns:s=""http://www.w3.org/2003/05/soap-envelope"">
    {securityHeader}
    <s:Body>
        <GetNetworkInterfaces xmlns=""http://www.onvif.org/ver10/device/wsdl""/>
    </s:Body>
</s:Envelope>";

        var response = await SendOnvifRequestAsync("/onvif/device_service", soapEnvelope);
        if (response == null) throw new InvalidOperationException("Failed to get network info");

        var tt = XNamespace.Get("http://www.onvif.org/ver10/schema");
        return new NetworkInfo
        {
            IpAddress = response.Descendants(tt + "Address").FirstOrDefault()?.Value ?? _cameraIp,
            MacAddress = response.Descendants(tt + "HwAddress").FirstOrDefault()?.Value ?? ""
        };
    }

    /// <summary>
    /// Get device hostname
    /// </summary>
    public async Task<string> GetHostnameAsync()
    {
        var securityHeader = GenerateSecurityHeader();
        var soapEnvelope = $@"<?xml version=""1.0"" encoding=""UTF-8""?>
<s:Envelope xmlns:s=""http://www.w3.org/2003/05/soap-envelope"">
    {securityHeader}
    <s:Body>
        <GetHostname xmlns=""http://www.onvif.org/ver10/device/wsdl""/>
    </s:Body>
</s:Envelope>";

        var response = await SendOnvifRequestAsync("/onvif/device_service", soapEnvelope);
        if (response == null) throw new InvalidOperationException("Failed to get hostname");

        var ns = XNamespace.Get("http://www.onvif.org/ver10/device/wsdl");
        var tt = XNamespace.Get("http://www.onvif.org/ver10/schema");
        return response.Descendants(tt + "Name").FirstOrDefault()?.Value ?? "";
    }

    /// <summary>
    /// Set device hostname
    /// </summary>
    public async Task<bool> SetHostnameAsync(string hostname)
    {
        var securityHeader = GenerateSecurityHeader();
        var soapEnvelope = $@"<?xml version=""1.0"" encoding=""UTF-8""?>
<s:Envelope xmlns:s=""http://www.w3.org/2003/05/soap-envelope""
            xmlns:tds=""http://www.onvif.org/ver10/device/wsdl"">
    {securityHeader}
    <s:Body>
        <tds:SetHostname>
            <tds:Name>{hostname}</tds:Name>
        </tds:SetHostname>
    </s:Body>
</s:Envelope>";

        var response = await SendOnvifRequestAsync("/onvif/device_service", soapEnvelope);
        return response != null;
    }

    /// <summary>
    /// Get NTP configuration
    /// </summary>
    public async Task<NtpInfo> GetNtpAsync()
    {
        var securityHeader = GenerateSecurityHeader();
        var soapEnvelope = $@"<?xml version=""1.0"" encoding=""UTF-8""?>
<s:Envelope xmlns:s=""http://www.w3.org/2003/05/soap-envelope"">
    {securityHeader}
    <s:Body>
        <GetNTP xmlns=""http://www.onvif.org/ver10/device/wsdl""/>
    </s:Body>
</s:Envelope>";

        var response = await SendOnvifRequestAsync("/onvif/device_service", soapEnvelope);
        if (response == null) throw new InvalidOperationException("Failed to get NTP info");

        var tt = XNamespace.Get("http://www.onvif.org/ver10/schema");
        return new NtpInfo
        {
            FromDHCP = bool.Parse(response.Descendants(tt + "FromDHCP").FirstOrDefault()?.Value ?? "false"),
            NtpServer = response.Descendants(tt + "DNSname").FirstOrDefault()?.Value
                ?? response.Descendants(tt + "IPv4Address").FirstOrDefault()?.Value ?? ""
        };
    }

    /// <summary>
    /// Set NTP configuration
    /// </summary>
    public async Task<bool> SetNtpAsync(string ntpServer, bool fromDhcp = false)
    {
        var securityHeader = GenerateSecurityHeader();
        var soapEnvelope = $@"<?xml version=""1.0"" encoding=""UTF-8""?>
<s:Envelope xmlns:s=""http://www.w3.org/2003/05/soap-envelope""
            xmlns:tds=""http://www.onvif.org/ver10/device/wsdl""
            xmlns:tt=""http://www.onvif.org/ver10/schema"">
    {securityHeader}
    <s:Body>
        <tds:SetNTP>
            <tds:FromDHCP>{fromDhcp.ToString().ToLower()}</tds:FromDHCP>
            <tds:NTPManual>
                <tt:Type>DNS</tt:Type>
                <tt:DNSname>{ntpServer}</tt:DNSname>
            </tds:NTPManual>
        </tds:SetNTP>
    </s:Body>
</s:Envelope>";

        var response = await SendOnvifRequestAsync("/onvif/device_service", soapEnvelope);
        return response != null;
    }

    /// <summary>
    /// Get video encoder configurations
    /// </summary>
    public async Task<List<VideoEncoderConfig>> GetVideoEncoderConfigsAsync()
    {
        var securityHeader = GenerateSecurityHeader();
        var soapEnvelope = $@"<?xml version=""1.0"" encoding=""UTF-8""?>
<s:Envelope xmlns:s=""http://www.w3.org/2003/05/soap-envelope"">
    {securityHeader}
    <s:Body>
        <GetVideoEncoderConfigurations xmlns=""http://www.onvif.org/ver10/media/wsdl""/>
    </s:Body>
</s:Envelope>";

        var response = await SendOnvifRequestAsync("/onvif/Media", soapEnvelope);
        if (response == null) return new List<VideoEncoderConfig>();

        var configs = new List<VideoEncoderConfig>();
        var ns = XNamespace.Get("http://www.onvif.org/ver10/media/wsdl");
        var tt = XNamespace.Get("http://www.onvif.org/ver10/schema");

        foreach (var config in response.Descendants(ns + "Configurations"))
        {
            configs.Add(new VideoEncoderConfig
            {
                Token = config.Attribute("token")?.Value ?? "",
                Name = config.Element(tt + "Name")?.Value ?? "",
                Encoding = config.Element(tt + "Encoding")?.Value ?? "",
                Width = int.TryParse(config.Descendants(tt + "Width").FirstOrDefault()?.Value, out var w) ? w : 0,
                Height = int.TryParse(config.Descendants(tt + "Height").FirstOrDefault()?.Value, out var h) ? h : 0,
                BitrateLimit = int.TryParse(config.Descendants(tt + "BitrateLimit").FirstOrDefault()?.Value, out var b) ? b : 0,
                FrameRateLimit = int.TryParse(config.Descendants(tt + "FrameRateLimit").FirstOrDefault()?.Value, out var f) ? f : 0
            });
        }

        return configs;
    }

    /// <summary>
    /// Perform factory reset
    /// </summary>
    /// <param name="hardReset">True for full reset, false to preserve network settings</param>
    public async Task<bool> FactoryResetAsync(bool hardReset = false)
    {
        var resetType = hardReset ? "Hard" : "Soft";
        var securityHeader = GenerateSecurityHeader();
        var soapEnvelope = $@"<?xml version=""1.0"" encoding=""UTF-8""?>
<s:Envelope xmlns:s=""http://www.w3.org/2003/05/soap-envelope""
            xmlns:tds=""http://www.onvif.org/ver10/device/wsdl"">
    {securityHeader}
    <s:Body>
        <tds:SetSystemFactoryDefault>
            <tds:FactoryDefault>{resetType}</tds:FactoryDefault>
        </tds:SetSystemFactoryDefault>
    </s:Body>
</s:Envelope>";

        var response = await SendOnvifRequestAsync("/onvif/device_service", soapEnvelope);
        return response != null;
    }

    /// <summary>
    /// Reboot the device
    /// </summary>
    public async Task<string> RebootAsync()
    {
        var securityHeader = GenerateSecurityHeader();
        var soapEnvelope = $@"<?xml version=""1.0"" encoding=""UTF-8""?>
<s:Envelope xmlns:s=""http://www.w3.org/2003/05/soap-envelope""
            xmlns:tds=""http://www.onvif.org/ver10/device/wsdl"">
    {securityHeader}
    <s:Body>
        <tds:SystemReboot/>
    </s:Body>
</s:Envelope>";

        var response = await SendOnvifRequestAsync("/onvif/device_service", soapEnvelope);
        if (response == null) return "Reboot command sent (no response)";

        var ns = XNamespace.Get("http://www.onvif.org/ver10/device/wsdl");
        return response.Descendants(ns + "Message").FirstOrDefault()?.Value ?? "Rebooting...";
    }

    /// <summary>
    /// Get system date and time
    /// </summary>
    public async Task<SystemDateTime> GetSystemDateTimeAsync()
    {
        var securityHeader = GenerateSecurityHeader();
        var soapEnvelope = $@"<?xml version=""1.0"" encoding=""UTF-8""?>
<s:Envelope xmlns:s=""http://www.w3.org/2003/05/soap-envelope"">
    {securityHeader}
    <s:Body>
        <GetSystemDateAndTime xmlns=""http://www.onvif.org/ver10/device/wsdl""/>
    </s:Body>
</s:Envelope>";

        var response = await SendOnvifRequestAsync("/onvif/device_service", soapEnvelope);
        if (response == null) throw new InvalidOperationException("Failed to get system date/time");

        var tt = XNamespace.Get("http://www.onvif.org/ver10/schema");
        var utcTime = response.Descendants(tt + "UTCDateTime").FirstOrDefault();

        return new SystemDateTime
        {
            DateTimeType = response.Descendants(tt + "DateTimeType").FirstOrDefault()?.Value ?? "",
            TimeZone = response.Descendants(tt + "TZ").FirstOrDefault()?.Value ?? "",
            UtcTime = utcTime != null ? new DateTime(
                int.Parse(utcTime.Element(tt + "Date")?.Element(tt + "Year")?.Value ?? "2000"),
                int.Parse(utcTime.Element(tt + "Date")?.Element(tt + "Month")?.Value ?? "1"),
                int.Parse(utcTime.Element(tt + "Date")?.Element(tt + "Day")?.Value ?? "1"),
                int.Parse(utcTime.Element(tt + "Time")?.Element(tt + "Hour")?.Value ?? "0"),
                int.Parse(utcTime.Element(tt + "Time")?.Element(tt + "Minute")?.Value ?? "0"),
                int.Parse(utcTime.Element(tt + "Time")?.Element(tt + "Second")?.Value ?? "0"),
                DateTimeKind.Utc
            ) : DateTime.UtcNow
        };
    }

    public void Dispose()
    {
        _httpClient?.Dispose();
    }
}

/// <summary>
/// Extended device information
/// </summary>
public class DeviceInformation
{
    public string Manufacturer { get; set; } = "";
    public string Model { get; set; } = "";
    public string FirmwareVersion { get; set; } = "";
    public string SerialNumber { get; set; } = "";
    public string HardwareId { get; set; } = "";
}

/// <summary>
/// Network interface information
/// </summary>
public class NetworkInfo
{
    public string IpAddress { get; set; } = "";
    public string MacAddress { get; set; } = "";
}

/// <summary>
/// NTP configuration
/// </summary>
public class NtpInfo
{
    public bool FromDHCP { get; set; }
    public string NtpServer { get; set; } = "";
}

/// <summary>
/// Video encoder configuration
/// </summary>
public class VideoEncoderConfig
{
    public string Token { get; set; } = "";
    public string Name { get; set; } = "";
    public string Encoding { get; set; } = "";
    public int Width { get; set; }
    public int Height { get; set; }
    public int BitrateLimit { get; set; }
    public int FrameRateLimit { get; set; }

    public override string ToString() => $"{Name}: {Encoding} {Width}x{Height} @ {BitrateLimit}kbps";
}

/// <summary>
/// System date and time information
/// </summary>
public class SystemDateTime
{
    public string DateTimeType { get; set; } = "";
    public string TimeZone { get; set; } = "";
    public DateTime UtcTime { get; set; }
}
