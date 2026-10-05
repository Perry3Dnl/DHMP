#!/usr/bin/env bash
set -euo pipefail

if command -v ip >/dev/null 2>&1; then
  ip link del dhmpxdp0 2>/dev/null || true

  if ip link add dhmpxdp0 type veth peer name dhmpxdp1 2>/tmp/dhmp-veth-error; then
    ip link set dhmpxdp0 up
    ip link set dhmpxdp1 up
    echo "DHMP_AF_XDP_VETH=ready"
  else
    echo "DHMP_AF_XDP_VETH=unavailable"
    cat /tmp/dhmp-veth-error >&2 || true
  fi
fi

exec dotnet DHMP.FramebufferDemo.dll
