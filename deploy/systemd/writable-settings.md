# Writable settings for M3

M3 saves validated configuration through an atomic file replacement. The daemon
needs a writable directory for that replacement, while the parent configuration
directory and the external secrets remain protected. Fresh provisioning creates
`settings/config.yaml`, mode 0600, in a daemon-owned 0700 `settings` directory.
The stable `config.yaml` path is a relative link to that file.

For an existing reference systemd installation, review
[`enable-settings.py`](enable-settings.py) and run:

```sh
python3 -I deploy/systemd/enable-settings.py
sudo python3 -I deploy/systemd/enable-settings.py --apply
```

The first command prints the plan. Applying it stops the running daemon,
preserves the configuration bytes in a root-private backup under
`/var/backups/secondbrain-settings`, creates the private settings directory,
installs `20-settings.conf` with a write allowance for only that directory,
reloads systemd, and restarts the daemon if it was previously running. Secrets,
network policy and other service drop-ins are retained. Repeated runs preserve
later configuration changes. Unexpected links, multiple hard links and
conflicting destination files are rejected.

After applying, check the running service:

```sh
systemctl is-active secondbrain.service
curl --fail http://127.0.0.1:7171/ready
```

The settings page still requires an authenticated admin session, password
step-up and a current configuration revision. This filesystem change grants no
additional HTTP authority. The migration helper does not deploy M3 binaries.

For an existing Compose installation, stop its daemon container, then use the
same migration on the host configuration directory before starting the updated
Compose definition:

```sh
sudo python3 -I deploy/systemd/enable-settings.py --apply \
  --compose-config-directory "$SECONDBRAIN_HOST_ROOT/config"
```

This mode uses the Compose daemon's UID/GID 1654 and does not touch systemd.
The daemon container mounts only `config/settings` writable; the secrets mount
remains read-only. Fresh `brain init --provision` installations already use this
layout. Existing regular configuration files are retained by provisioning until
the explicit migration is run.

The unprivileged migration tests use temporary directories:

```sh
python3 -m unittest discover -s deploy/tests -p test_settings_migration.py -v
```
