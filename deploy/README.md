# Deployment

`brain init --provision` is the root-only setup step. The daemon and extractor
serve as distinct unprivileged identities, with umask 0077 for daemon-created
files. Keep `/srv/secondbrain` and its key ring at 0700, owned by `secondbrain`.
The incoming tree is 0750, owned by `secondbrain-sync` with group `secondbrain`.
Configuration is 0600 in a private 0700 `config/settings` directory that the
daemon can update atomically. The stable `config.yaml` path is a relative link
to `settings/config.yaml`. External secret files remain 0600 and read-only to
the daemon. Directories need traversal bits: config/secrets are private traversable
directories, not 0600 directories. The shared runtime directory must be group
writable for the standalone extractor, with its socket at 0660.

Existing installations use the [M3 settings migration](systemd/writable-settings.md)
to adopt the private writable directory without changing configuration contents.

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
sudoedit "$SECONDBRAIN_HOST_ROOT/config/settings/config.yaml"
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
allowance. IPv6 external egress is denied by default; M4 can opt in to exact calendar TCP pins on a dual-stack bridge. Narrow neighbor-discovery rules support those IPv6 connections. An explicit read-only `resolv.conf`
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

## Calendar download allowances (M4 checkout)

Calendar subscriptions require exact HTTPS origins in `sources.calendar_allowed_origins` and matching host rules. These origins do not enter `privacy.trusted_services` or grant a model provider access. The default calendar allowance is empty. Full subscription links remain in protected state or private secret files; they never belong in these firewall files.

Using an account that can read the selected configuration and its secret references, prepare the policy:

```sh
brain --config /etc/secondbrain/config.yaml maintenance calendar-egress --output-dir ./calendar-network
```

The command resolves only the allowed origins and canary, checks all returned addresses against the public-unicast policy, and refuses a destination sharing the canary's address. It writes three reviewable files: `40-calendar-egress.conf`, `calendar-egress.env`, and `calendar-egress.json`. It does not install rules or restart services. Regenerating with an empty origin list removes the generated calendar pins. Re-run it when the approved hosts' DNS records change; new addresses fail closed until the matching policy is installed. A failed DNS or policy validation leaves the previous files intact.

For systemd, install the reviewed drop-in and restart the service:

```sh
sudo install -d /etc/systemd/system/secondbrain.service.d
sudo install -m 0644 ./calendar-network/40-calendar-egress.conf /etc/systemd/system/secondbrain.service.d/40-calendar-egress.conf
sudo systemctl daemon-reload
sudo systemctl restart secondbrain.service
```

This drop-in adds only the selected addresses and preserves the base deny rule and existing provider/control-plane allowances. systemd's address rules apply to both ingress and egress and do not filter destination ports; the daemon's source transport enforces the exact HTTPS origin, port and registered URL. Other installed drop-ins must not independently allow the canary.

For Compose, rebuild/use the egress image from this release and pass the generated environment file after the deployment's normal environment file. Validate the merged configuration before restarting the namespace helper and all its dependants together:

```sh
docker compose --env-file .env --env-file ./calendar-network/calendar-egress.env -f deploy/compose.yaml config --quiet
```

`SECONDBRAIN_CALENDAR_ALLOWLIST` contains only numeric `IPv4:port` or `[IPv6]:port` pairs. `SECONDBRAIN_CALENDAR_CANARY_ADDRESSES` contains the separately resolved canary addresses. The helper validates public destinations, rejects canary overlaps and grants calendar TCP access only to daemon UID1654, including under `local_only`. Tunnel UID65532 and extractor UID1655 receive no calendar allowance.

The default Compose bridge remains IPv4-only. To use IPv6 calendar pins, include `-f deploy/compose.ipv6.yaml` after the main file and provide a working host IPv6 uplink. Its optional `SECONDBRAIN_DOCKER_IPV6_SUBNET` defaults to `fd42:5342:1::/64`; choose a nonconflicting subnet. The overlay adds the IPv6 subnet while retaining the IPv4 subnet and leaves the extractor on `network_mode: none`.

