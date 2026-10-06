"""Dependency-free packaging/reference checks; does not claim to compile Unity."""
from pathlib import Path
import json
import re

ROOT = Path(__file__).resolve().parents[1]
PACKAGE = ROOT / "Packages/com.dhmp.networking"
manifest = json.loads((PACKAGE / "package.json").read_text())
assert manifest["name"] == "com.dhmp.networking"
assert manifest["samples"] and all((PACKAGE / sample["path"]).is_dir() for sample in manifest["samples"])
required = ["Runtime/Core/DHMP.Unity.Core.asmdef", "Runtime/Unity/DHMP.Unity.asmdef", "Editor/DHMP.Unity.Editor.asmdef",
            "Samples~/DemoArena/DemoArena.unity", "Samples~/DemoArena/DhmpDemoSettings.asset",
            "Samples~/DemoArena/Hosting/start-server.sh", "Samples~/DemoArena/Hosting/README.md", "LICENSE.md"]
assert all((PACKAGE / path).is_file() for path in required)
guids = {}
for meta in PACKAGE.rglob("*.meta"):
    match = re.search(r"^guid: ([0-9a-f]{32})$", meta.read_text(), re.M)
    assert match, f"Invalid GUID: {meta}"
    assert match[1] not in guids, f"Duplicate GUID: {meta} and {guids.get(match[1])}"
    guids[match[1]] = meta
    assert Path(str(meta)[:-5]).exists(), f"Orphan meta: {meta}"
for path in PACKAGE.rglob("*"):
    if path.suffix in {".cs", ".shader", ".unity", ".asset", ".asmdef"}:
        assert Path(str(path) + ".meta").is_file(), f"Missing meta: {path}"
    if path.suffix in {".unity", ".asset"}:
        for guid in re.findall(r"guid: ([0-9a-f]{32})", path.read_text()):
            assert guid in guids, f"Unresolved sample reference {guid}: {path}"
    if path.suffix == ".cs":
        assert not re.search(r"\b(?:TcpClient|TcpListener|UdpClient|NetworkStream|WebSocket|HttpClient)\b|ProtocolType\.(?:Tcp|Udp)", path.read_text()), f"Transport boundary violated: {path}"
assemblies = {json.loads(path.read_text())["name"]: json.loads(path.read_text()) for path in PACKAGE.rglob("*.asmdef")}
for name, definition in assemblies.items():
    assert name.startswith("DHMP."), name
    for reference in definition.get("references", []):
        assert reference in assemblies or reference == "Unity.InputSystem", f"Unknown assembly: {reference}"
    if name.endswith(".Editor"):
        assert definition["includePlatforms"] == ["Editor"]
assert assemblies["DHMP.Unity.Core"]["noEngineReferences"]
settings = (PACKAGE / "Samples~/DemoArena/DhmpDemoSettings.asset").read_text()
assert re.search(r"^  demoServerIpv6: ?$", settings, re.M), "Do not ship an invented demo endpoint"
print(f"PASS: Unity package, bundled arena, hosting files, {len(guids)} stable GUIDs and assembly references")
