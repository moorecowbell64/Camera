using System.Net.WebSockets;
using System.Text;

namespace JennovCamera;

/// <summary>
/// WebSocket client for Dahua camera streaming
/// Ports:
/// - 12351: Main stream
/// - 12352: Sub stream
/// - 12353: Playback
/// - 12354: Third stream
/// </summary>
public class WebSocketClient : IDisposable
{
    private readonly string _cameraIp;
    private readonly string _username;
    private readonly string _password;
    private ClientWebSocket? _webSocket;
    private CancellationTokenSource? _cts;
    private bool _isConnected;

    public bool IsConnected => _isConnected;

    public event EventHandler<byte[]>? DataReceived;
    public event EventHandler<Exception>? Error;
    public event EventHandler? Disconnected;

    public WebSocketClient(string cameraIp, string username, string password)
    {
        _cameraIp = cameraIp;
        _username = username;
        _password = password;
    }

    /// <summary>
    /// Connect to main stream (port 12351)
    /// </summary>
    public Task<bool> ConnectMainStreamAsync() => ConnectAsync(12351);

    /// <summary>
    /// Connect to sub stream (port 12352)
    /// </summary>
    public Task<bool> ConnectSubStreamAsync() => ConnectAsync(12352);

    /// <summary>
    /// Connect to playback stream (port 12353)
    /// </summary>
    public Task<bool> ConnectPlaybackAsync() => ConnectAsync(12353);

    /// <summary>
    /// Connect to a WebSocket stream
    /// </summary>
    public async Task<bool> ConnectAsync(int port)
    {
        try
        {
            _cts = new CancellationTokenSource();
            _webSocket = new ClientWebSocket();

            // Add authentication header
            var authBytes = Encoding.UTF8.GetBytes($"{_username}:{_password}");
            var authHeader = Convert.ToBase64String(authBytes);
            _webSocket.Options.SetRequestHeader("Authorization", $"Basic {authHeader}");

            var uri = new Uri($"ws://{_cameraIp}:{port}/");
            await _webSocket.ConnectAsync(uri, _cts.Token);

            _isConnected = _webSocket.State == WebSocketState.Open;

            if (_isConnected)
            {
                Console.WriteLine($"WebSocket connected to port {port}");
                _ = ReceiveLoopAsync(_cts.Token);
            }

            return _isConnected;
        }
        catch (Exception ex)
        {
            Console.WriteLine($"WebSocket connection error: {ex.Message}");
            Error?.Invoke(this, ex);
            return false;
        }
    }

    private async Task ReceiveLoopAsync(CancellationToken ct)
    {
        var buffer = new byte[1024 * 1024]; // 1MB buffer for video data

        try
        {
            while (!ct.IsCancellationRequested && _webSocket?.State == WebSocketState.Open)
            {
                var result = await _webSocket.ReceiveAsync(new ArraySegment<byte>(buffer), ct);

                if (result.MessageType == WebSocketMessageType.Close)
                {
                    _isConnected = false;
                    Disconnected?.Invoke(this, EventArgs.Empty);
                    break;
                }

                if (result.Count > 0)
                {
                    var data = new byte[result.Count];
                    Array.Copy(buffer, data, result.Count);
                    DataReceived?.Invoke(this, data);
                }
            }
        }
        catch (OperationCanceledException)
        {
            // Normal cancellation
        }
        catch (Exception ex)
        {
            Console.WriteLine($"WebSocket receive error: {ex.Message}");
            Error?.Invoke(this, ex);
        }
        finally
        {
            _isConnected = false;
            Disconnected?.Invoke(this, EventArgs.Empty);
        }
    }

    /// <summary>
    /// Send data to the WebSocket
    /// </summary>
    public async Task<bool> SendAsync(byte[] data)
    {
        if (_webSocket?.State != WebSocketState.Open)
            return false;

        try
        {
            await _webSocket.SendAsync(new ArraySegment<byte>(data), WebSocketMessageType.Binary, true, _cts?.Token ?? CancellationToken.None);
            return true;
        }
        catch (Exception ex)
        {
            Console.WriteLine($"WebSocket send error: {ex.Message}");
            Error?.Invoke(this, ex);
            return false;
        }
    }

    /// <summary>
    /// Send a text message to the WebSocket
    /// </summary>
    public async Task<bool> SendTextAsync(string message)
    {
        if (_webSocket?.State != WebSocketState.Open)
            return false;

        try
        {
            var data = Encoding.UTF8.GetBytes(message);
            await _webSocket.SendAsync(new ArraySegment<byte>(data), WebSocketMessageType.Text, true, _cts?.Token ?? CancellationToken.None);
            return true;
        }
        catch (Exception ex)
        {
            Console.WriteLine($"WebSocket send error: {ex.Message}");
            Error?.Invoke(this, ex);
            return false;
        }
    }

    /// <summary>
    /// Disconnect from the WebSocket
    /// </summary>
    public async Task DisconnectAsync()
    {
        if (_webSocket?.State == WebSocketState.Open)
        {
            try
            {
                await _webSocket.CloseAsync(WebSocketCloseStatus.NormalClosure, "Closing", CancellationToken.None);
            }
            catch (Exception ex)
            {
                Console.WriteLine($"WebSocket close error: {ex.Message}");
            }
        }

        _cts?.Cancel();
        _isConnected = false;
    }

    public void Dispose()
    {
        _cts?.Cancel();
        _cts?.Dispose();
        _webSocket?.Dispose();
    }
}

/// <summary>
/// Dahua WebSocket stream types
/// </summary>
public enum WebSocketStreamType
{
    Main = 12351,
    Sub = 12352,
    Playback = 12353,
    Third = 12354
}
