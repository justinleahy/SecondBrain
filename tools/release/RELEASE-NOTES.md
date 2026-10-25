This draft contains the verified Linux x64 Server, CLI and Extractor packages,
their complete file manifests, CycloneDX software inventories, vulnerability
reports and build metadata. The CLI package includes the daemon and web assets.

Verify `SHA256SUMS` against `SHA256SUMS.sigstore.json` with Cosign and the exact
GitHub workflow identity for this version tag, then check the archive checksum.
After extraction, check each package's own `SHA256SUMS`. Installation and data
migration are separate operator steps. See `tools/release/README.md` and the
deployment instructions before upgrading an initialized instance.
