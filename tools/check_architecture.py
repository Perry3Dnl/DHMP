"""Check the active standalone direct-IP boundary and local references without external dependencies."""
from pathlib import Path
import re
import sys
import xml.etree.ElementTree as ET

ROOT = Path(__file__).resolve().parents[1]
errors = []

files = [
    p for p in ROOT.rglob("*")
    if p.is_file()
    and not any(part in {".git", "bin", "obj", "__pycache__"} for part in p.relative_to(ROOT).parts)
]
paths = {p.relative_to(ROOT).as_posix() for p in files}

canonical = {}
for path in sorted(paths):
    parts = path.split("/")
    for length in range(1, len(parts) + 1):
        prefix = "/".join(parts[:length])
        key = prefix.casefold()
        if key in canonical and canonical[key] != prefix:
            errors.append(f"Case-only path collision: {canonical[key]} / {prefix}")
        canonical[key] = prefix

expected_projects = {"DHMP.Protocol", "DHMP.Client", "DHMP.Server", "DHMP.Licensing", "DHMP.AspNetCore", "DHMP.RawIpv6"}
actual_projects = {p.parent.name for p in (ROOT / "src").rglob("*.csproj")}
if actual_projects != expected_projects:
    errors.append(f"Unexpected runtime project layout: {sorted(actual_projects)}")

required_paths = {
    "src/DHMP.Protocol/DhmpProtocol.cs",
    "src/DHMP.Protocol/DhmpWireContract.cs",
    "src/DHMP.Protocol/DhmpSendPolicy.cs",
    "src/DHMP.Protocol/DhmpReceivePolicy.cs",
    "src/DHMP.Protocol/DhmpPacketProcessor.cs",
    "src/DHMP.Protocol/IDhmpPacketSender.cs",
    "src/DHMP.RawIpv6/DHMP.RawIpv6.csproj",
    "src/DHMP.RawIpv6/DhmpRawIpv6PacketSender.cs",
    "src/DHMP.RawIpv6/DhmpRawIpv6Receiver.cs",
    "docs/WIRE_CONTRACT_V1.md",
    "docs/DIRECT_TRANSPORT_DIRECTION.md",
}
for required in sorted(required_paths):
    if required not in paths:
        errors.append(f"Required standalone protocol file missing: {required}")

forbidden = re.compile(
    r"\b(?:TcpClient|TcpListener|UdpClient|NetworkStream|SslStream|HttpClient|HttpListener|"
    r"WebSocket|QuicConnection|DHMPFixedStreamProcessor|DhmpFixedFrameReader|" 
    r"DhmpSessionContract|DhmpFixedContract)\b"
    r"|\bSocketType\s*\.\s*(?:Stream|Dgram)\b"
    r"|\bProtocolType\s*\.\s*(?:Tcp|Udp)\b"
)

for file in files:
    relative = file.relative_to(ROOT).as_posix()

    if file.suffix == ".cs":
        text = file.read_text(encoding="utf-8-sig")
        if forbidden.search(text):
            errors.append(f"Legacy transport/stream implementation in {relative}")
        if re.search(r"\b(?:namespace|using)\s+Dhmp\.", text):
            errors.append(f"Noncanonical namespace in {relative}")

    if file.suffix == ".csproj":
        root = ET.parse(file).getroot()

        for ref in root.iter("ProjectReference"):
            target = (file.parent / ref.attrib["Include"]).resolve()
            if not target.is_file():
                errors.append(f"Missing project reference in {relative}: {ref.attrib['Include']}")

        for package in root.iter("PackageReference"):
            if re.search(r"grpc|quic|websocket", package.attrib["Include"], re.I):
                errors.append(f"Disallowed transport dependency in {relative}")

    if file.suffix == ".md":
        for target in re.findall(r"\]\(([^)]+)\)", file.read_text(encoding="utf-8")):
            if "://" in target or target.startswith("#"):
                continue
            destination = target.split("#", 1)[0]
            if destination and not (file.parent / destination).exists():
                errors.append(f"Broken local link in {relative}: {target}")

client_file = ROOT / "src" / "DHMP.Client" / "DhmpClient.cs"
server_file = ROOT / "src" / "DHMP.Server" / "DhmpServer.cs"
wire_file = ROOT / "docs" / "WIRE_CONTRACT_V1.md"

if client_file.is_file():
    client_text = client_file.read_text(encoding="utf-8-sig")
    if "DhmpWireContract" not in client_text or "DhmpSendPolicy" not in client_text:
        errors.append("DhmpClient must separate wire compatibility from local send policy")

if server_file.is_file():
    server_text = server_file.read_text(encoding="utf-8-sig")
    if "DhmpWireContract" not in server_text or "DhmpReceivePolicy" not in server_text:
        errors.append("DhmpServer must separate wire compatibility from local receive policy")

if wire_file.is_file():
    wire_text = wire_file.read_text(encoding="utf-8")
    if "zero DHMP header bytes" not in wire_text:
        errors.append("V1 wire contract must explicitly preserve the headerless data-plane rule")

for directory in (ROOT / "benchmarks").iterdir():
    if directory.is_dir() and not re.match(r"(?:mock-ip|raw-ipv6|packet-|direct-ip-)", directory.name):
        errors.append(f"Historical benchmark active again: {directory.name}")

for name in (
    "tcp-current.yml",
    "udp-latest-experiment.yml",
    "fixed-stream-carry-ab.yml",
    "dotnet-max-throughput.yml",
    "production-receive-boundary.yml",
    "processor-pipeline-benchmark.yml",
):
    if (ROOT / ".github" / "workflows" / name).exists():
        errors.append(f"Historical workflow active again: {name}")

if errors:
    print("\n".join(errors), file=sys.stderr)
    sys.exit(1)

print("PASS: standalone direct-IP boundary, headerless V1 contract, canonical projects and local references")
