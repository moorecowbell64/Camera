#!/usr/bin/env python3
"""
Jennov Camera PTZ Control using ONVIF
Works with Jennov P87HM85-30X-EAS and similar cameras
"""

from onvif import ONVIFCamera
import time
import sys

class JennovCamera:
    def __init__(self, ip, username, password, port=80):
        """Initialize connection to camera"""
        print(f"Connecting to camera at {ip}:{port}...")
        self.cam = ONVIFCamera(ip, port, username, password)

        # Get device info
        info = self.cam.devicemgmt.GetDeviceInformation()
        print(f"Connected: {info.Manufacturer} {info.Model}")
        print(f"Firmware: {info.FirmwareVersion}\n")

        # Setup PTZ
        self.ptz = self.cam.create_ptz_service()
        self.media = self.cam.create_media_service()

        # Get profile token
        profiles = self.media.GetProfiles()
        self.token = profiles[0].token
        print(f"Using profile: {self.token}")

        # Get PTZ configuration
        self.request = self.ptz.create_type('ContinuousMove')
        self.request.ProfileToken = self.token

        self.stop_request = self.ptz.create_type('Stop')
        self.stop_request.ProfileToken = self.token

    def move(self, pan_speed, tilt_speed, zoom_speed=0):
        """
        Move camera continuously
        Speeds range from -1.0 to 1.0
        """
        self.request.Velocity = {
            'PanTilt': {'x': pan_speed, 'y': tilt_speed},
            'Zoom': {'x': zoom_speed}
        }
        self.ptz.ContinuousMove(self.request)

    def stop(self):
        """Stop all movement"""
        self.stop_request.PanTilt = True
        self.stop_request.Zoom = True
        self.ptz.Stop(self.stop_request)

    def pan_left(self, speed=0.5, duration=2):
        """Pan left"""
        print(f"Panning left at speed {speed} for {duration}s...")
        self.move(-speed, 0)
        time.sleep(duration)
        self.stop()

    def pan_right(self, speed=0.5, duration=2):
        """Pan right"""
        print(f"Panning right at speed {speed} for {duration}s...")
        self.move(speed, 0)
        time.sleep(duration)
        self.stop()

    def tilt_up(self, speed=0.5, duration=2):
        """Tilt up"""
        print(f"Tilting up at speed {speed} for {duration}s...")
        self.move(0, speed)
        time.sleep(duration)
        self.stop()

    def tilt_down(self, speed=0.5, duration=2):
        """Tilt down"""
        print(f"Tilting down at speed {speed} for {duration}s...")
        self.move(0, -speed)
        time.sleep(duration)
        self.stop()

    def zoom_in(self, speed=0.5, duration=2):
        """Zoom in"""
        print(f"Zooming in at speed {speed} for {duration}s...")
        self.move(0, 0, speed)
        time.sleep(duration)
        self.stop()

    def zoom_out(self, speed=0.5, duration=2):
        """Zoom out"""
        print(f"Zooming out at speed {speed} for {duration}s...")
        self.move(0, 0, -speed)
        time.sleep(duration)
        self.stop()

    def goto_preset(self, preset_token, speed=1.0):
        """Go to a saved preset position"""
        print(f"Going to preset {preset_token}...")
        request = self.ptz.create_type('GotoPreset')
        request.ProfileToken = self.token
        request.PresetToken = str(preset_token)
        request.Speed = {'PanTilt': {'x': speed, 'y': speed}, 'Zoom': {'x': speed}}
        self.ptz.GotoPreset(request)

def main():
    # Camera configuration
    CAMERA_IP = "192.168.50.224"
    USERNAME = "admin"
    PASSWORD = "hydroLob99"

    # Connect to camera
    camera = JennovCamera(CAMERA_IP, USERNAME, PASSWORD)

    print("\n" + "="*50)
    print("PTZ Camera Control Demo")
    print("="*50)

    # Demo movements
    print("\n1. Testing Pan Left...")
    camera.pan_left(speed=0.5, duration=2)

    print("\n2. Testing Pan Right...")
    camera.pan_right(speed=0.5, duration=2)

    print("\n3. Testing Tilt Up...")
    camera.tilt_up(speed=0.5, duration=2)

    print("\n4. Testing Tilt Down...")
    camera.tilt_down(speed=0.5, duration=2)

    print("\n5. Testing Zoom In...")
    camera.zoom_in(speed=0.3, duration=1)

    print("\n6. Testing Zoom Out...")
    camera.zoom_out(speed=0.3, duration=1)

    print("\nDemo complete!")
    print("\nYou can control the camera with:")
    print("  camera.pan_left(speed, duration)")
    print("  camera.pan_right(speed, duration)")
    print("  camera.tilt_up(speed, duration)")
    print("  camera.tilt_down(speed, duration)")
    print("  camera.zoom_in(speed, duration)")
    print("  camera.zoom_out(speed, duration)")
    print("  camera.goto_preset(preset_number)")
    print("  camera.move(pan, tilt, zoom)")
    print("  camera.stop()")

if __name__ == "__main__":
    main()
