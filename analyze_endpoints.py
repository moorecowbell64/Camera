#!/usr/bin/env python3
"""
Analyze the actual HTTP endpoints and their parameters
"""

from scapy.all import rdpcap, TCP, IP, Raw
import re

def analyze_endpoints(pcap_file):
    print(f"Analyzing endpoints in {pcap_file}...\n")

    packets = rdpcap(pcap_file)

    # Find login requests
    login_requests = []
    ptz_requests = []
    preset_requests = []

    for i, pkt in enumerate(packets):
        if not pkt.haslayer(Raw):
            continue

        if IP in pkt and TCP in pkt:
            payload = bytes(pkt[Raw].load)

            try:
                decoded = payload.decode('utf-8', errors='ignore')

                # Look for login
                if 'POST /ipcLogin' in decoded or 'POST /WEBLogin' in decoded:
                    login_requests.append({'packet': i, 'data': decoded})

                # Look for PTZ commands
                if 'POST /setPTZCmd' in decoded:
                    ptz_requests.append({'packet': i, 'data': decoded[:800]})

                # Look for preset requests
                if 'POST /getPresetList' in decoded or 'POST /PresetList' in decoded:
                    preset_requests.append({'packet': i, 'data': decoded})

            except:
                pass

    print("=" * 80)
    print("LOGIN REQUESTS:")
    print("=" * 80)
    for req in login_requests[:3]:
        print(f"\nPacket {req['packet']}:")
        print(req['data'][:500])

    print("\n" + "=" * 80)
    print(f"PTZ COMMAND EXAMPLES ({len(ptz_requests)} total):")
    print("=" * 80)
    for req in ptz_requests[:10]:
        print(f"\nPacket {req['packet']}:")
        print(req['data'])
        print("-" * 40)

    print("\n" + "=" * 80)
    print("PRESET REQUESTS:")
    print("=" * 80)
    for req in preset_requests[:3]:
        print(f"\nPacket {req['packet']}:")
        print(req['data'][:500])

if __name__ == '__main__':
    pcap_file = r"C:\Users\moore\Documents\Camera1.pcapng"
    analyze_endpoints(pcap_file)
