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

expected_projects = {"DHMP.Protocol", "DHMP.Client", "DHMP.Server", "DHMP.Licensing", "DHMP.AspNetCore", "DHMP.RawIpv6", "DHMP.AfXdp", "DHMP.Security", "DHMP.Connector"}
actual_projects = {p.parent.name for p in (ROOT / "src").rglob("*.csproj")}
if actual_projects != expected_projects:
    errors.append(f"Unexpected runtime project layout: {sorted(actual_projects)}")

required_paths = {
    "src/DHMP.Connector/DHMP.Connector.csproj",
    "src/DHMP.Connector/DhmpConnector.cs",
    "src/DHMP.Connector/DhmpConnection.cs",
    "docs/CONNECTOR.md",
    "src/DHMP.Protocol/DhmpProtocol.cs",
    "src/DHMP.Protocol/DhmpWireContract.cs",
    "src/DHMP.Protocol/DhmpSendPolicy.cs",
    "src/DHMP.Protocol/DhmpReceivePolicy.cs",
    "src/DHMP.Protocol/DhmpRatePolicy.cs",
    "src/DHMP.Protocol/DhmpPacingSchedule.cs",
    "src/DHMP.Protocol/DhmpCongestionPressure.cs",
    "src/DHMP.Protocol/DhmpCongestionFeedback.cs",
    "src/DHMP.Protocol/DhmpAdaptiveRateController.cs",
    "src/DHMP.Protocol/DhmpPathTelemetry.cs",
    "src/DHMP.Protocol/DhmpPathRateAdvisor.cs",
    "src/DHMP.Protocol/DhmpPacketProcessor.cs",
    "src/DHMP.Protocol/DhmpControlCodec.cs",
    "src/DHMP.Protocol/DhmpControlNegotiator.cs",
    "src/DHMP.Protocol/DhmpPeerProfile.cs",
    "src/DHMP.Protocol/IDhmpPacketSender.cs",
    "src/DHMP.Protocol/IDhmpPacketDecoder.cs",
    "src/DHMP.Security/DHMP.Security.csproj",
    "src/DHMP.Security/DhmpPskChaCha20Poly1305Session.cs",
    "src/DHMP.Security/DhmpSecureReceiveSnapshot.cs",
    "src/DHMP.Security/DhmpPathProbeMessage.cs",
    "src/DHMP.Security/DhmpProtectedPacketSender.cs",
    "src/DHMP.Security/DhmpSecurityControlCodec.cs",
    "src/DHMP.RawIpv6/DHMP.RawIpv6.csproj",
    "src/DHMP.AfXdp/DHMP.AfXdp.csproj",
    "src/DHMP.AfXdp/DhmpAfXdpHostProbe.cs",
    "src/DHMP.AfXdp/DhmpAfXdpBenchmark.cs",
    "src/DHMP.RawIpv6/DhmpRawIpv6PacketSender.cs",
    "src/DHMP.RawIpv6/DhmpRawIpv6Receiver.cs",
    "src/DHMP.RawIpv6/DhmpRawIpv6ListenerOptions.cs",
    "src/DHMP.RawIpv6/DhmpIpv6PathBudget.cs",
    "src/DHMP.RawIpv6/DhmpPathMtuException.cs",
    "src/DHMP.RawIpv6/DhmpRawIpv6PeerBinding.cs",
    "src/DHMP.RawIpv6/DhmpRawIpv6PeerRouter.cs",
    "src/DHMP.RawIpv6/DhmpRawIpv6MultiPeerReceiver.cs",
    "src/DHMP.RawIpv6/DhmpRawIpv6ControlChannel.cs",
    "src/DHMP.RawIpv6/DhmpRawIpv6Handshake.cs",
    "src/DHMP.RawIpv6/DhmpRawIpv6SecurityHandshake.cs",
    "src/DHMP.RawIpv6/DhmpRawIpv6CongestionChannel.cs",
    "src/DHMP.Server/DhmpLatestGenerationFilter.cs",
    "src/DHMP.Server/DhmpReceiveDispatchMode.cs",
    "src/DHMP.Server/DhmpReceiveDispatchSnapshot.cs",
    "src/DHMP.Server/DhmpCongestionAdvisor.cs",
    "src/DHMP.Server/DhmpBoundedReceiveDispatcher.cs",
    "docs/WIRE_CONTRACT_V1.md",
    "docs/CONTROL_PLANE_V1.md",
    "docs/SECURITY_PSK_V1.md",
    "docs/OVERLOAD_BEHAVIOR.md",
    "docs/TEST_STRATEGY.md",
    "docs/CONGESTION_FEEDBACK.md",
    "docs/DIRECT_TRANSPORT_DIRECTION.md",
    "docs/TRANSPORT_RESOLVER.md",
}
for required in sorted(required_paths):
    if required not in paths:
        errors.append(f"Required standalone protocol file missing: {required}")

