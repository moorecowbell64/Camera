# Jennov Camera API Reference

## Camera Information
- **Model**: J800S_AF (ONVIF_ICAMERA)
- **Firmware**: V3.2.2.2
- **Hardware**: MC800S
- **Serial**: EF00000000E63633

## Supported Protocols

This camera **ONLY** supports **ONVIF SOAP** protocol. The web interface JavaScript
contains JSON-RPC API code, but this specific camera model does not respond to it.

---

## ONVIF API (Supported)

### Device Service (`/onvif/device_service`)

| Method | Description | Status |
|--------|-------------|--------|
| GetDeviceInformation | Get manufacturer, model, firmware, serial | ✅ Working |
| GetCapabilities | Get device capabilities | ✅ Working |
| GetServices | List available ONVIF services | ✅ Working |
| GetScopes | Get device scopes (name, location) | ✅ Working |
| GetHostname | Get device hostname | ✅ Working |
| SetHostname | Set device hostname | ✅ Working |
| GetNetworkInterfaces | Get IP and MAC addresses | ✅ Working |
| GetNTP | Get NTP configuration | ✅ Working |
| SetNTP | Set NTP server | ✅ Working |
| GetSystemDateAndTime | Get system time | ✅ Working |
| SetSystemFactoryDefault | Factory reset (Hard/Soft) | ✅ Working |
| SystemReboot | Reboot camera | ✅ Working |

### Media Service (`/onvif/Media`)

| Method | Description | Status |
|--------|-------------|--------|
| GetProfiles | Get media profiles | ✅ Working |
| GetVideoSources | Get video sources | ✅ Working |
| GetVideoEncoderConfigurations | Get encoder settings | ✅ Working |
| SetVideoEncoderConfiguration | Modify encoder (res, bitrate, fps) | ✅ Working |
| GetOSDs | Get OSD configurations | ✅ Working |
| SetOSD | Modify OSD text (camera title) | ✅ Working |
| GetSnapshotUri | Get snapshot URL | ✅ Working |
| GetStreamUri | Get RTSP stream URL | ✅ Working |

### PTZ Service (`/onvif/PTZ`)

| Method | Description | Status |
|--------|-------------|--------|
| GetPresets | Get all presets (255 max) | ✅ Working |
| GotoPreset | Move to preset position | ✅ Working |
| SetPreset | Save current position as preset | ✅ Working |
| RemovePreset | Delete a preset | ✅ Working |
| ContinuousMove | PTZ continuous movement | ✅ Working |
| AbsoluteMove | Move to absolute position | ✅ Working |
| RelativeMove | Move relative to current position | ✅ Working |
| Stop | Stop PTZ movement | ✅ Working |
| GetStatus | Get PTZ status | ✅ Working |
| GotoHomePosition | Move to home position | ❌ Not Supported |
| SetHomePosition | Set home position | ❌ Not Supported |

### Imaging Service (`/onvif/Imaging`)

| Method | Description | Status |
|--------|-------------|--------|
| GetImagingSettings | Brightness, contrast, etc. | ❌ NoImagingForSource |
| SetImagingSettings | Adjust image settings | ❌ Not Supported |

**Note**: This camera does not support ONVIF Imaging service.

---

## JSON-RPC API (NOT Supported on this camera)

The web interface JavaScript contains code for a JSON-RPC API at `/IPC` endpoint.
This is **NOT functional** on the J800S_AF model but documented here for reference.

### Authentication (Not Working)
```
POST /ipcLogin
Content-Type: text/xml

<?xml version="1.0"?>
<soap:Envelope xmlns:soap="http://www.w3.org/2001/12/soap-envelope">
    <soap:Header>
        <userid>{DES_encrypted_username}</userid>
        <passwd>{DES_encrypted_password}</passwd>
    </soap:Header>
    <soap:Body></soap:Body>
</soap:Envelope>
```

### Available Methods (from rpcCore.js)

#### Configuration Management
- `configManager.getConfig` - Get configuration by name
- `configManager.setConfig` - Set configuration
- `configManager.getDefault` - Get default configuration
- `configManager.restore` - Factory reset specific configs
- `configManager.restoreExcept` - Reset except specified configs

