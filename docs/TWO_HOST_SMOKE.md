# First two-host functional smoke run

Prepared 2026-09-30; not yet executed on two physical hosts. This is a low-rate functional test of the real Linux IPv6 backend, not a speed benchmark or release qualification.

## Environment and acceptance

Use two Linux machines on a controlled IPv6 LAN, .NET 10, native routable IPv6 addresses and the same git commit. Allow experimental IPv6 Next Header 253 (data) and 254 (control) between them. Raw sockets need root or appropriate `CAP_NET_RAW`. Do not infer a supported Windows raw backend from Windows unit tests.

Record the exact commit, kernel, CPU/NIC, IPv6 addresses/interface, configured MTU, permissions and both console outputs. The runner uses a conservative known 1280-byte PMTU budget, 32-byte application records and serialized SmoothPacing at 100 records/second. The PSK run adds the existing 24-byte security envelope. It does not discover PMTU or measure throughput.

For the initial controlled-LAN smoke run require: setup succeeds, 6000 records submitted, 6000 unique records received, zero malformed records and zero invalid/protection-rejected packets, and clean process termination. Record duplicate/reordered records separately; the transport permits them. A run cancelled, timed out, or failing these checks is not a pass. Investigate failures before increasing load.

The fixed, bounded application tracking array distinguishes unique records from duplicates and validates their content. Sequence bytes are owned by this sample schema, not a new DHMP packet header. Protected counters/session IDs use existing profile bytes.

## Build on both hosts

```bash
dotnet build benchmarks/raw-ipv6-smoke/Dhmp.RawIpv6Smoke.csproj -c Release
```

The executable is `benchmarks/raw-ipv6-smoke/bin/Release/net10.0/Dhmp.RawIpv6Smoke.dll`. Build coverage is included in the existing CI experiment-build job. Do not pipe or redirect stdin: the runner uses Enter for the manual ready/finished handoff; stdout can be captured with `tee`.

## Plain run

Replace the documentation IPv6 addresses below with actual host addresses. First start the receiver on host B; then start the sender on host A within the 120-second setup deadline:

```bash
# Host B
sudo dotnet benchmarks/raw-ipv6-smoke/bin/Release/net10.0/Dhmp.RawIpv6Smoke.dll receive 2001:db8::b 2001:db8::a plain 6000
# Host A (separate machine)
sudo dotnet benchmarks/raw-ipv6-smoke/bin/Release/net10.0/Dhmp.RawIpv6Smoke.dll send 2001:db8::a 2001:db8::b plain 6000
```

Wait until B prints `READY`, then press Enter on A. After A prints its `SEND` summary, press Enter on B; B allows a two-second drain and prints `RECEIVE`. The receive run has an overall timeout of expected send duration plus 120 seconds. Both summaries and exit codes are required evidence. Sender completion alone is insufficient.

Plain mode runs Control V1 compatibility negotiation with a fixed sample schema. That is not authentication. The receiver validates the sample pattern but cannot distinguish delayed compatible packets from a previous plain run solely by source address. Keep runs isolated.

## Protected run and restart

Create a temporary test-only 32-byte random PSK, represented as 64 hexadecimal characters in a restricted file on both machines. Transfer it through your approved secure administration path. Do not commit or print key contents. For example, generate the file locally with `umask 077; openssl rand -hex 32 > smoke.psk`. Use a dedicated test key rather than a production key.

Run the same commands with `psk` and the file path:

```bash
# Host B
sudo dotnet benchmarks/raw-ipv6-smoke/bin/Release/net10.0/Dhmp.RawIpv6Smoke.dll receive 2001:db8::b 2001:db8::a psk 6000 /absolute/path/smoke.psk
# Host A
sudo dotnet benchmarks/raw-ipv6-smoke/bin/Release/net10.0/Dhmp.RawIpv6Smoke.dll send 2001:db8::a 2001:db8::b psk 6000 /absolute/path/smoke.psk
```

Use the same manual ready/finished handoff. This mode runs PSK setup V2; its record schema is fixed out of band by this runner. Check that both hosts log the same session ID. Repeat the protected run and require a different session ID. Finally test Ctrl+C during a run: retain partial counts, require bounded exit, and mark it as an intentional cancellation rather than a pass.

This first run does not validate every feature: Latest/dispatcher overload, authenticated feedback/path probes, multiple peers, stale-packet injection, PMTU failures, fairness and prolonged-run memory behavior need subsequent targeted tests. The experimental PSK profile still needs independent review.
