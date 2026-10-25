# Release inventory, scanning and signing

Every CI build audits all locked NuGet dependencies, audits the editor's npm
lockfile, runs the parser mutation corpus, and scans the published Linux x64
packages and container images. Any reported vulnerability at low severity or
higher fails the gate, including unfixed findings. Database download/check
failures and stale or invalid scanner databases also fail the gate. There are no
blanket package exceptions. The daily CI schedule provides a fresh advisory check
even when application code has not changed.

`toolchain.json` pins the exact Syft, Grype and Cosign Linux x64 release archives
and their SHA-256 digests. `install_tools.py` verifies those bytes before making
the executable available; it does not run remote install scripts. Review both
the upstream release and digest when updating a pin.

The package inventory starts with Syft's CycloneDX output from the published
files. It includes the pinned self-contained .NET runtime and NuGet packages.
Framework entries prefixed `runtimepack.` in `deps.json` are normalized to their
actual NuGet package ids for advisory matching, while preserving their original
SBOM references and recording the source names.
`inventory.py` then adds the actual native SQLite version and binary hash where
SQLite is shipped, and the browser dependencies recorded by the editor build.
The extractor does not ship SQLite. Monaco's vendored Marked parser is recorded
with its source hash. Its vendored DOMPurify import is redirected at build time to
the reviewed, patched npm dependency; merely updating npm would leave the copied
vulnerable code in Monaco unchanged. Browser file hashes, dependency versions,
integrities and licenses are checked before the inventory is accepted.

`files.json` hashes every published file. `package.py` checks those files again
and hashes the bytes actually copied into the archive. Its `SHA256SUMS` binds the
archive, inventory, scanner report and build metadata. Build metadata records the
actual compiler version, source commit, dirty-tree status and, in CI, the workflow
identity and run. The runtime version is independently pinned in
`Directory.Packages.props`; the compiler patch selected by `global.json` is
recorded rather than guessed.

For a local package, publish to a fresh directory, then run:

```sh
python3 tools/release/install_tools.py /tmp/secondbrain-release-tools syft grype
mkdir -p /tmp/secondbrain-release-report
/tmp/secondbrain-release-tools/syft dir:/path/to/published-package \
  -o cyclonedx-json=/tmp/secondbrain-release-report/syft.cdx.json
python3 tools/release/inventory.py /path/to/published-package \
  /tmp/secondbrain-release-report/syft.cdx.json /tmp/secondbrain-release-report
GRYPE_DB_REQUIRE_UPDATE_CHECK=true GRYPE_DB_VALIDATE_AGE=true \
  GRYPE_DB_VALIDATE_BY_HASH_ON_START=true \
  /tmp/secondbrain-release-tools/grype \
  sbom:/tmp/secondbrain-release-report/sbom.cdx.json --fail-on low \
  -o json --file /tmp/secondbrain-release-report/vulnerabilities.json
python3 tools/release/package.py /path/to/published-package \
  /tmp/secondbrain-release-report secondbrain-linux-x64
```

Version-tag pushes run the same CI gates and hardened Compose smoke check. Only
after they succeed can the release job obtain GitHub OIDC signing authority. It
signs the combined release's checksum manifest with Cosign, immediately verifies
the exact workflow identity and issuer, and creates a **draft** GitHub release.
PR builds have no signing permission. Publishing the reviewed draft and deploying
it are separate operator actions. No release is created by a local build.

Verify a downloaded release before extraction, using its exact version tag:

```sh
cosign verify-blob SHA256SUMS --bundle SHA256SUMS.sigstore.json \
  --certificate-identity 'https://github.com/justinleahy/SecondBrain/.github/workflows/ci.yml@refs/tags/vVERSION' \
  --certificate-oidc-issuer https://token.actions.githubusercontent.com
sha256sum --check --strict SHA256SUMS
```

Keep signature and transparency-log verification enabled. After extracting the
combined release, check each package's own `SHA256SUMS` before extracting its
application archive. Installation still follows the deployment guide, including
the complete encrypted pre-upgrade backup for an initialized instance.

Advisories affecting the bundled runtime, SQLite, parsers or browser dependencies
require an updated build within seven days. Review the failing report, update the
appropriate locked dependency or base-image digest, regenerate affected browser
assets, and rerun deterministic, inventory, scanner and relevant browser/deployment
checks. A failed scan is not permission to disable the gate. Retain the old signed
release for rollback; never reuse its version tag for different bytes.

The mutation corpus uses 257 inputs per format across Markdown, text, HTML, VTT,
SRT, ICS, PDF and DOCX. Its default seed is reproducible; CI also runs it with the
workflow run number as the seed. It checks bounded results and valid segment/page
and calendar contracts. This supplements the focused bomb, descriptor, isolation
and malformed-input tests; it is not a claim of exhaustive parser coverage.
