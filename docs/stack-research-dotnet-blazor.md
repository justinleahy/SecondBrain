# Stack research: C# / .NET 10 with a Blazor front end

| | |
|---|---|
| **Date** | 2026-10-08 |
| **Purpose** | Evaluate C# / .NET 10 + Blazor as the implementation stack for the SecondBrain spec (draft v0.3) |
| **Machine** | .NET SDK 10.0.105 is already installed here, alongside Node 24 and Python 3.12 |

## Verdict

C# / .NET 10 with Blazor is a strong fit for this spec, and in one respect a better fit than TypeScript: the spec's provider-neutral model layer already exists as an official, GA Microsoft abstraction, with an official Anthropic adapter, an OpenAI adapter that also targets OpenAI-compatible servers, and an Ollama adapter. The official MCP C# SDK is on a stable 2.x line, integrates with ASP.NET Core, and lets one tool definition serve both the MCP door and the assistant's own tool loop. SQLite with FTS5 is built into the default .NET SQLite bundle.

The trade-offs are real but bounded: the sqlite-vec packaging for .NET is alpha, in-process local embeddings need a small wrapper, Blazor needs JavaScript interop for a code editor and a PDF viewer, and the OCR and transcription ecosystem is thinner than Python's though adequate through native bindings.

## How the spec maps onto .NET

