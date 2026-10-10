# Separate LAN provider qualification

The existing `artifacts/m1-qualification/live` results used a real provider on
the same host. They do not establish the separate LAN RTX 5090 reference gate.

This workload qualifies M1 embedding and ingestion only. Its fixture declares
chat/enrich roles against the embedding bridge, which does not serve chat
requests, and checks daemon liveness rather than readiness of all model roles.
Before claiming a complete reference topology, separately configure the actual
reference chat and embedding bindings and show their readiness using the
existing live-provider qualification procedure. This script alone does not
establish chat/enrich capability or readiness.

An operator must verify and record that the daemon runs on the required Linux
reference machine and the embedding provider runs on a **distinct machine with
an RTX 5090**, reached through the LAN. Record each machine's CPU, RAM, storage,
OS, GPU/model service versions, and the provider's numeric private IPv4 address.
The harness cannot independently validate physical separation or GPU identity.

This harness qualifies M1 embedding and ingestion timing. Its fixture maps
chat/enrich to the embedding bridge and checks `/health`, not all-role `/ready`.
It does not prove readiness of a real chat/enrichment service. A complete
reference-topology claim also requires the existing [live readiness
qualification](../../README.md#qualification-and-milestone-evidence), configured
with both the actual remote chat and embedding URLs, reviewed model capabilities,
and dimensions. Record that separate all-role `/ready` result alongside the
machine/topology evidence; an unset-URL no-op does not count.

On the daemon host, use this checkout and its existing prebuilt deployment
images. Ensure Docker is available, the incoming/data mounts use native NVMe
storage, and the operator-selected provider serves
`Qwen/Qwen3-Embedding-0.6B` with 1024 dimensions at `/v1/embeddings` and model
discovery at `/v1/models`. The bridge supplies no upstream authentication.
Confirm the provider is intentionally available to this daemon host before
running; this procedure does not install services or change host firewalls.

Only one fixture may run at a time. Verify that no `secondbrain-m1-live`
containers or network remain, no other benchmark is active, and ports 17174 and
11882 plus subnet `172.28.0.0/24` are free. Do not remove an unrelated deployment
to make room. If these resources are occupied, finish the prior authorized
fixture first.

Choose a new artifact directory on the daemon's NVMe filesystem. Replace the
example private address with the verified provider address, and choose its
actual port:

```sh
python3 deploy/tests/m1-live-qualification.py --run \
  --upstream-host 192.168.1.50 --upstream-port 8001 \
  --artifact-dir artifacts/m1-qualification/lan-2026-10-09-run1
```

The artifact directory must not already exist. The harness refuses existing
directories to preserve earlier evidence. Docker permission is required; follow
the host's existing authorized Docker access procedure. The project name and
private bridge stay fixed at `secondbrain-m1-live` and `172.28.0.1:11882`.
Daemon provider egress remains limited to that exact private bridge endpoint;
only the host-side bridge forwards to the explicit upstream address and port.
Numeric RFC1918 or loopback IPv4 addresses are accepted; URLs, hostnames,
credentials, public IPs, and invalid ports are rejected before provisioning.

Inspect `qualification.json`, `throughput.json`, and the provider JSONL in the
fresh artifact directory. Required observations are capture keyword under one
second, complete vectors under three seconds, a real semantic result, a
successful structured query while the bridge is stopped, anonymous jobs denial,
authenticated jobs access, and the four-worker 100 Markdown/20 PDF throughput
targets (PDF median 12 pages and p95 80 pages). Provider request timing and token
usage are separate metadata; concurrent request duration sums must not be
subtracted from wall time. Intent inferred from fixture activity is labeled.

Attach the operator's machine/topology evidence beside these results before
claiming the separate LAN reference gate. The harness reports supplied topology
as unverified. It removes its serving containers and network after the run,
retaining the isolated runtime volume and data/evidence for inspection.
