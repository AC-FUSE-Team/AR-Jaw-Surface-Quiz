#!/usr/bin/env python3
from __future__ import annotations
import argparse, ipaddress, os
import uvicorn

parser=argparse.ArgumentParser(description="Run the local Jaw Surface Quiz proxy")
parser.add_argument("--lan", action="store_true", help="Explicitly bind to one private LAN interface")
parser.add_argument("--host", default="", help="Private IPv4 address required with --lan")
parser.add_argument("--port", type=int, default=8765)
args=parser.parse_args()
if args.lan and not os.getenv("QUIZ_PROXY_TOKEN", "").strip():
    parser.error("--lan requires QUIZ_PROXY_TOKEN; unrestricted LAN mode is refused")
if args.lan:
    if not args.host:
        parser.error("--lan requires --host with the laptop's private IPv4 address")
    try:
        address = ipaddress.ip_address(args.host)
    except ValueError:
        parser.error("--host must be a valid IPv4 address")
    if address.version != 4 or not address.is_private or address.is_loopback or address.is_unspecified:
        parser.error("--host must be a specific private, non-loopback IPv4 address")
elif args.host:
    parser.error("--host is only accepted with --lan")
uvicorn.run("app.main:app", host=args.host if args.lan else "127.0.0.1", port=args.port,
            access_log=False)
