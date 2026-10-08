#!/usr/bin/env bash
# G1: provision as root, then brain serve as the daemon UID.
set -euo pipefail
umask 077
export SECONDBRAIN_PRIVATE_ADDRESS="${SECONDBRAIN_PRIVATE_ADDRESS:-127.0.0.1}"
export SECONDBRAIN_HOST_ROOT="${SECONDBRAIN_HOST_ROOT:-$(mktemp -d /tmp/secondbrain-compose.XXXXXX)}"
export SECONDBRAIN_DNS_IP="${SECONDBRAIN_DNS_IP:-10.8.0.1}"
export SECONDBRAIN_PROVIDER_ALLOWLIST="${SECONDBRAIN_PROVIDER_ALLOWLIST:-}"
export SECONDBRAIN_ACCESS_ALLOWLIST="${SECONDBRAIN_ACCESS_ALLOWLIST:-}"
smoke_project="${SECONDBRAIN_SMOKE_PROJECT:-secondbrain-smoke-$$}"
smoke_client_root=$(mktemp -d /tmp/secondbrain-compose-client.XXXXXX)
compose=(docker compose --project-name "$smoke_project" -f deploy/compose.yaml -f deploy/tests/compose-smoke.yaml)
cleanup() {
    result=$?
    if [ "$result" -eq 0 ] && [ "${SECONDBRAIN_SMOKE_KEEP:-0}" = 1 ]; then
        printf '\nDisposable smoke environment retained: project=%s host_root=%s client_root=%s origin=http://%s:%s\n' \
            "$smoke_project" "$SECONDBRAIN_HOST_ROOT" "$smoke_client_root" "$SECONDBRAIN_PRIVATE_ADDRESS" "${SECONDBRAIN_PORT:-7171}"
        exit 0
    fi
    if [ "$result" -ne 0 ]; then "${compose[@]}" logs --no-color; fi
    "${compose[@]}" down --remove-orphans --volumes || true
    rm -rf "$smoke_client_root"
    exit "$result"
}
trap cleanup EXIT
"${compose[@]}" config --quiet
"${compose[@]}" build daemon extractor egress mock-provider
mkdir -p "$SECONDBRAIN_HOST_ROOT"/{data,incoming,config,project}
printf 'nameserver %s\noptions timeout:1 attempts:1\n' "$SECONDBRAIN_DNS_IP" > "$SECONDBRAIN_HOST_ROOT/resolv.conf"
"${compose[@]}" --profile provision run --rm provision
# Replace the intentionally unconfigured bootstrap template with the CI fixture.
# Write through the root-only provisioning container, preserving private ownership.
"${compose[@]}" --profile provision run --rm --no-deps -T --entrypoint sh provision -c \
    'cat > /etc/secondbrain/config.yaml && chown 1654:1654 /etc/secondbrain/config.yaml && chmod 0600 /etc/secondbrain/config.yaml' \
    < deploy/tests/config.compose-smoke.yaml
# The bootstrap password and initial admin key are confined to this disposable run.
# init validates and creates both real stores; discard its one-time secret output.
"${compose[@]}" up --detach --wait --wait-timeout 30 egress
"${compose[@]}" run --rm --no-deps -T -e SECONDBRAIN_BOOTSTRAP_PASSWORD=compose-smoke-password \
    daemon dotnet /app/cli/brain.dll init --json > /dev/null
"${compose[@]}" up --detach --wait --wait-timeout 90 daemon extractor mock-provider
uid=$("${compose[@]}" exec -T daemon id -u)
[ "$uid" = 1654 ] || { printf 'Expected daemon UID1654, got %s\n' "$uid" >&2; exit 1; }
extractor_uid=$("${compose[@]}" exec -T extractor id -u)
[ "$extractor_uid" = 1655 ] || { printf 'Expected extractor UID1655, got %s\n' "$extractor_uid" >&2; exit 1; }
http_status=$(curl --fail --silent --show-error --retry 30 --retry-all-errors --retry-delay 1 --max-time 2 \
    --output /dev/null --write-out '%{http_code}' "http://${SECONDBRAIN_PRIVATE_ADDRESS}:${SECONDBRAIN_PORT:-7171}/health")
[ "$http_status" = 200 ] || { printf 'Expected /health HTTP200, got %s\n' "$http_status" >&2; exit 1; }
curl --fail --silent --show-error --retry 30 --retry-all-errors --retry-delay 1 --max-time 10 \
    "http://${SECONDBRAIN_PRIVATE_ADDRESS}:${SECONDBRAIN_PORT:-7171}/ready"

