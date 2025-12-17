#!/usr/bin/env python3
"""
PCAP Analyzer for PTZ Camera Traffic
Analyzes network traffic to identify camera protocols and commands
"""

try:
    from scapy.all import rdpcap, TCP, UDP, IP, Raw
    from scapy.layers.http import HTTPRequest, HTTPResponse
    SCAPY_AVAILABLE = True
except ImportError:
    SCAPY_AVAILABLE = False
    print("Scapy not available, using basic packet parsing")

import struct
import re

def analyze_pcap_basic(pcap_file):
    """Basic pcap analysis without scapy"""
    print(f"Analyzing {pcap_file}...")

    with open(pcap_file, 'rb') as f:
        # Read pcapng file header
        header = f.read(28)

        if not header:
            print("Empty file")
            return

        # Simple statistics
        http_requests = []
        rtsp_packets = []
        onvif_packets = []

        print("\nNote: For detailed analysis, install scapy:")
        print("  pip install scapy")
        print("\nBasic file info:")
        print(f"  File size: {len(open(pcap_file, 'rb').read())} bytes")

def analyze_pcap_with_scapy(pcap_file):
    """Detailed analysis using scapy"""
    print(f"\nAnalyzing {pcap_file} with Scapy...\n")

    try:
        packets = rdpcap(pcap_file)
    except Exception as e:
        print(f"Error reading pcap: {e}")
        return

    print(f"Total packets: {len(packets)}\n")

    # Find unique IP addresses
    ips = set()
    http_requests = []
    rtsp_packets = []
    onvif_packets = []
    tcp_ports = set()
    udp_ports = set()

    for i, pkt in enumerate(packets):
        if IP in pkt:
            ips.add(pkt[IP].src)
            ips.add(pkt[IP].dst)

        if TCP in pkt:
            tcp_ports.add(pkt[TCP].sport)
            tcp_ports.add(pkt[TCP].dport)

        if UDP in pkt:
            udp_ports.add(pkt[UDP].sport)
            udp_ports.add(pkt[UDP].dport)

        # Check for HTTP traffic
        if pkt.haslayer(Raw):
            payload = bytes(pkt[Raw].load)

            # Look for HTTP requests
            if payload.startswith(b'GET ') or payload.startswith(b'POST ') or \
               payload.startswith(b'PUT ') or payload.startswith(b'DELETE '):
                try:
                    decoded = payload.decode('utf-8', errors='ignore')
                    lines = decoded.split('\r\n')
                    if len(lines) > 0:
                        http_requests.append({
                            'packet': i,
                            'src': pkt[IP].src if IP in pkt else 'Unknown',
                            'dst': pkt[IP].dst if IP in pkt else 'Unknown',
                            'request': lines[0],
                            'payload': decoded[:500]  # First 500 chars
                        })
                except:
                    pass

            # Look for RTSP traffic
            if b'RTSP/' in payload or payload.startswith(b'DESCRIBE ') or \
               payload.startswith(b'SETUP ') or payload.startswith(b'PLAY '):
                try:
                    decoded = payload.decode('utf-8', errors='ignore')
                    rtsp_packets.append({
                        'packet': i,
                        'src': pkt[IP].src if IP in pkt else 'Unknown',
                        'dst': pkt[IP].dst if IP in pkt else 'Unknown',
                        'payload': decoded[:500]
                    })
                except:
                    pass

            # Look for ONVIF/SOAP traffic
            if b'<SOAP' in payload or b'<soap' in payload or \
               b'onvif' in payload.lower():
                try:
                    decoded = payload.decode('utf-8', errors='ignore')
                    onvif_packets.append({
                        'packet': i,
                        'src': pkt[IP].src if IP in pkt else 'Unknown',
                        'dst': pkt[IP].dst if IP in pkt else 'Unknown',
                        'payload': decoded[:1000]
                    })
                except:
                    pass

    # Print results
    print("=" * 80)
    print("IP ADDRESSES FOUND:")
    print("=" * 80)
    for ip in sorted(ips):
        print(f"  {ip}")

    print("\n" + "=" * 80)
    print("TCP PORTS USED:")
    print("=" * 80)
    common_ports = {80: 'HTTP', 443: 'HTTPS', 554: 'RTSP', 8000: 'Alt HTTP',
                    8080: 'HTTP Proxy', 8554: 'Alt RTSP', 37777: 'Dahua DVR'}
    for port in sorted(tcp_ports):
        port_name = common_ports.get(port, '')
        print(f"  {port} {port_name}")

    print("\n" + "=" * 80)
    print(f"HTTP REQUESTS FOUND: {len(http_requests)}")
    print("=" * 80)
    for req in http_requests[:20]:  # Show first 20
        print(f"\nPacket {req['packet']}: {req['src']} -> {req['dst']}")
        print(f"  {req['request']}")
        print(f"  Preview: {req['payload'][:200]}...")

    if len(http_requests) > 20:
        print(f"\n  ... and {len(http_requests) - 20} more HTTP requests")

    print("\n" + "=" * 80)
    print(f"RTSP PACKETS FOUND: {len(rtsp_packets)}")
    print("=" * 80)
    for pkt in rtsp_packets[:10]:  # Show first 10
        print(f"\nPacket {pkt['packet']}: {pkt['src']} -> {pkt['dst']}")
        print(f"  {pkt['payload'][:300]}...")

    if len(rtsp_packets) > 10:
        print(f"\n  ... and {len(rtsp_packets) - 10} more RTSP packets")

    print("\n" + "=" * 80)
    print(f"ONVIF/SOAP PACKETS FOUND: {len(onvif_packets)}")
    print("=" * 80)
    for pkt in onvif_packets[:5]:  # Show first 5
        print(f"\nPacket {pkt['packet']}: {pkt['src']} -> {pkt['dst']}")
        print(f"  {pkt['payload'][:500]}...")

    if len(onvif_packets) > 5:
        print(f"\n  ... and {len(onvif_packets) - 5} more ONVIF packets")

    # Summary
    print("\n" + "=" * 80)
    print("PROTOCOL SUMMARY:")
    print("=" * 80)
    if http_requests:
        print(f"  ✓ HTTP traffic detected ({len(http_requests)} requests)")
    if rtsp_packets:
        print(f"  ✓ RTSP streaming protocol detected ({len(rtsp_packets)} packets)")
    if onvif_packets:
        print(f"  ✓ ONVIF protocol detected ({len(onvif_packets)} packets)")

    if 37777 in tcp_ports:
        print(f"  ✓ Dahua proprietary protocol (port 37777) detected")

if __name__ == '__main__':
    import sys

    pcap_file = r"C:\Users\moore\Documents\Camera1.pcapng"

    if len(sys.argv) > 1:
        pcap_file = sys.argv[1]

    if SCAPY_AVAILABLE:
        analyze_pcap_with_scapy(pcap_file)
    else:
        analyze_pcap_basic(pcap_file)
        print("\nInstall scapy for detailed analysis:")
        print("  pip install scapy")
