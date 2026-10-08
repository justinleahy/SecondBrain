#!/usr/bin/env bash
# G1: provision as root, then brain serve as the daemon UID.
set -euo pipefail
export SECONDBRAIN_PRIVATE_ADDRESS="${SECONDBRAIN_PRIVATE_ADDRESS:-127.0.0.1}"
export SECONDBRAIN_HOST_ROOT="${SECONDBRAIN_HOST_ROOT:-$(mktemp -d /tmp/secondbrain-compose.XXXXXX)}"
export SECONDBRAIN_DNS_IP="${SECONDBRAIN_DNS_IP:-10.8.0.1}"
export SECONDBRAIN_PROVIDER_ALLOWLIST="${SECONDBRAIN_PROVIDER_ALLOWLIST:-}"
export SECONDBRAIN_ACCESS_ALLOWLIST="${SECONDBRAIN_ACCESS_ALLOWLIST:-}"
smoke_project="${SECONDBRAIN_SMOKE_PROJECT:-secondbrain-smoke-$$}"
compose=(docker compose --project-name "$smoke_project" -f deploy/compose.yaml)
cleanup() {
    result=$?
    if [ "$result" -ne 0 ]; then "${compose[@]}" logs --no-color; fi
    "${compose[@]}" down --remove-orphans --volumes || true
    exit "$result"
}
trap cleanup EXIT
"${compose[@]}" config --quiet
"${compose[@]}" build daemon extractor egress
mkdir -p "$SECONDBRAIN_HOST_ROOT"/{data,incoming,config,run,project}
printf 'nameserver %s\noptions timeout:1 attempts:1\n' "$SECONDBRAIN_DNS_IP" > "$SECONDBRAIN_HOST_ROOT/resolv.conf"
"${compose[@]}" --profile provision run --rm provision
"${compose[@]}" up --detach --wait --wait-timeout 60 daemon extractor
uid=$("${compose[@]}" exec -T daemon id -u)
[ "$uid" = 1654 ] || { printf 'Expected daemon UID1654, got %s\n' "$uid" >&2; exit 1; }
extractor_uid=$("${compose[@]}" exec -T extractor id -u)
[ "$extractor_uid" = 1655 ] || { printf 'Expected extractor UID1655, got %s\n' "$extractor_uid" >&2; exit 1; }
http_status=$(curl --fail --silent --show-error --retry 30 --retry-all-errors --retry-delay 1 --max-time 2 \
    --output /dev/null --write-out '%{http_code}' "http://${SECONDBRAIN_PRIVATE_ADDRESS}:${SECONDBRAIN_PORT:-7171}/health")
[ "$http_status" = 200 ] || { printf 'Expected /health HTTP200, got %s\n' "$http_status" >&2; exit 1; }
"${compose[@]}" exec -T daemon sh -c 'test "$(awk '\''/CapEff/ {print $2}'\'' /proc/self/status)" = 0000000000000000'
"${compose[@]}" exec -T daemon sh -c 'if touch /app/secondbrain-readonly-test 2>/dev/null; then exit 1; fi'
# The rule must block the public canary even if Cloudflare Tunnel is enabled.
"${compose[@]}" exec -T daemon bash -c 'if timeout 3 bash -c "exec 3<>/dev/tcp/1.1.1.1/443" 2>/dev/null; then echo "Egress canary unexpectedly connected" >&2; exit 1; fi'
printf '\nG1 passed: provision, UID1654 brain serve, /health 200, offline extractor, read-only rootfs, zero capabilities, blocked canary.\n'
