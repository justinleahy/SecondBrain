# M4 release-tooling checkpoint

`verification.json` records the executed checks and remaining gates. The `Cli`,
`Server` and `Extractor` folders preserve the generated inventories, complete
file manifests, build metadata and vulnerability results.

The recorded `SHA256SUMS` files describe the original complete build-output
folders. Those folders, including the larger application archives and raw Syft
inventories, are retained locally at `artifacts/m4-release-20261024/` and are
excluded from Git. This evidence snapshot omits those large files.

`local-test-SHA256SUMS`, `local-test-signature.sigstore.json` and
`local-test-signing.pub` demonstrate signature validation with a disposable local
key. They are not a GitHub release signature or proof of transparency-log
inclusion. Live GitHub OIDC signing and the configured container scans remain
unexecuted gates.
