namespace JennovCamera;

public class CameraClient : IDisposable
{
    private readonly OnvifClient _onvifClient;
    private bool _isConnected;

    public bool IsConnected => _isConnected;
    public OnvifClient Onvif => _onvifClient;

    public CameraClient(string cameraIp, int port = 80)
    {
        _onvifClient = new OnvifClient(cameraIp, "", "", port);
    }

    public async Task<bool> LoginAsync(string username, string password)
    {
        _onvifClient.SetCredentials(username, password);
        _isConnected = await _onvifClient.ConnectAsync();
        return _isConnected;
    }

    public Task<bool> LogoutAsync()
    {
        _isConnected = false;
        return Task.FromResult(true);
    }

    public void Dispose()
    {
        _onvifClient?.Dispose();
    }
}
