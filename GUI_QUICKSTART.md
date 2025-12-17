# GUI Quick Start - Get Running in 60 Seconds!

## Step 1: Launch the Application (Choose One)

### Option A: Double-Click the Launcher ⚡ EASIEST
```
📁 Double-click: start_camera_gui.bat
```

### Option B: Run from Terminal
```bash
cd "C:\Users\moore\Documents\GitHub\Camera"
python camera_gui.py
```

## Step 2: Connect to Camera

1. **Click the big blue button**: "🔌 Connect Camera"
2. **Wait 2-3 seconds** for connection
3. **Video appears automatically!**

```
┌─────────────────────────────────┐
│   [🔌 Connect Camera]  ← CLICK │
└─────────────────────────────────┘
```

Status will change from "● Disconnected" (red) to "● Connected" (green)

## Step 3: Control Your Camera

### Pan & Tilt
- **Click and HOLD** direction buttons:
  - ▲ = Tilt Up
  - ▼ = Tilt Down
  - ◄ = Pan Left
  - ► = Pan Right
- **Release** to stop
- Or click **⏹** to stop immediately

### Zoom
- **Click and HOLD**:
  - 🔍+ = Zoom In
  - 🔍- = Zoom Out

### Adjust Speed
- **Move the slider** left (slower) or right (faster)
- Speed shows next to slider (0.1 - 1.0)

```
Speed: [====o====] 0.5
```

## Step 4: Go to Presets (Optional)

1. **Select preset number** from dropdown (1-8)
2. **Click**: "📍 Go to Preset"
3. Camera moves automatically

```
Preset: [3 ▼]
[📍 Go to Preset]
```

## Step 5: Record Video (Optional)

### Start Recording
1. **Click**: "⏺ Start Recording"
2. **Button turns RED**
3. Status shows: "⏺ Recording: recording_20250104_123456.mp4"

### Stop Recording
1. **Click**: "⏹ Stop Recording"
2. **Popup shows** save location
3. File saved as MP4

### Take Snapshot
- **Click**: "📷 Take Snapshot"
- Saves current frame as JPG

## Complete Interface at a Glance

```
╔═══════════════════════════════════════════════════════════╗
║          JENNOV PTZ CAMERA CONTROL                       ║
╠════════════════════════════╦══════════════════════════════╣
║                            ║  ┌──────────────────────┐   ║
║                            ║  │ 🔌 Connect Camera    │   ║
║      LIVE VIDEO FEED       ║  └──────────────────────┘   ║
║       (Click Connect       ║                              ║
║        to Start)           ║  Speed: [====o====] 0.5     ║
║                            ║        ▲                     ║
║     960 x 540 pixels       ║     ◄  ⏹  ►                  ║
║                            ║        ▼                     ║
║                            ║  [🔍+ Zoom] [🔍- Zoom]      ║
╠════════════════════════════╣                              ║
║ ● Connected | ⏺ Recording  ║  Preset: [1 ▼]              ║
╚════════════════════════════╣  [📍 Go to Preset]          ║
                             ║                              ║
                             ║  [⏺ Start Recording]         ║
                             ║  [📷 Take Snapshot]          ║
                             ╚══════════════════════════════╝
```

## First-Time Tips

### ✅ DO:
- **Hold buttons** for smooth movement (don't click rapidly)
- **Use lower speeds** (0.2-0.4) for precise positioning
- **Stop before recording** for stable video
- **Test presets** to learn camera positions

### ❌ DON'T:
- Don't rapidly click direction buttons
- Don't change direction without stopping first
- Don't close app while recording (data loss!)
- Don't use maximum speed (1.0) unless needed

## Troubleshooting

### ❌ "No Video Feed" Displayed

**Quick Fixes:**
1. Check camera is powered ON
2. Verify camera IP: `ping 192.168.50.224`
3. Test in web browser: `http://192.168.50.224`
4. Try reconnecting (disconnect, wait 5s, connect)

### ❌ Camera Doesn't Move

**Quick Fixes:**
1. Ensure status is "● Connected" (green)
2. Try increasing speed slider
3. Hold button for 2+ seconds
4. Click Stop button, then try again

### ❌ Recording Won't Start

**Quick Fixes:**
1. Check disk space (need ~100MB minimum)
2. Ensure video feed is working first
3. Close and restart application
4. Check Windows permissions for folder

## Keyboard Shortcuts

| Key | Action |
|-----|--------|
| Arrow Keys | Pan/Tilt (if not focused on buttons) |
| Space | Stop (if implemented) |
| Ctrl+Q | Quit application |

## Camera Settings

Default settings (already configured):
- **IP**: 192.168.50.224
- **User**: admin
- **Pass**: hydroLob99
- **Port**: 80 (ONVIF), 554 (RTSP)

To change: Edit `camera_gui.py` lines 24-27

## Output Files

### Recordings
- **Location**: Same folder as camera_gui.py
- **Format**: `recording_YYYYMMDD_HHMMSS.mp4`
- **Size**: ~5-10 MB per minute (1080p)

### Snapshots
- **Location**: Same folder as camera_gui.py
- **Format**: `snapshot_YYYYMMDD_HHMMSS.jpg`
- **Size**: ~100-500 KB each

## Advanced: Test RTSP Stream Directly

Open **VLC Media Player**:
1. Media → Open Network Stream
2. Enter: `rtsp://admin:hydroLob99@192.168.50.224:554/stream1`
3. Click Play

If this works, the GUI will work too!

## Still Need Help?

### Check These First:
1. ✅ Python installed? `python --version`
2. ✅ Packages installed? `pip list | findstr PyQt5`
3. ✅ Camera reachable? `ping 192.168.50.224`
4. ✅ Web interface works? `http://192.168.50.224`

### Common Solutions:
- **Reinstall packages**: `pip install -r requirements.txt`
- **Restart camera**: Unplug power, wait 10s, plug back in
- **Check firewall**: Allow Python through Windows Firewall
- **Update drivers**: Ensure network adapter is working

### Test Command Line Version First:
```bash
python camera_control.py
```

If this works but GUI doesn't, it's a GUI-specific issue.

## Next Steps

Once you're comfortable:

1. **Explore presets**: Set up positions 1-8 in camera web interface
2. **Record videos**: Capture important events
3. **Take snapshots**: Document camera positions
4. **Adjust speeds**: Find your preferred control speed
5. **Create patterns**: Use presets to create patrol routes

## Pro Tips

### For Smooth Video
- Use **wired Ethernet** connection (not WiFi)
- Close **other network apps** while streaming
- Use **sub-stream** (stream2) for lower bandwidth
- Position camera before starting recording

### For Best PTZ Control
- **Lower speeds** for precise work
- **Hold buttons** for continuous motion
- **Stop between** direction changes
- **Test new areas** at slow speed first

### For Recording
- **Short segments** are better (5-10 minutes)
- **Check space** before long recordings
- **Stable feed** before hitting record
- **Stop properly** (don't force-close app)

## Enjoy Your Camera!

You now have professional PTZ camera control with:
- ✅ Live 1080p video streaming
- ✅ Full PTZ control (pan, tilt, zoom)
- ✅ 8 preset positions
- ✅ MP4 video recording
- ✅ JPEG snapshot capture
- ✅ Modern, beautiful interface

**Have fun controlling your camera!** 🎥

---

For detailed documentation, see `GUI_README.md`