smoke_brain() {
    "${compose[@]}" exec -T \
        -e SECONDBRAIN_CREDENTIAL_DIRECTORY=/tmp/credentials \
        -e SECONDBRAIN_CREDENTIAL_STORE=file \
        -e SECONDBRAIN_PASSWORD=compose-smoke-password \
        daemon dotnet /app/cli/brain.dll --url http://127.0.0.1:7171 \
        --credential-name compose-smoke "$@" --json
}
smoke_brain login http://127.0.0.1:7171 --name compose-smoke --scopes admin
# Consume the creation response without logging its one-time API key.
created_key_id=$(smoke_brain keys create --scopes read | python3 -c 'import json,sys; print(json.load(sys.stdin)["data"]["id"])')
smoke_brain keys list
smoke_brain keys revoke "$created_key_id"
smoke_brain providers list
smoke_brain providers test mock

# Exercise the mounted Razor form and signed-in shell using private cookie files.
smoke_origin="http://${SECONDBRAIN_PRIVATE_ADDRESS}:${SECONDBRAIN_PORT:-7171}"
curl --fail --silent --show-error --cookie-jar "$smoke_client_root/cookies" \
    "$smoke_origin/login" > "$smoke_client_root/login.html"
curl --fail --silent --show-error --output /dev/null "$smoke_origin/_framework/blazor.web.js"
antiforgery_token=$(python3 - "$smoke_client_root/login.html" <<'PY'
import html, re, sys
page = open(sys.argv[1]).read()
match = re.search(r'name="__RequestVerificationToken" value="([^"]+)"', page)
assert match is not None, 'The Razor login form did not include an antiforgery token.'
print(html.unescape(match.group(1)))
PY
)
login_status=$(curl --silent --show-error --cookie "$smoke_client_root/cookies" \
    --cookie-jar "$smoke_client_root/cookies" -H "Origin: $smoke_origin" \
    --data-urlencode Password=compose-smoke-password \
    --data-urlencode "__RequestVerificationToken=$antiforgery_token" \
    --output /dev/null --write-out '%{http_code}' "$smoke_origin/auth/login")
[ "$login_status" = 303 ] || { printf 'Expected form login HTTP303, got %s\n' "$login_status" >&2; exit 1; }
curl --fail --silent --show-error --cookie "$smoke_client_root/cookies" \
    "$smoke_origin/" > "$smoke_client_root/signed-in.html"
python3 - "$smoke_client_root/signed-in.html" <<'PY'
import sys
assert 'You are signed in.' in open(sys.argv[1]).read(), 'The signed-in Razor shell was not rendered.'
PY
browser_session_id=$(curl --fail --silent --show-error --cookie "$smoke_client_root/cookies" \
    "$smoke_origin/auth/me" | python3 -c 'import json,sys; print(json.load(sys.stdin)["id"])')
smoke_brain sessions list | python3 -c 'import json,sys; assert any(s["id"] == sys.argv[1] for s in json.load(sys.stdin)["data"])' "$browser_session_id"
smoke_brain sessions revoke "$browser_session_id"
session_status=$(curl --silent --show-error --cookie "$smoke_client_root/cookies" \
    --output /dev/null --write-out '%{http_code}' "$smoke_origin/auth/me")
[ "$session_status" = 401 ] || { printf 'Expected revoked browser session HTTP401, got %s\n' "$session_status" >&2; exit 1; }
curl --fail --silent --show-error --cookie-jar "$smoke_client_root/cookies" \
    -H "Origin: $smoke_origin" -H 'Content-Type: application/json' \
    --data '{"password":"compose-smoke-password"}' "$smoke_origin/auth/login" > /dev/null
smoke_brain sessions revoke-all
session_status=$(curl --silent --show-error --cookie "$smoke_client_root/cookies" \
    --output /dev/null --write-out '%{http_code}' "$smoke_origin/auth/me")
[ "$session_status" = 401 ] || { printf 'Expected epoch-rejected browser session HTTP401, got %s\n' "$session_status" >&2; exit 1; }
if smoke_brain keys list > /dev/null; then
    printf 'Expected the stored API key to be rejected after logout-all.\n' >&2
    exit 1
else
    [ "$?" = 4 ] || { printf 'Expected revoked API-key precondition exit4.\n' >&2; exit 1; }
fi
# Acquire authority in the new epoch so doctor can inspect authenticated diagnostics.
smoke_brain login http://127.0.0.1:7171 --name compose-smoke --scopes admin
smoke_brain doctor --extractor-socket /run/secondbrain/extractor.sock

"${compose[@]}" exec -T daemon sh -c 'test "$(awk '\''/CapEff/ {print $2}'\'' /proc/self/status)" = 0000000000000000'
"${compose[@]}" exec -T daemon sh -c 'if touch /app/secondbrain-readonly-test 2>/dev/null; then exit 1; fi'
# The rule must block the public canary even if Cloudflare Tunnel is enabled.
"${compose[@]}" exec -T daemon bash -c 'if timeout 3 bash -c "exec 3<>/dev/tcp/1.1.1.1/443" 2>/dev/null; then echo "Egress canary unexpectedly connected" >&2; exit 1; fi'
printf '\nG1 passed: provision, init, UID1654 serve, health/ready 200, CLI login/keys/providers/sessions/doctor, Razor login and signed-in shell, offline extractor, read-only rootfs, zero capabilities, blocked canary.\n'
