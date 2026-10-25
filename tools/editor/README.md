# Local note editor assets

The server serves a pinned Monaco 0.57.0 editor and its worker from `wwwroot/editor`; browsers do not load code from a CDN. The checked-in bundle makes normal .NET builds and deployments independent of npm.

To regenerate after editing the integration:

```sh
npm ci --prefix tools/editor --ignore-scripts
npm run --prefix tools/editor build
```

The lockfile pins dependencies, and `wwwroot/editor/manifest.json` records output hashes and the libraries actually included in the bundle. Preserve all licenses and third-party notices with the bundle.

Monaco 0.57.0 includes a copied DOMPurify 3.4.15 module. The build redirects that exact import to the pinned DOMPurify 3.4.17 npm package, which fixes the upstream advisories; changing the npm lock alone does not replace Monaco's copied code. The build fails if the reviewed import or vendor version changes, or if the old module remains in the output. It also records the vendored Marked 14.0.0 parser and its source hash. The release inventory verifies these dependency declarations against the npm lock before scanning them.

See the [upstream advisory](https://github.com/advisories/GHSA-cg2g-xh64-v4q7) and [3.4.17 release](https://github.com/cure53/DOMPurify/releases/tag/3.4.17). This dependency update does not claim that SecondBrain exposed every vulnerable library configuration.

Monaco uses generated layout attributes and stylesheet rules. The app allows inline **style attributes**, while style elements require a response nonce. A narrowly checked build patch gives Monaco's two style factories that nonce. Inline scripts, `eval`, remote images and remote workers remain forbidden by the application CSP. The build fails if the pinned style factories change unexpectedly.

`NoteEditor.razor` loads the module lazily. It exposes a plain-text mode, retains edits across mode changes, reports dirty state without streaming the document on each keystroke, and reads the current value at save time. Completion requests use authenticated, bounded, same-origin catalog reads.
