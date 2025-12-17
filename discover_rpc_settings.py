#!/usr/bin/env python3
"""
Discover camera settings via JSON-RPC API at /IPC endpoint
"""

import requests
from requests.auth import HTTPDigestAuth
import json

# Camera config
CAMERA_IP = "192.168.50.224"
USERNAME = "admin"
PASSWORD = "hydroLob99"
BASE_URL = f"http://{CAMERA_IP}"

session = requests.Session()
session.auth = HTTPDigestAuth(USERNAME, PASSWORD)

request_id = 1

def send_rpc(method, params=None, object_id=None):
    global request_id

    payload = {
        "method": method,
        "params": params,
        "session": 0,  # Will be set after login
        "id": request_id
    }

    if object_id:
        payload["object"] = object_id

    request_id += 1

    try:
        response = session.post(f"{BASE_URL}/IPC", json=payload, timeout=10)
        return response.json()
    except Exception as e:
        return {"error": str(e)}

def main():
    print("=" * 80)
    print("JSON-RPC API DISCOVERY")
    print("=" * 80)

    # Common config names to try
    config_names = [
        "VideoColor",           # Brightness, contrast, saturation
        "VideoInOptions",       # Video input options
        "Encode",               # Video encoding settings
        "Network",              # Network settings
        "General",              # General settings
        "NTP",                  # NTP settings
        "Locales",              # Language/locale
        "ChannelTitle",         # Channel name/title
        "PTZ",                  # PTZ settings
        "Storage",              # Storage settings
        "RecordMode",           # Recording mode
        "Snap",                 # Snapshot settings
        "MotionDetect",         # Motion detection
        "AudioDetect",          # Audio detection
        "BlindDetect",          # Tamper/blind detection
        "AlarmIn",              # Alarm input
        "AlarmOut",             # Alarm output
        "Email",                # Email notification
        "CommGlobal",           # Communication global settings
        "NetApp.FTP",           # FTP settings
        "LossDetect",           # Video loss detection
        "DigitalZoom",          # Digital zoom settings
        "Lighting",             # IR/White light settings
        "DayNightColor",        # Day/night mode
        "WhiteBalance",         # White balance
        "FlipRotate",           # Flip/rotate settings
    ]

    # 1. Get System Info
    print("\n[1] MagicBox - System Info:")
    result = send_rpc("magicBox.getSystemInfo")
    print(json.dumps(result, indent=2)[:1500])

    # 2. Get Device Type
    print("\n[2] MagicBox - Device Type:")
    result = send_rpc("magicBox.getDeviceType")
    print(json.dumps(result, indent=2))

    # 3. Try to get all available configs
    print("\n[3] Trying to get configurations:")
    for config_name in config_names:
        result = send_rpc("configManager.getConfig", {"name": config_name})
        if result.get("result") or "params" in result:
            print(f"\n  === {config_name} ===")
            params = result.get("params", result)
            print(json.dumps(params, indent=2)[:800])
        else:
            error = result.get("error", {})
            if isinstance(error, dict):
                code = error.get("code", "")
                if code != -2147024894:  # Not found error
                    print(f"\n  {config_name}: {error}")

    # 4. Get Video Color (brightness/contrast/saturation)
    print("\n" + "=" * 80)
    print("[4] Video Color Settings (detailed):")
    result = send_rpc("configManager.getConfig", {"name": "VideoColor"})
    print(json.dumps(result, indent=2))

    # 5. Get Default Video Color
    print("\n[5] Default Video Color Settings:")
    result = send_rpc("configManager.getDefault", {"name": "VideoColor"})
    print(json.dumps(result, indent=2))

    # 6. Get PTZ Configuration
    print("\n[6] PTZ Configuration:")
    result = send_rpc("configManager.getConfig", {"name": "Ptz"})
    print(json.dumps(result, indent=2))

    # 7. Get PTZ Presets via RPC
    print("\n[7] PTZ Presets (via ptz.getPresets):")
    result = send_rpc("ptz.getPresets")
    print(json.dumps(result, indent=2)[:1500])

    # 8. List available services
    print("\n[8] System - List Services:")
    result = send_rpc("system.listService")
    print(json.dumps(result, indent=2)[:2000])

    # 9. Get Encode configuration
    print("\n[9] Encode Configuration:")
    for enc in ["Encode", "Simplify.Encode"]:
        result = send_rpc("configManager.getConfig", {"name": enc})
        if result.get("result") or result.get("params"):
            print(f"\n  === {enc} ===")
            print(json.dumps(result, indent=2)[:1500])

    # 10. Check factory reset capability
    print("\n[10] Factory Reset Info:")
    print("  configManager.restore(['All']) - Reset all settings")
    print("  configManager.restoreExcept(['Network']) - Reset except network")
    print("  magicBox.reboot - Reboot camera")

if __name__ == "__main__":
    main()
