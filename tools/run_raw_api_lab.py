#!/usr/bin/env python3
"""Real DHMP API/socket rehearsal in two namespaces on one hosted Linux VM."""
import argparse
import base64
import json
import os
from pathlib import Path
import shutil
import signal
import subprocess
import sys
import tempfile
import time
import urllib.error
import urllib.request


def probe():
    data = json.load(sys.stdin)
    body = base64.b64decode(data["body"]) if data.get("body") else None
    request = urllib.request.Request("http://127.0.0.1:5080" + data["path"], data=body,
                                     headers=data.get("headers", {}), method=data.get("method", "GET"))
    try:
        response = urllib.request.urlopen(request, timeout=10)
    except urllib.error.HTTPError as error:
        response = error
    with response:
        print(json.dumps({"status": response.status, "body": base64.b64encode(response.read()).decode()}))


def run(command, **kwargs):
    return subprocess.run(command, check=True, text=True, capture_output=True, **kwargs).stdout


def main():
    parser = argparse.ArgumentParser()
    parser.add_argument("--sample", required=True)
    parser.add_argument("--fixture", required=True)
    parser.add_argument("--dotnet", required=True)
    parser.add_argument("--output", required=True)
    args = parser.parse_args()
    if sys.platform != "linux" or os.geteuid() != 0:
        raise RuntimeError("Run the isolated lab with root/network-namespace privileges.")
    for dependency in ("ip", "tc", "tcpdump"):
        if not shutil.which(dependency):
            raise RuntimeError("Missing required tool: " + dependency)
    output = Path(args.output).resolve()
    output.mkdir(parents=True, exist_ok=True)
    sample, fixture, dotnet = [str(Path(p).resolve()) for p in (args.sample, args.fixture, args.dotnet)]
    script = str(Path(__file__).resolve())
    suffix = str(os.getpid())
    front, back = "dhmp-a-" + suffix, "dhmp-b-" + suffix
    a, b = "fd42:253::1", "fd42:253::2"
    report = {"kind": "single-host-network-namespace-rehearsal", "physical_two_host_pass": False,
              "commit": os.environ.get("GITHUB_SHA", "local"), "kernel": run(["uname", "-a"]).strip(),
              "mtu": 1280, "checks": [], "sessions": [], "passed": False}
    processes, log_files = [], []
    capture = None
    private = tempfile.TemporaryDirectory(prefix="dhmp-api-config-")

    def ns(namespace, *command):
        return ["ip", "netns", "exec", namespace, *command]

    def request(path, method="GET", body=None, headers=None, namespace=front):
        data = {"path": path, "method": method, "headers": headers or {},
                "body": base64.b64encode(body).decode() if body else None}
        result = json.loads(run(ns(namespace, sys.executable, script, "--probe"), input=json.dumps(data), timeout=15))
        result["bytes"] = base64.b64decode(result["body"])
        return result

    def check(name, condition, detail=None):
        if not condition:
            raise AssertionError(name + (": " + str(detail) if detail else ""))
        report["checks"].append(name)
        print("PASS " + name, flush=True)

    def wait_until(action, timeout=20):
        deadline = time.monotonic() + timeout
        last = None
        while time.monotonic() < deadline:
            try:
                if action():
                    return
            except (Exception,) as error:
                last = error
            time.sleep(0.1)
        raise TimeoutError("Lab readiness deadline expired: " + str(last))

    def start(namespace, role, iteration):
        log = (output / (role + "-" + str(iteration) + ".log")).open("w")
        log_files.append(log)
        environment = dict(os.environ, DHMP_TEST_CONFIG=str(Path(private.name) / (role + ".json")),
                           ASPNETCORE_ENVIRONMENT="Production", DOTNET_NOLOGO="true")
        process = subprocess.Popen(ns(namespace, dotnet, sample), env=environment, stdout=log,
                                   stderr=subprocess.STDOUT, start_new_session=True)
        processes.append(process)
        return process

    def stop(process):
        if process.poll() is None:
            os.killpg(process.pid, signal.SIGINT)
            try:
                code = process.wait(timeout=15)
            except subprocess.TimeoutExpired:
                os.killpg(process.pid, signal.SIGKILL)
                process.wait(timeout=5)
                raise AssertionError("Application failed to join graceful shutdown within 15 seconds.")
        else:
            code = process.returncode
        check("clean shutdown pid=" + str(process.pid), code == 0, code)

    def start_pair(iteration):
        backend = start(back, "backend", iteration)
        # Wait for the actual configured raw control listener before sending a one-shot OFFER.
        def listener_bound():
            raw = run(ns(back, "cat", "/proc/net/raw6"))
            return any(line.split()[1].upper().endswith(":00FE") for line in raw.splitlines()[1:] if len(line.split()) > 3)
        wait_until(listener_bound)
        frontend = start(front, "frontend", iteration)
        states = {}
        for namespace in (back, front):
            def ready(namespace=namespace):
                result = request("/dhmp/status", namespace=namespace)
                state = json.loads(result["bytes"])
                states[namespace] = state
                return result["status"] == 200 and state["apiReady"]
            wait_until(ready)
        ids = [states[n]["apiSessionId"] for n in (front, back)]
        check("matching fresh PSK session " + str(iteration), ids[0] is not None and ids[0] == ids[1])
        report["sessions"].append(ids[0])
        return frontend, backend

    try:
        run([dotnet, fixture, private.name, a, b])
        run(["ip", "netns", "add", front])
        run(["ip", "netns", "add", back])
        run(["ip", "link", "add", "dapi-a", "type", "veth", "peer", "name", "dapi-b"])
        run(["ip", "link", "set", "dapi-a", "netns", front])
        run(["ip", "link", "set", "dapi-b", "netns", back])
        for namespace, interface, address in ((front, "dapi-a", a), (back, "dapi-b", b)):
            run(ns(namespace, "ip", "link", "set", "lo", "up"))
            run(ns(namespace, "ip", "link", "set", interface, "mtu", "1280", "up"))
            run(ns(namespace, "ip", "-6", "addr", "add", address + "/64", "dev", interface, "nodad"))
        capture_log = (output / "capture.log").open("w")
        log_files.append(capture_log)
        capture = subprocess.Popen(ns(front, "tcpdump", "-U", "-n", "-i", "dapi-a", "-w", str(output / "traffic.pcap"), "ip6"),
                                   stdout=capture_log, stderr=subprocess.STDOUT, start_new_session=True)
        wait_until(lambda: (output / "traffic.pcap").exists())
        frontend, backend = start_pair(1)
        hello = request("/lab/proxy/api/hello")
        check("GET through real DHMP API path", hello["status"] == 200 and json.loads(hello["bytes"])["process"] == backend.pid)
        payload = json.dumps({"message": "x" * 16000}).encode()
        echo = request("/lab/proxy/api/echo", "POST", payload, {"Content-Type": "application/json"})
        check("multi-record JSON POST and response", echo["status"] == 200 and json.loads(echo["bytes"])["message"] == "x" * 16000)
        check("authorization denied", request("/lab/proxy/api/lab/private")["status"] == 401)
        check("authorization header preserved", request("/lab/proxy/api/lab/private", headers={"Authorization": "Bearer lab-test"})["status"] == 200)
        check("unknown route remains 404", request("/lab/proxy/api/missing")["status"] == 404)
        oversized = request("/lab/proxy/api/echo", "POST", b"x" * 32769, {"Content-Type": "application/json"})
        check("oversized body rejected", oversized["status"] == 413)
        run(ns(back, "tc", "qdisc", "add", "dev", "dapi-b", "root", "netem", "loss", "100%"))
        try:
            lost = request("/lab/proxy/api/lab/mutate", "POST")
            check("lost response surfaces bounded timeout", lost["status"] == 504)
        finally:
            run(ns(back, "tc", "qdisc", "del", "dev", "dapi-b", "root"))
        metrics = request("/lab/proxy/api/lab/metrics")
        check("timed-out mutation was executed once, without retry", metrics["status"] == 200 and json.loads(metrics["bytes"])["mutations"] == 1)
        stop(backend)
        check("stopped backend does not trigger fallback", request("/lab/proxy/api/hello")["status"] == 504)
        stop(frontend)
        frontend, backend = start_pair(2)
        check("restart establishes a different session", report["sessions"][0] != report["sessions"][1])
        check("API works after fresh restart", request("/lab/proxy/api/hello")["status"] == 200)
        stop(frontend)
        stop(backend)
        os.killpg(capture.pid, signal.SIGINT)
        capture.wait(timeout=10)
        counts = {}
        for name, expression in (("data_253", "ip6 proto 253"), ("control_254", "ip6 proto 254"), ("tcp_udp", "ip6 and (tcp or udp)")):
            counts[name] = len(run(["tcpdump", "-nn", "-r", str(output / "traffic.pcap"), expression]).splitlines())
        report["packet_counts"] = counts
        check("packet trace contains DHMP data and setup", counts["data_253"] > 0 and counts["control_254"] >= 8)
        check("inter-namespace traffic has no TCP/UDP fallback", counts["tcp_udp"] == 0)
        report["passed"] = True
    except Exception as error:
        report["error"] = type(error).__name__ + ": " + str(error)
        print("FAIL " + report["error"], file=sys.stderr, flush=True)
    finally:
        for process in reversed(processes):
            if process.poll() is None:
                os.killpg(process.pid, signal.SIGTERM)
                try:
                    process.wait(timeout=10)
                except subprocess.TimeoutExpired:
                    os.killpg(process.pid, signal.SIGKILL)
                    process.wait(timeout=5)
        if capture and capture.poll() is None:
            os.killpg(capture.pid, signal.SIGINT)
            capture.wait(timeout=10)
        for namespace in (front, back):
            subprocess.run(["ip", "netns", "del", namespace], capture_output=True)
        for log in log_files:
            log.close()
        private.cleanup()
        (output / "report.json").write_text(json.dumps(report, indent=2))
        # Evidence contains only logs, encrypted capture and non-secret report, never fixture configs.
        for file in output.iterdir():
            file.chmod(0o644)
    print(json.dumps(report, indent=2), flush=True)
    return 0 if report["passed"] else 1


if __name__ == "__main__":
    if sys.argv[1:] == ["--probe"]:
        probe()
    else:
        sys.exit(main())
