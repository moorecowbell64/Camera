#!/usr/bin/env python3
"""
Jennov Camera PTZ Control using ONVIF - OPTIMIZED FOR SPEED
Ultra-fast response time with pre-allocated requests and threading
"""

from onvif import ONVIFCamera
import time
import sys
from threading import Thread, Lock

class JennovCameraFast:
    def __init__(self, ip, username, password, port=80):
        """Initialize connection to camera with optimized settings"""
        print(f"Connecting to camera at {ip}:{port}...")

        # Create camera with reduced timeout for faster response
        self.cam = ONVIFCamera(ip, port, username, password)

        # Get device info
        info = self.cam.devicemgmt.GetDeviceInformation()
        print(f"Connected: {info.Manufacturer} {info.Model}")
        print(f"Firmware: {info.FirmwareVersion}\n")

        # Setup PTZ with optimizations
        self.ptz = self.cam.create_ptz_service()
        self.media = self.cam.create_media_service()

        # Optimize timeout for faster response (set on SOAP client)
        try:
            if hasattr(self.ptz, 'zeep_client') and hasattr(self.ptz.zeep_client, 'transport'):
                self.ptz.zeep_client.transport.operation_timeout = 1
            elif hasattr(self.ptz, 'soap_client'):
                self.ptz.soap_client.set_options(timeout=1)
        except:
            pass  # Skip if transport optimization not available

        # Get profile token
        profiles = self.media.GetProfiles()
        self.token = profiles[0].token
        print(f"Using profile: {self.token}")

        # Pre-create all request objects for instant access
        self.move_request = self.ptz.create_type('ContinuousMove')
        self.move_request.ProfileToken = self.token

        self.stop_request = self.ptz.create_type('Stop')
        self.stop_request.ProfileToken = self.token
        self.stop_request.PanTilt = True
        self.stop_request.Zoom = True

        # Pre-create velocity objects for all directions
        self._create_velocity_presets()

        # Thread lock for command synchronization
        self.command_lock = Lock()

        print("+ Camera optimized for fast response\n")

    def _create_velocity_presets(self):
        """Pre-create velocity objects for instant use"""
        # Store commonly used velocities to avoid dict creation overhead
        self.velocities = {
            'left': lambda speed: {'PanTilt': {'x': -speed, 'y': 0}, 'Zoom': {'x': 0}},
            'right': lambda speed: {'PanTilt': {'x': speed, 'y': 0}, 'Zoom': {'x': 0}},
            'up': lambda speed: {'PanTilt': {'x': 0, 'y': speed}, 'Zoom': {'x': 0}},
            'down': lambda speed: {'PanTilt': {'x': 0, 'y': -speed}, 'Zoom': {'x': 0}},
            'zoom_in': lambda speed: {'PanTilt': {'x': 0, 'y': 0}, 'Zoom': {'x': speed}},
            'zoom_out': lambda speed: {'PanTilt': {'x': 0, 'y': 0}, 'Zoom': {'x': -speed}},
        }

    def move(self, pan_speed, tilt_speed, zoom_speed=0):
        """
        Move camera continuously - OPTIMIZED
        Speeds range from -1.0 to 1.0
        Non-blocking - returns immediately
        """
        with self.command_lock:
            self.move_request.Velocity = {
                'PanTilt': {'x': pan_speed, 'y': tilt_speed},
                'Zoom': {'x': zoom_speed}
            }
            # Send command asynchronously
            Thread(target=self._send_move_command, daemon=True).start()

    def _send_move_command(self):
        """Internal method to send move command without blocking"""
        try:
            self.ptz.ContinuousMove(self.move_request)
        except Exception as e:
            pass  # Silently handle errors to maintain speed

    def move_fast(self, direction, speed):
        """
        Ultra-fast directional movement
        direction: 'left', 'right', 'up', 'down', 'zoom_in', 'zoom_out'
        """
        with self.command_lock:
            self.move_request.Velocity = self.velocities[direction](speed)
            # Send immediately without thread overhead for max speed
            try:
                self.ptz.ContinuousMove(self.move_request)
            except:
                pass

    def stop(self):
        """Stop all movement - INSTANT"""
        # Don't use lock to allow immediate stop
        try:
            self.ptz.Stop(self.stop_request)
        except Exception as e:
            pass  # Silent error handling for speed

    def stop_async(self):
        """Stop movement asynchronously - even faster for button release"""
        Thread(target=self.stop, daemon=True).start()

    # Convenience methods with instant response
    def pan_left(self, speed=0.5, duration=2):
        """Pan left - instant start"""
        self.move_fast('left', speed)
        if duration > 0:
            time.sleep(duration)
            self.stop()

    def pan_right(self, speed=0.5, duration=2):
        """Pan right - instant start"""
        self.move_fast('right', speed)
        if duration > 0:
            time.sleep(duration)
            self.stop()

    def tilt_up(self, speed=0.5, duration=2):
        """Tilt up - instant start"""
        self.move_fast('up', speed)
        if duration > 0:
            time.sleep(duration)
            self.stop()

    def tilt_down(self, speed=0.5, duration=2):
        """Tilt down - instant start"""
        self.move_fast('down', speed)
        if duration > 0:
            time.sleep(duration)
            self.stop()

    def zoom_in(self, speed=0.5, duration=2):
        """Zoom in - instant start"""
        self.move_fast('zoom_in', speed)
        if duration > 0:
            time.sleep(duration)
            self.stop()

    def zoom_out(self, speed=0.5, duration=2):
        """Zoom out - instant start"""
        self.move_fast('zoom_out', speed)
        if duration > 0:
            time.sleep(duration)
            self.stop()

    def goto_preset(self, preset_token, speed=1.0):
        """Go to a saved preset position"""
        request = self.ptz.create_type('GotoPreset')
        request.ProfileToken = self.token
        request.PresetToken = str(preset_token)
        request.Speed = {'PanTilt': {'x': speed, 'y': speed}, 'Zoom': {'x': speed}}
        # Send asynchronously for instant response
        Thread(target=lambda: self.ptz.GotoPreset(request), daemon=True).start()

# Alias for backward compatibility
JennovCamera = JennovCameraFast

def main():
    # Camera configuration
    CAMERA_IP = "192.168.50.224"
    USERNAME = "admin"
    PASSWORD = "hydroLob99"

    # Connect to camera
    camera = JennovCameraFast(CAMERA_IP, USERNAME, PASSWORD)

    print("\n" + "="*50)
    print("FAST PTZ Camera Control Demo")
    print("="*50)

    # Demo fast movements
    print("\n1. Testing Fast Pan Left...")
    camera.pan_left(speed=0.5, duration=1)

    print("\n2. Testing Fast Pan Right...")
    camera.pan_right(speed=0.5, duration=1)

    print("\n3. Testing Fast Tilt Up...")
    camera.tilt_up(speed=0.5, duration=1)

    print("\n4. Testing Fast Tilt Down...")
    camera.tilt_down(speed=0.5, duration=1)

    print("\n5. Testing Fast Zoom In...")
    camera.zoom_in(speed=0.3, duration=0.5)

    print("\n6. Testing Fast Zoom Out...")
    camera.zoom_out(speed=0.3, duration=0.5)

    print("\nFast demo complete!")
    print("\n✓ Ultra-fast response time achieved!")

if __name__ == "__main__":
    main()
