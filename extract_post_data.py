#!/usr/bin/env python3
"""
Extract complete POST body data
"""

from scapy.all import rdpcap, TCP, IP, Raw
import re

def extract_post_bodies(pcap_file):
    print(f"Extracting POST bodies from {pcap_file}...\n")

    packets = rdpcap(pcap_file)

    for i, pkt in enumerate(packets):
        if not pkt.haslayer(Raw):
            continue

        if IP in pkt and TCP in pkt:
            payload = bytes(pkt[Raw].load)

            try:
                decoded = payload.decode('utf-8', errors='ignore')

                # Look for specific endpoints
                if 'POST /ipcLogin' in decoded:
                    print("=" * 80)
                    print(f"LOGIN REQUEST (Packet {i}):")
                    print("=" * 80)
                    # Find the body (after double \r\n)
                    parts = decoded.split('\r\n\r\n', 1)
                    if len(parts) > 1:
                        print(parts[1][:500])
                    print()

                elif 'POST /setPTZCmd' in decoded:
                    print("=" * 80)
                    print(f"PTZ COMMAND (Packet {i}):")
                    print("=" * 80)
                    parts = decoded.split('\r\n\r\n', 1)
                    if len(parts) > 1:
                        body = parts[1]
                        print(body)
                    print()

                    # Only show first few examples
                    if i > 30000:
                        break

                elif 'POST /getPresetList' in decoded:
                    print("=" * 80)
                    print(f"GET PRESET LIST (Packet {i}):")
                    print("=" * 80)
                    parts = decoded.split('\r\n\r\n', 1)
                    if len(parts) > 1:
                        print(parts[1][:500])
                    print()

            except:
                pass

if __name__ == '__main__':
    pcap_file = r"C:\Users\moore\Documents\Camera1.pcapng"
    extract_post_bodies(pcap_file)
