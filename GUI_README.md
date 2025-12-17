# Jennov PTZ Camera Control - GUI Application

## Beautiful, Modern Interface with Live Video Streaming

![Camera GUI](https://img.shields.io/badge/Python-3.8+-blue.svg) ![PyQt5](https://img.shields.io/badge/PyQt5-GUI-green.svg) ![OpenCV](https://img.shields.io/badge/OpenCV-Video-red.svg)

A professional-grade graphical interface for controlling your Jennov PTZ camera with real-time video streaming and recording capabilities.

## Features

### 🎥 Live Video Streaming
- **Real-time RTSP video feed** from your camera
- High-quality 1080p streaming
- Low-latency display
- Automatic reconnection

### 🎮 PTZ Controls
- **Directional pad** for pan and tilt
- **Zoom in/out** controls
- **Adjustable speed slider** (0.1 - 1.0)
- **Press and hold** for continuous movement
- **Instant stop** button

### 📍 Preset Management
- Quick access to **8 preset positions**
- One-click **goto preset**
- Smooth transitions

### ⏺️ Recording Features
- **Video recording** to MP4 format
- **Snapshot capture** with timestamp
- Automatic file naming
- Recording status indicator

### 🎨 Modern Interface
- **Dark theme** for reduced eye strain
- **Color-coded status indicators**
- **Responsive design**
- **Professional look and feel**

## Quick Start

### Method 1: Double-click the launcher
```
start_camera_gui.bat
```

### Method 2: Run with Python
```bash
cd "C:\Users\moore\Documents\GitHub\Camera"
python camera_gui.py
```

## Interface Guide

### Main Window Layout

```
┌─────────────────────────────────────────────────────────┐
│                  Jennov PTZ Camera Control              │
├──────────────────────────────┬──────────────────────────┤
│                              │   Connection             │
│                              │  [🔌 Connect Camera]     │
│       Live Video Feed        │                          │
│        (960 x 540)           │   PTZ Controls           │
│                              │   Speed: [====o====]     │
│                              │        ▲                 │
│                              │     ◄  ⏹  ►              │
│                              │        ▼                 │
│                              │   [🔍+ Zoom] [🔍- Zoom]  │
├──────────────────────────────┤                          │
│ ● Connected   ⏺ Not Recording│   Presets                │
└──────────────────────────────┤   [Select: 1▼]          │
                               │   [📍 Go to Preset]      │
                               │                          │
                               │   Recording              │
                               │   [⏺ Start Recording]    │
                               │   [📷 Take Snapshot]     │
                               └──────────────────────────┘
```

## Controls Reference

### Connection Panel
| Control | Function |
|---------|----------|
| Connect Camera | Establishes connection to camera and starts video stream |

### PTZ Control Panel
| Control | Function |
|---------|----------|
| Speed Slider | Adjusts movement speed (0.1 - 1.0) |
| ▲ Up | Tilts camera up (press and hold) |
| ▼ Down | Tilts camera down (press and hold) |
| ◄ Left | Pans camera left (press and hold) |
| ► Right | Pans camera right (press and hold) |
| ⏹ Stop | Immediately stops all movement |
| 🔍+ Zoom In | Zooms in (press and hold) |
| 🔍- Zoom Out | Zooms out (press and hold) |

### Preset Panel
| Control | Function |
|---------|----------|
| Preset Dropdown | Select preset number (1-8) |
| Go to Preset | Move camera to selected preset position |

### Recording Panel
| Control | Function |
|---------|----------|
| Start/Stop Recording | Records video to MP4 file with timestamp |
| Take Snapshot | Captures current frame as JPG image |

## Status Indicators

### Connection Status
- **● Connected** (Green) - Camera connected and streaming
- **● Connecting...** (Orange) - Attempting connection
- **● Disconnected** (Red) - No connection
- **● Connection Failed** (Red) - Connection error

### Recording Status
- **⏺ Not Recording** (Gray) - No active recording
- **⏺ Recording: filename.mp4** (Red) - Currently recording

## Using the Application

### 1. Connect to Camera

1. Launch the application
2. Click **"🔌 Connect Camera"**
3. Wait for connection (status will show "● Connected")
4. Video feed will appear automatically

### 2. Control Camera Movement

**Basic Movement:**
- Click and **hold** direction buttons (▲▼◄►)
- Release to stop
- Or click **⏹ Stop** button

**Adjust Speed:**
- Move the **Speed slider** left (slower) or right (faster)
- Current speed displays next to slider

**Zoom:**
- Click and **hold** 🔍+ or 🔍- buttons
- Release to stop zooming

### 3. Use Presets

1. Select preset number from dropdown (1-8)
2. Click **"📍 Go to Preset"**
3. Camera will smoothly move to saved position

### 4. Record Video

**Start Recording:**
1. Click **"⏺ Start Recording"**
2. Button turns red
3. Status shows recording filename
4. Video saves in MP4 format

**Stop Recording:**
1. Click **"⏹ Stop Recording"**
2. Confirmation shows save location
3. File: `recording_YYYYMMDD_HHMMSS.mp4`

### 5. Take Snapshots

1. Click **"📷 Take Snapshot"**
2. Current frame saves as JPG
3. File: `snapshot_YYYYMMDD_HHMMSS.jpg`

## Configuration

### Camera Settings

Edit these values at the top of `camera_gui.py`:

```python
self.camera_ip = "192.168.50.224"    # Your camera's IP
self.camera_user = "admin"            # Username
self.camera_pass = "hydroLob99"       # Password
self.rtsp_url = f"rtsp://{self.camera_user}:{self.camera_pass}@{self.camera_ip}:554/stream1"
```

### RTSP Stream Options

Your camera may have multiple streams:
- `stream1` - Main stream (1080p, higher quality)
- `stream2` - Sub stream (720p, lower bandwidth)

Change in the `rtsp_url` line to use different streams.

## Keyboard Shortcuts

While the GUI is focused:
- **Arrows** - Pan/Tilt (if focus not on buttons)
- **+** - Zoom in (if implemented)
- **-** - Zoom out (if implemented)
- **Space** - Stop movement (if implemented)
- **Ctrl+Q** - Quit application

## File Output

### Recording Files
- **Location**: Same folder as camera_gui.py
- **Format**: MP4 (H.264 codec)
- **Resolution**: 1920x1080 (from camera)
- **Frame rate**: 20 FPS
- **Naming**: `recording_YYYYMMDD_HHMMSS.mp4`

### Snapshot Files
- **Location**: Same folder as camera_gui.py
- **Format**: JPEG
- **Resolution**: Matches video display size
- **Naming**: `snapshot_YYYYMMDD_HHMMSS.jpg`

## Troubleshooting

### Video Feed Issues

**"No Video Feed" displayed:**
1. Check camera IP address is correct
2. Verify camera is powered on
3. Test RTSP URL in VLC Media Player:
   ```
   rtsp://admin:hydroLob99@192.168.50.224:554/stream1
   ```
4. Check network connection
5. Verify firewall isn't blocking port 554

**Video is laggy or frozen:**
1. Try sub-stream instead of main stream
2. Check network bandwidth
3. Reduce speed slider (lowers CPU usage)
4. Close other applications

**Black screen but connection works:**
1. Camera may need a few seconds to start streaming
2. Try disconnecting and reconnecting
3. Restart camera hardware

### PTZ Control Issues

**Camera doesn't move:**
1. Ensure "● Connected" status is green
2. Check that camera supports PTZ (not all do)
3. Try increasing speed slider
4. Verify camera isn't in a locked mode

**Movement is jerky:**
1. Lower the speed slider
2. Click and hold buttons (don't rapid-click)
3. Use the Stop button between movements

**Presets don't work:**
1. Verify presets are configured in camera
2. Check preset numbers match camera configuration
3. Some cameras use preset names instead of numbers

### Recording Issues

**Recording fails to start:**
1. Check disk space
2. Verify write permissions in folder
3. Ensure OpenCV is properly installed:
   ```bash
   pip install --upgrade opencv-python
   ```

**Recording file is corrupted:**
1. Always use "Stop Recording" button (don't close app while recording)
2. Ensure stable video feed before recording
3. Check available disk space

**Large file sizes:**
- 1080p video at 20 FPS uses ~5-10 MB per minute
- Use sub-stream for smaller files
- Consider reducing recording time

### Connection Issues

**"Connection Failed" error:**
1. Verify camera IP: `ping 192.168.50.224`
2. Check username/password
3. Ensure camera is on same network
4. Test camera web interface: `http://192.168.50.224`

**Camera connects but PTZ doesn't work:**
1. Camera may not support ONVIF PTZ
2. Try the Python command-line version to verify
3. Check camera ONVIF settings (may need to enable)

## Advanced Usage

### Custom Speed Profiles

You can modify the speed ranges in `camera_gui.py`:

```python
# Line ~23: PTZ state
self.ptz_speed = 0.5      # Default PTZ speed
self.zoom_speed = 0.3     # Default zoom speed
```

### Multiple Cameras

To control multiple cameras:

1. Run multiple instances of the GUI
2. Edit `camera_ip` for each instance
3. Or create a multi-camera version (contact for help)

### Integration with Other Systems

The GUI can be integrated into larger systems:

```python
from camera_gui import CameraGUI
from PyQt5.QtWidgets import QApplication

# Create your main application
app = QApplication(sys.argv)

# Embed camera GUI
camera_widget = CameraGUI()
camera_widget.show()

# ... rest of your application code
```

## System Requirements

### Minimum Requirements
- **OS**: Windows 10, Linux, macOS
- **Python**: 3.8 or higher
- **RAM**: 2 GB
- **Network**: 10 Mbps (for 1080p streaming)

### Recommended Requirements
- **OS**: Windows 10/11
- **Python**: 3.10 or higher
- **RAM**: 4 GB or more
- **Network**: 50+ Mbps
- **GPU**: Any modern GPU (for smooth video)

## Dependencies

The application requires:

```
onvif-zeep>=0.2.12  - ONVIF camera control
opencv-python>=4.8.0 - Video capture and processing
PyQt5>=5.15.9       - GUI framework
numpy>=1.24.0       - Array operations
```

Install all at once:
```bash
pip install -r requirements.txt
```

## Tips for Best Experience

### Video Quality
- Use **main stream** (stream1) for best quality
- Use **sub stream** (stream2) for lower latency
- Position camera before starting recording

### PTZ Control
- **Hold buttons** for smooth continuous movement
- Use **lower speeds** (0.2-0.4) for precise positioning
- Use **higher speeds** (0.7-1.0) for quick movements
- Always **stop** before changing direction

### Recording
- Ensure **stable connection** before recording
- Record in **short segments** (5-10 minutes)
- Check **disk space** before long recordings
- Use **snapshots** for still images instead of video

### Network Performance
- Connect camera via **wired Ethernet** (not WiFi)
- Close **other network applications**
- Use **dedicated network** if possible
- Consider **sub-stream** for remote access

## Customization

### Change Theme Colors

Edit the stylesheet in `camera_gui.py` (line ~40):

```python
# Primary color (currently cyan)
#3daee9 -> #your_color

# Background colors
#2b2b2b -> #your_background
#1e1e1e -> #your_panel_background
```

### Modify Button Layout

The PTZ buttons are in a grid layout (line ~180):

```python
dir_grid = QGridLayout()
# Modify grid positions as needed
```

### Add Custom Features

The code is modular and easy to extend:
- Add new buttons in the UI setup
- Connect to camera methods
- Implement additional ONVIF features

## Support

### Common Questions

**Q: Can I use this with other camera brands?**
A: Yes! Any ONVIF-compliant PTZ camera will work. Just change the IP address and credentials.

**Q: Does this work on Linux/Mac?**
A: Yes! Python and PyQt5 are cross-platform. Install dependencies and run normally.

**Q: Can I control multiple cameras?**
A: Currently one camera per instance. Run multiple instances for multiple cameras.

**Q: How do I set presets?**
A: Use your camera's web interface to configure presets 1-8, then use the GUI to recall them.

### Getting Help

1. Check troubleshooting section above
2. Test with command-line version (`camera_control.py`)
3. Verify camera works in web browser
4. Check Python and package versions

### Reporting Issues

If you encounter problems:

1. Note exact error message
2. Check Python version: `python --version`
3. Check package versions: `pip list`
4. Test RTSP URL in VLC Player
5. Include camera model and firmware version

## Future Enhancements

Planned features:
- [ ] Multi-camera support
- [ ] Pattern recording/playback
- [ ] Motion detection zones
- [ ] Email/notification alerts
- [ ] Cloud recording
- [ ] Mobile app companion
- [ ] Web-based version
- [ ] Preset management (add/edit/delete)
- [ ] Tour creation interface
- [ ] Video analytics integration

## License

MIT License - Free to use and modify!

## Credits

**Developed by**: Reverse engineered from Jennov P87HM85-30X-EAS
**Framework**: PyQt5
**Video**: OpenCV
**Protocol**: ONVIF Standard

## Version History

**v1.0.0** (2025-01-04)
- Initial release
- Live video streaming
- PTZ controls (pan, tilt, zoom)
- Preset recall
- Video recording
- Snapshot capture
- Modern dark theme UI

---

**Enjoy controlling your PTZ camera with this professional interface!**

For more information, see the main project README.md
