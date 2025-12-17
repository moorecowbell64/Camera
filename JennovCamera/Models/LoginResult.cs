using System.Text.Json.Serialization;

namespace JennovCamera.Models;

public class LoginResult
{
    [JsonPropertyName("session")]
    public int Session { get; set; }

    [JsonPropertyName("keepAliveInterval")]
    public int KeepAliveInterval { get; set; }
}
