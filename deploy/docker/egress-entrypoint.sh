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
# Kernel neighbor discovery is required before an explicitly permitted IPv6
# TCP connection can reach its next hop. Hop limit 255 confines this exchange
# to the local link; echo, router advertisements and arbitrary ICMP stay denied.
for direction in INPUT OUTPUT; do
    for neighbor_type in 135 136; do
        ip6tables -A "$direction" -p ipv6-icmp --icmpv6-type "$neighbor_type" -m hl --hl-eq 255 -j ACCEPT
    done
done
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
# Normalize only public unicast literals. Keys are also used to compare canary
# addresses regardless of IPv6 zero compression or hexadecimal capitalization.
calendar_address_key() {
    printf '%s\n' "$1" | awk '
      function hex(s, i,n,c) { n=0; for(i=1;i<=length(s);i++) { c=index("0123456789abcdef",substr(s,i,1))-1; if(c<0)exit 1; n=n*16+c } return n }
      {
        s=tolower($0)
        if (index(s,":")==0) {
          if(split(s,v,".")!=4)exit 1
          for(i=1;i<=4;i++)if(v[i]!~/^[0-9]+$/ || length(v[i])>3 || v[i]>255 || (length(v[i])>1 && substr(v[i],1,1)=="0"))exit 1
          a=v[1]+0;b=v[2]+0;c=v[3]+0
          if(a==0 || a==10 || a==127 || a>=224 || (a==100 && b>=64 && b<=127) ||
             (a==169 && b==254) || (a==172 && b>=16 && b<=31) ||
             (a==192 && (b==168 || (b==0 && (c==0 || c==2)) || (b==88 && c==99))) ||
             (a==198 && (b==18 || b==19 || (b==51 && c==100))) || (a==203 && b==0 && c==113))exit 1
          printf "v4:%d.%d.%d.%d\n",a,b,c,v[4];exit
        }
        if(s~/[^0-9a-f:]/)exit 1
        halves=split(s,h,"::");if(halves>2)exit 1
        left=h[1]=="" ? 0 : split(h[1],l,":");right=halves==1 || h[2]=="" ? 0 : split(h[2],r,":")
        if((halves==1 && left!=8) || (halves==2 && left+right>=8))exit 1
        for(i=1;i<=8;i++)v[i]=0
        for(i=1;i<=left;i++){if(length(l[i])<1 || length(l[i])>4)vfail=1;v[i]=hex(l[i])}
        for(i=1;i<=right;i++){if(length(r[i])<1 || length(r[i])>4)vfail=1;v[8-right+i]=hex(r[i])}
        if(vfail || v[1]<8192 || v[1]>=16384 || (v[1]==8193 && (v[2]<512 || v[2]==3512)) ||
           v[1]==8194 || (v[1]==16383 && v[2]<4096))exit 1
        printf "v6";for(i=1;i<=8;i++)printf ":%d",v[i];printf "\n"
      }'
}
allow_calendars() {
    count=0
    for entry in ${SECONDBRAIN_CALENDAR_ALLOWLIST:-}; do
        count=$((count+1))
        [ "$count" -le 1024 ] || { printf 'Too many calendar pins\n' >&2; exit 1; }
        case "$entry" in
            \[*\]:*) address="${entry#\[}"; address="${address%\]:*}"; port="${entry##*:}"; command=ip6tables;;
            *) address="${entry%:*}"; port="${entry##*:}"; command=iptables;;
        esac
        check_port "$port" && key="$(calendar_address_key "$address")" || {
            printf 'Calendar allowances require public numeric IPv4:port or [IPv6]:port pins\n' >&2; exit 1;
        }
        case "$command:$key" in iptables:v4:*|ip6tables:v6:*) ;; *) printf 'Calendar address family mismatch\n' >&2; exit 1;; esac
        for canary in ${SECONDBRAIN_CALENDAR_CANARY_ADDRESSES:-1.1.1.1}; do
            denied="$(calendar_address_key "$canary")" || { printf 'Calendar canary pins must be public numeric addresses\n' >&2; exit 1; }
            [ "$key" != "$denied" ] || { printf 'Calendar allowance overlaps the egress canary\n' >&2; exit 1; }
        done
        "$command" -A OUTPUT -m owner --uid-owner 1654 -p tcp -d "$address" --dport "$port" -j ACCEPT
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
allow_calendars
if [ "${SECONDBRAIN_LOCAL_ONLY:-true}" = false ]; then
    allow_tcp 1654 hosted "${SECONDBRAIN_HOSTED_ALLOWLIST:-}"
fi
# Cloudflare Tunnel endpoint allowance belongs ONLY to the cloudflared UID.
# http2 + edge-ip-version=4 removes the need for UDP or broad IPv6 allowances.
allow_tcp 65532 tunnel '198.41.192.167:7844 198.41.192.67:7844 198.41.192.57:7844 198.41.192.107:7844 198.41.192.27:7844 198.41.192.7:7844 198.41.192.227:7844 198.41.192.47:7844 198.41.192.37:7844 198.41.192.77:7844 198.41.200.13:7844 198.41.200.193:7844 198.41.200.33:7844 198.41.200.233:7844 198.41.200.53:7844 198.41.200.63:7844 198.41.200.113:7844 198.41.200.73:7844 198.41.200.43:7844 198.41.200.23:7844'
touch "$ready_path"
exec sleep infinity