The kernel qualification command is `python3 deploy/tests/calendar-egress-qualification.py --output /tmp/calendar-egress-result.json`. It uses the existing firewall image with the current script in a disposable `network_mode: none` container and a nested namespace. All fixture addresses, listeners and routes remain inside that container. It first proves every listener is reachable, then checks allowed/refused IPv4/IPv6 destinations, ports, the canary and process identities, plus the extractor's offline namespace. The fixture needs `SYS_ADMIN` and an unconfined seccomp profile for `unshare`; these are test-only settings. Shipping helpers retain `NET_ADMIN` alone and the normal seccomp profile. This is a firewall qualification, not evidence that the complete application has been deployed.

## systemd

For a dedicated Cloudflare Tunnel serving `https://leahy.ai`, use the
[native tunnel setup](systemd/cloudflare-tunnel.md). It includes the Access gate,
daemon configuration, credential delivery, and a separate persistent connector.

M1 provisioning creates `incoming/inbox` as daemon-owned mode1770 with the
daemon's shared group, and `.claimed` as daemon-owned mode0700. The sync
identity uses that shared group to deliver files; the sticky bit prevents it
from replacing the daemon's claim directory. Compose mounts the incoming tree
read-only and mounts only this inbox child writable. Re-run provisioning when
upgrading an M0 installation before starting the M1 daemon. The staging adapter
pins the validated inbox descriptor and rejects a replacement inbox owned by
the sync identity after restart.

Install the published self-contained binaries into `/usr/local/lib/secondbrain`
and run `brain init --provision --deployment systemd --templates /path/to/deploy`
with explicit `--daemon-uid`, `--daemon-gid`, `--extractor-uid` and `--sync-uid`
values; the defaults follow the invoking user. Merge all three published outputs,
Server, CLI and Extractor, into that one directory: the CLI output carries a layout
copy of the daemon but not the Blazor static-assets manifest
(`SecondBrain.Server.staticwebassets.endpoints.json`) or `wwwroot`, and without
them `brain serve` fails at startup. On SELinux-enforcing hosts, label the three
executables `bin_t` (`semanage fcontext -a -t bin_t` for the `brain`,
`SecondBrain.Server` and `SecondBrain.Extractor` paths, then `restorecon -RF` on
the directory). Files under `/usr/local/lib` default to `lib_t`, which keeps the
daemon in the confined `init_t` domain where its own data root is denied.
The template daemon unit uses loopback HTTP and default-deny IP rules. Replace
the sample trusted provider (10.8.0.5), resolver (10.8.0.1), and documentation-only
Access address (192.0.2.1) with resolved, pinned production addresses in a drop-in
before enabling cloud access. The Access placeholder intentionally fails closed.
Both units drop all capabilities, isolate the filesystem, disable core dumps,
and limit resources; the extractor also isolates its network and can access no
store, incoming tree, or external secret. The socket activates one service with
`LISTEN_FDS=1`, which consumes fd3 and answers the shared ping/framing contract.
The first connection starts the extractor; on the reference workstation its cold
start took about 5 s while the daemon's ping waits 2 s, so the first `/ready`
after boot can report the extractor as not ready. The next probe passes and the
service stays up.

```sh
sudo systemd-analyze verify /etc/systemd/system/secondbrain.service \
  /etc/systemd/system/secondbrain-extractor.socket \
  /etc/systemd/system/secondbrain-extractor.service
sudo systemctl daemon-reload
sudo systemctl enable --now secondbrain-extractor.socket secondbrain.service
```

### Upgrading the reference installation to M1

The qualified M1 bundle can replace an existing reference systemd installation
with `deploy/systemd/upgrade-m1.py`. The script verifies the complete bundle,
stops the installed services, holds the data-root lock while backing up, and
retains the previous binaries and a private data/configuration snapshot under
`/var/backups/secondbrain`. It preserves the existing configuration, credentials,
key ring and host drop-ins, installs the M1 base units, repairs inbox ownership,
and verifies migration, readiness, binary hashes and authentication enforcement.
If a step fails after stopping services, it restores the previous installation
and retains the failed M1 data separately rather than discarding new writes.

From the repository root, using the bundle qualified on 2026-10-24:

```sh
sudo python3 deploy/systemd/upgrade-m1.py \
  artifacts/m1-qualification/operator-publish-20261024-poll-deadline/merged \
  artifacts/m1-qualification/operator-publish-20261024-poll-deadline/sha256-manifest.json \
  --evidence-dir artifacts/m1-deployment-20261024
```

