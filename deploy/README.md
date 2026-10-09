# Deployment

`brain init --provision` is the root-only setup step. The daemon and extractor
serve as distinct unprivileged identities, with umask 0077 for daemon-created
files. Keep `/srv/secondbrain` and its key ring at 0700, owned by `secondbrain`.
The incoming tree is 0750, owned by `secondbrain-sync` with group `secondbrain`.
Config and external secret files are 0600 and mounted read-only to the daemon.
Directories need traversal bits: config/secrets are private traversable
directories, not 0600 directories. The shared runtime directory must be group
writable for the standalone extractor, with its socket at 0660.

The reference model provider is the separate [vllm](vllm/README.md) Compose
project: two vLLM servers on the GPU host, one per model, whose `IP:port` pairs
belong in `SECONDBRAIN_PROVIDER_ALLOWLIST` and `privacy.trusted_services`.

## Compose

Copy `provision/compose.env.example` into a private environment file and supply
it with `docker compose --env-file`. Set `SECONDBRAIN_PRIVATE_ADDRESS` to the
actual WireGuard or Tailscale interface address; it is required, with no wildcard
default. The helper refuses wildcard and publicly routable IPv4 host binds
before becoming ready. `SECONDBRAIN_HOST_ROOT` contains `data`, `incoming`, `config`,
`project`, and an explicit `resolv.conf`. Docker bind mounts reject missing
paths in serving services. The Compose default subnet is 172.30.0.0/24 and the
namespace address is 172.30.0.2; if these conflict with your host routes, change
both environment values and the bridge listener in `config.yaml` together.
The daemon and extractor share a Compose-managed Linux `runtime` volume for
their Unix socket. Provisioning creates its private group-writable directory;
host file shares on Docker Desktop cannot reliably support socket permissions.

From the repository root:

```sh
export SECONDBRAIN_PRIVATE_ADDRESS=100.64.0.2
export SECONDBRAIN_HOST_ROOT=/srv/secondbrain-compose
export SECONDBRAIN_DNS_IP=10.8.0.1
docker compose -f deploy/compose.yaml build daemon extractor egress
sudo mkdir -p "$SECONDBRAIN_HOST_ROOT"/{data,incoming,config,project}
printf 'nameserver %s\n' "$SECONDBRAIN_DNS_IP" | sudo tee "$SECONDBRAIN_HOST_ROOT/resolv.conf"
docker compose -f deploy/compose.yaml --profile provision run --rm provision
# Configure providers, all required model roles, and exact trusted origins first.
sudoedit "$SECONDBRAIN_HOST_ROOT/config/config.yaml"
docker compose -f deploy/compose.yaml up -d egress extractor
# Enter the account password and save the initial admin key shown once.
docker compose -f deploy/compose.yaml run --rm --no-deps daemon dotnet /app/cli/brain.dll init
docker compose -f deploy/compose.yaml up -d
```

The installed Compose project omits source build definitions and uses the
prebuilt local image tags. Build those images from the repository first (or
replace the image tags with your published immutable images), then run the
installed project with its environment file. Its `.env.example` and tunnel
config are starting templates, not production secrets.

Provisioning uses the image's numeric identities (`secondbrain` 1654,
`secondbrain-extract` 1655, `secondbrain-sync` 1656), without adding host users.
It installs the Compose bootstrap config with no model vendor or model selected.
The daemon refuses to start until the required chat, enrich and embed bindings
are configured. Set providers, model IDs, allowed hosts/origins, and
exact trusted service endpoints before starting the daemon. Place external
secrets in `config/secrets/<NAME>` and refer to them as `${NAME}` in YAML.
Create the daemon's stores, account and bootstrap credential through `brain init`
as the daemon user before `brain serve`. Supply the password interactively or
through `SECONDBRAIN_BOOTSTRAP_PASSWORD` for the one-shot initialization process.
Save the initial admin key once in a private credential store.

The `egress` helper owns an isolated Docker network namespace and has only
`NET_ADMIN`. It has no data, secrets, extractor socket, or Docker socket mount.
The daemon has no capabilities. Docker must support NET_ADMIN and the Linux
iptables owner/conntrack modules; a helper setup failure prevents daemon startup.
It waits for the helper to install IPv4 and
IPv6 default-deny OUTPUT/INPUT rules before starting. The namespace still has
a routed bridge, so exact trusted destinations can be reached; an
`internal: true` bridge alone cannot provide that allowance. Restart the stack
together after changing the helper's policy or image; never replace a running
namespace helper without stopping its dependants.

Rules distinguish process owners: UID1654 may reach the configured resolver
on TCP/UDP53, trusted provider IP:port pairs, and separately pinned Access JWKS
IP:443 pairs. Hosted providers are added only when `SECONDBRAIN_LOCAL_ONLY=false`.
Provider locality comes from the exact trusted origin in configuration; the
firewall enforces the operator-pinned address and port. UID65532 may reach
DNS and the published Cloudflare tunnel IPs on TCP7844; the daemon gets no tunnel
allowance. IPv6 external egress is denied. An explicit read-only `resolv.conf`
avoids Docker's embedded DNS forwarding. Allowlist entries accept numeric
IPv4:port pairs only, with no hostname or subnet wildcard. Pin every resolved
IP and port selected in config, and update those rules alongside config.

Access IP rules restrict destination/port, not HTTP paths. The daemon's
control-plane HTTP policy must limit the request to the configured team
certificate endpoint. Shared CDN addresses cannot establish host-level path
isolation. A failure to refresh keeps the last good JWKS in the authentication
layer. The egress canary proves a rule is in force for its public destination;
it does not prove every configured allowance is correct.

