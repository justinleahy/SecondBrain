# Cloudflare Tunnel for leahy.ai

This is the native systemd deployment for the existing SecondBrain service on
`127.0.0.1:7171`. It uses a dedicated tunnel, separate from T3 Code or other apps.
The files in this repository are templates; no tunnel or DNS route is created by
copying them. The tunnel UUID, Access team, and audience must be filled in first.

## Cloudflare account setup

1. In Cloudflare Zero Trust, create a self-hosted Access application for
   **leahy.ai**, covering every path. Add an **Allow** policy for the owner's
   exact email address and enable an appropriate login method, such as one-time
   PIN. Record the team domain (`TEAM.cloudflareaccess.com`) and the application's
   audience (AUD) tag. Do this before publishing the DNS route.
2. Authorize `cloudflared tunnel login` for the `leahy.ai` zone, then run
   `cloudflared tunnel create secondbrain-leahy-ai`. Keep its tunnel credential
   JSON private. The account's `cert.pem` is for management and is **not** the
   tunnel service credential.
3. Fill in `deploy/provision/cloudflared.systemd.yaml` with the tunnel UUID,
   Access team name (without `.cloudflareaccess.com`), and audience. Both the
   connector and SecondBrain validate Access assertions.

Cloudflare documents [local tunnel creation](https://developers.cloudflare.com/cloudflare-one/networks/connectors/cloudflare-tunnel/do-more-with-tunnels/local-management/create-local-tunnel/),
[Access assertion validation](https://developers.cloudflare.com/cloudflare-one/access-controls/applications/http-apps/authorization-cookie/validating-json/),
and [origin Access parameters](https://developers.cloudflare.com/cloudflare-one/networks/connectors/cloudflare-tunnel/configure-tunnels/cloudflared-parameters/origin-parameters/).

## Configure SecondBrain before starting the tunnel

Back up `/etc/secondbrain/config.yaml` and the service drop-ins privately. Merge
these values into the existing configuration; preserve listeners, providers,
other hosts/origins, trusted proxies, and control-plane entries:

```yaml
server:
  hosts: [leahy.ai] # Append to the existing list.
  origins: [https://leahy.ai] # Append to the existing list.
  trusted_proxies: [127.0.0.1] # Append if missing.
  cloudflare_access:
    public_hostname: leahy.ai
    team_domain: TEAM.cloudflareaccess.com
    audience: APPLICATION_AUDIENCE
privacy:
  control_plane_egress:
    - https://TEAM.cloudflareaccess.com/cdn-cgi/access/certs # Append.
```

Resolve the actual team domain and add its exact addresses to a new
`/etc/systemd/system/secondbrain.service.d/30-cloudflare-access.conf` with
`[Service]` and one `IPAddressAllow=ADDRESS` line per address. Preserve the
daemon's default-deny policy and existing provider/LAN rules. These addresses
are for Access signing-key refresh, not the tunnel transport. Pin them alongside
the host's port-443 egress rules and revisit them if Cloudflare DNS changes.

After `systemctl daemon-reload` and restarting SecondBrain, confirm loopback
`/ready` returns HTTP 200 and requests with `Host: leahy.ai` but no valid Access
assertion return HTTP 401. Verify a request with a valid assertion loads the
SecondBrain login page and that the daemon successfully fetched the team's JWKS.
The public host must never bypass the Access gate.

## Install the dedicated connector

Use an installed, verified cloudflared binary and the completed configuration.
Install the binary as `/usr/local/libexec/secondbrain-cloudflared` (0755), the
configuration as `/etc/secondbrain-tunnel/config.yaml` (0644), and the tunnel's
credential JSON as `/etc/secondbrain-tunnel/credentials.json` (0600, root-owned).
The configuration directory must allow the DynamicUser service to read the
non-secret YAML; use root ownership and mode 0755. Systemd delivers the secret
with `LoadCredential`, so the service does not need direct access to the source
JSON or the management certificate.

Install `secondbrain-tunnel.service` in `/etc/systemd/system/`. On the reference
SELinux host, give the executable the persistent `bin_t` label and run
`restorecon` on installed files. Validate the completed YAML with
`cloudflared tunnel --config /etc/secondbrain-tunnel/config.yaml ingress validate`
and verify the installed unit with `systemd-analyze verify` before enabling it.

```sh
sudo systemctl daemon-reload
sudo systemctl enable --now secondbrain-tunnel.service
# Requires the account cert.pem, not the tunnel credential JSON:
cloudflared tunnel route dns secondbrain-leahy-ai leahy.ai
```

Do not overwrite an existing DNS record blindly. `leahy.ai` had Cloudflare
nameservers and no apex A answer during inspection on 2026-10-24; recheck the
zone's A, AAAA, and CNAME records before creating the route.

## Verify and recover

Check the connector journal for registered connections. An anonymous request to
`https://leahy.ai` must reach Cloudflare Access. After signing in with the allowed
email, the SecondBrain login should load; the existing SecondBrain password is
still required. Verify browser login, assets, navigation, and a chat response.
Retest the existing LAN address and `/ready`, and confirm the daemon's public
egress canary remains blocked.

If verification fails, disable `secondbrain-tunnel.service` and remove only this
tunnel's DNS route. Restore the saved SecondBrain configuration and remove only
the new Access drop-in, then reload systemd and restart SecondBrain. Leave the
existing T3 Code tunnel, LAN rules, and provider settings intact. This setup
requires the workstation and both services to remain running.
