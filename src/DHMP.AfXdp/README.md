# DHMP.AfXdp

Experimental Linux-only AF_XDP research package for DHMP.

This project is deliberately separate from `DHMP.RawIpv6`. It does not change the
DHMP V1 wire contract and is not selected automatically by Connector.

The managed assembly exposes:

- `DhmpAfXdpHostProbe.Probe(...)` — verifies whether the native AF_XDP TX path
  can initialize on an explicit Linux interface/queue.
- `DhmpAfXdpBenchmark.RunTransmit(...)` — runs a directional AF_XDP TX
  microbenchmark with a DHMP-sized payload and reports the actual mode used.

The native helper is built separately on Linux and requires libxdp/libbpf
development headers:

```sh
sudo apt-get install libxdp-dev libbpf-dev libelf-dev zlib1g-dev
./src/DHMP.AfXdp/native/build-linux.sh
export LD_LIBRARY_PATH="$PWD/src/DHMP.AfXdp/native:$LD_LIBRARY_PATH"
```

AF_XDP zero-copy support depends on the NIC/driver. If zero-copy cannot bind,
the experiment falls back to AF_XDP copy mode and reports that fact explicitly.
If AF_XDP is unavailable, the benchmark reports unsupported rather than
substituting a simulated result.

The first comparison is a TX-path microbenchmark, not an end-to-end delivery or
latency claim. Physical multi-host/NIC validation remains separate work.
