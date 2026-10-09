#!/usr/bin/env bash
set -euo pipefail
: "${DHMP_LOCAL_IPV6:?Set DHMP_LOCAL_IPV6 to the explicit server IPv6 address}"
DHMP_SERVER_BINARY="${DHMP_SERVER_BINARY:-./DHMP-Arena.x86_64}"
exec "$DHMP_SERVER_BINARY" -batchmode -nographics --dhmp-server -logFile -
