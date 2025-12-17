#!/usr/bin/env python3
"""
Discover camera settings and factory controls via ONVIF
"""

import requests
from requests.auth import HTTPDigestAuth
import hashlib
import base64
import datetime
import random
import string

# Camera config
CAMERA_IP = "192.168.50.224"
USERNAME = "admin"
PASSWORD = "hydroLob99"

def generate_nonce():
    return ''.join(random.choices(string.ascii_letters + string.digits, k=16))

def generate_security_header():
    nonce = generate_nonce()
    created = datetime.datetime.utcnow().strftime('%Y-%m-%dT%H:%M:%S.000Z')

    # Password digest = Base64(SHA1(nonce + created + password))
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
        return response.text
    except Exception as e:
        return f"Error: {e}"

def main():
    print("=" * 80)
    print("ONVIF SERVICE DISCOVERY")
    print("=" * 80)

    # 1. Get Services
    print("\n[1] GetServices:")
    result = send_request("/onvif/device_service", '<GetServices xmlns="http://www.onvif.org/ver10/device/wsdl"><IncludeCapability>true</IncludeCapability></GetServices>')
    print(result[:3000])

    # 2. Get Device Capabilities
    print("\n" + "=" * 80)
    print("[2] GetCapabilities:")
    result = send_request("/onvif/device_service", '<GetCapabilities xmlns="http://www.onvif.org/ver10/device/wsdl"><Category>All</Category></GetCapabilities>')
    print(result[:3000])

    # 3. Get Video Sources
    print("\n" + "=" * 80)
    print("[3] GetVideoSources:")
    result = send_request("/onvif/Media", '<GetVideoSources xmlns="http://www.onvif.org/ver10/media/wsdl"/>')
    print(result[:2000])

    # 4. Get Imaging Settings (brightness, contrast, etc.)
    print("\n" + "=" * 80)
    print("[4] GetImagingSettings:")
    result = send_request("/onvif/Imaging", '<GetImagingSettings xmlns="http://www.onvif.org/ver20/imaging/wsdl"><VideoSourceToken>VideoSource_1</VideoSourceToken></GetImagingSettings>')
    print(result[:3000])

    # 5. Get Options for imaging
    print("\n" + "=" * 80)
    print("[5] GetOptions (Imaging):")
    result = send_request("/onvif/Imaging", '<GetOptions xmlns="http://www.onvif.org/ver20/imaging/wsdl"><VideoSourceToken>VideoSource_1</VideoSourceToken></GetOptions>')
    print(result[:3000])

    # 6. Get PTZ Presets
    print("\n" + "=" * 80)
    print("[6] GetPresets:")
    result = send_request("/onvif/PTZ", '<GetPresets xmlns="http://www.onvif.org/ver20/ptz/wsdl"><ProfileToken>MainStream</ProfileToken></GetPresets>')
    print(result[:2000])

    # 7. Get System Factory Default
    print("\n" + "=" * 80)
    print("[7] GetSystemFactoryDefault (check if available):")
    result = send_request("/onvif/device_service", '<GetSystemFactoryDefault xmlns="http://www.onvif.org/ver10/device/wsdl"/>')
    print(result[:1500])

    # 8. System Reboot
    print("\n" + "=" * 80)
    print("[8] SystemReboot (discovery only):")
    # Don't actually send this, just show what it would look like
    print("Available endpoint: /onvif/device_service - SystemReboot")

    # 9. Get Network Interfaces
    print("\n" + "=" * 80)
    print("[9] GetNetworkInterfaces:")
    result = send_request("/onvif/device_service", '<GetNetworkInterfaces xmlns="http://www.onvif.org/ver10/device/wsdl"/>')
    print(result[:2000])

    # 10. Get Video Encoder Configurations
    print("\n" + "=" * 80)
    print("[10] GetVideoEncoderConfigurations:")
    result = send_request("/onvif/Media", '<GetVideoEncoderConfigurations xmlns="http://www.onvif.org/ver10/media/wsdl"/>')
    print(result[:3000])

if __name__ == "__main__":
    main()
