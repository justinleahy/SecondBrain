# Chat submission and published static assets — 2026-10-24

The initial deployment returned HTTP 200 for JavaScript and stylesheets but served empty bodies. The page's prerendered HTML appeared, while the missing Blazor runtime meant typing could not enable Send. The deployment checks inspected status codes without verifying asset contents, and the initial production browser check consequently missed the inactive page.

The systemd service starts in `/srv/secondbrain`. The application previously inherited that directory as its content root, although published web assets live beside the binaries in `/usr/local/lib/secondbrain/wwwroot`. `Program.cs` now explicitly uses `AppContext.BaseDirectory` as the application content root. A regression checks the actual hosted environment, and the upgrade verifier now requires nonempty JavaScript/CSS/icon responses that exactly match installed files, including `/_framework/blazor.web.js`.

An isolated production static-asset host reproduced the failure with the same published manifest: the unrelated working directory returned HTTP 200 with zero bytes; the application directory returned all 200,645 bytes of the Blazor runtime. All 245 server tests and 18 deployment/setup safeguard tests passed. The corrected self-contained bundle is staged for managed deployment; post-installation verification remains to be recorded.

The user also requested deployment without repeated sudo handoffs. The [managed deployment setup](../../deploy/systemd/managed-deployment.md) limits the passwordless command to starting a fixed, root-owned deployment service. Installation of this permission requires one interactive sudo command.
