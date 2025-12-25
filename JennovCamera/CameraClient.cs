namespace JennovCamera;

public class CameraClient : IDisposable
{
    private readonly OnvifClient _onvifClient;
    private RpcClient? _rpcClient;
    private string _cameraIp;
    private int _port;
    private bool _isConnected;

    public bool IsConnected => _isConnected;
    public OnvifClient Onvif => _onvifClient;
    public RpcClient? Rpc => _rpcClient;

    public CameraClient(string cameraIp, int port = 80)
    {
        _cameraIp = cameraIp;
        _port = port;
        _onvifClient = new OnvifClient(cameraIp, "", "", port);
    }

    public async Task<bool> LoginAsync(string username, string password)
    {
        _onvifClient.SetCredentials(username, password);
        _isConnected = await _onvifClient.ConnectAsync();

        if (_isConnected)
        {
            // Also create RPC client for extended features
            _rpcClient = new RpcClient(_cameraIp, username, password, _port);
        }

        return _isConnected;
    }

    public Task<bool> LogoutAsync()
    {
        _isConnected = false;
        _rpcClient?.Dispose();
        _rpcClient = null;
        return Task.FromResult(true);
    }

    public void Dispose()
    {
        _onvifClient?.Dispose();
        _rpcClient?.Dispose();
    }
}
