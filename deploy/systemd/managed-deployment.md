# Managed deployments on the reference host

The optional `secondbrain-deploy.service` lets the workspace owner install staged SecondBrain releases without granting passwordless access to arbitrary root commands. It applies only to an already-provisioned reference systemd installation.

Run the one-time setup from the repository as the normal account:

```sh
sudo python3 deploy/systemd/install-managed-deploy.py
```

The installer validates the sudoers fragment with `visudo`, freezes the deployment implementation as a root-owned file under `/usr/local/libexec/secondbrain-deploy/`, and installs a root-owned oneshot service. The only passwordless command is:

```sh
sudo -n /usr/bin/systemctl start secondbrain-deploy.service
```

The service reads a fixed `artifacts/auto-deploy/merged` bundle and `artifacts/auto-deploy/sha256-manifest.json`. Prepare these as the normal user before starting the service. Its results appear in `artifacts/auto-deploy/result/deployment-result.json`; root-private backups and diagnostics remain under `/var/backups/secondbrain/`.

The privileged process uses isolated Python and a fixed system command path. It snapshots the existing installation, copies into a private root staging directory, rejects symlinks and special files, clears privileged/writable file modes, verifies all file hashes, and replaces the installed application. It does not execute staged application code as root or load workspace service definitions. The existing systemd units run the new application as the existing unprivileged service identities, so configuration, account state, LAN policy, and host security settings are preserved. Failure triggers the existing rollback path. Public evidence is written after dropping to the workspace owner's identity.

This permission trusts the workspace owner to deploy application code with the SecondBrain service's access to its vault and configuration. It does not permit root shell commands, arbitrary service operations, or host provisioning. Changes to the root deployment implementation or service policy require running the setup command again.

To revoke unattended deployment permission, run:

```sh
sudo rm /etc/sudoers.d/secondbrain-deploy
```
