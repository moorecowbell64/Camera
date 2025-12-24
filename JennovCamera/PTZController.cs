namespace JennovCamera;

public class PTZController
{
    private readonly CameraClient _client;

    public PTZController(CameraClient client)
    {
        _client = client;
    }

    /// <summary>
    /// Move camera continuously. Speed range: -1.0 to 1.0
    /// </summary>
    /// <param name="horizontalSpeed">Positive = right, Negative = left</param>
    /// <param name="verticalSpeed">Positive = up, Negative = down</param>
    /// <param name="zoomSpeed">Positive = zoom in, Negative = zoom out</param>
    public async Task<bool> MoveContinuouslyAsync(float horizontalSpeed, float verticalSpeed, float zoomSpeed = 0)
    {
        return await _client.Onvif.ContinuousMoveAsync(horizontalSpeed, verticalSpeed, zoomSpeed);
    }

    /// <summary>
    /// Stop camera movement
    /// </summary>
    public async Task<bool> StopMoveAsync()
    {
        return await _client.Onvif.StopAsync();
    }

    /// <summary>
    /// Pan left continuously (normalized speed: 0.0 to 1.0)
    /// </summary>
    public async Task<bool> PanLeftAsync(float speed = 0.5f, int durationMs = 1000)
    {
        await MoveContinuouslyAsync(-speed, 0, 0);
        await Task.Delay(durationMs);
        return await StopMoveAsync();
    }

    /// <summary>
    /// Pan right continuously (normalized speed: 0.0 to 1.0)
    /// </summary>
    public async Task<bool> PanRightAsync(float speed = 0.5f, int durationMs = 1000)
    {
        await MoveContinuouslyAsync(speed, 0, 0);
        await Task.Delay(durationMs);
        return await StopMoveAsync();
    }

    /// <summary>
    /// Tilt up continuously (normalized speed: 0.0 to 1.0)
    /// </summary>
    public async Task<bool> TiltUpAsync(float speed = 0.5f, int durationMs = 1000)
    {
        await MoveContinuouslyAsync(0, speed, 0);
        await Task.Delay(durationMs);
        return await StopMoveAsync();
    }

    /// <summary>
    /// Tilt down continuously (normalized speed: 0.0 to 1.0)
    /// </summary>
    public async Task<bool> TiltDownAsync(float speed = 0.5f, int durationMs = 1000)
    {
        await MoveContinuouslyAsync(0, -speed, 0);
        await Task.Delay(durationMs);
        return await StopMoveAsync();
    }

    /// <summary>
    /// Zoom in (normalized speed: 0.0 to 1.0)
    /// </summary>
    public async Task<bool> ZoomInAsync(float speed = 0.5f, int durationMs = 1000)
    {
        await MoveContinuouslyAsync(0, 0, speed);
        await Task.Delay(durationMs);
        return await StopMoveAsync();
    }

    /// <summary>
    /// Zoom out (normalized speed: 0.0 to 1.0)
    /// </summary>
    public async Task<bool> ZoomOutAsync(float speed = 0.5f, int durationMs = 1000)
    {
        await MoveContinuouslyAsync(0, 0, -speed);
        await Task.Delay(durationMs);
        return await StopMoveAsync();
    }

    #region Fast (Fire-and-Forget) Methods

    /// <summary>
    /// Start continuous movement immediately (fire-and-forget)
    /// </summary>
    public void ContinuousMoveFast(float pan, float tilt, float zoom = 0)
    {
        _client.Onvif.ContinuousMoveFast(pan, tilt, zoom);
    }

    /// <summary>
    /// Stop all PTZ movement immediately (fire-and-forget)
    /// </summary>
    public void StopFast()
    {
        _client.Onvif.StopFast();
    }

    /// <summary>
    /// Move focus toward near objects (fire-and-forget)
    /// </summary>
    public void FocusNearFast(float speed = 1.0f)
    {
        _client.Onvif.FocusNearFast(speed);
    }

    /// <summary>
    /// Move focus toward far objects (fire-and-forget)
    /// </summary>
    public void FocusFarFast(float speed = 1.0f)
    {
        _client.Onvif.FocusFarFast(speed);
    }

    /// <summary>
    /// Stop focus movement (fire-and-forget)
    /// </summary>
    public void FocusStopFast()
    {
        _client.Onvif.FocusStopFast();
    }

    /// <summary>
    /// Trigger auto-focus (fire-and-forget)
    /// </summary>
    public void AutoFocusFast()
    {
        _client.Onvif.AutoFocusFast();
    }

    #endregion
}