The command requires the installed reference paths and identities, a loopback
HTTP listener on port 7171, Python with PyYAML, and the existing SELinux apphost
label rules. It prints the private backup location and writes sanitized results
to `deployment-result.json` in the selected evidence directory. Detailed command
output stays in the root-only backup. Unprivileged safeguard tests run with
`python3 -m unittest discover -s deploy/tests -p test_systemd_upgrade.py -v`.

For the browser-chat release, use the same helper with `--release chat` and
the new bundle and manifest. This adds verification of the chat schema,
authenticated chat routes, home page, and static assets. See the
[browser chat deployment record](../docs/build/M2-browser-chat-deployment.md)
for the prepared command and release provenance.

The complete M2 server, CLI and extractor were subsequently installed through that managed workflow; see the [M2 deployment record](../docs/build/M2-complete-deployment.md).

For repeated deployments without password prompts, the reference host supports
an optional [fixed-purpose deployment service](systemd/managed-deployment.md).
Its one-time setup grants only permission to start that service.

### LAN listener on the reference workstation

Configured on 2026-10-24: open **http://10.66.10.20:7171** from
`10.66.10.0/24` or `10.66.20.0/24` and use the existing SecondBrain password.
Runtime and permanent rules, LAN/loopback readiness, the login page, API
authentication and the daemon UID outbound block were verified in the
[deployment evidence](../docs/build/evidence/lan-listener-20261024/independent-verification.json).
A client on another subnet was not used for the recorded test.

To add `http://10.66.10.20:7171` for clients on `10.66.10.0/24` and
`10.66.20.0/24`:

```sh
sudo python3 deploy/systemd/configure-lan.py \
  --allow-network 10.66.20.0/24 \
  --evidence-dir artifacts/lan-listener-20261024
```

This backs up the private configuration, adds the exact listener/Host/Origin,
and preserves existing listeners and provider settings. Firewalld rich rules
limit the LAN endpoint to the selected subnet, including when the workstation
zone already allows high ports. A UID-specific OUTPUT drop rule prevents new
daemon connections to that subnet while allowing established replies. The
systemd LAN allowance depends on firewalld so those restrictions load first.
The script validates readiness, the login page, anonymous API rejection and
an actual denied connection from UID1654. Failures restore the configuration
and remove only firewall rules added by this invocation.