The extractor has `network_mode: none`, a private scratch tmpfs, CPU/memory/PID/
file-size limits, no data root or secret mount, and only the shared descriptor
socket directory. Docker's default seccomp profile remains enabled. Real parser
work and timeout orchestration arrive in M1.

Public access is opt-in with `--profile tunnel`. Copy `provision/cloudflared.yaml`
to `$SECONDBRAIN_HOST_ROOT/cloudflared.yaml`, set the tunnel UUID and hostname,
and make the credential JSON readable only by tunnel UID65532. Set
`SECONDBRAIN_TUNNEL_CREDENTIALS` to that private file. Docker Compose file-backed
secrets preserve the host file permissions, so a host file owned by root with
0600 is not readable by UID65532; assign that UID explicitly. Configure
`server.cloudflare_access`, `server.hosts`, `server.origins`, the exact team JWKS
allowlist, and an Access application before publishing the tunnel. Its origin
is `http://127.0.0.1:7171` in the shared namespace. It uses `http2` and IPv4, with
updates disabled; its endpoints are pinned from Cloudflare's official
[firewall documentation](https://developers.cloudflare.com/cloudflare-one/networks/connectors/cloudflare-tunnel/configure-tunnels/tunnel-with-firewall/).

`tests/compose-smoke.sh` is G1's CI entrypoint. It builds both serving targets
and an isolated mock fixture, runs root provisioning followed by non-root
`brain init`, starts `brain serve` as UID1654 and the extractor as UID1655,
and checks `/health`, `/ready`, CLI login/keys/providers/sessions/doctor, the Razor
login and signed-in shell, rootfs immutability, capabilities and the blocked
public canary. The script uses Python 3 on the host to validate JSON and HTML,
and isolates CLI credentials in a disposable private file store.
`tests/compose-smoke.yaml` and its valid loopback provider config
are test fixtures only; the production Compose project supplies no mock provider.
Cloudflare credentials and live model providers are not needed.

For browser inspection after a successful run, use
`SECONDBRAIN_SMOKE_KEEP=1 deploy/tests/compose-smoke.sh`. It prints the disposable
project and root paths and leaves the stack running; failures still clean up.
Stop the retained stack explicitly with the same environment and both Compose
files using `docker compose --project-name <printed-project> -f deploy/compose.yaml
-f deploy/tests/compose-smoke.yaml down --remove-orphans --volumes`.

## systemd

Install the published self-contained binaries into `/usr/local/lib/secondbrain`
and run `brain init --provision --deployment systemd --templates /path/to/deploy`.
The template daemon unit uses loopback HTTP and default-deny IP rules. Replace
the sample trusted provider (10.8.0.5), resolver (10.8.0.1), and documentation-only
Access address (192.0.2.1) with resolved, pinned production addresses in a drop-in
before enabling cloud access. The Access placeholder intentionally fails closed.
Both units drop all capabilities, isolate the filesystem, disable core dumps,
and limit resources; the extractor also isolates its network and can access no
store, incoming tree, or external secret. The socket activates one service with
`LISTEN_FDS=1`, which consumes fd3 and answers the shared ping/framing contract.

```sh
sudo systemd-analyze verify /etc/systemd/system/secondbrain.service \
  /etc/systemd/system/secondbrain-extractor.socket \
  /etc/systemd/system/secondbrain-extractor.service
sudo systemctl daemon-reload
sudo systemctl enable --now secondbrain-extractor.socket secondbrain.service
```

### Linux egress spike

macOS cannot execute the cgroup-BPF systemd rule. On the target Linux host,
confirm the system manager supports IP filtering, start the units, and inspect
`systemctl show secondbrain -p IPAddressAllow -p IPAddressDeny -p IPAccounting`
and journal warnings. A parsed directive alone does not prove kernel enforcement.
Run `brain doctor --json`: the public canary must fail, the exact trusted vLLM
provider must succeed, and Access JWKS refresh must succeed. Remove the Access
IP temporarily to check that refresh fails while cached keys remain usable.
Record host/kernel/systemd versions and results before accepting the spike.

`IPAddressAllow` applies to ingress source addresses and egress destinations,
and admits all ports on each IP. The loopback template deliberately exposes no
private HTTPS listener until its certificate and peer rules are configured.
If adding a private listener, account for every allowed inbound peer and use
an additional direction/port-specific host firewall to retain exact egress
bounds. Avoid broad tailnet CIDRs in the daemon's IP allowlist. On systems where
cgroup-BPF filtering is unavailable, refuse `local_only` provider traffic when
the canary succeeds; do not infer enforcement from successful unit linting.

### Tailscale certificate spike

On a tailnet-connected Linux host, obtain a certificate using `tailscale cert`
for the host's actual `*.ts.net` name. Install the certificate/private key in the
read-only secrets directory, with private-key mode0600 and daemon ownership.
Configure an HTTPS listener on the tailnet IP and a file certificate accepted
by the HTTP lane; add exact Host/Origin values. From another tailnet peer,
request `https://<name>:7443/health` with normal certificate verification (no
`-k`) and record HTTP200, SAN, expiry, and peer identity. Check renewal reload
and expired/missing certificate rejection. Kestrel's Tailscale selector/file
support belongs to the HTTP lane; obtaining a certificate alone does not prove
the HTTPS spike. No tailnet peer or systemd instance is available in the local
macOS development environment, so these two operational spikes remain target-
host checks.
