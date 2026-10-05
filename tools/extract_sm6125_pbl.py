"""Rebuild the six SM6125 bootstrap ranges from the supplied 665 Bus Hound capture.

This is a capture-specific extraction profile, not a generic Loader recovery tool.
Unobserved bytes are zero placeholders and are never valid bootstrap requests.
"""
import argparse
import hashlib
import json
import re
import struct
from pathlib import Path

RANGES = [(0, 64, 276, 278), (64, 896, 279, 280),
          (4096, 4096, 281, 282), (8192, 4096, 284, 285),
          (12288, 4096, 287, 288), (16384, 4096, 290, 291)]
IMAGE_SHA256 = "ec2f75c34a1f89a207a528c83aef5ac42873d94231cb5bf49b70c0b238ed1a55"
POSITION = re.compile(r"(\d+)\.(\d+)\.(\d+)(?:\((\d+)\))?\s*$")


def extract(source):
    if source.stat().st_size > 16 * 1024 * 1024:
        raise ValueError("Capture exceeds the 16 MiB extraction limit")
    packets = {}
    current = None
    with source.open(encoding="utf-8-sig") as stream:
        for line in stream:
            if len(line) > 512:
                raise ValueError("Capture row exceeds 512 characters")
            position = POSITION.search(line)
            if not position:
                continue
            command, phase, offset = map(int, position.group(1, 2, 3))
            if command > 296:
                break
            device = line[:6].strip()
            if device:
                current = None
                if device == "9.1" and line[7:12].strip() in ("IN", "OUT"):
                    repeat = int(position[4] or 1)
                    if not 1 <= repeat <= 1000 or (command, phase) in packets:
                        raise ValueError("Invalid repetition count or duplicate phase")
                    current = {"direction": line[7:12].strip(), "repeat": repeat, "data": bytearray()}
                    packets[command, phase] = current
            if current is not None:
                if current is not packets.get((command, phase)) or offset != len(current["data"]):
                    raise ValueError("Non-contiguous capture offsets")
                raw = line[14:40].strip()
                if not re.fullmatch(r"(?:[0-9a-fA-F]{2}(?:\s+|$))*", raw):
                    raise ValueError("Invalid byte dump")
                data = bytes.fromhex(raw)
                if len(current["data"]) + len(data) > 4096:
                    raise ValueError("Captured bootstrap transfer exceeds 4096 bytes")
                current["data"].extend(data)

    def packet(command, direction):
        value = packets[command, 1]
        if value["direction"] != direction or value["repeat"] != 1:
            raise ValueError("Unexpected bootstrap direction or repetition")
        return value["data"]

    retries = []
    for (command, phase), value in packets.items():
        if command < 276 and value["direction"] == "OUT":
            if phase != 1 or value["data"] != bytes.fromhex("139a9a9a"):
                raise ValueError("Unexpected trigger payload")
            retries.append({"command": command, "repeat": value["repeat"]})
    if len(retries) != 12 or sum(row["repeat"] for row in retries) != 95:
        raise ValueError("Trigger repetition counts do not match the supplied capture")
    hello = bytes.fromhex("02000000300000000200000001000000") + bytes(32)
    if packet(277, "OUT") != hello or packet(295, "OUT") != hello:
        raise ValueError("Unexpected HelloResponse")
    if struct.unpack_from("<II", packet(293, "IN")) != (1, 48):
        raise ValueError("Missing post-bootstrap Hello")
    if struct.unpack("<IIQQQ", packet(294, "IN")) != (18, 32, 13, 0, 64):
        raise ValueError("Missing first Loader request")

    image = bytearray(20480)
    coverage = []
    for offset, length, request, output in RANGES:
        if struct.unpack("<IIQQQ", packet(request, "IN")) != (18, 32, 13, offset, length):
            raise ValueError("Unexpected bootstrap read request")
        payload = packet(output, "OUT")
        if len(payload) != length:
            raise ValueError("Incomplete bootstrap payload")
        image[offset:offset + length] = payload
        coverage.append({"offset": offset, "length": length, "request": request, "output": output,
                         "sha256": hashlib.sha256(payload).hexdigest()})
    digest = hashlib.sha256(image).hexdigest()
    if digest != IMAGE_SHA256:
        raise ValueError("Extracted bootstrap bytes do not match the supplied capture")
    manifest = {"source_sha256": hashlib.sha256(source.read_bytes()).hexdigest(),
                "resource_sha256": digest, "resource_length": len(image), "transmitted_bytes": 17344,
                "trigger_groups": retries, "trigger_count": 95, "coverage": coverage,
                "unobserved_zero_placeholder": {"offset": 960, "length": 3136},
                "complete_original_loader": False}
    return image, manifest


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("capture", type=Path)
    parser.add_argument("output_directory", type=Path)
    arguments = parser.parse_args()
    image, manifest = extract(arguments.capture)
    arguments.output_directory.mkdir(parents=True, exist_ok=True)
    for name, content in [("sm6125_pbl_bootstrap.bin", image),
                          ("sm6125_pbl_bootstrap.json", (json.dumps(manifest, indent=2) + "\n").encode())]:
        target = arguments.output_directory / name
        if target.exists() and target.read_bytes() != content:
            raise ValueError("Refusing to replace different output: " + str(target))
        target.write_bytes(content)
    print("Extracted 6 ranges, 17344 transmitted bytes, 95 trigger writes; SHA256 " + IMAGE_SHA256)


if __name__ == "__main__":
    main()
