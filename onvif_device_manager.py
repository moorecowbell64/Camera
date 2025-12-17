#!/usr/bin/env python3
"""
ONVIF Device Management - Factory Reset, Reboot, and Settings Discovery
"""

import requests
import hashlib
import base64
import datetime
import random
import string
import xml.etree.ElementTree as ET
import re

# Camera config
CAMERA_IP = "192.168.50.224"
USERNAME = "admin"
PASSWORD = "hydroLob99"

def generate_nonce():
    return ''.join(random.choices(string.ascii_letters + string.digits, k=16))

def generate_security_header():
    nonce = generate_nonce()
    created = datetime.datetime.now(datetime.UTC).strftime('%Y-%m-%dT%H:%M:%S.000Z')

    nonce_bytes = nonce.encode('utf-8')
    digest_input = nonce_bytes + created.encode('utf-8') + PASSWORD.encode('utf-8')
    password_digest = base64.b64encode(hashlib.sha1(digest_input).digest()).decode('utf-8')
    nonce_b64 = base64.b64encode(nonce_bytes).decode('utf-8')

    return f'''<s:Header>
        <Security xmlns="http://docs.oasis-open.org/wss/2004/01/oasis-200401-wss-wssecurity-secext-1.0.xsd" s:mustUnderstand="1">
            <UsernameToken>
                <Username>{USERNAME}</Username>
                <Password Type="http://docs.oasis-open.org/wss/2004/01/oasis-200401-wss-username-token-profile-1.0#PasswordDigest">{password_digest}</Password>
                <Nonce EncodingType="http://docs.oasis-open.org/wss/2004/01/oasis-200401-wss-soap-message-security-1.0#Base64Binary">{nonce_b64}</Nonce>
                <Created xmlns="http://docs.oasis-open.org/wss/2004/01/oasis-200401-wss-wssecurity-utility-1.0.xsd">{created}</Created>
            </UsernameToken>
        </Security>
    </s:Header>'''

def send_request(endpoint, body):
    header = generate_security_header()
    envelope = f'''<?xml version="1.0" encoding="UTF-8"?>
<s:Envelope xmlns:s="http://www.w3.org/2003/05/soap-envelope">
    {header}
    <s:Body xmlns:xsi="http://www.w3.org/2001/XMLSchema-instance" xmlns:xsd="http://www.w3.org/2001/XMLSchema">
        {body}
    </s:Body>
</s:Envelope>'''

    url = f"http://{CAMERA_IP}{endpoint}"
    try:
        response = requests.post(url, data=envelope, headers={'Content-Type': 'application/soap+xml'}, timeout=10)
        return response.text, response.status_code
    except Exception as e:
        return f"Error: {e}", 0

def extract_value(xml_text, tag_name):
    """Extract value from XML by tag local name"""
    pattern = rf'<[^:>]*:{tag_name}[^>]*>([^<]*)</[^:>]*:{tag_name}>'
    match = re.search(pattern, xml_text)
    if match:
        return match.group(1)
    # Try without namespace
    pattern = rf'<{tag_name}[^>]*>([^<]*)</{tag_name}>'
    match = re.search(pattern, xml_text)
    return match.group(1) if match else None

def extract_all_values(xml_text, tag_name):
    """Extract all values from XML by tag local name"""
    pattern = rf'<[^:>]*:{tag_name}[^>]*>([^<]*)</[^:>]*:{tag_name}>'
    matches = re.findall(pattern, xml_text)
    if not matches:
        pattern = rf'<{tag_name}[^>]*>([^<]*)</{tag_name}>'
        matches = re.findall(pattern, xml_text)
    return matches

def check_fault(xml_text):
    """Check if response is a SOAP fault"""
    if 'Fault' in xml_text:
        reason = extract_value(xml_text, 'Text') or extract_value(xml_text, 'Reason')
        code = extract_value(xml_text, 'Value')
        return True, f"SOAP Fault: {reason} ({code})"
    return False, None

