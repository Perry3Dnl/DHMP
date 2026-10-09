"""Verify two separate real-socket processes using the same core as the Unity client."""
from pathlib import Path
import json
import subprocess
import sys
import tempfile
import time

ROOT = Path(__file__).resolve().parents[1]


def main():
    dotnet = sys.argv[1] if len(sys.argv) > 1 else "dotnet"
    host = ROOT / "samples/DHMP.UnityArenaHost/bin/Debug/net10.0/DHMP.UnityArenaHost.dll"
    command = [dotnet, str(host)]
    denied = subprocess.run(command + ["check-host", "--bind", "::1", "--development"], capture_output=True, text=True, timeout=10)
    assert denied.returncode != 0 and "experimental-plaintext" in denied.stdout, "Experiment opt-in was bypassed"
    denied = subprocess.run(command + ["serve", "--bind", "2001:db8::1", "--experimental-plaintext", "--development"], capture_output=True, text=True, timeout=10)
    assert denied.returncode != 0 and "allow-client" in denied.stdout, "Remote-address source admission was bypassed"
    release = ROOT / "Packages/com.dhmp.networking/Samples~/DemoArena/Hosting/DHMP.UnityArenaHost~/bin/Release/net10.0/DHMP.UnityArenaHost.dll"
    if release.is_file():
        denied = subprocess.run([dotnet, str(release), "check-host", "--bind", "::1", "--experimental-plaintext", "--development"], capture_output=True, text=True, timeout=10)
        assert denied.returncode != 0 and "Debug host build" in denied.stdout, "Release host accepted development licensing"
    with tempfile.TemporaryDirectory(prefix="dhmp-host-") as folder:
        log = Path(folder) / "server.log"
        with log.open("w") as output:
            server = subprocess.Popen(command + ["serve", "--bind", "::1", "--experimental-plaintext", "--development"], stdout=output, stderr=subprocess.STDOUT)
            try:
                deadline = time.monotonic() + 10
                while time.monotonic() < deadline:
                    content = log.read_text()
                    if '"kind":"ready"' in content:
                        break
                    if server.poll() is not None:
                        raise RuntimeError(content)
                    time.sleep(0.05)
                else:
                    raise RuntimeError("Host did not become ready: " + log.read_text())
                client = subprocess.run(command + ["connect", "--bind", "::1", "--server", "::1", "--experimental-plaintext", "--development"], capture_output=True, text=True, timeout=15)
                assert client.returncode == 0, client.stdout + client.stderr
                messages = [json.loads(line) for line in client.stdout.splitlines() if line.startswith("{")]
                assert any(message["kind"] == "connection_verified" for message in messages), client.stdout
            finally:
                server.terminate()
                try:
                    server.wait(timeout=5)
                except subprocess.TimeoutExpired:
                    server.kill()
                    server.wait(timeout=5)
    print("PASS: standalone server and client processes completed real raw IPv6 join and input/state exchange; no Unity runtime or remote Internet claim")


if __name__ == "__main__":
    main()
