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
