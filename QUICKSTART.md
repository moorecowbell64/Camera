# Quick Start Guide

## Running the Demo Application

1. **Update camera settings** in `JennovCamera.Demo/Program.cs`:
   ```csharp
   string cameraIp = "192.168.50.224";  // Change to your camera's IP
   string username = "admin";            // Your username
   string password = "";                 // Your password
   ```

2. **Run the application**:
   ```bash
   cd "C:\Users\moore\Documents\GitHub\Camera"
   dotnet run --project JennovCamera.Demo
   ```

3. **Or pass credentials as arguments**:
   ```bash
   dotnet run --project JennovCamera.Demo -- 192.168.50.224 admin yourpassword
   ```

## Quick Test - PTZ Control

Once the app is running:
1. Select option `1` (PTZ Control)
2. Try option `1` (Pan Left) - enter speed `4` and duration `2000`
3. Your camera should pan left for 2 seconds!

## Quick Test - Presets

1. Position your camera using PTZ controls
2. Go back to main menu, select `2` (Preset Management)
3. Select `3` (Set/Save Preset)
4. Enter index `1` and name `"Test Position"`
5. Move camera to another position
6. Select `2` (Go to Preset), enter `1`
7. Camera should return to saved position!

## Using in Your Own Code

Create a new project:
```bash
dotnet new console -n MyCameraApp
cd MyCameraApp
dotnet add reference ../JennovCamera/JennovCamera.csproj
```

Simple example (`Program.cs`):
```csharp
using JennovCamera;

var camera = new CameraClient("192.168.50.224");
await camera.LoginAsync("admin", "yourpassword");

var ptz = new PTZController(camera);

// Pan around
await ptz.PanLeftAsync(speed: 5, durationMs: 2000);
await Task.Delay(500);
await ptz.PanRightAsync(speed: 5, durationMs: 2000);

await camera.LogoutAsync();
Console.WriteLine("Done!");
```

Run it:
```bash
dotnet run
```

## Common Camera IPs

Default IPs for IP cameras are often:
- 192.168.1.108
- 192.168.0.108
- Check your router's device list
- Use your camera's mobile app to find the IP

## Finding Your Camera's Port

Most Dahua-based cameras use:
- **Port 12351** (custom web API) - This library uses this
- Port 80 (standard web interface)
- Port 554 (RTSP streaming)
- Port 37777 (proprietary protocol)

If port 12351 doesn't work, check:
1. Camera's web interface under Network settings
2. Camera documentation
3. Use `nmap` to scan: `nmap -p 1-65535 <camera_ip>`

## Troubleshooting

**"Failed to login"**
- Double-check IP address: `ping 192.168.50.224`
- Verify credentials in camera web interface first
- Ensure camera is powered on and connected

**"Connection refused"**
- Confirm port 12351 is correct for your camera
- Check if firewall is blocking connections
- Try accessing `http://<camera_ip>:12351` in browser

**PTZ doesn't move**
- Some cameras need `await ptz.StartAsync()` first
- Verify camera model supports PTZ
- Check if camera is in a special mode (tour, pattern, etc.)

## Next Steps

1. Read [README.md](README.md) for detailed usage
2. Check [API_DOCUMENTATION.md](API_DOCUMENTATION.md) for all available methods
3. Explore the demo app menus to see all features
4. Build your own automation scripts!

## Example Automation Scripts

**Patrol Pattern**:
```csharp
var ptz = new PTZController(camera);
var presets = new PresetManager(camera);

// Set up 4 preset positions first (1-4)
// Then create a patrol:

while (true)
{
    for (int i = 1; i <= 4; i++)
    {
        await presets.GotoPresetAsync(i, speed: 8);
        await Task.Delay(5000); // Stay for 5 seconds
    }
}
```

**Motion Detection Response** (pseudo-code):
```csharp
// When motion detected at zone:
await presets.GotoPresetAsync(zonePresetNumber);
await recording.StartRecordingAsync();
await Task.Delay(30000); // Record for 30 seconds
await recording.StopRecordingAsync();
await recording.TakeSnapshotAsync();
```

Happy camera controlling!
