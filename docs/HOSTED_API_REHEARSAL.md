# GitHub-hosted raw IPv6 API rehearsal

This automated test uses two Linux network namespaces connected by veth interfaces inside one GitHub-hosted VM. It uses actual raw IPv6 sockets, a 1280-byte virtual link and the existing ASP.NET API integration. It is not a physical two-machine/NIC test or a throughput benchmark.

The `Raw IPv6 API hosted rehearsal` workflow runs on relevant pull requests/main pushes and supports manual dispatch. A missing raw-socket, namespace or capture capability fails the run; it is never reported as a skipped pass.

The lab creates temporary test-only license issuers and a shared random PSK locally. Issuer private keys are not exported. Configurations containing fixture credentials have restricted permissions and are deleted during cleanup, and are excluded from uploaded evidence. This fixture does not add production issuer trust or disable license validation.

Acceptance checks cover:

- Matching PSK session IDs at both endpoints; backend listener is bound before the one-shot initiator starts.
- An ordinary factory GET reaching the remote backend process through DHMP.
- A JSON request/response larger than one complete application record.
- Authorization rejection/success, unknown-route 404 and configured body limits.
- Deliberate loss of every backend response: the client times out and the mutating action executes once without automatic retry.
- Stopped-backend failure without fallback; both applications stop within the deadline.
- Restart with fresh, matching session IDs and another successful API call.
- Packet capture containing Next Header 253 data and 254 setup, with no TCP/UDP traffic between the virtual endpoints.

The HTTP driver talks only to each namespace's local website listener. Those calls represent the browser-to-website/admin side of the test; they are not an inter-peer DHMP transport. The mapped API origin is deliberately unresolvable, and the backend website listener binds only to namespace loopback. Successful inter-peer API calls must therefore use the raw DHMP path.

Evidence is uploaded for 14 days: `report.json`, application/capture logs and encrypted `traffic.pcap`. The report always sets `physical_two_host_pass` to false and records the exact commit, kernel, checks, session IDs and packet counts. No fixture configuration files are uploaded.

For a physical gate, repeat the [API test](ASP_NET_API_INTEGRATION.md) on two distinct Linux machines connected through their actual IPv6 network/NICs, with recorded hardware, MTU, privileges and results. A hosted rehearsal pass does not close that release gate.

The first real-socket attempt exposed that casting experimental numbers to the managed `ProtocolType` enum did not open them on .NET/Linux. The backend now opens the explicit native Linux raw descriptor with close-on-exec and transfers ownership through `SafeSocketHandle`; async I/O, binding and cleanup continue through the managed Socket API. The lab exercises this actual construction path rather than substituting a mock or silently skipping protocol creation failures.