| Spec component | .NET piece | Status | Notes |
|---|---|---|---|
| Daemon hosting REST, UI, MCP, watchers, workers | ASP.NET Core 10 on Kestrel, `BackgroundService` hosted services, dependency injection | GA, LTS | One process is the idiomatic shape. `IHostedService` for watchers, the job worker pool, and the scheduler. |
| REST with OpenAPI | Minimal APIs + built-in OpenAPI document generation | GA | Problem Details (RFC 9457) is built in. |
| Chat streaming (SSE) | `TypedResults.ServerSentEvents` and `System.Net.ServerSentEvents` | GA in .NET 10 | Native, typed, no SignalR needed for the REST door. |
| Provider-neutral model layer (§13) | `Microsoft.Extensions.AI`: `IChatClient`, `IEmbeddingGenerator<string, Embedding<float>>`, `FunctionInvokingChatClient`, middleware for logging, caching, OpenTelemetry | GA since May 2025, now 10.x | This is the spec's `ChatProvider` / `EmbeddingProvider` / engine-owned tool loop, already written. Capability declaration per binding (§13.3) is still ours to build on top. |
| Anthropic adapter | Official `Anthropic` NuGet package (v10+), `AsIChatClient()` | Official, labeled beta in docs | Plugs into `UseFunctionInvocation` and accepts MCP tools from the C# SDK. |
| OpenAI and OpenAI-compatible adapter | `Microsoft.Extensions.AI.OpenAI` over the official OpenAI .NET SDK with a custom endpoint | GA | Covers Ollama, LM Studio, vLLM, LiteLLM, OpenRouter, and most hosted vendors. |
| Ollama adapter | `OllamaSharp` implements both `IChatClient` and `IEmbeddingGenerator` | Community, mature | Native Ollama API including embeddings. |
| Google adapter | `GeminiDotnet.Extensions.AI` (community) or raw HTTP | Community | Verify before committing. |
| In-process local embeddings | `Microsoft.ML.OnnxRuntime` + `Microsoft.ML.Tokenizers` behind a custom `IEmbeddingGenerator` | Build it | Roughly a few hundred lines. `LLamaSharp` (llama.cpp binding) is the alternative and also does embeddings; it has no confirmed `IChatClient` adapter, so a thin wrapper is needed. |
| Agent loop | `FunctionInvokingChatClient` now; Microsoft Agent Framework 1.0 (GA April 2026) if a richer harness is wanted later | GA | Agent Framework is the Semantic Kernel successor and builds on the same abstractions; not required for v1. |
| MCP server (§7.4) | `ModelContextProtocol` + `ModelContextProtocol.AspNetCore` (stdio and Streamable HTTP, `MapMcp`) | Stable 2.x (Core 2.2.0, Aug 2026) | Tools are `AIFunction`s with schemas generated from method signatures, shared with the assistant. See the protocol note below. |
| SQLite | `Microsoft.Data.Sqlite` with `SQLitePCLRaw.bundle_e_sqlite3` | GA | FTS5, JSON1, and R*Tree are compiled into the default bundle. Dapper or raw SQL for the virtual tables; EF Core only if wanted for the durable state store. |
| Vector search | `sqlite-vec` NuGet (native binaries, 0.1.7-alpha) loaded per connection via `LoadExtension`; `CommunityToolkit.VectorData.SqliteVec` as a packaged alternative | Alpha packaging | Fallback below removes this as a blocker. |
| Markdown | `Markdig` (AST, frontmatter extension) | Mature | Heading-aware chunking from the AST. |
| YAML frontmatter | `YamlDotNet` | Mature | |
| JSON Schema validation for typed properties | `JsonSchema.Net` (json-everything) | Mature | |
| PDF text with positions and pages | `UglyToad.PdfPig` (Apache-2.0) | Mature | Avoid iText (AGPL). |
| DOCX | `DocumentFormat.OpenXml` or the .NET `Mammoth` port | Mature | |
| HTML readability and conversion | `SmartReader` (Readability port) + `ReverseMarkdown`; `HtmlSanitizer` for rendering | Mature | |
| Email (`.eml`, `.mbox`) | `MimeKit` (`MimeParser` handles mbox); `MailKit` for IMAP later | Best in class | Threading via `Message-ID` and `References` is straightforward. |
| Calendar (`.ics`) | `Ical.Net` (recurrence expansion with RRULE and exceptions) | Mature | |
| vCard | `FolkerKinzel.VCards` | Mature | |
| WebVTT / SRT | `SubtitlesParser` or a small hand-written parser | Simple | |
| Transcription (later) | `Whisper.net` (whisper.cpp binding; CoreML runtime on Apple) | 1.8.x | |
| OCR (later) | `Tesseract` .NET wrapper, or a vision model through the provider layer | Adequate | |
| Tokens for chunk sizing | `Microsoft.ML.Tokenizers` | GA | Approximate counts are all chunking needs. |
| File watching | `FileSystemWatcher` (FSEvents on macOS, inotify on Linux) plus the spec's periodic rescan | Built in | Known buffer-overflow and missed-event behavior; the rescan requirement already covers it. |
| CLI | `System.CommandLine` 2.0.x (stable) or `Spectre.Console.Cli` | Stable | |
| Background queue | `System.Threading.Channels` in-process, backed by the spec's SQLite jobs table | Built in | No external queue. |
| Observability | OpenTelemetry (first-class), health checks, structured logging | GA | `OpenTelemetryChatClient` instruments every model call. |
| Tests | xUnit, bUnit for Blazor components, Playwright for UI flows | Mature | |
| Packaging | Self-contained single-file publish per RID (macOS arm64, Linux x64/arm64, Windows), `launchd` / `systemd` units, Docker | GA | Native AOT is not realistic for Blazor Server and reflection-heavy libraries; expect a 70–100 MB self-contained binary and roughly 100–200 MB idle RSS. |

## Protocol note: MCP went stateless

The C# SDK 2.x targets the MCP specification revision dated 2026-07-28, which removed protocol-level sessions: no session header, no GET endpoint or SSE resumption, per-request protocol version and capabilities in `_meta`, `server/discover` for capability lookup, `subscriptions/listen` for change notifications, and server-minted handles passed as ordinary tool arguments for any state that must persist between calls. Roots, sampling, and logging are on a twelve-month deprecation path, and interoperability breaks in both directions across revisions, so the SDK runs discovery first and falls back to the old initialize handshake for down-level peers.

Consequences for the spec, whatever the language:
- §7.4 should describe the HTTP transport as stateless, drop any reliance on session resumption, and note that the SDK handles both revisions during the transition.
- The `mcp:<client name>` provenance from the initialize handshake is weaker than before; the review already flagged it as self-asserted. Provenance should come from the credential.
- Pagination cursors and any multi-call state must be self-contained tokens, which is what the review asked for anyway.

