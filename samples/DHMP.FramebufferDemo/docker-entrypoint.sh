#!/usr/bin/env bash
set -euo pipefail

MAX_AFXDP_WORKERS="${DHMP_AFXDP_MAX_WORKERS:-16}"

if command -v ip >/dev/null 2>&1; then
  ready=0

  for worker in $(seq 0 $((MAX_AFXDP_WORKERS - 1))); do
    iface="dhmpxdp$worker"
    peer="dhmppeer$worker"
    subnet=$((worker + 1))

    ip link del "$iface" 2>/dev/null || true
    ip link del "$peer" 2>/dev/null || true

    if ip link add "$iface" type veth peer name "$peer" 2>/tmp/dhmp-veth-error; then
      ip link set "$iface" up
      ip link set "$peer" up
      ip -6 addr add "fd42:6468:6d70:${subnet}::1/64" dev "$iface" nodad 2>/dev/null || true
      ready=$((ready + 1))
    else
      echo "DHMP_AF_XDP_VETH_WORKER_$worker=unavailable"
      cat /tmp/dhmp-veth-error >&2 || true
      break
    fi
  done

  echo "DHMP_AF_XDP_VETH_WORKERS=$ready"
fi

exec dotnet DHMP.FramebufferDemo.dll
