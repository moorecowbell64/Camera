#!/usr/bin/env python3
"""
Jennov Camera PTZ Control - ULTRA OPTIMIZED FOR MAXIMUM RESPONSIVENESS
Absolute minimum latency between click and camera movement
"""

from onvif import ONVIFCamera
import time
from threading import Thread, Lock
import requests
from requests.auth import HTTPDigestAuth

class JennovCameraUltra:
    def __init__(self, ip, username, password, port=80):
        """Initialize with maximum performance optimizations"""
        print(f"Connecting to camera at {ip}:{port}...")

        self.ip = ip
        self.port = port
        self.username = username
        self.password = password

        # Create camera connection
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

        # Get PTZ service URL for direct HTTP calls
        self.ptz_url = f"http://{ip}:{port}/onvif/PTZ"

        # Pre-create HTTP session with persistent connection
        self.session = requests.Session()
        self.session.auth = HTTPDigestAuth(username, password)
        self.session.headers.update({
            'Content-Type': 'application/soap+xml; charset=utf-8',
            'Connection': 'keep-alive'
        })

        # Pre-create all SOAP request templates for instant use
        self._create_soap_templates()

        # Pre-create ONVIF request objects
        self.move_request = self.ptz.create_type('ContinuousMove')
        self.move_request.ProfileToken = self.token

        self.stop_request = self.ptz.create_type('Stop')
        self.stop_request.ProfileToken = self.token
        self.stop_request.PanTilt = True
        self.stop_request.Zoom = True

        # Pre-create velocity objects (actual dicts, not lambdas for speed)
        self.velocities = {
            'left': {'PanTilt': {'x': -1.0, 'y': 0}, 'Zoom': {'x': 0}},
            'right': {'PanTilt': {'x': 1.0, 'y': 0}, 'Zoom': {'x': 0}},
            'up': {'PanTilt': {'x': 0, 'y': 1.0}, 'Zoom': {'x': 0}},
            'down': {'PanTilt': {'x': 0, 'y': -1.0}, 'Zoom': {'x': 0}},
            'zoom_in': {'PanTilt': {'x': 0, 'y': 0}, 'Zoom': {'x': 1.0}},
            'zoom_out': {'PanTilt': {'x': 0, 'y': 0}, 'Zoom': {'x': -1.0}},
        }

        # Speed multipliers (updated dynamically)
        self.speed_multipliers = {
            'left': 1.0, 'right': 1.0, 'up': 1.0,
            'down': 1.0, 'zoom_in': 1.0, 'zoom_out': 1.0
        }

        # Minimal lock for thread safety
        self.lock = Lock()

        # Keep connection warm with background thread
        self.keep_alive_active = True
        Thread(target=self._keep_alive, daemon=True).start()

        print("+ ULTRA mode: Maximum responsiveness enabled\n")

    def _create_soap_templates(self):
        """Pre-create SOAP envelope templates for direct HTTP calls"""
        # Templates with placeholders for speed values
        self.soap_move_template = """<?xml version="1.0" encoding="UTF-8"?>
<s:Envelope xmlns:s="http://www.w3.org/2003/05/soap-envelope" xmlns:tptz="http://www.onvif.org/ver20/ptz/wsdl" xmlns:tt="http://www.onvif.org/ver10/schema">
    <s:Body>
        <tptz:ContinuousMove>
            <tptz:ProfileToken>{token}</tptz:ProfileToken>
            <tptz:Velocity>
                <tt:PanTilt x="{pan}" y="{tilt}"/>
                <tt:Zoom x="{zoom}"/>
            </tptz:Velocity>
        </tptz:ContinuousMove>
    </s:Body>
</s:Envelope>"""

        self.soap_stop_template = """<?xml version="1.0" encoding="UTF-8"?>
<s:Envelope xmlns:s="http://www.w3.org/2003/05/soap-envelope" xmlns:tptz="http://www.onvif.org/ver20/ptz/wsdl">
    <s:Body>
        <tptz:Stop>
            <tptz:ProfileToken>{token}</tptz:ProfileToken>
            <tptz:PanTilt>true</tptz:PanTilt>
            <tptz:Zoom>true</tptz:Zoom>
        </tptz:Stop>
    </s:Body>
</s:Envelope>"""

    def _keep_alive(self):
        """Keep HTTP connection alive to avoid reconnection delays"""
        while self.keep_alive_active:
            time.sleep(30)  # Every 30 seconds
            try:
                # Send lightweight request to keep connection alive
                self.session.head(f"http://{self.ip}:{self.port}/onvif/device_service", timeout=1)
            except:
                pass

    def move_instant(self, direction, speed):
        """
        ULTRA-FAST movement - absolute minimum latency
        Fire-and-forget direct HTTP call
        """
        vel = self.velocities[direction]
        pan = vel['PanTilt']['x'] * speed
        tilt = vel['PanTilt']['y'] * speed
        zoom = vel['Zoom']['x'] * speed

        # Create SOAP envelope with actual values
        soap = self.soap_move_template.format(
            token=self.token,
            pan=pan,
            tilt=tilt,
            zoom=zoom
        )

        # Send immediately without waiting for response (fire-and-forget)
        Thread(target=self._send_raw, args=(soap,), daemon=True).start()

    def _send_raw(self, soap_data):
        """Send raw SOAP request without blocking"""
        try:
            self.session.post(self.ptz_url, data=soap_data, timeout=0.5)
        except:
            pass  # Silent - speed is priority

    def move_fast(self, direction, speed):
        """
        Optimized movement using pre-created objects
        Slightly slower than move_instant but more reliable
        """
        vel = self.velocities[direction]
        self.move_request.Velocity = {
            'PanTilt': {
                'x': vel['PanTilt']['x'] * speed,
                'y': vel['PanTilt']['y'] * speed
            },
            'Zoom': {'x': vel['Zoom']['x'] * speed}
        }

        # Send without blocking
        try:
            self.ptz.ContinuousMove(self.move_request)
        except:
            pass

    def stop_instant(self):
        """INSTANT stop - fire-and-forget"""
        soap = self.soap_stop_template.format(token=self.token)
        Thread(target=self._send_raw, args=(soap,), daemon=True).start()

    def stop(self):
        """Stop all movement - optimized version"""
        try:
            self.ptz.Stop(self.stop_request)
        except:
            pass

    def move(self, pan_speed, tilt_speed, zoom_speed=0):
        """
        Generic move method for backward compatibility
        Uses instant mode for maximum speed
        """
        # Determine direction from speeds
        if abs(pan_speed) > 0.01 or abs(tilt_speed) > 0.01:
            soap = self.soap_move_template.format(
                token=self.token,
                pan=pan_speed,
                tilt=tilt_speed,
                zoom=zoom_speed
            )
            Thread(target=self._send_raw, args=(soap,), daemon=True).start()
        elif abs(zoom_speed) > 0.01:
            soap = self.soap_move_template.format(
                token=self.token,
                pan=0,
                tilt=0,
                zoom=zoom_speed
            )
            Thread(target=self._send_raw, args=(soap,), daemon=True).start()

    # Convenience methods using ULTRA-FAST instant mode
    def pan_left(self, speed=0.5, duration=2):
        """Pan left - instant response"""
        self.move_instant('left', speed)
        if duration > 0:
            time.sleep(duration)
            self.stop_instant()

    def pan_right(self, speed=0.5, duration=2):
        """Pan right - instant response"""
        self.move_instant('right', speed)
        if duration > 0:
            time.sleep(duration)
            self.stop_instant()

    def tilt_up(self, speed=0.5, duration=2):
        """Tilt up - instant response"""
        self.move_instant('up', speed)
        if duration > 0:
            time.sleep(duration)
            self.stop_instant()

    def tilt_down(self, speed=0.5, duration=2):
        """Tilt down - instant response"""
        self.move_instant('down', speed)
        if duration > 0:
            time.sleep(duration)
            self.stop_instant()

    def zoom_in(self, speed=0.5, duration=2):
        """Zoom in - instant response"""
        self.move_instant('zoom_in', speed)
        if duration > 0:
            time.sleep(duration)
            self.stop_instant()

    def zoom_out(self, speed=0.5, duration=2):
        """Zoom out - instant response"""
        self.move_instant('zoom_out', speed)
        if duration > 0:
            time.sleep(duration)
            self.stop_instant()

    def goto_preset(self, preset_token, speed=1.0):
        """Go to preset - async for instant return"""
        request = self.ptz.create_type('GotoPreset')
        request.ProfileToken = self.token
        request.PresetToken = str(preset_token)
        request.Speed = {'PanTilt': {'x': speed, 'y': speed}, 'Zoom': {'x': speed}}
        Thread(target=lambda: self.ptz.GotoPreset(request), daemon=True).start()

    def __del__(self):
        """Cleanup on destruction"""
        self.keep_alive_active = False
        if hasattr(self, 'session'):
            self.session.close()

