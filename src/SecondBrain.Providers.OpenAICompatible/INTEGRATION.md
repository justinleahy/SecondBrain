# Lane B integration

`AddDomain()`, `AddProviders()` and `AddPrivacy()` register the lane's services. No
provider, endpoint or model role is supplied by default. Required `chat`, `enrich`
and `embed` bindings must resolve before startup; `rerank` is optional.

## Configuration and CLI (lane C)

- Use `ProviderRegistry.ValidateConfiguration(options, ModelCatalog, credentialResolver)` and
  `PrivacyPolicy.ValidateConfiguration(options)` before publishing a reload to
  `IOptionsMonitor<SecondBrainOptions>`. They throw on invalid candidates. Accepted
  `OnChange` notifications replace bindings and DNS pins. The loader owns retaining
  the previous options on rejection; any unapproved current options fail closed.
  The registry also registers its pure validation with the privacy policy, so
  rejected provider reloads cannot partially publish new privacy pins.
- Model names have no built-in limits or capabilities, including `mock-chat`, `gpt-4o`,
  and familiar embedding names. Production YAML must declare limits for every binding.
  Reviewed programmatic registrations use `Register(provider, model, capabilities, limits)`
  and apply only to that exact pair; provider aliases and adapter kinds confer no metadata.
  Enrichment/rerank models can supply explicit limits. Unknown chat models
  also need a reviewed capability declaration in configuration, for example
  `models.chat.capabilities: { tools: true, streaming: true }` (also
  `structured_output`). Unset capabilities stay unsupported; tools are required for
  chat and never inferred from an endpoint or provider. A declaration for a model in
  the adapter catalog must agree with that catalog entry, or validation rejects it.
  Fallbacks carry their own declarations. Unknown embedding models need all three
  embedding limits. `dimensions` overrides the selected embedding dimensions.
- Replace `IProviderCredentialResolver` with the configuration lane's secret
  resolver before calling `AddProviders()` when secrets may come from files. The
  default resolves `${NAME}` environment references and refuses literal keys.
- `IProviderRegistry.ProviderNames`, `GetProvider(name)` and `ListModelsAsync()`
  implement provider listing. `TestAsync()` returns timestamped reachability for
  every configured role. These checks use the policy-owned transport.

## Readiness and HTTP (lane D)

- `IPrivacyReadiness.GetReadiness()` returns `IsReady`, `LocalOnly`, `CanaryEnabled`,
  `CanaryState`, `LastCheckedAt`, `ProblemType` and a redacted detail. Include it in
  `/ready` and `brain doctor`. A reachable or unknown enabled canary blocks provider
  calls under `local_only`; hosted mode only reports the canary state.
- Use `IProviderRegistry.TestAsync()` for provider reachability and apply the
  five-minute freshness requirement when caching results for `/ready`.
- Map `PrivacyPolicyException.ProblemType` to the stable `privacy-policy` or
  `egress-unverified` problem URI. `ProviderRequestException` exposes a problem URI,
  HTTP status (when supplied), and `Retryable` flag. Neither includes provider
  response bodies or secrets in its diagnostic text.

## Transport and deployment

`IPolicyHttpClientFactory` is the sole provider HTTP factory in DI. One daemon-owned
`SocketsHttpHandler` disables proxies, redirects and cookies. Every request checks
the live role/configuration, request origin and canary, then resolves and checks
  DNS on every send. `ConnectCallback` resolves again, re-evaluates policy after DNS
  and connects only to checked addresses. After the connect, and again before every
  write on the connection, it checks the dialed address against the current pins and
  re-evaluates the writing request's policy, so a repin accepted while a connect is in
  flight cannot send to a removed address. This adapter currently sends exactly
  HTTP/1.1 because its checks are written per connection and per request: HTTP/3
  bypasses `ConnectCallback`, and HTTP/2 multiplexing does not fit the per-request
  policy context those checks read. Either protocol would need its own reviewed
  checks; this is a constraint of the current adapter, not of HTTP/2. Socket lifetime
  is zero so an accepted DNS repin cannot reuse a socket to the previous address;
  this deliberately gives up keep-alive until a pool-generation API is available.
  Adapter clients cannot expose vendor SDK clients or accept raw SDK option callbacks.

Model discovery reads at most 8 MiB of `/models` response, 10,000 entries and
512-character IDs, within the discovery client's timeout including the body. Larger
responses are `provider-malformed` failures.

The adapter disables SDK retries. Retry admission belongs to the engine; each
future admitted retry still crosses the same transport and fresh policy check.

See [Core privacy deployment note](../SecondBrain.Core/Privacy/DEPLOYMENT.md) for
the separate DNS and Access certificate control-plane allowances. The host firewall
remains lane C's deployment responsibility.

The mock has [standalone and in-process instructions](../../tests/SecondBrain.MockProvider/README.md).
Live-provider qualification and future batch/rerank adapters are outside this lane's
mock-based M0 verification.

## Convergence checklist for the orchestrator

The existing server `SmokeTests.DaemonHealthReturnsOk` starts without options. It
must inject configured role bindings (for example, the mock fixture), because an
unconfigured daemon is now correctly rejected at startup. Lane B leaves that
lane-D-owned test unchanged.

The approved Core Options reference also requires regenerated project-dependency
metadata in `src/SecondBrain.{Cli,Extractor,Storage}/packages.lock.json` and
`tests/SecondBrain.Storage.Tests/packages.lock.json`. Run `dotnet restore
--force-evaluate` after merging the lanes, then stage those lane-owned lock files.
No package versions change. Lane B commits only its own and explicitly approved
project/lock files.