## Vector search without betting on alpha packaging

The spec's hybrid search needs filtered nearest-neighbor search, and the review's finding 22 showed that post-filtering a global top-50 can miss every qualifying record. .NET makes a simple, exact design practical at v1 scale:

1. Run the structured filters in SQL to get candidate chunk ids.
2. Score those candidates in-process with `System.Numerics.Tensors.TensorPrimitives.CosineSimilarity`, which is SIMD-accelerated, over vectors kept in a memory-mapped file keyed by chunk row id.
3. Take the top 50 for fusion.

At 100k chunks of 1024 dimensions this is about 400 MB of vectors and tens of milliseconds per query on a laptop. It is exact, filter-correct, provider-agnostic, and needs no native extension. sqlite-vec or an approximate index becomes an optimization to add when measured scale demands it, behind the same storage interface. This also resolves the review's point about the vector table defaulting to L2 distance.

## Blazor hosting model

| Option | Fit for this app | Trade-offs |
|---|---|---|
| **Blazor Web App, Interactive Server render mode** (recommended for v1) | The daemon serves the UI; components call the engine directly over a SignalR circuit; streaming chat is a loop that updates state. No duplicated API client code. Cookie-based session auth and antiforgery are built in, which matches the review's call for a bounded UI credential instead of an injected API key. | Stateful circuits: a daemon restart drops UI state (the .NET 10 template ships a reconnection UI). Every interaction is a round trip, fine on loopback and acceptable over Tailscale. |
| Blazor WebAssembly | The UI becomes an ordinary REST client, which dogfoods the API contract. Works offline against a cached shell. | Larger initial download, slower startup, a second serialization boundary for every screen, SSE or fetch streaming from the browser. |
| Blazor Hybrid (.NET MAUI) | A native desktop app wrapper. | Adds MAUI and Mac Catalyst tooling for little gain while the daemon already serves a browser UI. Not for v1. |

Rich components need JavaScript interop regardless of mode: Monaco or CodeMirror for the note editor, pdf.js for the PDF viewer with highlight. Component libraries (MudBlazor, Fluent UI Blazor, Radzen) cover the rest. Markdown rendering is Markdig to HTML through `HtmlSanitizer`; original files are served as downloads from a separate path with a strict Content Security Policy, per review finding 4.

## Risks specific to this stack

| Risk | Mitigation |
|---|---|
| sqlite-vec .NET package is alpha | Exact in-process scoring (above) as the v1 default; sqlite-vec optional. |
| No off-the-shelf in-process embedding generator | Small ONNX Runtime wrapper behind `IEmbeddingGenerator`, or require Ollama for local embeddings in v1. |
| Anthropic C# SDK marked beta | It is official; pin versions and cover it with the adapter contract tests like every other adapter. |
| MCP C# SDK is younger than the TypeScript and Python SDKs | Stable 2.x line, Microsoft co-maintained, ASP.NET Core integration; track release notes. |
| Blazor circuit state loss on restart | Keep UI state minimal and recoverable from the engine; conversations are already persisted. |
| JavaScript interop surface for editor and PDF viewer | Use maintained wrappers (BlazorMonaco, pdf.js interop) and keep interop thin. |
| Thinner OCR and transcription ecosystem | Whisper.net and Tesseract bindings exist; both are post-v1 anyway, and the provider layer can route images to a vision model. |
| Self-contained binary size and memory | Acceptable for a personal daemon; document it in §17 with reference hardware. |

## What changes in the spec if this stack is chosen

- §18 is rewritten around .NET packages (the table above), and the Python alternative is dropped or demoted.
- §13 names `Microsoft.Extensions.AI` as the adapter contract, with the capability model layered on it.
- §7.4 is updated for the stateless MCP revision.
- §11 describes the Blazor Web App with Interactive Server rendering, cookie session auth, and a separate download origin for originals.
- §9 and Appendix A adopt the SQL-prefilter plus in-process exact scoring design as the v1 vector path, with sqlite-vec optional.
- §12 standardizes on `System.CommandLine`.
- §17 adds reference hardware and the self-contained binary footprint.