# Alias for compatibility
JennovCamera = JennovCameraUltra

def main():
    # Camera configuration
    CAMERA_IP = "192.168.50.224"
    USERNAME = "admin"
    PASSWORD = "hydroLob99"

    # Connect to camera
    camera = JennovCameraUltra(CAMERA_IP, USERNAME, PASSWORD)

    print("\n" + "="*50)
    print("ULTRA-FAST PTZ Camera Control Demo")
    print("="*50)

    # Test instant response
    print("\n1. Testing INSTANT Pan Left...")
    camera.pan_left(speed=0.5, duration=1)

    print("\n2. Testing INSTANT Pan Right...")
    camera.pan_right(speed=0.5, duration=1)

    print("\n3. Testing INSTANT Tilt Up...")
    camera.tilt_up(speed=0.5, duration=1)

    print("\n4. Testing INSTANT Tilt Down...")
    camera.tilt_down(speed=0.5, duration=1)

    print("\n5. Testing INSTANT Zoom In...")
    camera.zoom_in(speed=0.3, duration=0.5)

    print("\n6. Testing INSTANT Zoom Out...")
    camera.zoom_out(speed=0.3, duration=0.5)

    print("\nULTRA-FAST demo complete!")
    print("\n+ Maximum responsiveness achieved!")

if __name__ == "__main__":
    main()
