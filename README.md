# Jennov PTZ Camera Control

A C# .NET library for controlling Jennov P87HM85-30X-EAS PTZ cameras and similar Dahua-based IP cameras.

## Features

- **PTZ Control**: Pan, tilt, zoom with precise speed and duration control
- **Preset Management**: Save, recall, and manage camera preset positions
- **Recording**: Start/stop recording and capture snapshots
- **JSON-RPC API**: Full support for Dahua JSON-RPC protocol
- **Easy to Use**: Simple, intuitive API with async/await support

## Supported Models

This library has been tested with:
- Jennov P87HM85-30X-EAS

It should work with other Dahua-based PTZ cameras that use the JSON-RPC API protocol.

## Getting Started

### Prerequisites

- .NET 8.0 or later
- Network access to your camera
- Camera credentials (username and password)

### Installation

1. Clone this repository:
```bash
git clone https://github.com/yourusername/Camera.git
cd Camera
```

2. Build the solution:
```bash
dotnet build
```

3. Run the demo application:
```bash
dotnet run --project JennovCamera.Demo
```

### Configuration

Edit the camera settings in `JennovCamera.Demo/Program.cs`:

```csharp
string cameraIp = "192.168.50.224";  // Your camera IP
int port = 12351;                     // Camera API port
string username = "admin";            // Username
string password = "";                 // Your password
```

Or pass them as command-line arguments:
```bash
dotnet run --project JennovCamera.Demo -- <camera_ip> <username> <password>
```

## Usage Examples

### Basic Connection

```csharp
using JennovCamera;

var client = new CameraClient("192.168.50.224", 12351);
await client.LoginAsync("admin", "password");

// Use the camera...

await client.LogoutAsync();
```

### PTZ Control

```csharp
var ptz = new PTZController(client);

// Pan left for 2 seconds at speed 6
await ptz.PanLeftAsync(speed: 6, durationMs: 2000);

// Tilt up
await ptz.TiltUpAsync(speed: 4, durationMs: 1000);

// Move to specific coordinates (0-100 range)
await ptz.MoveDirectlyAsync(x: 50, y: 75);

// Custom movement
await ptz.MoveContinuouslyAsync(horizontalSpeed: 4, verticalSpeed: 2, timeoutMs: 1500);

// Stop movement
await ptz.StopMoveAsync();
```

### Preset Management

```csharp
var presets = new PresetManager(client);

// Save current position as preset
await presets.SetPresetAsync(index: 1, name: "Front Door");

// Go to a preset
await presets.GotoPresetAsync(index: 1, speed: 8);

// List all presets
var allPresets = await presets.GetPresetsAsync();
foreach (var preset in allPresets)
{
    Console.WriteLine($"[{preset.Index}] {preset.Name}");
}

// Remove a preset
await presets.RemovePresetAsync(index: 1);
```

### Recording

```csharp
var recording = new RecordingManager(client);

// Start recording
await recording.StartRecordingAsync();

// Check status
if (recording.IsRecording)
{
    Console.WriteLine("Recording in progress...");
}

// Stop recording
await recording.StopRecordingAsync();

// Take a snapshot
await recording.TakeSnapshotAsync();
```

## Project Structure

```
Camera/
├── JennovCamera/              # Core library
│   ├── Models/                # Data models
│   │   ├── JsonRpcRequest.cs
│   │   ├── JsonRpcResponse.cs
│   │   ├── LoginResult.cs
│   │   └── PresetInfo.cs
│   ├── CameraClient.cs        # Main API client
│   ├── PTZController.cs       # PTZ control
│   ├── PresetManager.cs       # Preset management
│   └── RecordingManager.cs    # Recording functionality
├── JennovCamera.Demo/         # Demo console application
│   └── Program.cs
├── API_DOCUMENTATION.md       # Complete API reference
└── README.md                  # This file
```

## API Documentation

See [API_DOCUMENTATION.md](API_DOCUMENTATION.md) for complete API reference including:
- All JSON-RPC methods
- Request/response formats
- Parameter details
- Error handling

## Network Protocol

The camera uses:
- **Protocol**: JSON-RPC over HTTP
- **Default Port**: 12351
- **Endpoint**: `/RPC2`
- **Content-Type**: `application/json`

Based on Dahua's proprietary JSON-RPC protocol.

## Reverse Engineering

This library was created by capturing and analyzing network traffic from the camera's web interface using Wireshark. The analysis scripts are included:
- `analyze_pcap.py` - Analyzes packet captures
- `extract_commands.py` - Extracts API commands

## Contributing

Contributions are welcome! Please feel free to submit pull requests or open issues.

## License

MIT License - See LICENSE file for details

## Acknowledgments

- Camera protocol based on Dahua's JSON-RPC API
- Reverse engineered using Wireshark and Scapy

## Troubleshooting

### Cannot connect to camera
- Verify the camera IP address and port
- Ensure the camera is on the same network
- Check firewall settings
- Try accessing the camera's web interface first

### Login fails
- Verify username and password
- Check if the camera requires authentication
- Ensure no other client is connected (some cameras limit concurrent connections)

### PTZ commands don't work
- Some cameras require calling `ptz.start()` before PTZ operations
- Verify the camera supports PTZ (some models are fixed)
- Check camera logs for errors

### Recording doesn't save
- Ensure the camera has storage configured (SD card or NAS)
- Check available storage space
- Verify recording settings in camera configuration

## Support

For issues and questions:
1. Check the API documentation
2. Review camera logs
3. Capture network traffic for analysis
4. Open an issue on GitHub

## Future Enhancements

Potential additions:
- [ ] Video streaming support (RTSP/WebSocket)
- [ ] Tour management
- [ ] Pattern recording/playback
- [ ] Event handling
- [ ] Configuration management
- [ ] GUI application (WPF/WinForms)
- [ ] Multi-camera support