forbidden = re.compile(
    r"\b(?:TcpClient|TcpListener|UdpClient|NetworkStream|SslStream|HttpListener|"
    r"WebSocket|QuicConnection|DHMPFixedStreamProcessor|DhmpFixedFrameReader|" 
    r"DhmpSessionContract|DhmpFixedContract|ExperimentalIpv6NextHeader)\b"
    r"|\bSocketType\s*\.\s*(?:Stream|Dgram)\b"
    r"|\bProtocolType\s*\.\s*(?:Tcp|Udp)\b"
)

comparison_benchmark_files = {
    "samples/DHMP.FramebufferDemo/ProtocolComparisonBenchmarks.cs",
}

udp_compatibility_files = {
    "src/DHMP.Connector/DhmpUdpRuntime.cs",
}

for file in files:
    relative = file.relative_to(ROOT).as_posix()

    if file.suffix == ".cs":
        text = file.read_text(encoding="utf-8-sig")
        is_comparison_benchmark = relative in comparison_benchmark_files
        is_udp_compatibility = relative in udp_compatibility_files

        if (
            re.search(r"\bHttpClient\b", text)
            and relative not in {
                "src/DHMP.AspNetCore/Api/DhmpApiHttpMessageHandler.cs",
                "tests/DHMP.AspNetCore.Tests/DhmpApiIntegrationTests.cs",
            }
            and not is_comparison_benchmark
        ):
            errors.append(f"HTTP client outside the explicit API application facade in {relative}")

        # Legacy stream transports remain forbidden. UDP datagram sockets are allowed
        # only in the explicit Connector compatibility backend selected by the resolver.
        boundary_text = text
        if relative == "tests/DHMP.ConnectedSendCheck/Program.cs":
            # This local-only fixture verifies Socket.SendAsync pending lifecycle.
            # Exempt only these exact Unix datagram constructors, not IP datagrams,
            # streams, arbitrary test transports, or a production backend.
            boundary_text = re.sub(
                r"new\s+Socket\(\s*AddressFamily\.Unix\s*,\s*SocketType\.Dgram\s*,\s*ProtocolType\.Unspecified\s*\)",
                "",
                boundary_text,
            )
        if forbidden.search(boundary_text) and not (is_comparison_benchmark or is_udp_compatibility):
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
protocol_file = ROOT / "src" / "DHMP.Protocol" / "DhmpProtocol.cs"

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

if protocol_file.is_file():
    protocol_text = protocol_file.read_text(encoding="utf-8-sig")
    if "ExperimentalIpv6DataNextHeader = 253" not in protocol_text:
        errors.append("Experimental DHMP data binding must remain explicit")
    if "ExperimentalIpv6ControlNextHeader = 254" not in protocol_text:
        errors.append("Experimental DHMP control binding must remain separate from data")

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

print("PASS: native/compatibility transport boundary, headerless V1 contract, canonical projects and local references")

