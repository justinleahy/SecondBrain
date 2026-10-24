# Open-source "second brain" tools with AI assistants, as of 2026-10-09 (competitors to SecondBrain)

Method note: all GitHub star counts, licenses, archive flags, latest-release tags and dates below were pulled directly from the GitHub REST/GraphQL API on **2026-10-09** (cited as "GitHub API 2026-10-09" with the repo URL). Obsidian plugin download counts were read from the `community-plugin-stats.json` file in `obsidianmd/obsidian-releases` on the same day. Web search quota ran out mid-research, so user-sentiment evidence comes from each project's own issue tracker (top-commented open issues) rather than Reddit/HN; this is flagged in the Gaps sections. Anything dated before 2025 is marked "possibly stale".

---

## Khoj (khoj.dev) — self-hostable "AI second brain" server

### Takeaway
Khoj is a mature (37.6k stars, AGPL-3.0) multi-client Postgres/pgvector RAG server with agents and automations, but its hosted cloud was deprecated on 2026-04-15, its last release is a March-2026 beta, and the company's visible momentum has moved to a new product (Pipali). It is an MCP *client* (agents call MCP servers), not an MCP server, has opt-out telemetry on by default, and shows note-level (not passage-level) references.

### Cited Findings
**What / who / maturity**
- README tagline "Your AI second brain. Self-hostable. Get answers from the web or your docs. Build custom agents, schedule automations, do deep research. Turn any online or local LLM into your personal, autonomous AI"; 37,610 stars, AGPL-3.0, 164 open issues, last push 2026-08-02 — [GitHub API 2026-10-09, khoj-ai/khoj](https://github.com/khoj-ai/khoj)
- Latest release is **2.0.0-beta.28, published 2026-03-26**; notes contain only two fixes, one of them "Fix showing deprecation banner on Khoj cloud" — [Release 2.0.0-beta.28](https://github.com/khoj-ai/khoj/releases/tag/2.0.0-beta.28)
- The web app's deprecation banner source reads: "**Khoj Cloud is being deprecated on April 15, 2026.**" — [deprecationBanner.tsx](https://github.com/khoj-ai/khoj/blob/master/src/interface/web/app/components/deprecationBanner.tsx); commit of 2026-03-26 "Fix getting billing config to show deprecation banner on Khoj cloud" — [commit fdd5fd8f7](https://github.com/khoj-ai/khoj/commit/fdd5fd8f7)
- Company: docs footer "Copyright © 2026 Khoj, Inc."; homepage describes itself as "an applied artificial intelligence company" and "a tiny team of hackers"; homepage now headlines "Pipali Beta — Your AI co-worker. Research, create, and automate" plus "Open Paper", with no pricing and no cloud sunset notice — [khoj.dev](https://khoj.dev/), [docs.khoj.dev](https://docs.khoj.dev/)
- Pipali (separate repo): 308 stars, Apache-2.0, latest 0.10.0 on 2026-09-14, "Get an ai co-worker on your machine. It can read-write files, code safely in sandbox…" — [GitHub API 2026-10-09, khoj-ai/pipali](https://github.com/khoj-ai/pipali)
- README still advertises "Cloud: app.khoj.dev … no setup required" and "Enterprise: available as a cloud service, on-premises, or as a hybrid solution" — contradicted by the deprecation banner above — [README](https://github.com/khoj-ai/khoj)
- Third-party listing claims Khoj Cloud closed 2026-04-15 and "self-hosting is now the only supported way" (secondary; consistent with banner) — [promptquorum review](https://www.promptquorum.com/power-local-llm/khoj-ai-second-brain-review)

**Deployment**
- Install paths: Docker Compose or `pip install 'khoj[local]'`; minimum 8 GB RAM, 16 GB VRAM recommended, 5 GB disk; GPU builds for CUDA, ROCm, Vulkan, Metal; default URL `http://localhost:42110`; "Khoj is only accessible on the machine it is running" by default; `--anonymous-mode` skips login for single-user; reverse-proxy envs `KHOJ_ALLOWED_DOMAIN`, `KHOJ_DOMAIN`, `KHOJ_NO_HTTPS` — [Self-host setup](https://docs.khoj.dev/get-started/setup)
- docker-compose stack: `pgvector/pgvector:pg15` database, `ghcr.io/khoj-ai/terrarium` (code sandbox), `searxng/searxng` (web search), `ghcr.io/khoj-ai/khoj-computer`, `ghcr.io/khoj-ai/khoj`; `OPENAI_BASE_URL=http://host.docker.internal:11434/v1/` example commented in; `# - KHOJ_TELEMETRY_DISABLE=True` commented out — [docker-compose.yml](https://github.com/khoj-ai/khoj/blob/master/docker-compose.yml)
- Clients: "Browser, Obsidian, Emacs, Desktop, Phone or Whatsapp" — [README](https://github.com/khoj-ai/khoj); docs client pages exist for desktop, emacs, obsidian, web, whatsapp — [docs/clients tree](https://github.com/khoj-ai/khoj/tree/master/documentation/docs/clients)
- Obsidian plugin: "By default, your Obsidian vault is automatically synced periodically", "Force Sync" command, chat/search/"Find Similar Notes" inside Obsidian; connects to `Khoj URL` (default `http://127.0.0.1:42110`) with optional API key — [Obsidian client docs](https://docs.khoj.dev/clients/obsidian)

**Storage / ingestion**
- Data lives in Postgres (pgvector image above); pip path uses `USE_EMBEDDED_DB="true"`; uninstall step is `dropdb khoj` — [Setup](https://docs.khoj.dev/get-started/setup)
- Ingests "image, pdf, markdown, org-mode, word, notion files" — [README](https://github.com/khoj-ai/khoj); docs list PDF, plaintext, Markdown, org-mode, Notion; data-source docs exist for GitHub and Notion — [docs overview](https://docs.khoj.dev/), [data-sources tree](https://github.com/khoj-ai/khoj/tree/master/documentation/docs/data-sources)
- Sync: Desktop/Obsidian/Emacs clients keep files/folders synced; Notion syncs from web app; drag-and-drop upload — [Setup](https://docs.khoj.dev/get-started/setup)

**AI features / retrieval / citations**
- Chat "retrieves the notes most relevant to your query using RAG-based search", then sends them with "your last few messages, and associated metadata" to the chat model; commands `/notes` (limit to notes), `/online`, `/image`, `/diagram` (Excalidraw), `/code` (runs Python), `/research` ("Go deeper in a topic"); filters via "conversation file filters" and query filters — [Chat docs](https://docs.khoj.dev/features/chat)
- Citations: "Shows reference notes used to generate a response" (file/note level; passage level not stated) — [Chat docs](https://docs.khoj.dev/features/chat)
- Custom agents "with custom knowledge, persona, chat model and tools"; automations deliver "personal newsletters and smart notifications" — [README](https://github.com/khoj-ai/khoj); feature docs: agents, automations, code_execution, image_generation, online_search, search, voice-chat — [docs/features tree](https://github.com/khoj-ai/khoj/tree/master/documentation/docs/features)
- Reranking exists in code: `cross_encoder` appears in `src/khoj/search_type/text_search.py`, `src/khoj/processor/embeddings.py` and migrations `0026_searchmodelconfig_cross_encoder_inference_endpoint_and_more.py`, `0056_searchmodelconfig_cross_encoder_model_config.py` — [GitHub code search "cross_encoder" in khoj-ai/khoj](https://github.com/search?q=repo%3Akhoj-ai%2Fkhoj+cross_encoder&type=code)

**Model flexibility**
- Commercial providers via `OPENAI_API_KEY`, `ANTHROPIC_API_KEY`, `GEMINI_API_KEY`; local: "run an OpenAI-compatible server (llama.cpp server, Ollama, or vLLM), then add an AI Model API with its URL"; Docker users set `OPENAI_BASE_URL` for "Ollama, vLLM, or LM Studio"; "Vision support is available for OpenAI models only" — [Setup](https://docs.khoj.dev/get-started/setup)

**Agent integration (MCP)**
- Khoj contains an MCP **client**: `class MCPClient: "A client for interacting with MCP servers… Supports both stdio and sse"` — [src/khoj/processor/tools/mcp.py](https://github.com/khoj-ai/khoj/blob/master/src/khoj/processor/tools/mcp.py); DB migration creates model `McpServer` — [0096_mcpserver.py](https://github.com/khoj-ai/khoj/blob/master/src/khoj/database/migrations/0096_mcpserver.py). No file in the repo implements an MCP server (code search for "mcp" returns only the client, research router, migrations, admin, models) — [code search](https://github.com/search?q=repo%3Akhoj-ai%2Fkhoj+mcp&type=code)

**Privacy / telemetry**
- Self-hosted telemetry collects "high level, anonymized metadata about usage of self-hosted Khoj": client used, API usage (Search, Chat), configured content types, request metadata (host, referrer); disable with `KHOJ_TELEMETRY_DISABLE=True` — [Telemetry docs](https://docs.khoj.dev/miscellaneous/telemetry)

**Reported weaknesses (issue tracker)**
- Top-commented open issues include "[FIX] Agents stop outputting 'Generating a well-informed response'" (2025-01-07), "Admin login issue" (2025-05-29), "DeepSeek-compatible OpenAI API fails due to unsupported response_format type" (2026-06-07), "'Aborted' after Offline Model download" (2024-10-08, possibly stale) — [open issues sorted by comments](https://github.com/khoj-ai/khoj/issues?q=is%3Aissue+is%3Aopen+sort%3Acomments-desc)

### Inferences
- Telemetry is on by default for self-hosters (docs describe an opt-out; compose file ships the disable flag commented out).
- "Local-only" is achievable by choosing local models, but nothing enforces it: the stack bundles SearXNG web search and `/online`, and no egress policy or firewall-level control is documented.
- Versus SecondBrain: Khoj is a multi-user Django/Postgres server that copies documents into a database index, whereas SecondBrain keeps a Markdown vault plus disposable SQLite index; Khoj has no typed-document SQL layer (who/when/type questions go through embeddings + filters), references are note-level, there is no proposal/approval gate for writes, and it cannot be mounted as an MCP server by Claude/other agents. Khoj's strengths over SecondBrain's current (foundations-only) state are breadth: WhatsApp/Emacs/Obsidian clients, agents, scheduled automations, image/voice, Notion/GitHub connectors.
- Release cadence (no release since 2026-03-26, last push 2026-08-02, flagship marketing now Pipali) suggests maintenance mode for Khoj proper.

### Gaps
- Whether Khoj can write back to notes/vault (no documentation found either way).
- Passage-level vs note-level citation behaviour is not specified in docs.
- No calendar/transcript ingestion found in docs.
- Could not retrieve a primary blog post announcing the cloud shutdown (only the in-app banner source and release note); exact migration terms unknown.

---

## Obsidian (core app) — official stance on AI, Bases, CLI, Headless, Web Clipper, Sync

### Takeaway
Obsidian's core ships **no AI, LLM or MCP feature** and none appears on its roadmap, changelog or blog; its official AI-adjacent surfaces are the bring-your-own-model Web Clipper "Interpreter" (Nov 2024), a Keychain for plugin secrets (1.11, Jan 2026), a CLI (1.12, Feb 2026) explicitly pitched for "agentic coding tools", and a Headless sync client that can give "agentic tools access to a vault without access to your full computer". Typed metadata is first-class via YAML properties and Bases (Aug 2025+), queryable from the CLI with `base:query`.

### Cited Findings
**Product / company / pricing**
- "Free without limits", no sign-up; Sync $4/user/month annual or $5 monthly; Publish $8/$10; Commercial license $50/user/year; Catalyst $25 one-time; "apps don't collect telemetry", "we never sell user data", Sync AES-256 end-to-end — [obsidian.md/pricing](https://obsidian.md/pricing)
- "Obsidian is now free for work" (2025-02-20): the Commercial license became optional — [Obsidian blog index](https://obsidian.md/blog/)
- Privacy policy (last updated 2023-11-01, possibly stale): "We do not collect any telemetry data"; "All data is saved locally on your device and is never sent to our servers"; Sync is end-to-end encrypted "including file names"; directory plugins must follow Developer policies under which "capturing client-side telemetry data is prohibited" and network usage "must be clearly disclosed" — [obsidian.md/privacy](https://obsidian.md/privacy)
- Terms (last updated 2025-02-20) contain no AI or model-training clauses — [obsidian.md/terms](https://obsidian.md/terms)
- Latest desktop release v1.14.4 published 2026-10-05 in the releases repo (22,104 stars) — [GitHub API 2026-10-09, obsidianmd/obsidian-releases](https://github.com/obsidianmd/obsidian-releases/releases)

**Official AI stance (absence of AI features)**
- Changelog entries 1.13.6 (2026-08-10) through 1.14 (2026-10-05) contain no mention of AI, LLM, MCP or agents — [obsidian.md/changelog](https://obsidian.md/changelog/)
- Roadmap: "No AI, LLM, MCP, or agent items appear"; planned items are Background Sync on mobile, Calendar view for Bases, Multiplayer, PDF annotation, Bases/Canvas support for Publish — [obsidian.md/roadmap](https://obsidian.md/roadmap)
- Blog 2023–Oct 2026 has no AI/LLM/MCP post; relevant posts: "The future of Obsidian plugins" (2026-05-12, new Community site + developer dashboard), "Obsidian Sync audits by Cure53 and Trail of Bits" (2026-05-13), "Less is safer: how Obsidian reduces the risk of supply chain attacks" (2025-09-19) — [obsidian.md/blog](https://obsidian.md/blog/)
- Web Clipper **Interpreter**: natural-language processing of clipped pages; preset providers Anthropic, Azure OpenAI, DeepSeek, Google Gemini, Hugging Face, Meta, Ollama, OpenAI, OpenRouter, Perplexity, xAI Grok, plus custom chat-completions endpoints; "Obsidian does not gather or store any data about your requests"; Ollama runs locally with no API key (needs browser-extension origin allowed; default 2,048-token context) — [Interpreter help](https://obsidian.md/help/web-clipper/interpreter)
- Keychain (1.11, January 2026): "Storage for API keys, tokens, and other secrets used by plugins" — [roadmap](https://obsidian.md/roadmap)

**Bases / properties (typed metadata)**
- Properties shipped August 2023: "New editor for file properties with support for internal links, stored in YAML" (possibly stale date but still current feature) — [roadmap](https://obsidian.md/roadmap)
- Bases shipped August 2025 (1.9): "Create database-like views of your notes that you can edit, sort, and filter"; November 2025: Map view, List view, Group results, Summaries, Bases API ("Allow plugins to create new views types for bases"); February 2026: Bases search; August 2026: Airtable import ("Convert Airtable data to Markdown files and Obsidian Bases"); October 2026 (1.14): Kanban view, create/reorder groups — [roadmap](https://obsidian.md/roadmap), [changelog 1.14.0 Sep 2 2026](https://obsidian.md/changelog/)
- Bases help: "database-like views of your notes"; views saved "as a `.base` file or embedded in code blocks within your Markdown files"; layouts Table, List, Cards, Kanban, Map; formulas "using data from other properties"; data "stored in your local Markdown files and their properties" — [Bases help](https://obsidian.md/help/bases)

**CLI and Headless (server-side / agent access)**
- Obsidian CLI (1.12, February 2026): "A command line interface that lets you control Obsidian from your terminal for scripting, automation, and integration with external tools" — [roadmap](https://obsidian.md/roadmap); requires "Obsidian installer version (1.12.7+)"; "Obsidian must be running. The CLI connects to the running Obsidian instance"; commands include `search`, `create`, `read`, `property:read/set/remove`, `base:query` (json/csv/tsv/md/paths output), `daily:append`, `eval`; developer commands "allow agentic coding tools to automatically test and debug" — [CLI help](https://obsidian.md/help/cli)
- Obsidian Headless (open beta): "a headless client for Obsidian services"; `npm install -g obsidian-headless`, requires Node.js 22+; syncs vaults via Obsidian Sync without the desktop app; use cases include automated backups, scheduled note automations, and "give agentic tools access to a vault without access to your full computer"; no API/MCP of its own — [Headless help](https://obsidian.md/help/headless)
- Web Clipper launched 2024-11-11 ("Save the web"; possibly stale), Reader view March 2026, template logic February 2026 — [blog](https://obsidian.md/blog/), [roadmap](https://obsidian.md/roadmap)

### Inferences
- Obsidian's official position is expressed by omission plus enabling primitives: it stays a local Markdown editor with zero telemetry and BYO-model hooks (Interpreter, Keychain, CLI, Headless) and leaves AI to plugins.
- For an always-on server, Obsidian alone is insufficient: the CLI needs the GUI running, and Headless only syncs files; a daemon like SecondBrain that reads/writes the same Markdown folder is complementary rather than competitive (a user can keep editing in Obsidian while SecondBrain indexes the vault).
- Typed metadata: Obsidian properties + Bases give user-facing typed views and formulas over YAML frontmatter; SecondBrain's typed documents (note/article/file/transcript/calendar event with people and time) answered by SQL is the daemon-side analogue. A SecondBrain vault that writes Obsidian-compatible frontmatter would be queryable by both.

### Gaps
- Could not load the Developer policies page at its old URL (`docs.obsidian.md/Developer+policies` returned "does not exist"); the telemetry/network-disclosure wording above is quoted via the privacy policy.
- No explicit public statement by Obsidian/Steph Ango on AI was retrievable through primary sources with the remaining tool budget (search quota exhausted); the "no AI in core" finding rests on roadmap/changelog/blog absence.
- Obsidian user counts and company size not researched.

---

## Obsidian AI plugins — Smart Connections, Copilot, Smart Composer, Local GPT

### Takeaway
The two dominant AI plugins (Copilot: 2.43M downloads; Smart Connections: 1.23M) are both commercially driven: Copilot v4 (Aug 2026) rewrote itself around running Claude Code/Codex/opencode agents inside Obsidian with a $14.99/month Plus tier, and Smart Connections moved from GPLv3 to a source-available "Smart Plugins License" (Dec 2025) with cloud/local model integrations moved into paid "Pro" plugins, triggering vocal community backlash. Smart Composer is MIT but "not under active development"; Local GPT is small, MIT and actively released. None provides per-tool scopes, approval gates, or degraded-retrieval signalling.

### Cited Findings
**Smart Connections (brianpetro)**
- 5,491 stars; license reported as NOASSERTION; latest 4.7.2 (2026-08-06); last push 2026-10-03; 497 open issues — [GitHub API 2026-10-09](https://github.com/brianpetro/obsidian-smart-connections); 1,231,481 downloads — [community-plugin-stats.json](https://github.com/obsidianmd/obsidian-releases/blob/master/community-plugin-stats.json)
- LICENSE file: "Smart Plugins License Agreement, Copyright (c) 2025, Jobsi, Inc." — MIT-style grant plus clause 2 forbidding use "as a substantial component of any product or service that (a) is marketed for use with, or primarily interoperates with, Obsidian… (b) is offered as a general-purpose solution to multiple unrelated customers… and (c) directly competes with any commercial offering of the Licensor" — [LICENSE](https://github.com/brianpetro/obsidian-smart-connections/blob/HEAD/LICENSE)
- Issue #1293 (opened 2026-01-28, closed, 3 comments) "License change removes open source protections; contributor consent unclear": "the license was changed in commit f411b3e on December 9, 2025 from GPLv3 to a modified version of the MIT license with a noncompete clause"; "previously free features were moved behind a paywall" — [issue #1293](https://github.com/brianpetro/obsidian-smart-connections/issues/1293)
- Issue #1269 (2025-12-12, 30 comments) "Thoughts on recent changes": "The default embedding models are currently required, and local/Ollama usage isn't available"; "Updating the plugin seems to force usage of paid models" — [issue #1269](https://github.com/brianpetro/obsidian-smart-connections/issues/1269)
- README (v4): Connections view (list + graph) and Lookup semantic search; Smart Chat is now a separate plugin and "API-based model integrations, cloud and local, moved into Smart Chat Pro"; core "ships with a local embedding model" and is "Private and offline by default"; Pro (for active supporters) adds inline connections, footer connections, configurable scoring/ranking, "Bases integration", advanced filters/models; third-party sync must ignore `.smart-env/` — [README](https://github.com/brianpetro/obsidian-smart-connections)
- Site: "Local embeddings by default"; "Suggestions don't edit your notes"; Smart Chat Core saves "the link, not a copy of the conversation" in a note; Smart Context lets you "choose and review sources for an AI request"; v4.8.1 referenced; "© 2026 Jobsi, inc." — [smartconnections.app](https://smartconnections.app/)
- Long-lived loading/indexing complaints: "Pane is always loading" (2024-09-16, 39 comments), "Stuck Loading Smart Connections on Linux Obsidian" (2024-09-01, 26), "After the upgrade, it has been stuck here" (2024-03-01, 34), "No result found of my note for possible relationship" (2025-07-10, 39) — [open issues by comments](https://github.com/brianpetro/obsidian-smart-connections/issues?q=is%3Aissue+is%3Aopen+sort%3Acomments-desc) (2024 items possibly stale)

**Copilot for Obsidian (logancyang / Brevilabs)**
- 7,806 stars, AGPL-3.0, latest 4.0.13 (2026-10-02), pushed 2026-10-09, 150 open issues; description "Run agents in Obsidian - OpenCode, Codex, Claude Code etc." — [GitHub API 2026-10-09](https://github.com/logancyang/obsidian-copilot); 2,427,070 downloads — [stats](https://github.com/obsidianmd/obsidian-releases/blob/master/community-plugin-stats.json)
- v4.0.0 (2026-08-11): "Copilot has been rewritten end to end. Instead of a single built-in assistant, frontier agents — Claude Code, Codex, and opencode — now run natively inside Obsidian: they plan, search your vault by meaning, use tools, and write results back to your notes" — [release 4.0.0](https://github.com/logancyang/obsidian-copilot/releases/tag/4.0.0)
- README: Agent mode is desktop-only ("backends run local processes"); Quick Chat, Quick Ask, Projects; "local Miyo search" indexes stay on device; BYOK cloud/local/OpenAI-compatible; Brevilabs receives data "only when you use a hosted feature or upload a diagnostic report"; random UUID for rate limiting; frontend open source, "backend services for hosted features are closed source"; "Free opencode Zen models may log or train on prompts"; agent can create files "with permissions you control" — [README](https://github.com/logancyang/obsidian-copilot)
- Pricing: Free $0 (BYOK/subscription/local models; Agents, Project Mode, semantic search); Plus $14.99/month or $139.99/year; Supporter $349.99 one-time (2 years Plus, Miyo Lifetime, OpenArtifacts Lifetime, "lifetime self-host mode"); 14-day refunds — [obsidiancopilot.com/en/pricing](https://www.obsidiancopilot.com/en/pricing)
- Miyo: "free desktop app that searches the vault by meaning", local indexing of notes plus outside PDFs/EPUBs; local models via "LM Studio, Ollama, and any OpenAI-compatible endpoint" through opencode; no citation feature described beyond wiki-links in results — [obsidiancopilot.com](https://www.obsidiancopilot.com/)

**Smart Composer (glowingjade)**
- 2,330 stars, MIT, latest 1.2.9 (2026-01-26), last push 2026-02-16 — [GitHub API 2026-10-09](https://github.com/glowingjade/obsidian-smart-composer); 175,650 downloads — [stats](https://github.com/obsidianmd/obsidian-releases/blob/master/community-plugin-stats.json)
- README: "not under active development" (single maintainer; community forks listed); chat with `@file` context, one-click apply edits, Vault Search (RAG); providers OpenAI, Anthropic, Gemini, Groq, DeepSeek, OpenRouter, Azure, Ollama, LM Studio, MorphLLM, OpenAI-compatible; chat can connect to external MCP servers (MCP client); warns Anthropic restricted third-party OAuth in January 2026 and "some users reported bans"; PDF/DOCX context "coming soon" — [README](https://github.com/glowingjade/obsidian-smart-composer)

**Local GPT (pfrankov)**
- 677 stars, MIT, latest 4.3.6 (2026-10-08), 1 open issue — [GitHub API 2026-10-09](https://github.com/pfrankov/obsidian-local-gpt); 100,796 downloads — [stats](https://github.com/obsidianmd/obsidian-releases/blob/master/community-plugin-stats.json)
- Action-on-selection assistant (no chat UI described); Ollama and "OpenAI-like" endpoints via a required AI Providers plugin; "Enhanced Actions" pull context from links, backlinks and PDFs using embeddings (`nomic-embed-text`, `bge-m3`); image input (bakllava demo); pitched for "maximum privacy and offline access" — [README](https://github.com/pfrankov/obsidian-local-gpt)

### Inferences
- Retrieval in all four is similarity-first (local embeddings; Copilot's Miyo; Smart Composer RAG; Local GPT link/backlink context); none offers typed-metadata SQL queries, though Smart Connections Pro advertises a Bases integration.
- Write safety ranges from "suggestions don't edit" (Smart Connections) to agents that "write results back to your notes" (Copilot v4); approval is per-agent-runtime (Claude Code/Codex permission prompts), not a vault-level proposal queue.
- Versus SecondBrain: these are desktop-session tools tied to one machine; they do not expose the vault to external agents (except via separate MCP plugins, next section) and have no egress manifest or enforced local-only mode. SecondBrain's differentiators remain the daemon model, citations resolving to revisions, degraded-retrieval reporting and proposal approvals.

### Gaps
- Smart Connections Pro supporter price not found on the site or README.
- Copilot's privacy FAQ answer ("Where do my notes go when I use hosted models?") was not captured.
- Where Smart Composer/Copilot store embeddings on disk is not documented in the fetched pages.
- No Reddit/forum sentiment captured (search quota).

---

## Obsidian MCP servers — Local REST API (built-in MCP), mcp-obsidian, obsidian-mcp-tools and successors

### Takeaway
As of Oct 2026 the Local REST API plugin (777k downloads, v5.4.0) ships its own Streamable-HTTP MCP server with full read/write/delete tools behind a single API key, making wrapper servers like `mcp-obsidian` (4.5k stars, no releases) redundant; `obsidian-mcp-tools` was archived 2026-05-13 and succeeded by `istefox/obsidian-mcp-connector` (64 tools, on-device embeddings, per-client tokens). All require the Obsidian desktop app to be running except filesystem-direct servers (cyanheads, seekstone).

### Cited Findings
- Local REST API: 3,010 stars, MIT, latest 5.4.0 (2026-10-06); description "A secure REST API and Model Context Protocol (MCP) server for your vault" — [GitHub API 2026-10-09](https://github.com/coddingtonbear/obsidian-local-rest-api); 777,605 downloads — [stats](https://github.com/obsidianmd/obsidian-releases/blob/master/community-plugin-stats.json)
- README: MCP server at `https://127.0.0.1:27124/mcp/` (Streamable HTTP, `Authorization: Bearer <api-key>`; optional plain HTTP :27123); says third-party Obsidian MCP servers are "no longer necessary"; tools `vault_list/read/read_binary/write/append/patch/delete (trash by default)/move/copy/get_document_map`, `search_simple` (Obsidian fuzzy full-text), `search_query` (JsonLogic over frontmatter/tags/path/content), `tag_list`, `command_execute`, `open_file`; no read-only mode or scopes; `.obsidian` blocked by default; 10 bad credentials/minute → 429; signed URLs 300 s — [README](https://github.com/coddingtonbear/obsidian-local-rest-api)
- Earliest releases whose notes mention MCP: 4.0.0 (2026-05-15), 4.0.3 (2026-05-21), 4.1.1 (2026-05-27) — [releases](https://github.com/coddingtonbear/obsidian-local-rest-api/releases)
- mcp-obsidian (MarkusPfundstein): 4,467 stars, MIT, no GitHub releases, last push 2026-08-31, 104 open issues; tools `list_files_in_vault`, `list_files_in_dir`, `get_file_contents`, `search`, `patch_content`, `append_content`, `delete_file`; depends on the Local REST API plugin; pinned to `mcp` SDK 1.x because its handler API "was removed in `mcp` 2.0" — [GitHub API 2026-10-09 and README](https://github.com/MarkusPfundstein/mcp-obsidian)
- obsidian-mcp-tools (jacksteamdev): archived 2026-05-13T16:21:58Z, 829 stars, MIT, last release 0.2.33 — [GitHub GraphQL/API 2026-10-09](https://github.com/jacksteamdev/obsidian-mcp-tools)
- Successor istefox/obsidian-mcp-connector (plugin id `mcp-tools-istefox`): 98 stars, MIT, 3.0.1 (2026-10-08); README: "64 tools", "Semantic search is on-device. Transformers.js runs the embedding model locally. No API key, no Smart Connections requirement", per-client bearer tokens and tool profiles, broker at `http://127.0.0.1:27200/v1/<route-id>/mcp`, "Five releases followed a code and protocol audit (docs/audit-2026-10-02.md)… writes got safer" — [README](https://github.com/istefox/obsidian-mcp-connector); 43,891 downloads — [stats](https://github.com/obsidianmd/obsidian-releases/blob/master/community-plugin-stats.json)
- Other in-app MCP plugins and downloads (2026-10-09): `semantic-vault-mcp` (aaronsb/obsidian-mcp-plugin) 27,876; `vault-as-mcp` (ebullient/obsidian-vault-mcp, "read, search, and edit operations") 10,076; `local-rest-api-second-brain-mcp-extension` 6,199; `cli-rest-mcp` 5,017; `agent-mcp` ("Run coding agents (Claude Code, Codex, Ollama) inside Obsidian") 1,209; `claude-sidebar` ("Run Claude Code in your sidebar") 85,085; `claude-code-ide` 18,638 — [stats](https://github.com/obsidianmd/obsidian-releases/blob/master/community-plugin-stats.json), [community-plugins.json](https://github.com/obsidianmd/obsidian-releases/blob/master/community-plugins.json)
- Filesystem-direct servers (no app needed): cyanheads/obsidian-mcp-server 695 stars, Apache-2.0, v3.7.0 (2026-10-06), "Read, write, search, and surgically edit Obsidian vault notes, tags, and frontmatter via MCP. STDIO or Streamable HTTP" — [GitHub API 2026-10-09](https://github.com/cyanheads/obsidian-mcp-server); shaqmughal/seekstone 30 stars, MIT, "needs no plugin, no running Obsidian app — and doesn't blow your context window" — [GitHub API 2026-10-09](https://github.com/shaqmughal/seekstone)
- GitHub search "obsidian mcp server" (sorted by stars) lists >12 active repos in 2026 — [search](https://github.com/search?q=obsidian+mcp+server&type=repositories&s=stars&o=desc)

### Inferences
- The Obsidian MCP niche is crowded and churning (one leading server archived in May 2026, another unreleased, the REST plugin absorbing the function). Authorization is coarse: one vault-wide API key (Local REST API) or per-client tokens with tool profiles (istefox); nothing matches SecondBrain's per-tool scopes plus human-approved write proposals.
- Search exposed to agents is Obsidian's fuzzy full-text or JsonLogic metadata filters (REST API) or on-device embeddings (istefox/semantic-vault); none reports degraded retrieval.
- Server reachability: in-app servers bind to 127.0.0.1 on the desktop; reaching them from a phone requires the desktop to be on plus a tunnel — the opposite of SecondBrain's always-on daemon.

### Gaps
- Download counts are cumulative lifetime figures; no active-user data.
- No security audit of the Local REST API MCP beyond the README's own notes.

---

## Reor — local-first AI note app (archived)

### Takeaway
Reor (8.5k stars, AGPL-3.0) is a Markdown-on-disk desktop app with LanceDB vector search, auto-linking and RAG Q&A over local Ollama/Transformers.js models, but the repo was **archived on 2026-03-07** with its last release in April 2025; it is no longer a live competitor.

### Cited Findings
- 8,544 stars, AGPL-3.0, archived (`archivedAt` 2026-03-07T22:59:21Z), last push 2025-05-13, latest release v-0.2.32 (2025-04-05), 127 open issues — [GitHub API/GraphQL 2026-10-09](https://github.com/reorproject/reor)
- README: desktop app for Mac/Linux/Windows; notes are markdown files in one chosen directory; "each note is chunked and embedded into an internal vector database" (LanceDB); related notes auto-linked by vector similarity; RAG Q&A; semantic search; Ollama for local LLMs, Transformers.js embeddings; any OpenAI-compatible API (Oobabooga, Ollama, OpenAI); limitations: frontmatter "may not parse correctly", import by copying files; no MCP/API/citation feature described; the only status note is a Discord announcement that the team is "shipping very quickly" — [README](https://github.com/reorproject/reor)
- LinuxLinks review (secondary, undated) reports first-run choice of vault, embedding model and LLM, and warns the chat "can state false information confidently" — [LinuxLinks](https://linuxlinks.com/machine-learning-linux-reor-ai-note-taking-app/2)

### Inferences
- Reor validated the "plain Markdown + local vectors" shape SecondBrain also uses, but as a single-machine Electron app with similarity-only retrieval, no typed metadata, no citations, no agent interface, and now no maintenance.

### Gaps
- No announcement explaining the archival or naming a successor was found.

---

## SurfSense — open-source NotebookLM alternative (pivoted to a local desktop app, Sept 2026)

### Takeaway
SurfSense (16.3k stars) abandoned its hosted/connector-heavy web product in September 2026 for an "air-gapped" desktop app with a SQLite index under `~/.surfsense`, bundled llama.cpp models, no telemetry, and an outbound "egress panel" that is off by default; cloud data is deleted on 2026-10-18. Connectors are being reborn as paid "scraper plugins" that run against SurfSense's servers; the licence is Apache-2.0 with a BUSL-1.1 proprietary directory.

### Cited Findings
- 16,340 stars, license NOASSERTION, latest v2.1.0 (2026-10-04), pushed 2026-10-09, 91 open issues; description "Air gapped, privacy focused open source NotebookLM alternative" — [GitHub API 2026-10-09](https://github.com/MODSetter/SurfSense)
- LICENSE: "All content that resides under the `surfsense_backend/app/proprietary/` directory… is licensed under the Business Source License 1.1"; README states Apache-2.0 for the rest — [LICENSE](https://github.com/MODSetter/SurfSense/blob/HEAD/LICENSE), [README](https://github.com/MODSetter/SurfSense)
- Releases: v2.0.0 (2026-09-18) "[release] 2.0 local desktop", "wind down the hosted service behind SUNSET_MODE", Keygen offline licences + Stripe, "API keys kept in the Keychain with egress consent", "gate egress-sensitive actions"; v2.0.3 (2026-09-26) "replace Ollama with a bundled llama.cpp runtime, a curated GGUF catalog", local image generation via sd-server, Kokoro default podcast voice; v2.1.0 (2026-10-04) plugin system (SDK, author CLI, per-process plugins), images to vision models, direct Anthropic connection, egress grants withdrawn when a host's last connection is deleted — [releases](https://github.com/MODSetter/SurfSense/releases)
- README (2.x): installers for Windows x64, macOS Apple Silicon, Linux (AppImage/deb); Docker stack "community-supported: no SLA and no hosted service behind it"; "The desktop index is SQLite under `~/.surfsense`"; PDFs, Office files and images parsed locally; "every answer cites the source it came from"; Studio outputs: summary, flashcards, quiz, mind map, pptx, docx, xlsx, web page, PDF, podcast (Kokoro-82M offline), image, infographic; "Any OpenAI-compatible API, or a local one"; Qwen3 "in six sizes (from 0.5 GB)"; the app "does not upload any of it", "Outbound connections are off by default, with an egress panel", "No telemetry or crash reporting", secrets in OS keychain; MCP server in `surfsense_mcp` "for the hosted scraper API"; hosted web app export window closes 18 October 2026 — [README](https://github.com/MODSetter/SurfSense)
- Sunset page: "SurfSense is moving to a local app"; cloud is export-only; "18 October 2026: Everything stored in the cloud is deleted"; export ZIP contains markdown documents, folder structure and chat threads but "does not include original uploads, generated artifacts, tool calls, agent steps, or live citation links" — [surfsense.com/sunset](https://www.surfsense.com/sunset)
- Pricing: Free desktop app $0 ("No account, no expiry", 30-day plugin licence by email); Individual licence $60/year first year then $120/year adding "every scraper plugin (nine platforms plus the web crawler)" and priority support; Enterprise custom with "No egress at all: licensing and plugins mirrored inside your network"; scraper plugins "run against SurfSense's servers"; "No plugin ships in version 2.0" — [surfsense.com/pricing](https://www.surfsense.com/pricing)
- Docs site: "Docs in progress"; "2.0 is a rebuild, not an upgrade" of 0.0.40; 0.0.40 docs (Docker stack, connectors, messaging channels, local models) "shouldn't be assumed to apply to 2.0" — [surfsense.com/docs](https://www.surfsense.com/docs)
- Pre-pivot (0.0.x, possibly stale) descriptions: "two-tier RAG pipeline", connectors to Slack, Linear, Jira, ClickUp, Confluence, Notion, YouTube, GitHub, Discord — [rohitraj.tech comparison (secondary, 2026)](https://rohitraj.tech/en/notes/open-notebook-vs-khoj-vs-surfsense-notebooklm-2026)
- Top open issue: "No Studio format passes a JSON schema, so compliance rests on the prompt" (2026-09-28) — [issues](https://github.com/MODSetter/SurfSense/issues?q=is%3Aissue+is%3Aopen+sort%3Acomments-desc)

### Inferences
- SurfSense 2.x is the closest open-source product to SecondBrain's *privacy posture* (egress off by default, explicit per-destination grants, keychain secrets, no telemetry) but implemented as a desktop app with a UI toggle rather than a policy-owned transport plus firewall canary; "zero egress" is only promised in the Enterprise tier.
- It is a research notebook (sources → chat/studio artifacts), not a vault: no plain-Markdown source of truth, no typed documents, and the export omits citation links — weak portability.
- The connector story reversed: previously self-hosted connectors, now paid cloud-run scrapers.

### Gaps
- No 2.x documentation of the retrieval pipeline (hybrid/RRF/reranker) or MCP tool list; "Known gaps" list referenced by the README was not retrievable.
- Current version of the Docker stack and whether multi-user/RBAC survived the pivot are undocumented.

---

## Open Notebook (lfnovo) — self-hosted NotebookLM alternative

### Takeaway
Open Notebook (40.0k stars, MIT, v1.15.0 on 2026-10-04, roughly monthly releases) is a Docker-deployed research server on SurrealDB with 24 model providers, full-text + vector search, cited answers, a REST API and an MCP server package; its weaknesses are DB-bound storage (no Markdown vault), default no-password exposure, and a cluster of high/critical security advisories in April 2026.

### Cited Findings
- 39,995 stars, MIT, latest v1.15.0 (2026-10-04), pushed 2026-10-05, 154 open issues — [GitHub API 2026-10-09](https://github.com/lfnovo/open-notebook)
- Deployment: Docker Compose with two services, SurrealDB and Open Notebook (UI :8502, API :5055, background worker); "the UI… and API… are exposed on all interfaces with no password by default"; no hosted/desktop/mobile version mentioned — [README](https://github.com/lfnovo/open-notebook)
- Storage: SurrealDB; export formats not described — [README](https://github.com/lfnovo/open-notebook)
- Ingestion: PDFs, videos, audio, web pages, Office documents; v1.13.0 (2026-07-14) rebuilt extraction on Content Core 2.0 with opt-in Docling (OCR/layout) and Crawl4AI, EPUB and Reddit, pdfplumber; v1.15.0 added OpenDocument uploads — [README](https://github.com/lfnovo/open-notebook), [releases](https://github.com/lfnovo/open-notebook/releases)
- AI features: context-aware chat, AI-assisted notes ("Generate insights or write notes manually"), multi-speaker podcasts (1–4 speakers), transformations, search ("full-text and vector"); "Answers include source citations"; "Choose exactly what to share with AI models" — [README](https://github.com/lfnovo/open-notebook)
- Providers: Esperanto library, "20+" / 24 entries incl. OpenAI, Anthropic, Google GenAI, Mistral, Groq, Ollama, LM Studio (OpenAI-compatible), Azure; v1.14.0 added Cohere, Novita, Deepgram STT, oMLX; Ollama `num_ctx` default lowered to 8192 (v1.9.0); model calls time out after 180 s (v1.15.0, "Ollama waited indefinitely" before) — [README](https://github.com/lfnovo/open-notebook), [releases](https://github.com/lfnovo/open-notebook/releases)
- REST API at `localhost:5055/docs`; `GET /api/providers` (v1.12.0), `GET /api/capabilities` (v1.13.0) — [README](https://github.com/lfnovo/open-notebook), [releases](https://github.com/lfnovo/open-notebook/releases)
- MCP: separate `open-notebook-mcp` package run via `uvx open-notebook-mcp` with `OPEN_NOTEBOOK_URL`/`OPEN_NOTEBOOK_PASSWORD`; lets assistants "Access your notebooks", "Search your research content", "Create and manage chat sessions", "Generate notes", "Automate workflows using the full Open Notebook API" — [docs/5-CONFIGURATION/mcp-integration.md](https://github.com/lfnovo/open-notebook/blob/HEAD/docs/5-CONFIGURATION/mcp-integration.md); VISION.md lists agents operating Open Notebook through MCP as a future direction — [README](https://github.com/lfnovo/open-notebook)
- Security advisories (published 2026-04-07/09): critical "Remote Code Execution via Jinja2 Server-Side Template Injection in transformations"; high "Arbitrary file read via Local File Inclusion in source creation"; high "Arbitrary file write via path traversal in file upload"; high "SurrealDB injection via unsanitized order_by parameter" — [security advisories](https://github.com/lfnovo/open-notebook/security/advisories); v1.11.0 (2026-07-11) "Security pass with 16 fixes" incl. SSRF protection and constant-time password comparison; v1.14.0 DNS-pinning; v1.15.0 PBKDF2-HMAC-SHA256 credential encryption — [releases](https://github.com/lfnovo/open-notebook/releases)
- Privacy: "with local models nothing leaves your machine"; pricing "Pay only for AI usage"; multi-user is a future direction — [README](https://github.com/lfnovo/open-notebook)

### Inferences
- Always-on server, reachable from any device via browser, with MCP and REST — structurally similar to SecondBrain's daemon — but knowledge is locked in SurrealDB, retrieval is vector + text without typed filters, and "local only" is a provider choice rather than an enforced policy.
- Citations are present but their resolution granularity is undocumented; no write-approval mechanism exists (the AI and MCP can "Generate notes" directly).

### Gaps
- CVE-2026-33587 (mentioned by a third-party listing) could not be verified against the repo's advisories, which carry no CVE IDs.
- Export/portability format and citation granularity not documented in fetched pages.

---

## Karakeep (formerly Hoarder) — self-hosted bookmark/note archive with AI tagging

### Takeaway
Karakeep (29.6k stars, AGPL-3.0, v0.33.2 on 2026-08-11) is a web-archive style "bookmark everything" server on SQLite + Meilisearch with LLM auto-tagging/summaries/OCR via OpenAI-compatible or Ollama endpoints, experimental hybrid semantic search (0.33.1), a first-party 29-tool MCP server, scoped API keys, mobile apps and a $4/month cloud tier; it is not a Markdown vault and has no chat/QA.

### Cited Findings
- 29,551 stars, AGPL-3.0, latest v0.33.2 (2026-08-11), pushed 2026-10-07, 694 open issues; "A self-hostable bookmark-everything app (links, notes and images) with AI-based automatic tagging and full text search" — [GitHub API 2026-10-09](https://github.com/karakeep-app/karakeep)
- Previously called Hoarder; Chrome/Firefox/Safari extensions; iOS/Android apps with offline reading; auto-hoarding from RSS; importers for Chrome, Pocket, Linkwarden, Omnivore, Tab Session Manager; monolith full-page archiving, yt-dlp video archiving; rule engine; REST API and CLI "friendly to LLM agents, with official skills"; read-only demo at try.karakeep.app — [docs intro](https://docs.karakeep.app/)
- Storage/config: `DATA_DIR` "where the db lives" (SQLite; `DB_WAL_MODE` option), `ASSETS_DIR`, Meilisearch via `MEILI_ADDR` ("If not set, Search will be disabled") — [environment variables doc](https://github.com/karakeep-app/karakeep/blob/HEAD/docs/docs/03-configuration/01-environment-variables.md)
- AI config: "Either `OPENAI_API_KEY` or `OLLAMA_BASE_URL` need to be set for automatic tagging"; `OPENAI_BASE_URL` for OpenAI-compatible APIs; `INFERENCE_ENABLE_AUTO_TAGGING` default true, `INFERENCE_ENABLE_AUTO_SUMMARIZATION` default false; `SEMANTIC_SEARCH_ENABLED` default true "**Experimental.**"; `EMBEDDING_TEXT_MODEL` default `text-embedding-3-small`, `EMBEDDING_DIMENSIONS` 1536; `EMBEDDING_ENABLE_AUTO_INDEXING` "enabled by default if you're using the default OpenAI settings… Otherwise, you need to manually enable it"; default text model listed as `gpt-6-luna` (another row says "5.6 luna") — [environment variables doc](https://github.com/karakeep-app/karakeep/blob/HEAD/docs/docs/03-configuration/01-environment-variables.md); provider guide shows Ollama with `gemma3`/`llava`/`embeddinggemma`, Gemini, OpenRouter, Perplexity examples — [different AI providers doc](https://github.com/karakeep-app/karakeep/blob/HEAD/docs/docs/03-configuration/02-different-ai-providers.md)
- Releases 2026: 0.30.0 (Jan 1) PDF archives (`CRAWLER_STORE_PDF`), notes in edit dialog, per-user AI toggles, managed cloud "public beta"; 0.31.0 (Feb 22) LLM-based OCR alternative to Tesseract, revamped import, tag suggestions limited to existing tags; 0.32.0 (May 8) mobile redesign, Safari extension, "official agent skill" on ClawHub/skills.sh, CLI `karakeep auth init`, "granular API key scopes", yearly cloud subscriptions; 0.33.1 (Aug 1) "Full text, Semantic, and Hybrid" search modes — "Semantic search is still new and experimental, so results might not be super accurate yet", MCP server "from 8 to 29 tools", readable-content API returning markdown/plain text in paginated chunks, mobile offline reading; 0.33.2 (Aug 11) custom Chrome image, SingleFile upload — [releases](https://github.com/karakeep-app/karakeep/releases)
- MCP server tools: `search-bookmarks`, `get-bookmark`, `get-bookmark-content`, `create-bookmark`, `update-bookmark`, `delete-bookmark`, list CRUD, tag CRUD, highlight CRUD, `get-asset`; "Currently, the MCP server only exposes tools (no resources)" — [apps/mcp/README.md](https://github.com/karakeep-app/karakeep/blob/HEAD/apps/mcp/README.md)
- API keys: "Per-scope permissions: no access, read-only, or read & write"; first-party MCP server, CLI, REST API, webhooks, agentic skills — [karakeep.app](https://karakeep.app/)
- Cloud pricing: Free $0 (10 bookmarks, 20 MB); Pro $4/month or $40/year (50,000 bookmarks, 50 GB, AI tagging, full-text search); Corporate custom; self-hosted "Free forever… includes all features, and is open source"; 7-day money-back — [karakeep.app/pricing](https://karakeep.app/pricing/)
- Complaints: "[BUG] Memory leak in 0.30 (with Pangolin) - RAM usage continuously growing over time" (2026-01-04, 55 comments); "Failed to connect to the browser instance, will retry in 5 secs" (2024-11-19, 26 comments, possibly stale); feature requests for VNC login to the crawler browser and Reddit full-page archiving — [open issues by comments](https://github.com/karakeep-app/karakeep/issues?q=is%3Aissue+is%3Aopen+sort%3Acomments-desc)

### Inferences
- Karakeep is the best-engineered *ingestion* competitor (extensions, mobile share, RSS, importers, archiving) but stores everything in SQLite/Meilisearch/asset blobs; export is API-driven, not a readable Markdown folder.
- Default AI configuration points at OpenAI; Ollama is supported but semantic indexing must be enabled manually for non-OpenAI providers — local-only is configurable, not enforced; no telemetry statement found.
- Its scoped API keys (read-only vs read&write) and MCP delete tools are coarser than SecondBrain's per-tool scopes and proposal approvals; there is no QA/chat or citation layer.

### Gaps
- No telemetry documentation found in the config docs.
- Hybrid ranking method (fusion, reranking) not documented.

---

## Basic Memory (Basic Machines) — Markdown + SQLite + MCP memory for Claude/Codex/Cursor

### Takeaway
Basic Memory (4.1k stars, AGPL-3.0, v0.23.2 on 2026-08-25, ~monthly releases) is the closest architectural cousin to SecondBrain: plain Markdown files with frontmatter plus a local SQLite index, hybrid full-text + FastEmbed vector search with optional cross-encoder reranking that "fails loudly", and a rich MCP tool set with read-only/destructive annotations; it differs by being an agent-memory layer (no ingestion pipeline, no typed documents), by giving the AI unguarded edit/delete tools (approval left to the client), and by default telemetry to Umami.

### Cited Findings
- 4,123 stars, AGPL-3.0, latest v0.23.2 (2026-08-25), pushed 2026-10-09, 63 open issues; "AI conversations that actually remember" — [GitHub API 2026-10-09](https://github.com/basicmachines-co/basic-memory); release cadence v0.21.5 (2026-05-26), v0.22.0 (06-11), v0.22.1 (06-13), v0.23.0 (08-24), v0.23.1/2 (08-25) — [releases](https://github.com/basicmachines-co/basic-memory/releases)
- Storage: Markdown files with frontmatter (title, type, permalink, tags) plus "Observations" (`- [category] fact #tag`) and "Relations" (wikilinks) forming a graph; local SQLite index; Postgres supported with optional Milvus vectors; `doctor` command checks file/DB consistency; humans and AI edit the same files; Obsidian reads them directly — [README](https://github.com/basicmachines-co/basic-memory)
- Search: "Hybrid full-text plus vector ranking, using FastEmbed embeddings"; optional cross-encoder reranking (local `jina-reranker-v1-tiny-en` default or LiteLLM), off by default, "fails loudly instead of falling back silently"; v0.23.0 "repair FTS half of hybrid search for natural-language queries" — [README](https://github.com/basicmachines-co/basic-memory), [v0.23.0](https://github.com/basicmachines-co/basic-memory/releases/tag/v0.23.0)
- MCP tools: `write_note`, `read_note`, `edit_note`, `move_note`, `delete_note`, `read_content`, `view_note`, `search_notes`, `recent_activity`, `list_directory`, `build_context` (navigates `memory://` URLs), project tools, `schema_infer/validate/diff`, `search`, `fetch`; tools carry MCP hints (read-only, destructive, idempotent, open-world); clients Claude Desktop, Claude Code, Codex CLI, Cursor, VS Code, ChatGPT (Custom GPT actions); transports stdio (local) and https (cloud) — [README](https://github.com/basicmachines-co/basic-memory)
- Write safety: AI can write, edit, move, delete; "`write_note` guards against accidental overwrites"; no built-in approval gate — "Codex still asks before destructive-annotated tools… unless you pre-approve" — [README](https://github.com/basicmachines-co/basic-memory)
- Cloud: hosted version with web/mobile/desktop access, snapshots, rclone bidirectional sync; "$15.00/mo locked for life during beta (regular price $19; yearly $12.50/mo)", 7-day trial; built on WorkOS AuthKit, Neon Postgres, Tigris S3; local cross-device sync is manual (Git/Syncthing) — [README](https://github.com/basicmachines-co/basic-memory)
- Telemetry: anonymous events to Umami Cloud (cloud promo impressions, login attempts/outcomes, promo opt-outs); "file contents and note data are not collected"; opt out with `BASIC_MEMORY_NO_PROMOS=1` (disables promos and all telemetry); auto-update every 24 h for uv/Homebrew installs — [README](https://github.com/basicmachines-co/basic-memory)
- Open issues are mostly feature work (man pages for `bm` CLI, Pi/Hermes integrations, "Git-based undo/recovery system for operations" 2025-06-04) — [open issues by comments](https://github.com/basicmachines-co/basic-memory/issues?q=is%3Aissue+is%3Aopen+sort%3Acomments-desc)

### Inferences
- Basic Memory's "fail loudly" reranker and tool annotations are the nearest existing analogue to SecondBrain's degraded-retrieval reporting and per-tool scopes, but its scope model is advisory (hints) rather than enforced per key.
- Not an always-on server unless you pay for cloud; the local server is a stdio process on the machine where the files live. There is no inbox/watched-folder ingestion, no PDF/transcript/calendar typing, and who/when questions rely on frontmatter + search rather than SQL over typed documents.

### Gaps
- Citation granularity in `build_context`/`search_notes` responses (whole note vs excerpt) not documented in fetched material.
- Whether FTS uses SQLite FTS5 specifically is not stated.

---

## Brief notes: Trilium (TriliumNext), SiYuan, AFFiNE, Anytype, Logseq, Memos

### Takeaway
Among the general PKM apps, SiYuan (built-in agent + semantic search + OAuth-2.1 MCP server since mid-2026) and Trilium (rebuilt LLM chat + built-in MCP server since v0.103.0, May 2026) now expose their databases to external agents with no fine-grained permissions; Logseq's DB version (2.0 beta, July 2026) moved from Markdown files to SQLite with an export-only "Markdown Mirror" and an MCP server; AFFiNE and Anytype sell AI/sync subscriptions on partly source-available code; Memos has no AI at all.

### Cited Findings
**Trilium Notes (TriliumNext)**
- 38,261 stars, AGPL-3.0, latest v0.106.0 (2026-09-25), 640 open issues — [GitHub API 2026-10-09](https://github.com/TriliumNext/Trilium); AI was removed in v0.102.0 (2026-03-01) and reintroduced in v0.103.0 (2026-05-13) — [releases](https://github.com/TriliumNext/Trilium/releases), [LLM docs](https://docs.triliumnotes.org/user-guide/llm)
- LLM docs: "Trilium ships no model of its own"; providers Anthropic, OpenAI, Google Gemini, DeepSeek (API), Claude Code (subscription), Ollama and LM Studio (local), custom OpenAI-compatible (OpenRouter, Groq, Mistral); tools let the assistant search notes, read metadata/content, edit (rewrite/find-replace/append), create, rename, delete, manage attributes, move/clone; "Edits save a revision that can be reverted"; "Currently there is no permission management implemented for note tools"; per-conversation "Note access" toggle; PDF/image attachments sent natively; with local models "nothing leaves the machine" — [LLM docs](https://docs.triliumnotes.org/user-guide/llm)
- Built-in MCP server since v0.103.0, off by default, HTTP transport only ("`stdio` method is not supported"), bearer auth via an ETAPI token, exposes "the same tools that are supported by the internal chat"; "Trilium's AI implementation is still experimental" — [docs/User Guide/AI/MCP.md](https://github.com/TriliumNext/Trilium/blob/HEAD/docs/User%20Guide/User%20Guide/AI/MCP.md)
- Storage is a server-side database with sync server, Markdown import/export, Android (TriliumDroid) and iOS (Trinote) clients — [README](https://github.com/TriliumNext/Trilium); community MCP servers perfectra1n/triliumnext-mcp (71 stars) and tan-yong-sheng/triliumnext-mcp (69) — [GitHub search](https://github.com/search?q=trilium+mcp&type=repositories&s=stars&o=desc)

**SiYuan**
- 46,699 stars, AGPL-3.0, latest v3.8.6 (2026-09-29), 17 open issues; description now "An open-source, privacy-first, self-hosted knowledge workspace where humans and AI agents work together" — [GitHub API 2026-10-09](https://github.com/siyuan-note/siyuan)
- v3.7.0 (2026-06-30): "SiYuan Agent and vector embedding search have entered public testing"; v3.8.0 (2026-08-12): "Semantic search powered by embeddings and reranking… the new AI Agent lets you work with your knowledge in natural language… MCP support lets external AI assistants…"; v3.8.6: "The built-in MCP server now supports OAuth 2.1, and workspace AGENTS.md files can provide custom instructions for the AI agent. Agent and MCP tools can manage Bazaar packages"; Oct 2026 alphas add ChatGPT account sign-in for the agent, AI OCR, Xiaomi MiMo preset — [releases](https://github.com/siyuan-note/siyuan/releases)
- Storage: documents saved as `.sy` JSON files in notebook folders plus a SQLite index ("A copy of the live SQLite index alone is insufficient" for backup); export "Standard Markdown with assets", PDF, Word, HTML; "Import Markdown file is not supported" in Docker; platforms desktop, iOS/Android/HarmonyOS, Docker/Kubernetes/Unraid/TrueNAS (browser-only); "AI writing and Q/A chat via OpenAI API"; "Some features are only available to paid members" — [README](https://github.com/siyuan-note/siyuan)
- Pricing: Free; PRO Features $64 lifetime (S3/WebDAV sync); Subscription $148 lifetime (8 GB cloud, Cloud Inbox); "Refunds are not available"; page footer © 2023 (possibly stale) — [b3log.org pricing](https://b3log.org/siyuan/en/pricing.html)

**AFFiNE**
- 73,346 stars, license NOASSERTION, latest v0.27.4 (2026-08-18), 776 open issues — [GitHub API 2026-10-09](https://github.com/toeverything/AFFiNE); LICENSE: `packages/backend` and `packages/common/native` fall under `packages/backend/server/LICENSE`, the rest MIT — [LICENSE](https://github.com/toeverything/AFFiNE/blob/HEAD/LICENSE); backend licence is "The AFFiNE Enterprise Edition (EE) license… may only be used in production, if you… have a valid AFFiNE Enterprise Edition subscription" — [backend LICENSE](https://github.com/toeverything/AFFiNE/blob/HEAD/packages/backend/server/LICENSE); README still says "AFFiNE Community Edition (CE)… free for self-host under the MIT license" and "EE is yet to be published" — contradiction — [README](https://github.com/toeverything/AFFiNE)
- Pricing (USD, "AFFiNE Pricing 2026"): Free; Pro $6.75/month; Team $10/seat/month annual; Believer $499.99 one-time; **AFFiNE AI add-on $8.90/month billed annually** (writing, summarizing, mind maps, presentations beta; "Auto-sorting and auto-tagging… coming soon"); "The editor and most of the codebase are MIT-licensed. The backend uses the source-available EE license"; whether AI works self-hosted is not stated — [affine.pro/pricing](https://affine.pro/pricing)
- Local-first via Yjs/y-octo CRDT (OctoBase) — [README](https://github.com/toeverything/AFFiNE)

**Anytype**
- anytype-ts client: 8,890 stars, latest v0.57.4 (2026-10-03); licence "Any Source Available License 1.0" (not OSI); "Offline-first, local storage with optional peer-to-peer sync", "Zero-knowledge encryption powered by any-sync", gRPC API — [GitHub API 2026-10-09 and README](https://github.com/anyproto/anytype-ts)
- Official MCP server `@anyproto/anytype-mcp`: 529 stars, MIT, v2.0.1 (2026-09-19); converts Anytype's OpenAPI spec into MCP tools; connects to the local API `http://127.0.0.1:31009` (or `anytype-cli` on 31012) with an API key created in App Settings; CRUD on objects/lists/spaces/types, global search; spec loaded once per process — [anytype-mcp README](https://github.com/anyproto/anytype-mcp)
- AI: v0.57.0 (2026-09-22) ships "import v2" with an "AI provider dropdown", a "BYOK disclosure" and a fix to "make the AI disclosure accurate about where data goes" — i.e. AI-assisted import with user-supplied keys — [v0.57.0 release](https://github.com/anyproto/anytype-ts/releases/tag/v0.57.0)
- Docs: "Everything you create lives on your device first… You can work offline, sync across your own devices, or self-host your data"; AnySync is "an open-source protocol" — [doc.anytype.io](https://doc.anytype.io/)

**Logseq**
- 45,188 stars, AGPL-3.0, latest 2.0.2 Beta (2026-10-07), 993 open issues — [GitHub API 2026-10-09](https://github.com/logseq/logseq); 2.0.1 Beta (2026-07-13) "Logseq 2.0 Beta (DB version) is here!… please back up your important data before testing" — [releases](https://github.com/logseq/logseq/releases)
- Split announcement (2026-04-24): "Logseq OG" (file-based, github.com/logseq/og) "will continue to be maintained" with security/dependency fixes and "no new features"; "Markdown will always be supported through Logseq OG"; DB graph "runs exclusively on your local device"; Sync is E2EE; "Model Context Protocol (MCP) server for desktop and CLI" listed among DB improvements; self-hosted sync; sponsors ($15/mo Open Collective) get Sync/TestFlight invites — [logseq.io/p/e3YDyX5AYr](https://logseq.io/p/e3YDyX5AYr)
- May 16 2026 update: "DB graphs live in SQLite"; Markdown Mirror is opt-in, writes a markdown projection and the two-way version is "not yet shipped"; `qmd query` CLI for agents and a `logseq-answer-machine` skill; typed/asset properties — [discuss.logseq.com](https://discuss.logseq.com/t/whats-new-with-logseq-db-may-16th-2026/35020); MCP server code at `src/electron/electron/mcp_server.cljs` — [code search](https://github.com/search?q=repo%3Alogseq%2Flogseq+mcp&type=code)

**Memos**
- 63,647 stars, MIT, latest v0.31.0 (2026-09-19) — [GitHub API 2026-10-09](https://github.com/usememos/memos); "Write in Markdown", "zero telemetry", "full export", Chrome/Firefox web clipper; no AI features in README — [README](https://github.com/usememos/memos); storage drivers `mysql`, `postgres`, `sqlite` (content in database, not files) — [store/db](https://github.com/usememos/memos/tree/HEAD/store/db); community MCP servers are tiny (≤6 stars) — [GitHub search](https://github.com/search?q=memos+mcp&type=repositories&s=stars&o=desc)

### Inferences
- SiYuan and Trilium are the general PKM apps moving fastest toward "agent-operable knowledge base", but both grant the agent/MCP the full tool set with coarse auth (SiYuan OAuth 2.1; Trilium ETAPI bearer) and no approval workflow; both store notes in app-specific formats (.sy JSON / SQLite) with Markdown only as export.
- Logseq's DB pivot moves it away from the plain-Markdown camp SecondBrain sits in; its Markdown Mirror is export-only.
- AFFiNE's and Anytype's AI are subscription or BYOK features on partly source-available code; neither documents local-model support in the fetched sources.

### Gaps
- Anytype pricing tiers and any in-app assistant could not be retrieved (site is JS-rendered; docs index not fetched).
- AFFiNE self-host AI configuration docs returned HTTP 403.
- SiYuan: which model providers the agent supports and whether AI features are member-only were not found on the pricing page.
- Logseq MCP server tool list and auth not documented in fetched sources.

---

## Other 2025–2026 "second brain + AI + MCP" projects (GitHub sweep)

### Takeaway
A GitHub search on 2026-10-09 shows the "second brain MCP" space is a long tail of tiny (≤20-star) hobby servers wrapping Obsidian vaults; the only large adjacent projects are AnythingLLM (66.9k, local-first RAG/agent app with MCP-client support and PostHog telemetry) and Quivr (39.6k), which has pivoted away from the second-brain positioning. No open-source project combining a Markdown vault, typed metadata queries, an always-on daemon, scoped MCP tools and write approvals was found.

### Cited Findings
- GitHub search "second brain mcp" by stars: rahilp/second-brain-obsidian-plugin 17 stars ("self-hosted Second Brain MCP server on Cloudflare Workers"), CoMfUcIoS/second-brain-mcp 13 ("read-only access to your Obsidian vault"), alexsmirnov/obsidian-second-brain-mcp 10, vinay3254/Second-Brain-MCP 8 ("KuzuDB for the wikilink graph"), noesskeetit/second-brain-mcp 7 ("semantic memory for any coding agent", pushed 2026-10-09), bitbemol/second-brain-mcp 6 (Swift, GPL-3.0), ddmanyes/second-brain-mcp 6 ("DuckDB, and biological memory models") — [search](https://github.com/search?q=second+brain+mcp&type=repositories&s=stars&o=desc)
- "personal knowledge mcp server": NocturnLabs/opencode-personal-knowledge 15 stars; kimonus/knowledge-vault-mcp 0 stars ("PostgreSQL, pgvector") — [search](https://github.com/search?q=personal+knowledge+mcp+server&type=repositories&s=stars&o=desc); "markdown vault llm" returned no repositories — [search](https://github.com/search?q=markdown+vault+llm&type=repositories)
- Registry listings (secondary): `@remember-md/mcp` v0.1.0, MIT, single `search_brain` tool, "no cloud calls and no telemetry" — [socket.dev](https://socket.dev/npm/package/@remember-md/mcp); qingleiw/secondbrain-mcp exposes list/search/read/append over a Markdown folder — [glama.ai](https://glama.ai/mcp/servers/qingleiw/secondbrain-mcp); Hjarni is a hosted (non-OSS) knowledge base with built-in MCP — [hjarni.com](https://hjarni.com/blog/mcp-second-brain)
- AnythingLLM: 66,856 stars, MIT, v1.17.0 (2026-10-01); desktop app or Docker; providers incl. Ollama, LM Studio, LocalAI, OpenAI, Anthropic, Gemini, Bedrock; embedders incl. native, Ollama, LM Studio; LanceDB default vector DB; agents + "MCP-compatibility"; multi-user (Docker only); source citations in chat; telemetry to PostHog (install type, document add/remove events, provider/model tag, chat-sent events) disabled via `DISABLE_TELEMETRY` — [GitHub API 2026-10-09 and README](https://github.com/Mintplex-Labs/anything-llm)
- Quivr: 39,579 stars, MIT, latest core-0.0.33 (2025-02-04, possibly stale); description now "An open-source engine that turns continuous content streams into search and monitoring" — [GitHub API 2026-10-09](https://github.com/QuivrHQ/quivr)
- Obsidian "Claude Code inside the vault" plugins are a 2026 trend: `claude-sidebar` 85,085 downloads, `claude-code-ide` 18,638, `agent-mcp` 1,209 — [stats](https://github.com/obsidianmd/obsidian-releases/blob/master/community-plugin-stats.json)

### Inferences
- The market signal is that users want agents (Claude Code/Codex) *inside* their Markdown vault; the infrastructure gap is a trustworthy, always-on, scoped broker between vault and agents — the role SecondBrain targets.

### Gaps
- Star counts for small repos are too low to indicate adoption; no download/usage data.
- Registry pages (glama/socket) are secondary; licences for several tiny servers unverified.

---

## Cross-cutting answers to the key questions

### Takeaway
Plain-Markdown-on-disk knowledge is kept only by Obsidian (plus its plugins), Basic Memory, Reor (dead) and Logseq OG; everything that runs as an always-on server (Khoj, Open Notebook, Karakeep, Trilium, SiYuan/Docker, Memos, AFFiNE) stores knowledge in a database. Typed metadata with real queries exists in Obsidian (properties/Bases/CLI `base:query`), Logseq DB and SiYuan databases, not in the RAG tools, which are similarity-first. MCP exposure is now common (Local REST API, Basic Memory, Karakeep, Open Notebook, Trilium, SiYuan, Anytype, Logseq) but authorization is coarse and writes are unguarded; no tool enforces local-only at the transport layer, and SurfSense's UI egress panel is the nearest equivalent.

### Cited Findings
- **Plain Markdown vs database**: Obsidian "data is stored in your local Markdown files and their properties" — [Bases help](https://obsidian.md/help/bases); Basic Memory "Knowledge is stored as Markdown files on disk" — [README](https://github.com/basicmachines-co/basic-memory); Reor notes are markdown files — [README](https://github.com/reorproject/reor); Logseq DB "graphs live in SQLite", Markdown Mirror export-only — [forum](https://discuss.logseq.com/t/whats-new-with-logseq-db-may-16th-2026/35020); Khoj Postgres/pgvector — [docker-compose](https://github.com/khoj-ai/khoj/blob/master/docker-compose.yml); Open Notebook SurrealDB — [README](https://github.com/lfnovo/open-notebook); Karakeep SQLite + Meilisearch — [env docs](https://github.com/karakeep-app/karakeep/blob/HEAD/docs/docs/03-configuration/01-environment-variables.md); SurfSense SQLite index under `~/.surfsense` — [README](https://github.com/MODSetter/SurfSense); SiYuan `.sy` JSON + SQLite — [README](https://github.com/siyuan-note/siyuan); Memos mysql/postgres/sqlite — [store/db](https://github.com/usememos/memos/tree/HEAD/store/db)
- **Always-on server vs desktop**: Khoj default `localhost:42110` web server — [setup](https://docs.khoj.dev/get-started/setup); Open Notebook Docker UI :8502/API :5055 — [README](https://github.com/lfnovo/open-notebook); Karakeep Docker with mobile apps — [docs](https://docs.karakeep.app/); Trilium server + sync — [README](https://github.com/TriliumNext/Trilium); SurfSense moved to desktop, Docker "community-supported" — [README](https://github.com/MODSetter/SurfSense); Obsidian CLI "must be running… connects to the running Obsidian instance" — [CLI help](https://obsidian.md/help/cli), Headless syncs without the app — [Headless help](https://obsidian.md/help/headless); Copilot Agent mode "desktop-only" — [README](https://github.com/logancyang/obsidian-copilot); Basic Memory mobile "requires cloud" — [README](https://github.com/basicmachines-co/basic-memory); Reor desktop only — [README](https://github.com/reorproject/reor)
- **Typed metadata / queries**: Obsidian Bases formulas/filters and CLI `base:query` with json/csv output — [Bases help](https://obsidian.md/help/bases), [CLI help](https://obsidian.md/help/cli); Local REST API `search_query` JsonLogic over frontmatter/tags/path — [README](https://github.com/coddingtonbear/obsidian-local-rest-api); Logseq DB typed/asset properties — [forum](https://discuss.logseq.com/t/whats-new-with-logseq-db-may-16th-2026/35020); Basic Memory `schema_infer/validate/diff` — [README](https://github.com/basicmachines-co/basic-memory); Khoj uses file/query filters over RAG — [chat docs](https://docs.khoj.dev/features/chat); Karakeep tags/lists/rules — [releases](https://github.com/karakeep-app/karakeep/releases)
- **MCP exposure**: Local REST API built-in MCP — [README](https://github.com/coddingtonbear/obsidian-local-rest-api); Basic Memory — [README](https://github.com/basicmachines-co/basic-memory); Karakeep 29 tools — [apps/mcp README](https://github.com/karakeep-app/karakeep/blob/HEAD/apps/mcp/README.md); Open Notebook `open-notebook-mcp` — [doc](https://github.com/lfnovo/open-notebook/blob/HEAD/docs/5-CONFIGURATION/mcp-integration.md); Trilium built-in (HTTP only) — [MCP.md](https://github.com/TriliumNext/Trilium/blob/HEAD/docs/User%20Guide/User%20Guide/AI/MCP.md); SiYuan built-in with OAuth 2.1 — [releases](https://github.com/siyuan-note/siyuan/releases); Anytype official — [anytype-mcp](https://github.com/anyproto/anytype-mcp); Logseq "MCP server for desktop and CLI" — [post](https://logseq.io/p/e3YDyX5AYr); Khoj is an MCP client only — [mcp.py](https://github.com/khoj-ai/khoj/blob/master/src/khoj/processor/tools/mcp.py); Smart Composer and AnythingLLM are MCP clients — [Smart Composer](https://github.com/glowingjade/obsidian-smart-composer), [AnythingLLM](https://github.com/Mintplex-Labs/anything-llm); SurfSense MCP only "for the hosted scraper API" — [README](https://github.com/MODSetter/SurfSense)
- **Citation precision**: Khoj "Shows reference notes used" — [chat docs](https://docs.khoj.dev/features/chat); SurfSense "every answer cites the source it came from" — [README](https://github.com/MODSetter/SurfSense); Open Notebook "Answers include source citations" — [README](https://github.com/lfnovo/open-notebook); AnythingLLM "source citations" — [README](https://github.com/Mintplex-Labs/anything-llm); Copilot no dedicated citation feature — [site](https://www.obsidiancopilot.com/); Smart Connections "Smart Context lets you choose and review sources" — [site](https://smartconnections.app/); none documents revision-level or excerpt-level resolution
- **Zero egress, enforced vs configurable**: SurfSense "Outbound connections are off by default, with an egress panel", Enterprise "No egress at all" — [README](https://github.com/MODSetter/SurfSense), [pricing](https://www.surfsense.com/pricing); Obsidian "We do not collect any telemetry data" — [privacy](https://obsidian.md/privacy); Khoj telemetry opt-out `KHOJ_TELEMETRY_DISABLE` — [telemetry](https://docs.khoj.dev/miscellaneous/telemetry); Basic Memory telemetry opt-out `BASIC_MEMORY_NO_PROMOS=1`, auto-updates — [README](https://github.com/basicmachines-co/basic-memory); AnythingLLM PostHog `DISABLE_TELEMETRY` — [README](https://github.com/Mintplex-Labs/anything-llm); Trilium "nothing leaves the machine" with local providers — [LLM docs](https://docs.triliumnotes.org/user-guide/llm); Memos "zero telemetry" — [README](https://github.com/usememos/memos); Karakeep defaults to OpenAI unless Ollama configured — [env docs](https://github.com/karakeep-app/karakeep/blob/HEAD/docs/docs/03-configuration/01-environment-variables.md)
- **Write safety**: Local REST API MCP `vault_write/patch/delete`, single key, no read-only mode — [README](https://github.com/coddingtonbear/obsidian-local-rest-api); Basic Memory edit/delete with client-side approval only — [README](https://github.com/basicmachines-co/basic-memory); Trilium "no permission management implemented for note tools" but revisions — [LLM docs](https://docs.triliumnotes.org/user-guide/llm); Karakeep MCP `delete-bookmark`, API scopes read-only/read&write — [MCP README](https://github.com/karakeep-app/karakeep/blob/HEAD/apps/mcp/README.md), [karakeep.app](https://karakeep.app/); Copilot agents "write results back to your notes" — [release 4.0.0](https://github.com/logancyang/obsidian-copilot/releases/tag/4.0.0); Smart Connections "Suggestions don't edit your notes" — [site](https://smartconnections.app/)
- **User complaints (issue trackers, 2025–2026)**: Smart Connections paywall/licence and loading hangs — [#1293](https://github.com/brianpetro/obsidian-smart-connections/issues/1293), [#1269](https://github.com/brianpetro/obsidian-smart-connections/issues/1269); Karakeep memory leak — [issues](https://github.com/karakeep-app/karakeep/issues?q=is%3Aissue+is%3Aopen+sort%3Acomments-desc); Open Notebook critical/high advisories — [advisories](https://github.com/lfnovo/open-notebook/security/advisories); Khoj agent/offline-model failures — [issues](https://github.com/khoj-ai/khoj/issues?q=is%3Aissue+is%3Aopen+sort%3Acomments-desc); Smart Composer "not under active development" and Anthropic OAuth bans — [README](https://github.com/glowingjade/obsidian-smart-composer); abandoned projects: Reor (archived 2026-03-07), obsidian-mcp-tools (archived 2026-05-13), Khoj Cloud (deprecated 2026-04-15), SurfSense cloud (deleted 2026-10-18)

### Inferences
- SecondBrain's combination (Markdown vault + SQLite state + rebuildable index + typed SQL + hybrid FTS5/vector with RRF and degraded-status reporting + revision-resolving citations + proposal-gated writes + per-tool MCP scopes + transport-enforced local-only + sandboxed parsing + always-on home server) has no single open-source equivalent; each dimension exists somewhere (Basic Memory's loud-failure reranker, SurfSense's egress gating, Obsidian's typed Bases, Karakeep's scoped keys, Trilium's revisions) but never together.
- The strongest competitive pressure on SecondBrain comes from the "Obsidian + Claude Code/Codex inside the vault" pattern (Copilot v4, claude-sidebar, Local REST API MCP), which gives users agentic vault editing today without a daemon — at the cost of coarse permissions, desktop-only reachability and no retrieval guarantees.

### Gaps
- Reddit/HN/forum sentiment was not collected (web-search quota exhausted mid-task); complaints above are from issue trackers only.
- Indexing speed and embedding cost figures were not found in primary sources for any tool.
- Passage-level citation behaviour was undocumented for every tool; a hands-on test would be required.