def main():
    print("=" * 80)
    print("ONVIF DEVICE MANAGEMENT")
    print("=" * 80)
    print(f"\nCamera: {CAMERA_IP}")
    print(f"Username: {USERNAME}")

    # 1. Get Device Information
    print("\n" + "=" * 60)
    print("[1] DEVICE INFORMATION")
    print("=" * 60)
    result, status = send_request("/onvif/device_service",
        '<GetDeviceInformation xmlns="http://www.onvif.org/ver10/device/wsdl"/>')

    is_fault, fault_msg = check_fault(result)
    if is_fault:
        print(f"  Error: {fault_msg}")
    else:
        print(f"  Manufacturer: {extract_value(result, 'Manufacturer')}")
        print(f"  Model: {extract_value(result, 'Model')}")
        print(f"  Firmware: {extract_value(result, 'FirmwareVersion')}")
        print(f"  Serial: {extract_value(result, 'SerialNumber')}")
        print(f"  Hardware: {extract_value(result, 'HardwareId')}")

    # 2. Get Scopes (Device Name, Location)
    print("\n" + "=" * 60)
    print("[2] DEVICE SCOPES")
    print("=" * 60)
    result, status = send_request("/onvif/device_service",
        '<GetScopes xmlns="http://www.onvif.org/ver10/device/wsdl"/>')

    is_fault, fault_msg = check_fault(result)
    if is_fault:
        print(f"  Error: {fault_msg}")
    else:
        scopes = extract_all_values(result, 'ScopeItem')
        for scope in scopes[:10]:
            print(f"  {scope}")

    # 3. Get Network Settings
    print("\n" + "=" * 60)
    print("[3] NETWORK CONFIGURATION")
    print("=" * 60)
    result, status = send_request("/onvif/device_service",
        '<GetNetworkInterfaces xmlns="http://www.onvif.org/ver10/device/wsdl"/>')

    is_fault, fault_msg = check_fault(result)
    if is_fault:
        print(f"  Error: {fault_msg}")
    else:
        # Parse network info
        addresses = extract_all_values(result, 'Address')
        print(f"  IP Addresses: {', '.join(addresses[:5])}")
        mac = extract_value(result, 'HwAddress')
        print(f"  MAC Address: {mac}")

    # 4. Get NTP Settings
    print("\n" + "=" * 60)
    print("[4] NTP CONFIGURATION")
    print("=" * 60)
    result, status = send_request("/onvif/device_service",
        '<GetNTP xmlns="http://www.onvif.org/ver10/device/wsdl"/>')

    is_fault, fault_msg = check_fault(result)
    if is_fault:
        print(f"  Error: {fault_msg}")
    else:
        from_dhcp = extract_value(result, 'FromDHCP')
        ntp_server = extract_value(result, 'IPv4Address') or extract_value(result, 'DNSname')
        print(f"  FromDHCP: {from_dhcp}")
        print(f"  NTP Server: {ntp_server}")

    # 5. Get System Date/Time
    print("\n" + "=" * 60)
    print("[5] SYSTEM DATE/TIME")
    print("=" * 60)
    result, status = send_request("/onvif/device_service",
        '<GetSystemDateAndTime xmlns="http://www.onvif.org/ver10/device/wsdl"/>')

    is_fault, fault_msg = check_fault(result)
    if is_fault:
        print(f"  Error: {fault_msg}")
    else:
        date_type = extract_value(result, 'DateTimeType')
        tz = extract_value(result, 'TZ')
        year = extract_value(result, 'Year')
        month = extract_value(result, 'Month')
        day = extract_value(result, 'Day')
        hour = extract_value(result, 'Hour')
        minute = extract_value(result, 'Minute')
        print(f"  Type: {date_type}")
        print(f"  Timezone: {tz}")
        print(f"  UTC Time: {year}-{month}-{day} {hour}:{minute}")

    # 6. Get Video Encoder Settings
    print("\n" + "=" * 60)
    print("[6] VIDEO ENCODER CONFIGURATIONS")
    print("=" * 60)
    result, status = send_request("/onvif/Media",
        '<GetVideoEncoderConfigurations xmlns="http://www.onvif.org/ver10/media/wsdl"/>')

    is_fault, fault_msg = check_fault(result)
    if is_fault:
        print(f"  Error: {fault_msg}")
    else:
        encodings = extract_all_values(result, 'Encoding')
        widths = extract_all_values(result, 'Width')
        heights = extract_all_values(result, 'Height')
        bitrates = extract_all_values(result, 'BitrateLimit')
        for i, enc in enumerate(encodings):
            w = widths[i] if i < len(widths) else "?"
            h = heights[i] if i < len(heights) else "?"
            br = bitrates[i] if i < len(bitrates) else "?"
            print(f"  Stream {i+1}: {enc} {w}x{h} @ {br}kbps")

    # 7. Get PTZ Presets
    print("\n" + "=" * 60)
    print("[7] PTZ PRESETS")
    print("=" * 60)
    result, status = send_request("/onvif/PTZ",
        '<GetPresets xmlns="http://www.onvif.org/ver20/ptz/wsdl"><ProfileToken>MainStream</ProfileToken></GetPresets>')

    is_fault, fault_msg = check_fault(result)
    if is_fault:
        print(f"  Error: {fault_msg}")
    else:
        # Extract preset tokens and names
        preset_pattern = r'token="([^"]+)"[^>]*>.*?<[^:>]*:Name>([^<]*)</[^:>]*:Name>'
        presets = re.findall(preset_pattern, result, re.DOTALL)
        print(f"  Found {len(presets)} presets:")
        for token, name in presets[:10]:
            print(f"    [{token}] {name}")

    # 8. Available Factory Reset Options
    print("\n" + "=" * 60)
    print("[8] FACTORY RESET CAPABILITIES")
    print("=" * 60)
    print("  ONVIF SetSystemFactoryDefault command available.")
    print("  Options:")
    print("    - Hard: Full factory reset (all settings)")
    print("    - Soft: Partial reset (preserves network settings)")
    print("")
    print("  WARNING: Factory reset will erase all configuration!")
    print("  Use with caution.")

    # 9. Reboot Capability
    print("\n" + "=" * 60)
    print("[9] REBOOT CAPABILITY")
    print("=" * 60)
    print("  ONVIF SystemReboot command available.")
    print("  This will restart the camera.")

    # 10. Test SetHostname (non-destructive config test)
    print("\n" + "=" * 60)
    print("[10] GET HOSTNAME")
    print("=" * 60)
    result, status = send_request("/onvif/device_service",
        '<GetHostname xmlns="http://www.onvif.org/ver10/device/wsdl"/>')

    is_fault, fault_msg = check_fault(result)
    if is_fault:
        print(f"  Error: {fault_msg}")
    else:
        hostname = extract_value(result, 'Name')
        print(f"  Hostname: {hostname}")

    # Summary
    print("\n" + "=" * 80)
    print("SUMMARY - AVAILABLE ONVIF COMMANDS")
    print("=" * 80)
    print("""
Device Management:
  - GetDeviceInformation    - Get manufacturer, model, firmware
  - GetScopes               - Get device name, location
  - GetHostname / SetHostname - Get/set device hostname
  - GetNetworkInterfaces    - Get IP, MAC addresses
  - GetNTP / SetNTP         - Get/set NTP configuration
  - GetSystemDateAndTime    - Get system time
  - SetSystemFactoryDefault - Factory reset (Hard/Soft)
  - SystemReboot            - Reboot device

Media:
  - GetVideoEncoderConfigurations - Get encoding settings
  - SetVideoEncoderConfiguration  - Change resolution/bitrate
  - GetVideoSourceConfigurations  - Get video source info

PTZ:
  - GetPresets / SetPreset / RemovePreset - Manage presets
  - GotoPreset              - Go to saved position
  - ContinuousMove / Stop   - PTZ movement control

Note: Imaging settings (brightness/contrast/saturation) are NOT
supported by this camera via ONVIF (returns NoImagingForSource).
""")

if __name__ == "__main__":
    main()
