# Jennov PTZ Camera Control - Installation Guide

## 📦 What's in This Package

Complete PTZ camera control system with GUI application, Python library, and C# project.

### Main Components

```
JennovCameraControl/
├── 🎮 GUI Application (Python)
│   ├── camera_gui.py - Main GUI application ⭐ START HERE
│   ├── camera_control.py - PTZ control library
│   ├── start_camera_gui.bat - Windows launcher
│   └── requirements.txt - Python dependencies
│
├── 📚 Documentation
│   ├── GUI_QUICKSTART.md - 60-second quick start
│   ├── GUI_README.md - Complete GUI documentation
│   ├── CONTROLS_REFERENCE.txt - Control reference card
│   ├── PROJECT_COMPLETE.md - Project summary
│   └── [other docs...]
│
├── 🔧 C# Project
│   ├── JennovCamera/ - Library source code
│   ├── JennovCamera.Demo/ - Demo application
│   └── JennovCameraControl.sln - Visual Studio solution
│
└── 🔍 Analysis Tools
    ├── analyze_pcap.py - Traffic analyzer
    └── extract_commands.py - Command extractor
```

## ⚡ Quick Start (60 Seconds)

### Step 1: Extract the ZIP
Extract all files to a folder like:
```
C:\JennovCamera\
```

### Step 2: Install Python Dependencies
Open Command Prompt in the extracted folder:
```bash
cd C:\JennovCamera
pip install -r requirements.txt
```

This installs:
- PyQt5 (GUI framework)
- OpenCV (video streaming)
- onvif-zeep (camera control)
- NumPy (array processing)

### Step 3: Launch the Application

**Windows (Double-Click):**
```
start_camera_gui.bat
```

**Or from Command Line:**
```bash
python camera_gui.py
```

### Step 4: Connect to Camera

1. Click "🔌 Connect Camera" button
2. Wait for status: "● Connected" (green)
3. Video appears automatically!

### Step 5: Control Your Camera

- **Pan/Tilt**: Click and hold ▲▼◄► buttons
- **Zoom**: Click and hold 🔍+ / 🔍- buttons
- **Speed**: Adjust slider (0.1 - 1.0)
- **Presets**: Select preset 1-8, click "Go to Preset"
- **Record**: Click "Start Recording" button
- **Snapshot**: Click "Take Snapshot" button

## 📋 System Requirements

### Minimum
- **OS**: Windows 10, Linux, or macOS
- **Python**: 3.8 or higher
- **RAM**: 2 GB
- **Network**: 10 Mbps (for video streaming)

### Recommended
- **OS**: Windows 10/11
- **Python**: 3.10 or higher
- **RAM**: 4+ GB
- **Network**: 50+ Mbps wired connection

## 🔧 Installation Options

### Option A: Python GUI (Recommended - Full Featured)

**Prerequisites:**
1. Python 3.8+ installed
2. pip (Python package manager)

**Installation:**
```bash
# Navigate to extracted folder
cd C:\JennovCamera

# Install dependencies
pip install -r requirements.txt

# Run application
python camera_gui.py
```

**Features:**
- ✅ Live 1080p video streaming
- ✅ Full PTZ controls
- ✅ Video recording
- ✅ Snapshot capture
- ✅ Modern GUI

### Option B: Python Command Line

**For scripting and automation:**

```python
from camera_control import JennovCamera

# Connect
camera = JennovCamera("192.168.50.224", "admin", "hydroLob99")

# Control camera
camera.pan_left(speed=0.5, duration=2)
camera.tilt_up(speed=0.5, duration=2)
camera.goto_preset("1")
```

### Option C: C# Project

**Prerequisites:**
1. Visual Studio 2022 or .NET 8.0 SDK
2. Windows OS

**Build:**
```bash
cd C:\JennovCamera
dotnet build JennovCameraControl.sln
```

**Note:** C# implementation needs WS-Security authentication (90% complete)

## 🎯 First-Time Setup

### 1. Verify Python Installation
```bash
python --version
# Should show: Python 3.8.0 or higher
```

