#!/usr/bin/env bash
set -euo pipefail
cd "$(dirname "$0")"

cc -O3 -fPIC -shared \
  -Wall -Wextra -Werror \
  -o libdhmp_afxdp.so \
  dhmp_afxdp.c \
  -lxdp -lbpf -lelf -lz

echo "Built $(pwd)/libdhmp_afxdp.so"
