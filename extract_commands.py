#!/usr/bin/env python3
"""
Extract PTZ Commands and API calls from PCAP
"""

from scapy.all import rdpcap, TCP, IP, Raw
import re
import json

def extract_soap_commands(pcap_file):
    """Extract SOAP/XML API commands"""
    print(f"Extracting commands from {pcap_file}...\n")

    packets = rdpcap(pcap_file)

    camera_ip = "192.168.50.224"  # From analysis
    client_ip = "192.168.50.151"

    print(f"Camera IP: {camera_ip}")
    print(f"Client IP: {client_ip}\n")

    soap_requests = []
    json_rpc_calls = []
    ptz_commands = []

    for i, pkt in enumerate(packets):
        if not pkt.haslayer(Raw):
            continue

        payload = bytes(pkt[Raw].load)

        try:
            decoded = payload.decode('utf-8', errors='ignore')

            # Look for SOAP envelopes
            if 'soap:Envelope' in decoded and IP in pkt:
                soap_requests.append({
                    'packet': i,
                    'src': pkt[IP].src,
                    'dst': pkt[IP].dst,
                    'payload': decoded
                })

            # Look for JSON-RPC calls (common in IP cameras)
            if '"method"' in decoded and '"params"' in decoded:
                # Try to extract JSON
                json_matches = re.findall(r'\{[^{}]*"method"[^}]*\}', decoded)
                for match in json_matches:
                    try:
                        json_obj = json.loads(match)
                        json_rpc_calls.append({
                            'packet': i,
                            'method': json_obj.get('method', 'unknown'),
                            'params': json_obj.get('params', {}),
                            'full': match
                        })
                    except:
                        # Not valid JSON, try to extract method name at least
                        method_match = re.search(r'"method"\s*:\s*"([^"]+)"', match)
                        if method_match:
                            json_rpc_calls.append({
                                'packet': i,
                                'method': method_match.group(1),
                                'params': 'parse_error',
                                'full': match[:200]
                            })

            # Look for PTZ-related keywords
            if any(keyword in decoded.lower() for keyword in ['ptz', 'pan', 'tilt', 'zoom', 'preset']):
                if len(decoded) < 2000:  # Only short commands
                    ptz_commands.append({
                        'packet': i,
                        'content': decoded[:500]
                    })

        except:
            pass

    # Print SOAP requests
    print("=" * 80)
    print(f"SOAP/XML REQUESTS: {len(soap_requests)}")
    print("=" * 80)
    for req in soap_requests[:10]:
        print(f"\nPacket {req['packet']}: {req['src']} -> {req['dst']}")
        # Try to format nicely
        if '<soap:Body>' in req['payload']:
            body_start = req['payload'].find('<soap:Body>')
            body_end = req['payload'].find('</soap:Body>')
            if body_start > 0 and body_end > 0:
                print(req['payload'][body_start:body_end + 12])
        else:
            print(req['payload'][:300])

    # Print JSON-RPC calls
    print("\n" + "=" * 80)
    print(f"JSON-RPC API CALLS: {len(json_rpc_calls)}")
    print("=" * 80)

    # Group by method
    methods = {}
    for call in json_rpc_calls:
        method = call['method']
        if method not in methods:
            methods[method] = []
        methods[method].append(call)

    for method, calls in sorted(methods.items()):
        print(f"\n{method} ({len(calls)} calls)")
        # Show first example
        example = calls[0]
        print(f"  Example: {example['full'][:200]}")

    # Print PTZ commands
    print("\n" + "=" * 80)
    print(f"PTZ-RELATED COMMANDS: {len(ptz_commands)}")
    print("=" * 80)
    for cmd in ptz_commands[:5]:
        print(f"\nPacket {cmd['packet']}:")
        print(cmd['content'])

    # Summary
    print("\n" + "=" * 80)
    print("API SUMMARY:")
    print("=" * 80)
    print(f"Camera appears to use HTTP-based API on port 12351")
    print(f"Protocol: Custom SOAP + JSON-RPC")
    print(f"\nAvailable methods:")
    for method in sorted(methods.keys())[:20]:
        print(f"  - {method}")

if __name__ == '__main__':
    pcap_file = r"C:\Users\moore\Documents\Camera1.pcapng"
    extract_soap_commands(pcap_file)
