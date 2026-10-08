#!/bin/sh
set -eu
umask 0077
ready_path="${SECONDBRAIN_EGRESS_READY_PATH:-/run/egress-ready}"
rm -f "$ready_path"

# Fail closed before any dependent process enters this network namespace.
iptables -P OUTPUT DROP
iptables -P INPUT DROP
iptables -P FORWARD DROP
ip6tables -P OUTPUT DROP
ip6tables -P INPUT DROP
ip6tables -P FORWARD DROP
iptables -F OUTPUT
iptables -F INPUT
ip6tables -F OUTPUT
ip6tables -F INPUT
iptables -A OUTPUT -o lo -j ACCEPT
iptables -A INPUT -i lo -j ACCEPT
ip6tables -A OUTPUT -o lo -j ACCEPT
ip6tables -A INPUT -i lo -j ACCEPT
iptables -A OUTPUT -m conntrack --ctstate ESTABLISHED,RELATED -j ACCEPT
iptables -A INPUT -m conntrack --ctstate ESTABLISHED,RELATED -j ACCEPT
ip6tables -A OUTPUT -m conntrack --ctstate ESTABLISHED,RELATED -j ACCEPT
ip6tables -A INPUT -m conntrack --ctstate ESTABLISHED,RELATED -j ACCEPT
iptables -A INPUT -p tcp --dport 8080 -j ACCEPT

check_ipv4() {
    printf '%s\n' "$1" | awk -F. '
      NF != 4 { exit 1 }
      { for (i=1;i<=4;i++) if ($i !~ /^[0-9]+$/ || $i>255 || length($i)>3) exit 1 }'
}
check_private_bind() {
    printf '%s\n' "$1" | awk -F. '
      $1==10 || $1==127 || ($1==172 && $2>=16 && $2<=31) ||
      ($1==192 && $2==168) || ($1==100 && $2>=64 && $2<=127) { ok=1 }
      END { exit !ok }'
}
check_port() {
    case "$1" in ''|*[!0-9]*) return 1;; esac
    [ "$1" -gt 0 ] && [ "$1" -le 65535 ]
}
allow_tcp() {
    uid="$1"; category="$2"; entries="$3"
    for entry in $entries; do
        address="${entry%:*}"; port="${entry##*:}"
        check_ipv4 "$address" && check_port "$port" || {
            printf 'Invalid %s allowance: expected numeric IPv4:port\n' "$category" >&2; exit 1;
        }
        if [ "$category" = access ] && [ "$port" != 443 ]; then
            printf 'Access control-plane allowance must use HTTPS port 443\n' >&2; exit 1
        fi
        iptables -A OUTPUT -m owner --uid-owner "$uid" -p tcp -d "$address" --dport "$port" -j ACCEPT
    done
}
check_ipv4 "${SECONDBRAIN_PRIVATE_ADDRESS:-127.0.0.1}" &&
    check_private_bind "${SECONDBRAIN_PRIVATE_ADDRESS:-127.0.0.1}" || {
        printf 'Host binding must be a private or loopback IPv4 address\n' >&2; exit 1;
    }
case "${SECONDBRAIN_LOCAL_ONLY:-true}" in true|false) ;; *) printf 'local_only must be true or false\n' >&2; exit 1;; esac
check_ipv4 "${SECONDBRAIN_DNS_IP:?explicit resolver required}" || { printf 'Resolver must be numeric IPv4\n' >&2; exit 1; }
# The daemon and tunnel may reach only this resolver over DNS. The namespace
# has no Docker embedded resolver in resolv.conf, avoiding a forwarding bypass.
for uid in 1654 65532; do
    for protocol in tcp udp; do
        iptables -A OUTPUT -m owner --uid-owner "$uid" -p "$protocol" -d "$SECONDBRAIN_DNS_IP" --dport 53 -j ACCEPT
    done
done
# Provider data and Access key refresh are distinct rules for the daemon UID.
allow_tcp 1654 provider "${SECONDBRAIN_PROVIDER_ALLOWLIST:-}"
allow_tcp 1654 access "${SECONDBRAIN_ACCESS_ALLOWLIST:-}"
if [ "${SECONDBRAIN_LOCAL_ONLY:-true}" = false ]; then
    allow_tcp 1654 hosted "${SECONDBRAIN_HOSTED_ALLOWLIST:-}"
fi
# Cloudflare Tunnel endpoint allowance belongs ONLY to the cloudflared UID.
# http2 + edge-ip-version=4 removes the need for UDP or broad IPv6 allowances.
allow_tcp 65532 tunnel '198.41.192.167:7844 198.41.192.67:7844 198.41.192.57:7844 198.41.192.107:7844 198.41.192.27:7844 198.41.192.7:7844 198.41.192.227:7844 198.41.192.47:7844 198.41.192.37:7844 198.41.192.77:7844 198.41.200.13:7844 198.41.200.193:7844 198.41.200.33:7844 198.41.200.233:7844 198.41.200.53:7844 198.41.200.63:7844 198.41.200.113:7844 198.41.200.73:7844 198.41.200.43:7844 198.41.200.23:7844'
touch "$ready_path"
exec sleep infinity