If Python is not installed:
- Download from: https://www.python.org/downloads/
- During installation, check "Add Python to PATH"

### 2. Install Dependencies
```bash
pip install -r requirements.txt
```

Expected output:
```
Installing collected packages: PyQt5, opencv-python, onvif-zeep, numpy
Successfully installed ...
```

### 3. Verify Camera Connection
```bash
# Test camera reachability
ping 192.168.50.224

# Test web interface
# Open browser: http://192.168.50.224
```

### 4. Configure Camera Settings (If Needed)

Edit `camera_gui.py` (lines 24-27):
```python
self.camera_ip = "192.168.50.224"    # Your camera IP
self.camera_user = "admin"            # Username
self.camera_pass = "hydroLob99"       # Password
self.rtsp_url = f"rtsp://{self.camera_user}:{self.camera_pass}@{self.camera_ip}:554/stream1"
```

## 🚨 Troubleshooting

### Installation Issues

**"pip: command not found"**
```bash
# Use python -m pip instead
python -m pip install -r requirements.txt
```

**"Permission denied"**
```bash
# Use --user flag
pip install --user -r requirements.txt
```

**"Package installation fails"**
```bash
# Upgrade pip first
python -m pip install --upgrade pip

# Then retry
pip install -r requirements.txt
```

### Application Issues

**"ModuleNotFoundError: No module named 'PyQt5'"**
```bash
# Install PyQt5 specifically
pip install PyQt5
```

**"Cannot connect to camera"**
1. Verify camera IP: `ping 192.168.50.224`
2. Check camera web interface: `http://192.168.50.224`
3. Verify credentials are correct
4. Ensure camera and PC are on same network

**"No video feed"**
1. Test RTSP in VLC Media Player:
   - Open VLC
   - Media → Open Network Stream
   - Enter: `rtsp://admin:hydroLob99@192.168.50.224:554/stream1`
2. Check firewall settings (allow port 554)
3. Try sub-stream: change `stream1` to `stream2`

**"GUI won't start"**
```bash
# Test command-line version first
python camera_control.py

# If that works, reinstall PyQt5
pip uninstall PyQt5
pip install PyQt5
```

## 📖 Documentation Guide

### Getting Started
1. **INSTALLATION.md** (this file) - Setup instructions
2. **GUI_QUICKSTART.md** - 60-second start guide
3. **CONTROLS_REFERENCE.txt** - Quick reference card

### User Guides
4. **GUI_README.md** - Complete GUI documentation
5. **QUICKSTART.md** - Command-line quick start

### Technical
6. **PROJECT_COMPLETE.md** - Full project summary
7. **FINAL_SUMMARY.md** - Technical details
8. **API_DOCUMENTATION.md** - API reference

### Reference
9. **README.md** - Project overview

## 🎮 Using the Application

### Basic Controls

**Connection:**
- Click "Connect Camera" to start
- Status shows "● Connected" when ready
- Video streams automatically

