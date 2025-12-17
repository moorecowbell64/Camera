#!/usr/bin/env python3
"""
Find the actual RPC endpoint from pcap
"""

from scapy.all import rdpcap, TCP, IP, Raw
import re

def find_rpc_endpoint(pcap_file):
    print(f"Searching for RPC endpoint in {pcap_file}...\n")

    packets = rdpcap(pcap_file)

    camera_ip = "192.168.50.224"
    client_ip = "192.168.50.151"

    post_requests = []

    for i, pkt in enumerate(packets):
        if not pkt.haslayer(Raw):
            continue

        if IP in pkt and TCP in pkt:
            payload = bytes(pkt[Raw].load)

            try:
                decoded = payload.decode('utf-8', errors='ignore')

                # Look for POST requests
                if decoded.startswith('POST '):
                    lines = decoded.split('\r\n')
                    if len(lines) > 0:
                        # Extract POST path
                        parts = lines[0].split(' ')
                        if len(parts) >= 2:
                            path = parts[1]

                            # Look for JSON content
                            has_json = '"method"' in decoded

                            post_requests.append({
                                'packet': i,
                                'src': pkt[IP].src,
                                'dst': pkt[IP].dst,
                                'dst_port': pkt[TCP].dport,
                                'path': path,
                                'has_json': has_json,
                                'first_line': lines[0],
                                'preview': decoded[:300]
                            })
            except:
                pass

    print("=" * 80)
    print(f"POST REQUESTS FOUND: {len(post_requests)}")
    print("=" * 80)

    # Group by path
    paths = {}
    for req in post_requests:
        path = req['path']
        port = req['dst_port']
        key = f"{path} (port {port})"
        if key not in paths:
            paths[key] = []
        paths[key].append(req)

    for path, reqs in sorted(paths.items()):
        json_count = sum(1 for r in reqs if r['has_json'])
        print(f"\n{path}")
        print(f"  Total requests: {len(reqs)}")
        print(f"  JSON-RPC requests: {json_count}")

        # Show first JSON example
        json_req = next((r for r in reqs if r['has_json']), None)
        if json_req:
            print(f"  Example (packet {json_req['packet']}):")
            print(f"    {json_req['src']}:{json_req.get('src_port', '?')} -> {json_req['dst']}:{json_req['dst_port']}")
            print(f"    {json_req['preview']}")

if __name__ == '__main__':
    pcap_file = r"C:\Users\moore\Documents\Camera1.pcapng"
    find_rpc_endpoint(pcap_file)