## Sources

- MCP C# SDK: [csharp.sdk.modelcontextprotocol.io](https://csharp.sdk.modelcontextprotocol.io/), [ModelContextProtocol 2.0.0 on NuGet](https://www.nuget.org/packages/ModelContextProtocol/2.0.0), [ModelContextProtocol.Core 2.1.0](https://www.nuget.org/packages/ModelContextProtocol.Core/2.1.0), [C# SDK 2.0 announced](https://www.i-programmer.info/news/89-net/19065-c-sdk-for-model-context-protocol-version-20-announced.html), [transports](https://csharp.sdk.modelcontextprotocol.io/v1/concepts/transports/transports.html)
- MCP 2026-07-28 revision: [changelog](https://modelcontextprotocol.net/specification/2026-07-28/changelog), [Composio summary](https://composio.dev/blog/mcp-2026-07-28-update-statelessness-apps-auth), [Appwrite summary](https://appwrite.io/blog/post/mcp-goes-stateless-in-the-2026-07-28-specification), [Better Stack guide](https://betterstack.com/community/guides/ai/mcp-stateless/)
- Microsoft.Extensions.AI: [GA announcement](https://devblogs.microsoft.com/dotnet/ai-vector-data-dotnet-extensions-ga/), [IChatClient docs](https://learn.microsoft.com/dotnet/ai/ichatclient), [dotnet/extensions 10.8.4](https://newreleases.io/project/github/dotnet/extensions/release/v10.8.4)
- Anthropic C# SDK: [official docs](https://platform.claude.com/docs/cli-sdks-libraries/sdks/csharp), [tryAGI community package](https://github.com/tryAGI/Anthropic)
- SQLite: [sqlite-vec on NuGet](https://www.nuget.org/packages/sqlite-vec/), [CommunityToolkit.VectorData.SqliteVec](https://www.nuget.org/packages/CommunityToolkit.VectorData.SqliteVec), [Microsoft.Data.Sqlite extensions](https://learn.microsoft.com/dotnet/standard/data/sqlite/extensions), [Semantic Kernel SQLite connector](https://learn.microsoft.com/en-us/semantic-kernel/concepts/vector-store-connectors/out-of-the-box-connectors/sqlite-connector), [FTS5 in bundle_e_sqlite3](https://github.com/dotnet/docs/pull/18437/files)
- ASP.NET Core 10 and Blazor: [release notes](https://learn.microsoft.com/aspnet/core/release-notes/aspnetcore-10.0), [ASP.NET Core 10 release](https://infoq.com/news/2025/12/asp-net-core-10-release), [SSE in .NET 10](https://www.milanjovanovic.tech/blog/server-sent-events-in-aspnetcore-and-dotnet-10), [Blazor .NET 10 features](https://cp.adaptivewebhosting.com/knowledgebase/173/Blazor-.NET-10-New-Features.html)
- CLI: [System.CommandLine 2.0.11](https://www.nuget.org/packages/System.CommandLine/2.0.11)
- Local inference: [OllamaSharp docs](https://docsearch.algolia.com/mcp/docs/repo/awaescher/ollamasharp), [LLamaSharp docs](https://docsearch.algolia.com/mcp/docs/repo/scisharp/llamasharp), [ElBruno.LocalLLMs](https://www.nuget.org/packages/ElBruno.LocalLLMs/0.7.0)
- Agent Framework: [Semantic Kernel and Agent Framework](https://devblogs.microsoft.com/semantic-kernel/semantic-kernel-and-microsoft-agent-framework/), [Agent Framework overview](https://atlan.com/know/ai-agent/microsoft/agent-framework/)
- Transcription: [Whisper.net on NuGet](https://www.nuget.org/packages/Whisper.net/1.3.0), [Whisper.net docs](https://docsearch.algolia.com/mcp/docs/repo/sandrohanea/whisper.net)