**PTZ Control:**
- **Click and HOLD** direction buttons (don't tap!)
- Adjust speed slider for precision/speed
- Use Stop button to halt movement

**Recording:**
- Click "Start Recording" to begin
- Button turns red during recording
- Click "Stop Recording" to save

**Snapshots:**
- Click "Take Snapshot" anytime
- Saves current frame as JPEG
- File appears in same folder

### Output Files

**Recordings:**
- Format: `recording_YYYYMMDD_HHMMSS.mp4`
- Location: Same folder as camera_gui.py
- Size: ~5-10 MB per minute

**Snapshots:**
- Format: `snapshot_YYYYMMDD_HHMMSS.jpg`
- Location: Same folder as camera_gui.py
- Size: ~100-500 KB each

## 🔐 Security Notes

### Default Credentials
```
IP: 192.168.50.224
Username: admin
Password: hydroLob99
Port: 80 (ONVIF), 554 (RTSP)
```

### Changing Credentials
1. Log into camera web interface
2. Change password in camera settings
3. Update credentials in `camera_gui.py` (lines 25-26)

### Network Security
- Camera accessible on local network only
- RTSP stream is unencrypted
- Use VPN for remote access
- Consider changing default password

## 🌟 Features Overview

### GUI Application
- ✅ Live 1080p video streaming
- ✅ Pan, tilt, zoom controls
- ✅ Adjustable speed (0.1-1.0)
- ✅ 8 preset positions
- ✅ Video recording (MP4)
- ✅ Snapshot capture (JPEG)
- ✅ Modern dark theme
- ✅ Status indicators

### Python Library
- ✅ ONVIF protocol support
- ✅ PTZ control methods
- ✅ Preset management
- ✅ Easy integration
- ✅ Well documented

### C# Project
- ✅ ONVIF client structure
- ✅ PTZ controller
- ✅ Preset manager
- ⚠️ Needs WS-Security auth (90% complete)

## 💡 Tips for Best Results

### Video Quality
- Use wired Ethernet (not WiFi)
- Close other network apps
- Use main stream (stream1) for quality
- Use sub stream (stream2) for speed

### PTZ Control
- Lower speeds (0.2-0.4) for precision
- Higher speeds (0.7-1.0) for scanning
- Hold buttons for smooth movement
- Stop before changing direction

### Recording
- Position camera before recording
- Record in short segments (5-10 min)
- Check disk space before long recordings
- Always use "Stop Recording" button

## 🆘 Getting Help

### Step 1: Check Documentation
- GUI_QUICKSTART.md - Quick start guide
- GUI_README.md - Complete documentation
- CONTROLS_REFERENCE.txt - Control guide

### Step 2: Verify Basics
```bash
# Python version
python --version

# Installed packages
pip list

# Camera connectivity
ping 192.168.50.224

# Test web interface
# Browser: http://192.168.50.224
```

### Step 3: Test Command Line
```bash
# Try simple control
python camera_control.py
```

If command line works but GUI doesn't, it's a GUI-specific issue.

### Step 4: Common Solutions
- **Reinstall packages**: `pip install -r requirements.txt --force-reinstall`
- **Restart camera**: Unplug power, wait 10s, reconnect
- **Check firewall**: Allow Python through Windows Firewall
- **Update Python**: Ensure Python 3.8 or higher

## 📊 Package Contents

### What's Included
- ✅ Complete GUI application
- ✅ Python PTZ library
- ✅ C# project source
- ✅ 8 documentation files
- ✅ Analysis tools
- ✅ Example code
- ✅ Requirements file

### What's NOT Included
- ❌ PCAP capture file (119 MB - too large)
- ❌ Compiled binaries (build from source)
- ❌ Third-party libraries (install via pip)

## 🚀 Next Steps

### After Installation

1. **Test basic functionality**
   - Launch GUI
   - Connect to camera
   - Try each control

2. **Read documentation**
   - GUI_QUICKSTART.md for basics
   - GUI_README.md for details
   - CONTROLS_REFERENCE.txt for quick reference

3. **Explore features**
   - Try all PTZ directions
   - Test zoom controls
   - Use presets
   - Record a video
   - Take snapshots

4. **Customize**
   - Adjust camera settings
   - Set up preset positions
   - Create automation scripts
   - Integrate with other systems

## 📞 Support Information

### Resources
- Documentation files (8 included)
- Example code (camera_control.py)
- Analysis tools (for troubleshooting)

### Camera Specifications
- Brand: Jennov
- Model: P87HM85-30X-EAS
- Firmware: J800S_AF V3.2.2.2
- Protocol: ONVIF (standard)
- Video: 1080p HD

### Tested On
- ✅ Windows 10
- ✅ Python 3.8, 3.10, 3.13
- ✅ PyQt5 5.15.9
- ✅ OpenCV 4.8.0

## 📜 License

MIT License - Free to use, modify, and distribute

---

## 🎉 You're Ready!

Everything is set up and ready to use. Just:

1. **Extract** this ZIP file
2. **Install** dependencies: `pip install -r requirements.txt`
3. **Run** the GUI: `start_camera_gui.bat`
4. **Click** "Connect Camera"
5. **Enjoy** professional PTZ camera control!

For the fastest start, see **GUI_QUICKSTART.md**

Have fun controlling your camera! 🎥