After the clean-architecture refactor, Core's packages reach the lock files of every
project that references Core, including `src/SecondBrain.Infrastructure/packages.lock.json`;
`src/SecondBrain.Extractor` no longer references Core. Regenerate with
`dotnet restore SecondBrain.slnx --force-evaluate` and commit every changed lock file.

## M4 native adapters

OllamaSharp 5.5.0 and Anthropic 13.3.0 stay in this provider assembly. Both receive an explicitly supplied HttpClient over the shared policy transport. Model discovery is restricted to each binding's exact read-only URL. Native response bodies have a 32 MiB bound and requests have a 30-second deadline. Caller model, token-budget and raw-option overrides cannot bypass the binding.

Ollama uses its native chat, embedding and model-list APIs. Embeddings request no truncation and verify count, dimensions and finite values. Their protocol identity differs from compatible embeddings at the same endpoint. Follow the SDK's [external HttpClient configuration](https://github.com/awaescher/OllamaSharp/blob/main/docs/advanced-configuration.md).

Anthropic uses the official SDK's IChatClient integration. SDK retries and telemetry are disabled. Explicit credentials, including explicit null, prevent environment/profile credential discovery. A final header filter removes SDK environment headers and host metadata and installs only the configured authentication and required protocol headers. The pinned SDK uses API version `2026-10-21`; model discovery uses the same version and bearer authentication. The adapter waits for `message_stop`, preserves neutral tools and citation annotations, and maps cache-read/cache-creation counts into total input usage. A declared `prompt_caching` capability places an ephemeral breakpoint after the final leading system text block without mutating caller history. SDK exceptions are sanitized before leaving the adapter. See the [pinned SDK integration source](https://github.com/anthropics/anthropic-sdk-csharp/blob/a0cdd4c2cd909c5ae29ed939a8ad6e1568c0377a/src/Anthropic/AnthropicClientExtensions.cs) and [client options](https://github.com/anthropics/anthropic-sdk-csharp/blob/a0cdd4c2cd909c5ae29ed939a8ad6e1568c0377a/src/Anthropic/Core/ClientOptions.cs).

Native Anthropic chat supports an explicit `native_citations` capability. Captured, hash-verified passages become inline plain-text documents with citations enabled; document indexes map only to that request's registered sources. Buffered text citations and streamed citation deltas retain their native range data. The adapter accepts only character ranges with a unique exact quote match under UTF-16 or Unicode-scalar indexing, then normalizes them to UTF-16 offsets relative to the captured excerpt. Unsupported, mismatched or ambiguous ranges cannot create evidence links. Generic SDK annotations are never evidence authority. See the [native citation contract](https://platform.claude.com/docs/en/build-with-claude/citations).

The server sends each captured body once per request, including tool continuations, and counts those bytes before reserving usage. Core verifies source hashes and every exact span, removes model-written numeric markers and bounds quote count/bytes. Storage revalidates spans before settling the answer. `ChatCitation.spans` preserves quote text, offsets and individual SHA-256 hashes alongside the original passage snapshot; `granularity` distinguishes span and passage citations. Existing revision/deletion states and purge lineage apply to both. The browser and CLI display exact quotes when present. Structured-output response formats are rejected when native citation documents are present. Application schema remains 8. These working-tree adapters have not been deployed.

## Native OpenAI batch lifecycle

`kind: openai` adds the native `IBatchProvider` lifecycle to the existing pinned OpenAI 2.14.0 SDK chat/embedding integration. Its experimental Batch API diagnostic is suppressed only in `OpenAINativeBatch.cs`. Every upload, submission, lookup, poll, cancellation, download and deletion uses policy-owned HTTP with retries disabled. Inputs, response bodies, line count, per-line size, identifiers, expected output ids and operation deadlines are bounded. The implementation follows the [official Batch guide](https://developers.openai.com/api/docs/guides/batch) and [create reference](https://developers.openai.com/api/reference/resources/batches/methods/create).

`EnrichmentBatchProcessor` persists an intent before each potentially ambiguous external operation. OpenAI metadata reconciles a lost submission response; the deterministic upload filename supports upload recovery. A missing lookup never authorizes resubmission. At most one batch is active, with up to sixteen automatically queued documents. Explicit requests use the individual worker. A lifecycle-only binding can finish or cancel existing work after the enrich model changes but cannot admit fresh inference.

Schema 8 adds non-content batch metadata. Request and result bodies carry document lineage in ordinary payload storage, so purge removes them. Startup preserves batch-owned job leases and unsettled reservations; other abandoned work retains its earlier recovery behavior. Terminal batches release payload references for retention. Provider changes and refused cleanup retain sanitized audit metadata and remote ids.

## Native Anthropic batches

Anthropic 13.3.0 implements the same batch port through `Messages.Batches`. Staging is a deterministic local marker, with no upload. Inline submission uses opaque 64-character ids derived from the submission key and local request id. The core retains the original mapping. Declared prompt caching and adaptive reasoning are applied, and unsupported schema size constraints are retained as descriptions while core result validation still enforces the original bounds.

Collection opens the SDK's fixed results route through its raw response API, reads bounded JSONL and maps per-item success, error, cancellation, expiration and missing results. The provider's `results_url` is not followed. Native usage includes cache creation and cache reads; missing input is not treated as zero. Finished native batches can be deleted, and a repeated deletion tolerates a 404.

The [native create contract](https://platform.claude.com/docs/en/api/messages/batches/create) and [batch guide](https://platform.claude.com/docs/en/build-with-claude/batch-processing), checked against the pinned SDK source, provide no batch-level client metadata lookup or submission idempotency guarantee. A known id resumes; an ambiguous create is never replayed automatically or reconciled by scanning unrelated result contents. The shared worker's 48-hour unconfirmed-submission outcome preserves the charge and audit record. Generic SDK documentation suggesting batch-level metadata was not supported by the native contract and is not used.