Firewalld's [direct interface](https://firewalld.org/documentation/man-pages/firewalld.direct.html)
is used only for the owner/connection-state condition unavailable in rich rules;
ordinary ingress uses [rich rules](https://firewalld.org/documentation/man-pages/firewalld.richlanguage.html).
Both runtime and permanent rules are updated without reloading unrelated rules.

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
by the HTTP lane; add exact Host/Origin values. `IPAddressAllow` governs ingress
as well: allow the peer's exact address in a drop-in, and expect a request from
the host's own tailnet address to be dropped unless that address is allowed too.
From another tailnet peer, request `https://<name>:7443/health` with normal
certificate verification (no `-k`) and record HTTP200, SAN, expiry, and peer
identity. Certificate loading currently happens at listener startup; file
replacement alone does not establish renewal adoption. Renewal and
expired/missing-certificate handling remain
[#6](https://github.com/justinleahy/SecondBrain/issues/6), to complete before the
reference certificate expires on 2026-11-25.

The RTX 5090 workstation proved the canary/vLLM portion of the systemd spike
and peer HTTPS on 2026-10-09. The Access JWKS portion of the egress spike and
live Tunnel remain [#3](https://github.com/justinleahy/SecondBrain/issues/3).
M0 closed for the Fedora/Tailscale deployment with these qualifications
explicitly deferred; see [the closure record](../docs/build/M0-convergence-report.md#m0-closure-2026-10-09).

The daemon defaults to an 8 GiB memory ceiling: one million 1024-dimensional float32 vectors alone occupy about 3.8 GiB, with additional space needed for old readers, SQLite and managed state. Set `SECONDBRAIN_DAEMON_MEMORY_LIMIT` for Compose or override `MemoryMax` in a systemd drop-in to match the corpus and embedding dimensions. The extractor supervisor has no cumulative CPU-time ceiling; each disposable parser sets a kernel-enforced 120-second CPU limit before receiving input, alongside the existing request deadline and memory/process limits.

## M4 upgrade preparation (checkout only)

Before first starting M4 against an initialized M3 data root, stop the daemon and provision its backup key with the local `brain maintenance backup-key --recovery-copy ABSOLUTE_PATH` command. Both destination parents must already exist and be owned by the invoking user; the key directory must be private. Keep a verified recovery copy off the server. See the [CLI backup instructions](../src/SecondBrain.Cli/README.md#encrypted-backups-m4-checkout).

For the existing reference systemd installation, `deploy/systemd/prepare-backup-key.py` can prepare the key before the M4 CLI is installed. Review its default plan with `python3 deploy/systemd/prepare-backup-key.py --recovery-owner USER`, then run the same command with `sudo python3 -I` and `--apply`. This fixed-layout helper creates a raw 32-byte key at `/etc/secondbrain/secrets/backup.key` owned by the daemon, and a matching private copy at `/var/lib/secondbrain-recovery/backup.key` owned by the chosen operator. Existing files are verified and never overwritten or rotated. It changes neither service state nor configuration. The operator must store the recovery copy off-server and confirm custody; a same-server handoff copy does not satisfy recovery custody. Its eight unprivileged safeguard tests cover matching copies, repeat execution, mismatches, missing originals, unsafe links/permissions/owners and exclusive publication.

M4 refuses an initialized upgrade without a complete encrypted pre-upgrade archive. Resolve unavailable external source files before retrying a partial backup. The migration records its phase and the archive name, retains the archive under normal backup retention, and completes the index transition on restart. Fresh empty initialization does not need a backup key. This checkout's migration checks do not constitute production deployment acceptance; the reference service remains on M3 until its full M4 deployment checks are complete.


### M4 isolated Compose qualification

Run the existing deployment workflow with `SECONDBRAIN_M4_QUALIFY=1`, an unused loopback port in 18000–18999, and a distinct `secondbrain-m4-` project name. It uses separate `:m4` image tags, then runs the mounted-source encrypted backup/restore and synthetic VTT/SRT/calendar checks. `SECONDBRAIN_SMOKE_RESULTS` selects the JSON result directory. The fixture is removed on exit unless `SECONDBRAIN_SMOKE_KEEP=1` is explicitly set for subsequent browser or network qualification.

```sh
SECONDBRAIN_PRIVATE_ADDRESS=127.0.0.1 \
SECONDBRAIN_PORT=18340 \
SECONDBRAIN_M4_QUALIFY=1 \
bash deploy/tests/compose-smoke.sh
```

These checks use synthetic data and a disposable account. They do not establish production key custody, large-corpus bounds, live-provider quality or release-signing acceptance.

For controlled HTTPS subscription qualification, retain that disposable stack and run `deploy/tests/m4-calendar-hardened.py` with its `--project`, `--host-root`, `--origin` and an `--output` JSON path. Preserve the same Compose environment, including its private address, port and subnet. The harness requires the mounted-backup preparation above. It creates a temporary TLS peer inside the fixture network, verifies certificates through a fixture-only CA, checks permitted and denied addresses, and exercises conditional refresh, failure recovery and encrypted subscription restore. It restores the original configuration, removes the test source and TLS peer, and recreates the base fixture on exit. Its peer image is pinned; no live feed credentials are needed. Remove the retained Compose stack after any browser/network checks finish.


### Container base provenance

The runtime uses a digest-pinned Wolfi ASP.NET base with the same .NET servicing version as the self-contained packages. The firewall helper uses a separately pinned Wolfi base, explicit firewall/diagnostic package versions, and the nft backend. The build verifies both installed .NET runtime versions. Serving identities, capabilities, read-only roots and network policy remain governed by the existing Compose profile.

Base-image changes require fresh signatures, inventories, vulnerability scans and hardened deployment qualification. The [current base comparison](../docs/build/evidence/m4-clean-base-20261024/verification.json) records those checks and their remaining scope. Historical Ubuntu/Alpine results remain attached to their original image IDs.