#### Device Info (MagicBox)
- `magicBox.getSystemInfo` - System information
- `magicBox.getDeviceType` - Device type
- `magicBox.getSerialNo` - Serial number
- `magicBox.reboot` - Reboot device

#### PTZ Control
- `ptz.getPresets` - Get presets
- `ptz.setPreset` - Save preset
- `ptz.gotoPreset` - Go to preset
- `ptz.removePreset` - Delete preset
- `ptz.start` / `ptz.stop` - Movement control
- `ptz.startTour` / `ptz.stopTour` - Tour control
- `ptz.startScan` / `ptz.stopScan` - Auto scan
- `ptz.reset` - Reset PTZ

#### User Management
- `userManager.getUserInfoAll` - List users
- `userManager.addUser` / `deleteUser` - Manage users
- `userManager.modifyPassword` - Change password

#### Event/Alarm
- `eventManager.attach` / `detach` - Event subscription
- `alarm.getInState` / `getOutState` - Alarm status

#### Storage
- `storage.getDeviceNames` - List storage devices
- `devStorage.formatPartition` - Format storage

#### Recording
- `recordManager.start` / `stop` - Recording control
- `snapManager.start` / `stop` - Snapshot control

---

## Configuration Names (from web interface)

These configuration names are used with `configManager.getConfig`:

| Name | Description |
|------|-------------|
| VideoColor | Brightness, contrast, saturation |
| VideoInOptions | Video input options |
| Encode | Video encoding settings |
| Network | Network configuration |
| General | General settings |
| NTP | NTP settings |
| Locales | Language/locale |
| ChannelTitle | Channel name |
| PTZ | PTZ settings |
| Storage | Storage configuration |
| RecordMode | Recording mode |
| Snap | Snapshot settings |
| MotionDetect | Motion detection |
| AudioDetect | Audio detection |
| BlindDetect | Tamper detection |
| AlarmIn | Alarm input |
| AlarmOut | Alarm output |
| Email | Email notifications |
| Lighting | IR/White light settings |
| DayNightColor | Day/night mode |
| WhiteBalance | White balance |
| FlipRotate | Image flip/rotate |

---

## RTSP Streams

| Stream | URL | Resolution |
|--------|-----|------------|
| Main | `rtsp://admin:password@192.168.50.224:554/stream1` | 3840x2160 (4K) |
| Sub | `rtsp://admin:password@192.168.50.224:554/stream2` | 720x480 |

---

## Video Encoder Configuration

### Main Stream (VideoEncodeMain)
- **Encoding**: H264 High profile
- **Resolution**: 3840x2160 (4K)
- **Frame Rate**: 1-60 fps (default 20)
- **Bitrate**: 0-8192 kbps (0 = auto)
- **GOP Length**: 1-200 (default 40)

### Supported Resolutions
| Resolution | Aspect |
|------------|--------|
| 3840x2160 | 16:9 (4K) |
| 2592x1944 | 4:3 |
| 2560x1440 | 16:9 |
| 2304x1296 | 16:9 |
| 1920x1080 | 16:9 (1080p) |
| 1280x720 | 16:9 (720p) |

---

## OSD Configuration

The camera supports two OSD elements:

| Token | Type | Position | Description |
|-------|------|----------|-------------|
| osd_title | Plain Text | UpperLeft | Camera name/title |
| osd_time | DateAndTime | LowerRight | Date/time overlay |

### OSD Positions
- UpperLeft, UpperRight
- LowerLeft, LowerRight
- Custom (x,y coordinates)

---

## Implementation Status

### Python
- ✅ camera_gui.py - Full PTZ control GUI
- ✅ camera_control_ultra.py - Optimized control library
- ✅ onvif_device_manager.py - Device management discovery

### C# (.NET 8)
- ✅ OnvifClient - PTZ, snapshots, streaming
- ✅ DeviceManager - Factory reset, reboot, settings
- ✅ RecordingManager - RTSP recording via OpenCvSharp
- ✅ PresetManager - Preset management
- ✅ PTZController - Movement control
