# Commercial (cloud, closed-source) PKM and "AI second brain" products vs SecondBrain — state as of 2026-10-09

Scope note: this file covers only commercial/cloud products. Obsidian, Logseq and other open-source tools are covered by another researcher. Every price and figure is dated; anything whose only source predates 2025 is marked "(possibly stale)". Most facts come from official pricing/help/developer pages fetched on 2026-10-09; where only third-party aggregators were available this is flagged, because the aggregators frequently disagree with each other. Research stopped when the shared web-search budget for this run was exhausted; the resulting gaps are listed per product.

Method caveat for the report writer: "SecondBrain" facts (single-user, self-hosted .NET 10 daemon, Markdown vault + SQLite, typed documents, hybrid retrieval with degraded-status reporting, citation-only chat, human-approved write proposals, egress manifest, MCP server, provider-agnostic roles with `local_only`) come from the assignment brief, not from research; only the foundations milestone exists as of 2026-10-09, so every "versus SecondBrain" line compares shipped products against a design.

---

## Google NotebookLM (renamed "Gemini Notebook" in July 2026) — consumer and Enterprise/Workspace editions

### Takeaway
Gemini Notebook (ex-NotebookLM) is the strongest commercial product at passage-level citation over user-uploaded sources, but it is a closed notebook (no public consumer API, no official MCP, no bulk export) tied to Google's AI subscriptions, with a fixed Gemini model and per-notebook source caps.

### Cited Findings
**What it is / who makes it / changes 2025-2026**
- Google renamed the product on 2026-07-16: "We're renaming NotebookLM to Gemini Notebook." "It remains a standalone product focused on being your premier research tool." Google says more than 30 million people and over 600,000 organizations use it; the rename shipped with a "secure cloud computer" in every notebook that writes and executes code for data analysis grounded in the user's sources, available first to Google AI Ultra users and Workspace business customers with AI Ultra/AI Expanded Access, with Pro web users to follow "over the coming weeks" — [Google blog, 2026-07-16](https://blog.google/innovation-and-ai/products/gemini-notebook/notebooklm-gemini-notebook/)
- Secondary coverage of the rename (same date) — [chromestory.com, July 2026](https://chromestory.com/2026/07/notebooklm-now-gemini-notebook-explained/); [designcompass.org, 2026-07-17](https://designcompass.org/en/2026/07/17/google-gemini-notebook-renaming/)
- Mobile apps launched 2025-05-19 for iOS (iOS 17+) and Android (Android 10+), with offline Audio Overviews, background playback, and a share-sheet that adds websites, PDFs and YouTube videos as sources — [Google blog, 2025-05-19](https://blog.google/technology/ai/notebooklm-app/)
- A secondary guide reports that after NotebookLM moved to Gemini 3.1 Pro on 2026-02-19, users reported "source blindness" (the tool claims information is missing when the uploaded file contains it) and gap-filling with general knowledge — [st-hakky guide](https://book.st-hakky.com/en/service/how-to-use-notebooklm-and-information-about-hallucination) (secondhand reports, not measured rates)

**Pricing and limits (2026)**
- Google's "Upgrade" help page (product called "Gemini Notebook" throughout): notebooks per user / sources per notebook — Standard (free, Gmail sign-up) 100 / 50; Plus (Google AI Plus) 200 / 100; Pro (Google AI Pro) 500 / 300; Ultra 20 TB plan 500 / 500; Ultra 30 TB plan 500 / 600. "Sharing a notebook does not change the source limit for any collaborator." Enterprise access is through Google Cloud with higher artifact (Audio/Video Overview) limits; limits are "Subject to Change"; the page states "daily quotas are reset after 24 hours; monthly quotas are reset after 30 days" and lists chats at 50/day on the free tier — [Google help: Upgrade NotebookLM](https://support.google.com/notebooklm/answer/16213268)
- There is no standalone NotebookLM subscription; access tiers map to Google AI plans. Engadget (2026-05-19): Google AI Ultra "now you can get it for $100 a month" with 20 TB storage, a $200 option within Ultra adds Project Genie access and "4X the usage limits of the $100 tier"; "Before, the Google AI Ultra plan cost a whopping $250 a month" and capped at 30 TB; AI Pro is "the $20-a-month AI Pro plan" — [Engadget, 2026-05-19](https://www.engadget.com/2176060/the-google-ai-ultra-plan-now-starts-at-100-a-month)
- TechCabal (2026-06-05) lists five rungs: free Gemini, Google AI Plus $7.99/mo, Google AI Pro $19.99/mo, Google AI Ultra at $100 and $200/mo — [TechCabal, 2026-06-05](https://techcabal.com/2026/06/05/google-ai-subscription-plans-explained/) (third-party; one aggregator lists Plus at $4.99, treated as outlier — [krater.ai](https://krater.ai/blog/gemini-pricing))
- Per-source cap of 500,000 words or 200 MB on every plan — [fast.io, 2026](https://fast.io/resources/notebooklm-limits/) (third-party; not confirmed on a Google page in this pass)
- A single secondary news source reports that from 2026-09-02 Gemini Notebook dropped fixed daily caps for a compute-based usage budget that refreshes every five hours with a weekly cap (Plus 2x and Pro 4x the free budget), and that Video Overviews/presentations queue for later generation — [pasqualepillitteri.it, Sept 2026](https://pasqualepillitteri.it/en/news/14117/gemini-notebook-compute-limits-5-hour-reset) (fetch failed twice; unverified against Google, and Google's upgrade page still describes 24-hour resets — treat as conflicting)

**Ingestion**
- Share-sheet sources on mobile: websites, PDFs, YouTube videos, "more input types will be added over time" — [Google blog, 2025-05-19](https://blog.google/technology/ai/notebooklm-app/)
- Workspace edition: "Gemini Notebook caches Drive files to improve performance" and Drive files added as sources are autosynced — [Google Workspace admin help](https://knowledge.workspace.google.com/admin/generative-ai/notebooklm)

**Export**
- No built-in bulk export. Documented workarounds: "convert all notes to source", then copy the consolidated text into Google Docs and download — [Supademo guide](https://supademo.com/blog/how-to-export-notes-from-notebooklm); third-party Chrome extensions scrape sources to .md/JSON/ZIP — [nlmtools](https://www.nlmtools.com/blog/notebooklm-export-sources), [NotebookLM Export Pro (Chrome Web Store)](https://chromewebstore.google.com/detail/fhplgheiijiledgfpabdiiheblmjoaog)

**AI features, retrieval and citation precision**
- Google: "inline citations now take you directly to supporting passages in your sources, so you can easily fact-check the AI response or dive deeper in the original text"; images can be cited as evidence — [Google blog (2024, possibly stale wording; behaviour confirmed by 2025-26 guides)](https://blog.google/technology/ai/notebooklm-goes-global-support-for-websites-slides-fact-check/)
- University guide: "Inline citations (numbered markers) appear within the response — click any citation number to jump to the specific passage in the original source" — [Bowdoin IT KB](https://bowdoin.teamdynamix.com/TDClient/1814/Portal/KB/ArticleDet?ID=171371)
- Limitation: "if a source is very short, NotebookLM might cite the entire document rather than specific sentences" — [CASRAI guide](https://www.casrai.org/guides/notebooklm-for-research-workflows)
- Hands-on test: it declined to answer an outside-the-documents question (answers only from uploads), but the Audio Overview "compressed the last three papers into vague language and dropped several specific findings" — [Vantaige review](https://vantaige.io/ai-tool/notebooklm)
- 2025 arXiv critique: lack of fact-checking can propagate outdated/discredited information and verifying its citations is itself error-prone — [arXiv 2505.01955](https://arxiv.org/pdf/2505.01955v1)
- Retrieval mechanism is not disclosed by Google; no source found describing its indexing. Model is Google's Gemini (3.1 Pro as of Feb 2026 per the st-hakky guide above); no user model choice and no bring-your-own key.

**Privacy / data use**
- Workspace/Education editions (official): "Your data is protected, and is not used to train Gemini Notebook unless you provide feedback." "Your uploaded files, chats and model outputs won't be reviewed by human reviewers or used to improve generative AI models." Feedback exception: "When you share feedback to help us improve, we may review the full context of that interaction." Data residency: "Your organization's data region settings don't apply to data processed and cached by Gemini Notebook." Included as a core service in Business Starter/Standard/Plus, Enterprise Starter/Standard/Plus, Essentials, Frontline, Nonprofits, Education Fundamentals/Standard/Plus, and Google AI Pro for Education — [Google Workspace admin help](https://knowledge.workspace.google.com/admin/generative-ai/notebooklm)
- Consumer accounts: Google's consumer wording as quoted by a university KB: content "will not be used to directly train our foundational AI models, unless you choose to provide feedback" — [UNC Charlotte KB](https://services.help.charlotte.edu/TDClient/33/Portal/KB/Article/4175/What-is-Google-s-Privacy-policy-for-NotebookLM-Does-NotebookLM-keep-my-data-or-train-its-AI-on-my-da?SIDs=39); a university IT page says personal accounts "will not have the same data protections" as institutional accounts and that NotebookLM is not HIPAA compliant — [UMN IT](https://it.umn.edu/services-technologies/notebooklm)
- Notebooks surfaced inside the Gemini app follow Gemini Apps rules: chats "are saved according to your Keep Activity setting and used as described in the Gemini Apps Privacy Notice, including to improve Gemini with the help of human reviewers"; files added as sources are not used directly for training — [memx.app write-up](https://memx.app/blog/is-notebooklm-private/) (third-party quoting Google; verify against the Gemini Apps Privacy Notice)

**API / MCP**
- Enterprise only: the Gemini Notebook Enterprise API (`discoveryengine.googleapis.com`, `v1alpha`, `notebooks.create`, multi-region `us-`/`eu-`/`global-`) supports creating, retrieving, sharing, deleting notebooks and managing sources — [Google Cloud docs](https://cloud.google.com/agentspace/notebooklm-enterprise/docs/api-notebooks); product page "What is Gemini Notebook Enterprise?" — [Google Cloud docs](https://docs.cloud.google.com/gemini/enterprise/notebooklm-enterprise/docs)
- No official consumer API and no official MCP server as of October 2026; community MCP servers drive a real Chrome browser (Patchright/Playwright) or use undocumented endpoints and need a Google sign-in — [julianoczkowski/notebooklm-mcp-2026](https://github.com/julianoczkowski/notebooklm-mcp-2026); [MCP.Directory guide 2026](https://mcp.directory/blog/notebooklm-mcp-complete-guide-2026); [Pantheon-Security/notebooklm-mcp-secure](https://github.com/Pantheon-Security/notebooklm-mcp-secure); unofficial Rust/Python client for the Enterprise alpha API — [nblm-rs](https://github.com/K-dash/nblm-rs)

**Write safety**
- No source describes NotebookLM editing or deleting user sources; it generates Studio artifacts and notes. (See Inferences.)

### Inferences
- Citation precision is passage-level and the best of the commercial set, but trust depends on a fixed Gemini model whose behaviour changed under users in Feb 2026; SecondBrain's "citation resolves to a document revision and excerpt, plus degraded-retrieval flag" design is comparable in precision and stronger on auditability (revision-pinned, degradation reported).
- Lock-in is high: content is per-notebook (50-600 sources), there is no bulk export, no consumer API, and the only API is an enterprise alpha; agents cannot read or write it without scraping. SecondBrain's Markdown vault + MCP is the opposite posture.
- Structured who/when/type questions are answered by LLM reading of sources, not by metadata queries; there is no document typing or SQL-style filtering disclosed.
- Continuity risk: low shutdown risk (Google-scale, 30M users), but high churn risk in naming, plan structure (Ultra repriced $250 to $100/$200 in May 2026) and usage-limit mechanics (reported Sept 2026 budget change).
- Versus SecondBrain: NotebookLM wins on polish, Audio/Video Overviews, mobile apps and zero setup; SecondBrain wins on ownership, agent access (MCP read/write), provider choice/local models, structured queries, and auditability of what left the machine.

### Gaps
- Could not fetch Google's consumer "Privacy and Terms of Use in Gemini Notebook" article directly (index page only); consumer training policy is quoted via universities.
- Could not verify the reported September 2026 "5-hour usage budget" change against an official Google page (fetch failed; search budget exhausted).
- Exact Google AI Plus price and storage in October 2026 rest on third-party reporting ($7.99/mo), not a fetched Google page.
- No disclosed retrieval architecture (chunking, embeddings, reranking) found.

---

## Notion AI (Q&A/Enterprise Search, Agents, Meeting Notes, MCP) — pricing after the 2025 bundling change

### Takeaway
Notion AI is now only fully available on the $20/member/month Business plan (the $10 add-on ended for new customers in May 2025); it has the most complete official MCP (hosted, OAuth, read/write) and a strong no-training/zero-retention policy, but citations are page-level, models are vendor-chosen (Notion/Anthropic/OpenAI, premium models metered in credits), and export is lossy for databases.

### Cited Findings
**Pricing (fetched 2026-10-09)**
- Plans per member/month: Free $0; Plus $10; Business $20 ("Recommended"); Enterprise custom; yearly billing "Save up to 20%". Free and Plus get only a "trial of Notion AI". Business includes Notion Agent, AI Meeting Notes, Enterprise Search (Beta, searches Slack/GitHub etc.). Enterprise adds "zero data retention with LLM providers". Custom Agents: "Free to try, then $10 per 1,000 monthly Notion credits"; Workers (Beta) also consume credits — [Notion pricing](https://www.notion.com/pricing)
- "Notion AI is available on Business and Enterprise Plans"; Free/Plus users get "a limited number of complimentary AI responses"; "Business trials may not have access to the model picker" — [Notion AI FAQs](https://www.notion.com/help/notion-ai-faqs)
- The standalone AI add-on was eliminated on 2025-05-13 and full AI (Agents, "Ask Notion") moved into Business; prior add-on subscribers reportedly grandfathered — [coworker.ai pricing guide](https://coworker.ai/blog/notion-ai-pricing) (secondary; Notion's own announcement not retrieved). Free/Plus trial is reported as 20 lifetime AI responses — [fast.io review 2026](https://fast.io/resources/notion-ai-review-2026/)
- Custom Agents entered public beta 2026-02-24, were free through 2026-05-03 and began consuming Notion credits on 2026-05-04; Notion says >1 million Custom Agents were created within ~2 months — [Airframe analysis](https://www.airframe.ai/product/notion-com-product-agents/analysis)
- Aggregator price-history: Business top plan raised from $19.50 to $20/user/month on 2026-02-05 — [CostBench changelog](https://costbench.com/changelog/notion-price-increase-2026-02/); SMB Notion pricing up 12.38% YoY in a 160-customer sample — [SpendHound](https://www.spendhound.com/marketplace/notion-pricing) (aggregators disagree on details; treat as directional)

**Platforms / ingestion**
- Web, desktop, iOS/Android apps and web clipper exist (general knowledge; not re-verified in this pass — see Gaps). AI Meeting Notes (Business) and Enterprise Search connectors to Slack, GitHub and other apps are listed on the pricing page — [Notion pricing](https://www.notion.com/pricing)

**Export**
- Formats: PDF, HTML, "Markdown & CSV" (databases export as CSV; "Callout blocks will be exported as HTML, as there is no Markdown equivalent"). Whole-workspace export "only ... on desktop or web", download link "will expire after 7 days", exports "can take up to 30 hours to process". Not included/limited: pages the exporter can't access, content restricted by teamspace settings, form views, "Exporting all views at once isn't supported" (only current/default database view), custom emojis in PDF. Including subpages in PDF requires Business/Enterprise; Enterprise admins can disable export; workspace export via Admin API is beta on Enterprise — [Notion help: Export your content](https://www.notion.com/help/export-your-content)

**AI features, retrieval, citations**
- Q&A/"Find" is described as "Get answers, instantly—with citations"; Enterprise Search draws on workspace pages the user can access plus connected apps and the web; the FAQ does not state citation granularity — [Notion AI FAQs](https://www.notion.com/help/notion-ai-faqs)
- Embeddings are created with OpenAI's embeddings API and stored in Turbopuffer (vector DB); deleted "within 60 days from when the page or workspace is deleted" — [Notion AI security practices](https://www.notion.com/help/notion-ai-security-practices)
- Community complaints concentrate on classic keyword search (failing to find known content, slow mobile search, large workspaces), with more favourable impressions of AI-assisted Enterprise Search — [Airframe Enterprise Search analysis](https://www.airframe.ai/product/notion-com-product-enterprise-search/analysis)
- Incident: 2026-06-24 degraded performance on Notion AI Enterprise Search, resolved after ~8 hours — [Notion status incident](https://www.notion-status.com/incidents/w1hy63fvgy1r)

**Model choice**
- "You can even switch between the latest AI models"; premium models use Notion credits and must be enabled by a workspace owner/admin; bring-your-own-key is not mentioned — [Notion AI FAQs](https://www.notion.com/help/notion-ai-faqs)
- Notion "hosts some LLMs itself and also uses models from Anthropic and OpenAI" — [Notion AI security practices](https://www.notion.com/help/notion-ai-security-practices)

**Privacy**
- "Notion and its AI Subprocessors do not use Customer Data to train any models"; Enterprise: "by default our LLM providers utilize zero data retention"; non-Enterprise: "LLM providers only retain Customer Data for 30 days or fewer before deletion"; features needing data-retaining LLMs are off by default; TLS 1.2+ in transit; SOC 2 Type 2 and ISO 27001 in scope; HIPAA via zero-retention APIs — [Notion AI security practices](https://www.notion.com/help/notion-ai-security-practices)

**API / MCP**
- Official hosted remote MCP server, OAuth; capabilities: search Notion and connected sources, read and update content, create pages and databases; example clients Claude Code, Cursor, Codex; workspace owners manage client access in Settings → Connections; org owners can list/revoke via Admin API — [Notion developers: MCP](https://developers.notion.com/docs/mcp); endpoint `https://mcp.notion.com/mcp`, streamable-HTTP, tools include search/fetch/create/update pages, move/duplicate, create/update databases, comments — [ToolHive guide](https://docs.stacklok.com/toolhive/guides-mcp/notion-remote); open-source self-hosted server also published — [Notion developers](https://developers.notion.com/docs/hosting-open-source-mcp)

**Write safety**
- Notion Agent can "take on tasks from beginning to end", creating or editing pages and databases; inline edits require approval (accept or discard); the FAQ does not say whether AI can delete pages — [Notion AI FAQs](https://www.notion.com/help/notion-ai-faqs)
- Single-aggregator report: May 2026 "Plan Mode" (clarifying questions and a proposed plan before high-impact agent changes) and August 2026 "suggested edits" approvable line by line — [yespress product log](https://yespress.io/products/notion-ai.md) (unverified against Notion release notes)

### Inferences
- Citation granularity appears to be page-level (answers link to pages), not passage-level; no source describes excerpt-level anchors. SecondBrain's revision+excerpt citations are more precise.
- Notion's 30-day provider retention on non-Enterprise plans means an individual's notes do transit to OpenAI/Anthropic and sit there up to 30 days unless on Enterprise; SecondBrain's `local_only` transport and egress manifest address exactly this.
- Export completeness is partial for databases (one view, CSV, relations flattened), so a heavy database user cannot round-trip; Markdown pages export fine.
- Write safety: Notion lets agents (including scheduled Custom Agents) create/edit autonomously with approval only for inline edits; SecondBrain's "every write is a proposal" is stricter.
- Continuity: shutdown risk is negligible; price/packaging churn is real (add-on removed 2025, credits introduced 2026).

### Gaps
- Notion's own May 2025 announcement of the AI bundling was not retrieved (secondary sources only).
- Notion Mail and Notion Calendar were not researched in this pass (search budget exhausted).
- No 2025-2026 funding/valuation/headcount figures retrieved.
- Citation granularity (page vs passage) not stated in any fetched Notion page.

---

## Mem (Mem 2.0, get.mem.ai)

### Takeaway
Mem relaunched as "Mem 2.0" on 2025-10-01 and by August 2026 replaced its flat Pro plan with a seven-rung metered ladder (Free, $9, $29, $49, $99, $149, $199/month) whose counters cap messages, new notes and routines; it has a REST API and MCP server and a no-training policy, but export is JSON-only via settings, there is no Android app, and the company's last disclosed funding is the 2022 OpenAI-Startup-Fund Series A.

### Cited Findings
- Company/funding: $23.5M Series A led by the OpenAI Startup Fund, November 2022, bringing total raised to about $29M — [SaaS News (citing TechCrunch, 2022-11-10)](https://www.thesaasnews.com/news/mem-raises-23-5-million-in-series-a/) (possibly stale); $110M post-money reported — [Clay dossier](https://www.clay.com/dossier/mem-funding); partnership included early access to OpenAI features and Azure resources — [W&B ML news](https://wandb.ai/telidavies/ml-news/reports/Mem-Raises-23-5-Million-From-OpenAI-Fund-For-Self-Organizing-Workspace-App--VmlldzoyOTUwODgx)
- Mem 2.0 launch post "Introducing Mem 2.0: The World's First AI Thought Partner", 2025-10-01, rebuilt product, new brand, "offline-first" — [Mem blog](https://get.mem.ai/blog/mem-2-dot-0); transition guide (Feb 2025) notes table editor, Deep Search (AI results appended after keyword matches) and that some 1.0 features do not carry over — [Mem transition guide](https://get.mem.ai/blog/mem-2-dot-0-transition-guide)
- Pricing page (fetched 2026-10-09; monthly only, no yearly prices): Free; Plus $9; Pro $29 (recommended); Pro 2x $49; Pro 5x $99; Pro 10x $149; Pro 20x $199. Monthly caps — Free: 25 messages to Mem, 25 new notes, 25 notes tracked, 15 open tasks/projects, 3 Voice Mode hours; Pro: 100/100/100/50, 1 custom routine, 50 routine executions, 1 connection, 25 voice hours; Pro 20x: 2,000/2,000/2,000/1,000, 20 routines, 1,000 executions, 20 connections, 500 voice hours. Extra Usage $50/unit on Pro 20x. Connections (Gmail, Slack, Todoist) and Custom Routines from Pro up; Voice Mode (meeting/spoken-note transcripts), Deep Search and templates on all plans; web clipper and calendar listed as features — [Mem pricing](https://get.mem.ai/pricing)
- Price history: a usage-pricing tracker (verified 2026-08-25) says the ladder replaced an earlier flat $12/mo Pro plan and a $99/mo "Mem Proactive" tier — [usagepricing.com](https://www.usagepricing.com/blueprint/mem); earlier 2026 aggregators listed Pro at $14.99 — [shipsquad.ai](https://shipsquad.ai/pricing/mem-ai) (aggregators conflict)
- AI features per a vendor roundup: auto-connects related notes without tags/folders, "answers questions over your notes with source links", voice memos to notes, meeting-brief beta — [Tana blog, verified July 2026](https://tana.inc/blog/best-second-brain-apps-2026)
- API and MCP: "Mem's API gives you programmatic access to your notes" — create ("Push content into Mem from any source"), read, search ("by content, date, or meaning"), collections, and "Export notes to other formats or platforms"; an MCP server lets Claude "read notes, search them, and create new ones"; docs at help.mem.ai/features/api; plan requirement not stated — [Mem guide: API & MCP](https://get.mem.ai/guides/mem-api-mcp-custom-workflows)
- Export: a third-party import plugin documents exporting from mem.ai/settings/exports as JSON, delivered by email as a Google Storage link — [Amplenote Mem import notes](https://public.amplenote.com/uHhuZS9DTwF5tPEy5gr2ARYD); Mem's own help documents only import of .md/.txt (folder structure not preserved) — [help.mem.ai imports](https://help.mem.ai/features/imports)
- Privacy (revised 2026-08-13): "We do not use integration queries to train AI models"; data kept no longer than the account exists; subprocessors and data locations are listed only at trust.mem.ai; no encryption statement — [Mem privacy policy](https://get.mem.ai/privacy-policy)
- Weaknesses in 2026 reviews: no official Android app and a Chrome extension that "frequently has bugs and connectivity issues" — [Tooliverse](https://tooliverse.ai/tools/mem); mobile lags desktop and support is slow for bugs — [aiagentrank](https://aiagentrank.io/blog/mem-ai-review-2026); Mem 2.0 explicitly aimed at "persistent complaints about the earlier product's sluggishness and data inconsistencies" — [workgpt review](https://workgpt.com/en/app-reviews/mem-ai)
- Name collision: "Mem AI" is frequently confused with Mem0 (developer memory API) in 2026 search results — [SolidAITech, July 2026](https://www.solidaitech.com/2026/07/mem-ai-note-taking-app-vs-mem0-api.html)

### Inferences
- Mem's metering (25 new notes/month free, 100 on $29 Pro) makes it expensive as an "everything I read" archive; SecondBrain's ingestion has no per-note price.
- Citations are "source links" (note-level), not excerpts.
- Export is JSON by email, not Markdown; migration requires conversion. Mem's own API "read" and "export" capabilities mitigate lock-in for a technical user.
- Continuity: a VC-backed company with no disclosed round since 2022 and a sharp 2026 repricing ($12 to $29 for the recommended tier) signals monetisation pressure; moderate shutdown/pivot risk.
- Versus SecondBrain: comparable ambitions (auto-organise, routines, connections to Gmail/Slack), but fixed vendor models, counted usage, and no local option.

### Gaps
- help.mem.ai API documentation (auth, endpoints, plan gating) not fetched.
- Official export documentation not found; JSON export described only by a third party.
- Which LLM providers Mem uses is not in the privacy policy (deferred to trust.mem.ai, not fetched).
- No 2025-2026 funding, headcount or revenue figures.

---

## Limitless (formerly Rewind) and other life-logging assistants (Bee/Amazon, Microsoft Recall)

### Takeaway
Limitless was acquired by Meta on 2025-12-05 and stopped selling its Pendant; existing owners got a free "Unlimited" plan but must accept Meta-era terms, EU/UK/other regional accounts were deleted after 2025-12-19, and the Rewind desktop app and web/desktop recording were wound down — a textbook continuity failure for a personal-memory product. Bee was bought by Amazon (July 2025). Microsoft Recall is on-device and opt-in but remains "(preview)", EEA-only for export, and is actively blocked by Signal, Brave and AdGuard.

### Cited Findings
**Limitless / Meta**
- Official page: "Limitless has been acquired by Meta"; "We are no longer selling the Pendant as of December 5, 2025"; "We will continue to support existing Pendant customers for at least another year" (FAQ: "throughout 2026"); "We're making the Limitless Unlimited Plan free for all existing customers"; "We just launched a feature to make it easy to export all of your data"; "you can delete your Limitless account or export your Limitless data at any time"; "your data will remain safe" — [limitless.ai](https://www.limitless.ai/)
- TechInformed (2025-12-09): Limitless is "winding down several non-pendant products" including the original Rewind desktop app and recording features in the web/desktop apps; remaining users must accept an updated Privacy Policy and Terms of Service; service ends in the EU, UK, Brazil, Israel, South Korea, Turkey and China with a 2025-12-19 deadline to export before accounts are permanently deleted — [TechInformed](https://techinformed.com/meta-acquires-limitless-pendant-users-moved-to-free-unlimited-plan/)
- Deal framed as part of Meta's "personal superintelligence" hardware push; team folded into Reality Labs — [Outlook Business](https://www.outlookbusiness.com/corporate/meta-acquires-ai-wearables-start-up-limitless-as-it-doubles-down-on-personal-superintelligence); Pendant price was $99 — [KuCoin news flash](https://www.kucoin.com/news/flash/meta-acquires-ai-pendant-maker-limitless-to-boost-hardware-strategy) (secondary)
- Support cliff: sources disagree whether support ends "at least a year" (~Dec 2026) or "throughout 2026" — [becomefluent.io, May 2026](https://becomefluent.io/blog/2026-05/what-happened-to-limitless-pendant/)

**Bee / Amazon**
- Amazon agreed to acquire Bee (Bluush Inc.), maker of a $50 always-listening bracelet with a $19/month subscription, announced 2025-07-22; deal signed but not closed at the time; ~$8.5M raised; the device transcribes audio and stores text, not audio; Amazon said it would improve the mute/user controls — [Entrepreneur](https://www.entrepreneur.com/business-news/amazon-acquires-bee-startup-behind-eavesdropping-wearable/494971); [ConsumerAffairs](https://www.consumeraffairs.com/news/amazon-acquires-a-wearable-company-whose-product-is-always-listening-072425.html)

**Microsoft Recall (Windows 11, Copilot+ PCs)**
- Official privacy page: "Snapshots and associated data are stored locally on the device"; "Recall does not share snapshots or associated data with Microsoft or third parties"; "No internet or cloud connections are required or used to save and analyze snapshots"; "Snapshots and any associated information in the vector database are always encrypted" with keys in the TPM and operations in a Virtualization-based Security Enclave; Windows Hello (biometric) required before access; "If you don't choose to opt in, it will be off by default and snapshots will not be saved"; "Sensitive information filtering is on by default"; feature still labelled "(preview)"; export described only as a related link — [Microsoft Support](https://support.microsoft.com/en-us/windows/privacy-and-control-over-your-recall-experience-d404f672-7647-41e5-886c-a3c59680af15)
- Timeline: announced May 2024; "Total Recall" tool showed unencrypted local data in June 2024; release delayed 2024-06-14; "more secure" opt-in version with database encryption and uninstall announced late Sept 2024 — [Wikipedia: Windows Recall](https://en.wikipedia.org/wiki/Windows_Recall); general availability (still "preview") via the April 2025 non-security update, rolling to Snapdragon, Intel and AMD Copilot+ PCs — [Tom's Hardware via TagTeam](https://tagteam.harvard.edu/hub_feeds/4247/feed_items/13678074/about)
- Export: Insider feature for EEA users only; one-time encryption code that Microsoft cannot recover; export 7/30 days or everything, optional ongoing export; `AllowRecallExport` Group Policy — [The Register, 2025-06-16](https://theregister.com/2025/06/16/microsoft_snapshot_export_recall)
- App blocking: Signal's "screen security" (May 2025, default on) uses DRM to keep its window out of Recall; Brave 1.81 and AdGuard block Recall by default (July 2025) with user toggles — [The Register, 2025-07-23](https://www.theregister.com/2025/07/23/brave_browse_block_microsoft_recall/); [Engadget](https://engadget.com/ai/brave-and-adguard-now-block-microsoft-recall-by-default-152601475.html)

### Inferences
- Limitless is the clearest 2025-26 example of the brief's "continuity risk": an acquirer changed terms, ended hardware sales, deleted entire regions' accounts, and killed the desktop (Rewind) product within weeks. Life-logging data locked in a vendor cloud is only as durable as the vendor.
- Recall is the only mainstream product with SecondBrain-like local-only guarantees (no cloud, encrypted at rest, enclave), but it is Windows/Copilot+-only, screenshot-based rather than document-typed, has no API/MCP, and its export is region-gated and key-fragile.
- Versus SecondBrain: SecondBrain's audio/transcript ingestion would have to come from third-party recorders (Pendant data is exportable, Bee is Amazon-bound); its value is being the durable, vendor-neutral sink for such streams.

### Gaps
- No official Limitless/Meta statement on whether pendant data is now processed by Meta's models or under Meta's privacy policy (secondary sources only).
- Whether the Amazon-Bee deal closed, and Bee's current pricing/privacy, not verified (coverage found only from July 2025).
- No 2026 Microsoft documentation found on Recall changes (e.g., expansion beyond Copilot+ PCs or integration with Copilot); the privacy page carries no 2025-26 dates.
- Other wearables (Plaud, Omi, Friend) not researched.

---

## Reflect Notes (Reflect Classic and the July 2026 "Reflect Open" rewrite)

### Takeaway
Reflect pivoted on 2026-07-14 from a proprietary end-to-end-encrypted cloud app ("Reflect Classic", ~$10/month) to "Reflect Open", a free, open-source, local-first Mac app that stores plain Markdown files, syncs via iCloud Drive or Git, and calls AI only with the user's own OpenAI/Anthropic/Google/OpenRouter key — the closest commercial-origin product to SecondBrain's ownership model, though it has no server, no MCP in the launch post, and only a write-only API on Classic.

### Cited Findings
- Reflect Open launch post (2026-07-14): the original app relied on "two big technical bets" (its own end-to-end encryption and a custom note format/sync engine) that made it hard to evolve; the rebuilt app stores notes "as ordinary Markdown files on your computer", syncs through iCloud Drive or a Git remote, adds no E2E layer of its own (iCloud's own E2E can be enabled in many countries); notes marked "Private" are never sent to AI; Reflect Classic "will remain available" and will keep being maintained; migration is one-way and non-destructive with no deadline; existing subscriptions carry over, Mac app is free and open source, mobile will eventually be paid; AI requires the user's own API key from OpenAI, Anthropic, Google or OpenRouter stored in the OS keychain; a CLI (`reflect today`, `reflect search`, `reflect show`) lets scripts and agents read notes, and the FAQ suggests pointing Claude Code at the folder — [Reflect blog: Introducing Reflect Open](https://reflect.app/blog/reflect-open); source — [GitHub team-reflect/reflect-open](https://github.com/team-reflect/reflect-open)
- Homepage (2026-10-09): Mac "No subscription required"; iPhone/iPad "Paid subscription" (App Store pricing); Classic subscribers get one year of the mobile app free; "Connect your own API key to get started"; AI features: "Ask your notes", summarise, improve writing, transcribe voice notes via your transcription provider, custom prompts; Chrome extension for links/text/screenshots; "Log in to Classic" link remains — [reflect.app](https://reflect.app/)
- Classic API: write-only and append-only "because note contents are end-to-end encrypted" so servers cannot read them; OAuth credentials at reflect.app/developer/oauth; base URL `https://reflect.app/api`; daily-note append via PUT with `transform_type: "list-append"` — [Reflect blog: API is live](https://reflect.app/blog/reflect-update-api); roadmap confirms no read endpoints — [Reflect roadmap](https://roadmap.reflect.app/feature-requests/p/rest-api); Power Automate connector throttled at 100 calls/60 s — [Microsoft Learn](https://learn.microsoft.com/en-us/connectors/reflectip)
- Classic pricing per aggregators (2026): ~$10/month billed annually ($120/year) with a 14-day trial, other listings $12-$15 monthly — [Capterra](https://www.capterra.com/p/10005017/Reflect/); [CostBench](https://costbench.com/software/note-taking/reflect/) (official pricing page returned 404 on 2026-10-09)
- Vendor roundup describes Classic as offering GPT-4 inline writing/summaries, calendar sync and "end-to-end encryption" — [Tana blog](https://tana.inc/blog/best-second-brain-apps-2026)

### Inferences
- Reflect Open validates SecondBrain's core bets (plain Markdown, BYO provider, private-note exclusion from AI), but it is a single-device desktop app without a daemon, REST/MCP surface, typed documents, or hybrid search; agents access it via files/CLI.
- Classic's E2E design made server-side AI and read APIs impossible — a useful data point for SecondBrain: SecondBrain achieves privacy via network placement (WireGuard/Tailscale, `local_only`) rather than E2E, which is what lets it offer server-side search and MCP.
- Continuity: the company kept Classic alive and open-sourced the new app, lowering shutdown risk for users; the pivot itself is a feature-churn risk for Classic users (no edit sync between the two).

### Gaps
- Classic export format and completeness not documented in any fetched source.
- Official current Classic price not retrievable (404).
- Reflect Open MCP support is only teased in a related post; not verified.
- Android support not mentioned anywhere fetched.

---

## Tana (Tana Outliner and the new "Tana")

### Takeaway
Tana split into two products in March 2026 (existing app renamed "Tana Outliner"; a new meeting-centred, block-editor "Tana" for collaboration/agents), raised Pro to $20/user/month early-bird (list $30) with a Free tier, ships official MCP (remote and local) with read/write and a human "proposal" review step, offers multiple vendor models (GPT-5.4 default, Claude 4.6, Gemini 3.1 Pro) but no local model yet, and exports Markdown/JSON.

### Cited Findings
- Pricing page (2026-10-09): Free — 5 hosted meetings/month, 1 connected calendar, 50 AI queries; Pro — $20 per user/month "early bird" shown against a struck-through $30 (billing period not labelled), 30-day free trial, "20x more AI than the free plan", unlimited hosted meetings*, "Botless meeting agent for Zoom, Teams, and Google Meet", "Integrations and full MCP"; Business — custom, yearly, SAML SSO, audit logs, custom DPA, data residency/retention control, "LLM-agnostic", GDPR/SOC 2/HIPAA*; AI credit top-ups; 60-day refund on yearly — [Tana pricing](https://tana.inc/pricing)
- Aggregator price history: 2026-02-04 top tier $14 to $20/month; 2026-07-12 fourth plan added with top tier $120/month ("Max") — [CostBench Feb 2026](https://costbench.com/changelog/tana-price-increase-2026-02/); [CostBench July 2026](https://costbench.com/changelog/tana-plan-added-2026-07/) (aggregator; the fetched official page shows only Free/Pro/Business)
- March 2026 "Tana Current": new product "built from the ground up for collaboration, agentic work"; "the product you know today is staying and evolving as Tana Outliner"; "We will not force a migration"; existing subscriptions unchanged; both products will offer MCP; "Support for local AI models" is "On the horizon"; Q2 includes "Better export (building on export as markdown)" — [outliner.tana.inc, March 2026](https://outliner.tana.inc/blog/tana-current-march-2026)
- April 2026: new models "Claude Sonnet 4.6, Claude Opus 4.6, OpenAI GPT 5.4, Gemini 3.1 Pro" with GPT-5.4 as default; "MCP and local API fixes and improvements, shipped continuously since launch"; Copy-node-ID command for MCP users; MCP concurrency fixes; MCP access to full transcripts coming; trash restore planned partly because MCP usage "can lead to accidental deletions"; global desktop web clipper; Q2 focus "Core reliability, long-standing paper cuts" — [outliner.tana.inc, April 2026](https://outliner.tana.inc/blog/tana-current-april-2026)
- Rename shipped in the 2026-05-13 release notes (apps renamed to Tana Outliner; data/settings unchanged) — [Tana releases wk 15-20](https://outliner.tana.inc/releases/2026-wk-15-20); May 2026: new Tana opened to community feedback; block editor; "Supertags are now called Types, and Fields are now called Attributes" — [outliner.tana.inc, May 2026](https://outliner.tana.inc/blog/tana-current-may-2026)
- Tana's own positioning (verified July 2026): extracted decisions/tasks "land as proposals you approve, so a human stays in the loop"; re-running extraction "updates the record you already have and de-duplicates"; meetings captured "without a bot"; "every item is permissioned"; MCP lets Claude Code "read and write your Tana knowledge through the same proposal review"; scheduled custom agents — [Tana blog: best second brain apps 2026](https://tana.inc/blog/best-second-brain-apps-2026)
- Funding: $14M Series A led by Tola Capital, announced early Feb 2025, with Lightspeed, Northzone, Firstminute, Alliance VC — [StartupIntros](https://startupintros.com/news/2025-02-01-tana-series-a); an Alliance VC partner puts cumulative funding at $25M — [torstensson.com](https://torstensson.com/2025/02/03/alliance-vc-invests-in-tana/) (totals conflict: $14M-$25M)
- MCP endpoints per a third-party guide: remote `https://home.tana.inc/mcp` and local `http://localhost:8262/mcp` (local runs only while the desktop app is open); community `tana-mcp` (write-only Input API) archived 2026-08-20 — [ContextBolt guide](https://contextbolt.com/blog/tana-mcp/); competitor comparison: local MCP supports read and write (search/read nodes, tag, set fields), export in JSON and Markdown, credit-based AI — [Hjarni vs Tana](https://hjarni.com/compare/hjarni-vs-tana) (competitor page; unverified)
- Community concern that the Outliner will get less development now that the new product is the focus — [Outliner Software forum](https://outlinersoftware.com/topics/viewt/12624); [MPU forum](https://talk.macpowerusers.com/t/replacement-for-tana/44724)

### Inferences
- Tana is the commercial product whose write-safety story most resembles SecondBrain's (AI output lands as approvable proposals, MCP writes go through the same review) — but it is cloud-hosted, per-seat, credit-metered, and the trash-restore note shows MCP deletions already happen.
- Structured queries: Tana's typed nodes (supertags/fields, `parent.field` search expressions) give real who/when/type querying, comparable in intent to SecondBrain's typed documents and SQL properties.
- Model choice: vendor menu, no BYO key found, local models only "on the horizon"; "LLM-agnostic" is a Business-tier promise.
- Continuity: well-funded but mid-pivot with a price doubling in a year; moderate churn risk for Outliner users.

### Gaps
- Official Tana docs for export (formats/completeness) and for the MCP server were not fetched (search budget exhausted); endpoints come from third parties.
- Whether Tana supports bring-your-own API keys is not stated in any fetched page.
- Pricing of the new "Tana" product has not been published (per March post).

---

## Capacities

### Takeaway
Capacities is an object-typed notes app (free Basic with unlimited objects; Pro $9.99/month) with a public REST API, an official MCP-based "AI Chat Connectors" feature that grants read/write to one space per connection for ChatGPT, Claude, Claude Code, Cursor, Perplexity, Codex, LM Studio and others, and email/WhatsApp/Telegram capture — but its AI budget is small unless the $8.33/month "Plus" add-on is bought, and export formats were not confirmed.

### Cited Findings
- Pricing (2026-10-09): Basic free "For everybody" — unlimited spaces/objects/blocks/custom object types, sync, offline, full-text search, "Media uploads up to 5 GB total, 100 MB per month afterwards", view-only sharing, "Full support for import and export", save-to-Capacities from email, WhatsApp, Telegram, Twitter; Pro $9.99/month (yearly -16%; structured data shows $11.99 monthly) — task management, AI assistant, AI Chat Connectors, calendar integration, Readwise/Kindle, smart queries, public API, version history, unlimited media (fair use); Believer from $12.49/month with beta access; Plus add-on $8.33/month billed annually for a "Much larger monthly AI budget"; the standard AI budget is "sized for trying AI features or very light monthly use"; top-ups require Plus — [Capacities pricing](https://capacities.io/pricing)
- Aggregator price history: top tier up to $14.99 in Feb 2026, lowered to $12.49 on 2026-04-24 — [CostBench](https://costbench.com/changelog/capacities-price-decrease-2026-04/)
- AI Chat Connectors (official docs): the connected assistant can "Search your space by keywords and topics", list/read objects incl. properties and body, create pages/tasks/objects and upload files, update properties, append to notes, edit blocks, "Save text to your daily note", "Link to objects in its replies"; "The connection requests read and write access to your Capacities space"; "Access is limited to your selected space" (one space per connection, consent screen); AI Assistant must be enabled for the space; usage does not draw on the Capacities AI budget; setup guides for ChatGPT, Claude, Claude Code, Cursor, Le Chat, Perplexity, Gemini CLI, Gemini Enterprise, VS Code, Codex, Raycast, LM Studio, OpenCode, Antigravity, Notion Custom Agents and "any MCP-compatible client"; the connected tool's privacy policy governs data it obtains — [Capacities docs: AI Chat Connectors](https://docs.capacities.io/reference/ai-chat-connectors); the feature is Pro and "in Beta" — [search summary of the same page]
- API: the Beta API (Bearer token from Settings > Capacities API; token "grants access to all of your data"; per-user per-endpoint rate limits with `RateLimit-*` headers) "will be discontinued on September 1, 2026" in favour of the new API at developers.capacities.io — [Capacities developer docs (beta)](https://docs.capacities.io/developer/api); new REST API covers objects, properties, block content and daily notes, with a TypeScript SDK sample and a note on "How the API and MCP extend a space" — [developers.capacities.io](https://developers.capacities.io/); community servers reference an official `@capacities/api` SDK and `api:read`/`api:write` scopes — [mcp.so listing](https://mcp.so/servers/capacities-mcp)
- Pro adds "task management, AI assistant, calendar integrations, smart queries and API access"; monthly AI budget capped unless Plus is added — [frontdeskreview, Sept 2026](https://frontdeskreview.com/software/note-taking-apps/capacities/)

### Inferences
- Capacities' object model (typed objects with properties) is the closest consumer analogue to SecondBrain's typed documents; its "smart queries" are property-driven, not purely semantic.
- Agent surface is strong (official MCP connector + REST with read/write scopes), but every connector has full read/write of a space with no proposal/approval layer — the AI tool can mutate content directly.
- The AI budget model pushes users to pay for the vendor's AI; connectors let a user bring their own assistant (and thus their own model) for chat, which partially matches SecondBrain's provider neutrality.

### Gaps
- Export formats (Markdown/PDF/JSON) and completeness not verified (docs page not fetched; search budget exhausted).
- Company size/funding, data residency, and which LLM powers the built-in AI assistant not researched.
- Citation behaviour of the built-in AI chat not documented in fetched pages.

---

## Saner.ai

### Takeaway
Saner.ai is a small (reported ~8 people, founded 2023) ADHD-oriented "personal AI assistant" over notes, email, calendar, Drive and Slack; plans are Free / Starter $8 / Standard $16 per month (yearly) with daily AI-message caps, and it lets users route chat to external GPT/Claude/Gemini via pay-as-you-go or their own API key; export is HTML/Markdown per the developer, but its AI subprocessor and training policies were not found.

### Cited Findings
- Pricing (2026-10-09, yearly toggle, "-20%"): Free $0 — 30 AI messages/month, 30 AI requests/month, 100 notes, PDF 1 MB/file, 100 MB storage; Starter $8/month — 30 AI messages/day, 1,000 notes, 5 MB PDFs, 5 GB; Standard $16/month — "Unlimited* AI messages", 100 AI requests/day, unlimited notes, 10 MB PDFs, 100 GB, "Extended" Reasoning Mode; all plans: "Sync data from Google Drive, Email, Slack, Calendar", OCR, Chrome extension, mobile app; "External AI models (GPT, Claude, Gemini)" billed separately "pay-as-you-go or through your own API key" and not counted against plan limits — [Saner.ai pricing](https://www.saner.ai/pricing)
- Monthly-billed prices: Starter $12, Standard $20 (eesel, Aug 2026) — [eesel.ai](https://www.eesel.ai/blog/saner-ai-pricing)
- Export: developer post says users can "instantly export your notes or folders as HTML or Markdown files" — [OpenAI community post by Saner.ai](https://community.openai.com/t/saner-ai-a-personal-ai-assistant-for-adhders-notes-emails-todo-list-gpt-internet-browsing-in-one-place/673847)
- Privacy policy covers web app and extensions and Google API data policy; no AI-provider or training section visible — [Saner.ai privacy policy](https://saner.ai/privacy-policy)
- Company: founded 2023, Wilmington DE, founder/CEO Giap Vo, ~$120K seed incl. Techstars Tokyo (2024-08-18) — [CB Insights](https://www.cbinsights.com/company/sanerai); described elsewhere as bootstrapped with ~$880K revenue and 8 employees — [Latka](https://getlatka.com/companies/saner.ai) (conflicting)
- A tool directory carries a warning that the listing "has been flagged for review manipulation or questionable practices" — [FutureTools](https://www.futuretools.io/tools/saner-ai) (unconfirmed)

### Inferences
- Tiny-team continuity risk is high relative to the others here; Markdown export mitigates it.
- BYO-key for external models is a partial match to SecondBrain's provider neutrality, but the built-in "Skai" assistant's model and data handling are undisclosed.

### Gaps
- No documentation of citation behaviour, retrieval approach, API/MCP, or AI subprocessors found.
- No independent 2025-26 reviews beyond pricing write-ups.

---

## Heptabase

### Takeaway
Heptabase (YC-backed, cash-flow positive since year one per the company) sells whiteboard-centred notes at $8.99/$17.99/$53.99 per month with AI credits (Gemini-only on Pro; GPT/Claude/Gemini on Premium+), exports everything as Markdown plus an `All-Data.json` backup, and runs an official hosted MCP at `https://api.heptabase.com/mcp` with search (keyword and semantic), read, create and edit tools gated by read-only vs write authorisation.

### Cited Findings
- Pricing (2026-10-09, monthly rates; "Yearly (Save 25%)"): Pro $8.99/month, 100 AI credits, "AI agent limited to Gemini models"; Premium $17.99, 1,800 credits, GPT/Claude/Gemini + AI tutor; Premium+ $53.99, 8,100 credits, 33% discount on credits — [Heptabase pricing](https://heptabase.com/pricing); no free tier, 7-day trial with Premium-level credits; credits do not roll over; August 2026 on-demand overage with a monthly spending limit — [usagepricing.com](https://usagepricing.com/blueprint/heptabase), [usagepricing activity 2026-08-28](https://usagepricing.com/blueprint/activity/heptabase-2026-08-28-packaging) (third-party)
- Export and sustainability (official): "Settings → Backup → Export now" exports all notes as Markdown; "The receiving app may not preserve Heptabase's whiteboard layouts, relationships, or metadata in the same way"; complete export includes `All-Data.json`; founded Sept 2021; ~$1.7M seed in early 2022 from Y Combinator, HOF Capital, Kleiner Perkins, Moving Capital; "we have been cash-flow positive since our first year of operation"; "90% of the growth being completely organic"; if shut down "we would open-source Heptabase and maintain it together with the user community" — [Heptabase support: sustainability/export](https://support.heptabase.com/en/articles/10364279-how-sustainable-is-heptabase-can-i-easily-export-my-data-and-move-on)
- MCP (official): endpoint `https://api.heptabase.com/mcp`; lets external AI "list, search, read, create, edit, and organize content in your Heptabase space"; named tools `search_by_keyword`, `search_by_semantic`, `read_object`; "A read-only connection can browse and read content, but writing tools will not appear in its tool list" (write access granted at authorisation); clients: ChatGPT (custom MCP app or official plugin), Claude Code, Cursor, Antigravity CLI — [Heptabase support: How to use Heptabase MCP](https://support.heptabase.com/en/articles/12679581-how-to-use-heptabase-mcp); an earlier revision described the MCP as "private beta" with only two write tools (save card to Inbox, append to today's journal) — same article, earlier cached version (per search summary)
- December 2025 update added AI actions on whiteboard cards and MCP integration — [Vantaige](https://vantaige.io/ai-tool/heptabase)
- Earlier conflicting claims about whether Heptabase MCP was official came from a competitor — [Hjarni vs Heptabase](https://hjarni.com/compare/hjarni-vs-heptabase)

### Inferences
- Heptabase's export is the most complete of the paid cloud tools (Markdown + full JSON), and its "open-source on shutdown" pledge plus profitability lowers continuity risk.
- Its MCP offers both keyword and semantic search tools, i.e., a hybrid surface similar to SecondBrain's; a read-only authorisation mode is a meaningful safety feature, though write connections still mutate directly without proposals.
- Model choice is a vendor menu gated by tier; no BYO key or local model.

### Gaps
- Citation precision of Heptabase AI chat and its privacy/training policy not found.
- Team size not disclosed.

---

## Readwise Reader (and Readwise highlights)

### Takeaway
Readwise ($9.99/month annual, $12.99 monthly, 30-day trial; includes Reader) now has the most capable official MCP of the reading tools (`https://mcp2.readwise.io/mcp`, read and write, plus a tool that exports all Reader documents as a ZIP of Markdown), an August 2026 "Global Ghostreader" that chats across the whole library "with citations", and lets users bring their own OpenAI key for custom Ghostreader prompts.

### Cited Findings
- Pricing (2026-10-09): Readwise Lite $5.59/month billed annually (daily review email/app); Readwise full $9.99/month annually ($119.88/12 months) or $12.99 monthly, "includes access to our own, all-in-one reading app: Reader"; "Readwise is free for first 30 days" — [Readwise pricing](https://readwise.io/pricing)
- MCP (official): remote server `https://mcp2.readwise.io/mcp` (stdio fallback via `npx -y mcp-remote ...`); sign in with Readwise account; read tools (`readwise_search_highlights`, `readwise_list_highlights`, daily review, search/list Reader documents, document details, tags, document highlights, export status) and write tools (create/update/delete highlights, create Reader documents and highlights, move documents, bulk-edit metadata, add/remove tags, set notes); `reader_export_documents` "triggers an export of your Reader documents as a ZIP of Markdown files"; clients Claude, Claude Cowork, Claude Code, ChatGPT, Codex, Cursor, Perplexity (Perplexity custom connectors need Pro/Max/Enterprise) — [readwise.io/mcp](https://readwise.io/mcp)
- Reader Public Beta Update #14 (2026-08-06): Global Ghostreader chats with the whole library, "grounded in your saved content with citations", and can tag, move and edit metadata; Readwise 2.0 mobile rebuild; Mobile Chat; rebuilt "Better Search" (opt-in toggle, "live everywhere"); Quick Lookup; MCP/CLI for Claude, ChatGPT, Perplexity, Poke plus agent skills for Claude Code, Codex, OpenClaw; users who "bring their own OpenAI API key for custom Ghostreader prompts" can select GPT-5 through the GPT-5.6 family — [Readwise update Aug 2026](https://readwise.io/reader/update-aug2026)
- Secondary timeline: MCP and CLI launched 2026-03-20; Lookup in Reader 2026-05-22; GPT-5.5 in Ghostreader 2026-05-15 — [BibiGPT blog](https://growth.bibigpt.co/blog/posts/readwise-lookup-gpt55-mcp-bibigpt) (unverified against Readwise changelog)

### Inferences
- Readwise is a strong upstream feed for SecondBrain rather than a competitor: its MCP export tool and Markdown ZIP make it one of the least locked-in commercial services.
- Citation granularity of Global Ghostreader (document vs passage) is not specified; BYO key applies to custom prompts, not necessarily to the built-in chat.

### Gaps
- Readwise's privacy policy regarding AI processing and training not fetched.
- Company size/funding not researched (widely described as bootstrapped; not verified here).
- Whether Lite includes Reader is unclear from the pricing page text.

---

## Recall (recall.it, formerly getrecall.ai)

### Takeaway
Recall (getrecall.ai now redirects to recall.it) is a save-anything "AI knowledge base" with a free tier that already includes API and MCP access, Plus at $10/month (yearly) for unlimited summaries, AI chat, auto-tagging, automatic knowledge graph and spaced repetition, and Max at $38/month for user model selection (ChatGPT/Claude/Gemini); whole-library Markdown export exists (web app only) but Obsidian fidelity is poor.

### Cited Findings
- `https://www.getrecall.ai/pricing` returned HTTP 301 to `https://www.recall.it/pricing` on 2026-10-09 (observed).
- Pricing: Free $0 — 10 AI summaries/month, unlimited saving and notes, "API and MCP access"; Plus $10/month billed yearly — unlimited AI summaries, AI chat (saved content, open web, or both), automatic tagging, automatic knowledge graph, spaced repetition quizzes, bulk import up to 1,000 bookmarks or 10,000 Markdown notes; Max $38/month billed yearly — model selection (ChatGPT, Claude, Gemini, other frontier models), bulk AI actions on 100 items, 1:1 onboarding; Plus auto-selects among "frontier, open-source, and Gemini Flash" models; browser extension for Chrome, Firefox, Edge, Safari; iOS and Android apps; Fair Use Policy — [Recall pricing](https://www.recall.it/pricing)
- Export: "Export to Markdown" for a single card or the entire library; the button "is only available in the Recall Web App and not in the mobile app" — [Recall docs: Exporting content](https://docs.recall.it/getting-started/7-exporting-content); user feedback: `@card:` links are not converted to `[[wikilinks]]`, tags use a non-Obsidian format, embedded media/LaTeX break — [Recall feedback board](https://feedback.recall.it/feature-requests/p/obsidian-plugin)
- Positioning against Obsidian: "Obsidian gives you a powerful local vault, but you build every link yourself", Recall "connects material automatically and offers export anytime" — [recall.it comparison page](https://www.recall.it/compare/obsidian-alternative) (vendor marketing)

### Inferences
- Free-tier MCP/API is unusual and makes Recall easy to drain into SecondBrain; model choice only on the $38 tier and never local.
- "Knowledge graph" is auto-generated; no document typing or date/person querying documented.

### Gaps
- Company, founders, funding, privacy/training policy and citation behaviour not found.

---

## mymind

### Takeaway
mymind is an independent, "zero outside funding" visual bookmarking/notes app ($7.99 or $12.99 per month; $79/$129 per year) that promises no tracking, no ads and that "Nobody can see what you save"; its AI is tagging/OCR/summaries rather than chat, and export yields original files plus a `cards.csv` (no import exists).

### Cited Findings
- Pricing (2026-10-09): Student of Life $7.99/month or $79/year; Mastermind $12.99/month or $129/year (adds Advanced AI, Reading Mode, Article Backup, video up to 500 MB, Same Vibe, AI Summaries, custom iOS icons, PDF Analyzer); Newton $299/year "not yet available"; The Bookmarker $4.99/month with no AI; claims: "Nobody can see what you save to your mind", "We don't track your data and will never sell it", "We don't run ads", "zero outside funding"; AI features: image tagging, image text recognition, Smart Spaces, Serendipity, AI Summaries, PDF Analyzer — [mymind pricing](https://access.mymind.com/pricing)
- Export: Account → "Export my mind"; saved images/PDFs are written as individual files and "cards.csv" holds notes, quotes, bookmark links and text highlights; works only in current Chrome/Edge; "mymind does not support an import function right now" — [mymind help: Can I export?](https://mymind.helpscoutdocs.com/article/18-can-i-export)

### Inferences
- No chat-over-notes, API or MCP found; mymind is a capture/recall tool, not an "ask your notes" product. Low lock-in via CSV+files, but no Markdown.

### Gaps
- AI model provider and training policy not found; no API/MCP documentation found.

---

## Fabric (fabric.so)

### Takeaway
Fabric is a cloud "AI workspace" for files, links and notes with plans Plus $8 / Pro $18 / Max $54 per month (billed yearly) differentiated by storage and multiplied AI usage, an official OAuth MCP at `https://mcp.api.fabric.so/mcp` exposing 19 tools including keyword+semantic search, read, create, edit, move and delete, and a "Fabric API"; its pricing page has changed structure within 2026 and company details were not found.

### Cited Findings
- Pricing (fetched 2026-10-09): Plus $8/month "Billed yearly at $96/yr" — 50 GB, 50 MB upload limit, 5x AI usage; Pro $18/month "$216/yr" — 250 GB, 15x AI usage, up to 10 scheduled AI jobs, "Premium data connections"; Max $54/month "$648/yr" — 1 TB, 45x AI usage, up to 50 scheduled jobs; Teams (pooled usage); AI top-ups with spending limits; "Fabric API" listed under Extras; no refunds — [Fabric pricing](https://fabric.so/pricing-and-plans). An earlier search snippet of the same URL showed Free $0 (250 MB, 10 spaces, 1,000 items), Basic $5/month, Pro $12.50/month — structure appears to have changed during 2026 (conflict noted).
- MCP (official guide): endpoint `https://mcp.api.fabric.so/mcp`, "the standard Model Context Protocol with OAuth authentication"; tools: fabric-create-bookmark/folder/memory/note/task, fabric-delete-item, fabric-delete-task, fabric-edit-memory/notepad/task, fabric-find-items ("Search your saved links, files and notes by keyword and semantic similarity"), fabric-list-memories/tasks, fabric-move-items, fabric-read-item, fabric-read-memory, fabric-rename-item, fabric-search-memories, fabric-search-tags; clients Claude Code, Claude Desktop, Cursor, Windsurf, Codex, VS Code/Copilot, ChatGPT; no plan requirement stated — [Fabric MCP setup guide](https://fabric.so/guide/ai-tools/MCP-setup)
- A reviewer notes Pro AI credits "can run out quickly" — [recall.it summary of a video review](https://www.recall.it/summary/tool-finder/i-tried-fabric-again) (secondary)

### Inferences
- Fabric's MCP gives agents unrestricted delete/move/edit — the opposite of SecondBrain's proposal gate.
- Hybrid (keyword+semantic) search is explicitly exposed, a rare disclosure.

### Gaps
- Company, funding, privacy/training policy, export formats and citation behaviour not found (search budget exhausted before the funding query ran).

---

## Evernote (Bending Spoons) — v11 AI

### Takeaway
Evernote v11 (2026-01-19) added an OpenAI-built AI Assistant, Semantic Search and AI Meeting Notes, and Bending Spoons restructured plans to Starter ($99/year) and Advanced ($249.99/year) in July 2026 with a 1,000-note cap on Starter — prompting long-time users to leave; it also runs an official MCP server (read + create) for all paid users.

### Cited Findings
- v11 (2026-01-19): "first major product update in five years"; AI Assistant "developed in close collaboration with OpenAI" (chat with notes, tasks, calendar; browse the web); Semantic Search (natural-language, e.g., "UK vacations" finds "London Trip"); AI Meeting Notes with speaker identification; features available to all customers — [TechRadar](https://www.techradar.com/pro/evernote-unveils-its-biggest-update-yet-and-unsurprisingly-theres-ai-involved); press release — [Business Wire, 2026-01-19](https://www.businesswire.com/news/home/20260119103272/en) (page returned 403 on fetch; contents via TechRadar)
- Plan limits (compare page, no prices shown): Free 50 notes, 1 notebook, 1 device, 1 GB; Starter 1,000 notes, 20 notebooks, 3 devices, 5 GB, 5 external calendars; Advanced unlimited* (safeguard limits apply); Flexible and Unlimited tiers; AI features listed: AI Assistant, Semantic Search, AI Transcribe, AI Edit, AI Cleanup (mobile only), "MCP connection" — [Evernote compare plans](https://evernote.com/compare-plans)
- Prices from the Bending Spoons "Moving Day" email quoted by users (thread started 2026-07-11): "Evernote Advanced (USD 20.83/month, billed annually at USD 249.99, or USD 24.99/month, billed monthly)"; "Evernote Starter (USD 8.25/month, billed annually at USD 99.00, or USD 14.99/month, billed monthly)"; a 12,000-note user calls AI search results "useless and irrelevant" and is migrating to DEVONthink/Apple Notes; others moving to UpNote/Apple Notes — [TidBITS Talk](https://talk.tidbits.com/t/evernote-2026-price-tiers-floored-me/33792)
- MCP (official): "The Evernote MCP server is available to all paid Evernote users"; acts as "a secure gateway between your Evernote account and external AI tools"; capabilities listed: reading existing notes and creating new notes; example client Claude — [Evernote help: MCP](https://help.evernote.com/hc/en-us/articles/51596325640339-Evernote-MCP-Model-Context-Protocol)

### Inferences
- Evernote's AI is OpenAI-hosted; no model choice. The 2026 repricing is a textbook "price increase" continuity event. Export (ENEX/HTML) is long-standing but was not re-verified here.

### Gaps
- Evernote AI privacy/training terms and export documentation not fetched.
- Which plans include each AI feature not stated on the compare page.

---

## Microsoft OneNote — Copilot Notebooks

### Takeaway
Copilot Notebooks in OneNote are a Microsoft 365 "gather sources, then ask Copilot" workspace (chats, Word/Excel/PowerPoint files, OneNote pages, links, Teams meetings) for Copilot/Copilot Chat-licensed and M365 Personal/Family/Premium users; it is being rolled out by default (no admin off-switch on Mac, Aug 2026) and offers no API/MCP or export of its own.

### Cited Findings
- Official: notebooks gather "Copilot chats, Microsoft 365 files, OneNote pages, links, and more"; Copilot gives "insights grounded in the content of your notebook" and generates summaries, action items and drafts; "available to Microsoft Copilot and Copilot Chat licensed users"; "Accounts must have a SharePoint or OneDrive license"; M365 Personal, Family, Premium (and Pro) subscribers can also use it; citation format and data handling not described — [Microsoft Support](https://support.microsoft.com/en-us/microsoft-365-copilot/what-are-microsoft-365-copilot-notebooks-in-onenote)
- Mac rollout GA early-to-mid August 2026, "enabled by default without admin controls", requires Microsoft 365 Copilot license — [Message Center MC1409042](https://mc.merill.net/message/MC1409042); suggested artifact creation (Word/Excel/PowerPoint) preview July, GA August 2026 — [Roadmap RM566870](https://mc.merill.net/message/RM566870); Teams meeting transcripts/notes as knowledge sources — [M365 admin digest](https://m365admin.handsontek.net/?p=20929)

### Inferences
- Effectively NotebookLM-for-M365; content stays in SharePoint/OneDrive; for an individual it requires a Copilot or consumer M365 subscription and provides no agent surface.

### Gaps
- Citation precision, source limits and pricing of consumer Copilot access not fetched.

---

## Apple Notes with Apple Intelligence (brief)

### Takeaway
Apple Notes gains on-device/Private-Cloud transcription of audio recordings and phone calls with Apple Intelligence summaries (iOS 18.1+/iOS 26, macOS 15.1+, M1/A17 Pro-class hardware); it has no chat-over-notes, no API or MCP, and export is per-note.

### Cited Findings
- Mac: record audio in Notes and generate a summary of the transcript; requires a Mac with M1 or later and macOS 15.1 or later with Apple Intelligence on; recorded iPhone calls appear in Notes — [Apple Support (macOS User Guide)](https://support.apple.com/en-al/guide/mac-help/mchl2102c2ae/mac)
- iPhone: needs iPhone 15 Pro/Pro Max or iPhone 16 family and iOS 18.1; call recording from the Phone app with transcripts in Notes — [Tom's Guide](https://www.tomsguide.com/phones/iphones/how-to-summarize-audio-recordings-on-your-iphone-with-apple-intelligence); carrier guide: summaries "available with Apple Intelligence in iOS 26 or later" — [AT&T device support](https://www.att.com/device-support/article/151263/apple/iphone-16/)

### Gaps
- No Apple page fetched on training/data use for Notes summaries (Apple's Private Cloud Compute claims not verified here); export and API absence are from general knowledge, not fetched sources.

---

## Craft (brief)

### Takeaway
Craft is a design-led docs/notes app with a free Starter (1,500 blocks, 1 GB, 15 AI credits), Plus at $8/month (or $6.40 on the other billing period) with 50 AI credits/month, Family and Team plans; AI uses OpenAI GPT-4o-class models by tier; export includes Markdown and TextBundle but bulk export is Mac/iPad-only; no official MCP found.

### Cited Findings
- Pricing (2026-10-09): Starter free — 15 AI credits, 1 GB, 1,500 blocks, 25 MB uploads; Plus "$8.0 or $6.4 per month" (billing period unlabeled) — 50 AI credits/month, unlimited content/storage; Family "$15.0 or $12.0" for 2-6 Plus accounts; Team $50/month for up to 10 Plus accounts — [Craft pricing](https://www.craft.do/pricing); tiers list "50 requests / month" on GPT-4o mini vs "500 requests / month" on GPT-4o — [craft.do/pricing (search snippet)](https://craft.do/pricing)
- Export: documents, folders or entire spaces; formats include Markdown (.md) and TextBundle (.textbundle, keeps images/attachments); "Bulk exporting multiple documents at once is only available on Mac and iPad" — [Craft support: Export](https://support.craft.do/en/import-and-export/export)
- Only a community MCP server found — [phkus/craft-mcp](https://glama.ai/mcp/servers/@phkus/craft-mcp/blob/98bb54440eeba0690e9e54202b8637972d7c21ed/CLAUDE.md)

### Gaps
- Chat-over-notes, citation behaviour and privacy terms not researched.

---

## Notable 2025-2026 entrants and market signals

### Takeaway
The "AI second brain" category in 2026 is dominated by vendor-written roundups; the notable capital events are Granola's $125M Series C (March 2026, $1.5B valuation) and a $10M seed for a personal-AI agent "Persona" (Oct 2026), while two incumbents pivoted toward openness (Reflect Open, open-source local Markdown) or split products (Tana).

### Cited Findings
- Granola: $125M Series C led by Index Ventures (Danny Rimer) with Kleiner Perkins, March 2026, valuation from $250M to $1.5B in under a year, ~$192M total; MCP server launched February 2026; personal API (business/enterprise plans) and enterprise API; Spaces — [Reworked](https://www.reworked.co/digital-workplace/granola-raises-125m-launches-enterprise-context-tools/); [Mezha](https://mezha.net/eng/bukvy/granola_raised_-125m/); Manus MCP connector — [createwith.com](https://www.createwith.com/tool/granola/updates/granola-launches-mcp-connector-for-manus-ai-to-enable-context-aware-workflows)
- Persona (Zach Yadegari, Cal AI co-founder): $10M raised, led by Vine Ventures, reported 2026-10-08 — [TechCrunch](https://techcrunch.com/2026/10/08/cal-ais-19-year-old-founder-just-raised-10m-for-his-new-ai-startup)
- Vendor roundups of "best AI second brain apps 2026" (each ranks its own product first): Tana — [tana.inc](https://tana.inc/blog/best-second-brain-apps-2026); Saner.ai — [saner.ai](https://www.saner.ai/blogs/10-best-second-brain-ai-apps); Marqly — [marqly.com](https://www.marqly.com/blog/best-second-brain-apps-2026); Notelyn — [notelyn.com](https://www.notelyn.com/blog/second-brain-apps); buildin.ai — [buildin.ai](https://buildin.ai/blog/best-second-brain-apps-2026); Everpaper (Mac app "still in private beta") — [everpaper.app](https://everpaper.app/docs/blog/best-ai-second-brain-apps-2026/); Storyflow — [storyflow.so](https://storyflow.so/blog/best-ai-second-brain-apps-2026); Supernormal — [supernormal.com](https://www.supernormal.com/blog/best-second-brain-apps)
- Hjarni, a newer competitor, publishes comparison pages against Tana and Heptabase — [hjarni.com](https://hjarni.com/compare/hjarni-vs-tana)
- Community signal: r/PKMS recent threads include "voice notes feeling clunky in most productivity Apps in 2026" and "Notion, Obsidian, Jira... still not happy" — [GummySearch r/PKMS snapshot](https://gummysearch.com/r/PKMS); a 2026 guide from a voice-capture vendor argues for Markdown on the user's own device — [yaps.ai](https://www.yaps.ai/ja/blog/ai-second-brain-app)

### Inferences
- Money is flowing to meeting-capture (Granola) and agent-memory, not to classic PKM; several small consumer entrants (Everpaper, Storyflow, Marqly, Notelyn) are pre-traction and carry high continuity risk.
- The directional trend (Reflect Open, Tana proposals, Heptabase read-only MCP mode, Readwise export tool) is toward openness and agent access — the niche SecondBrain targets.

### Gaps
- No 2026 funding round found for a company self-described as a "second brain" PKM app (searches returned BCI and general-AI rounds).
- Everpaper/Storyflow/Hjarni/Marqly/Notelyn pricing, export and privacy not researched.

---

## Key question: Which products let the user retrieve everything as plain files, and which effectively lock content in?

### Takeaway
Full plain-file export exists for Heptabase (Markdown + All-Data.json), Reflect Open (notes are local Markdown), Readwise Reader (MCP-triggered Markdown ZIP), Recall (Markdown, web only), Craft (Markdown/TextBundle) and Saner.ai (HTML/Markdown); Notion and Evernote export but lossily/with limits; NotebookLM, Mem (JSON by email), mymind (CSV + files), Copilot Notebooks, Apple Notes and Limitless-era data are effectively locked in or require conversion.

### Cited Findings
- Heptabase: Markdown export of all notes plus `All-Data.json`; whiteboard layout may not survive — [Heptabase support](https://support.heptabase.com/en/articles/10364279-how-sustainable-is-heptabase-can-i-easily-export-my-data-and-move-on)
- Reflect Open: "ordinary Markdown files on your computer" — [Reflect blog](https://reflect.app/blog/reflect-open)
- Readwise: `reader_export_documents` ZIP of Markdown — [readwise.io/mcp](https://readwise.io/mcp)
- Recall: whole-library Markdown export, web app only — [Recall docs](https://docs.recall.it/getting-started/7-exporting-content)
- Craft: Markdown and TextBundle; bulk export Mac/iPad only — [Craft support](https://support.craft.do/en/import-and-export/export)
- Notion: Markdown & CSV/HTML/PDF, whole workspace, but one database view, no form views, callouts as HTML, 7-day link, up to 30 hours, admins can disable — [Notion help](https://www.notion.com/help/export-your-content)
- Tana: "export as markdown" exists and is being improved (March 2026) — [Tana](https://outliner.tana.inc/blog/tana-current-march-2026); JSON also per competitor — [Hjarni](https://hjarni.com/compare/hjarni-vs-tana)
- Capacities: "Full support for import and export" on free tier; formats unspecified — [Capacities pricing](https://capacities.io/pricing)
- Mem: JSON export via settings, emailed link (third-party description) — [Amplenote](https://public.amplenote.com/uHhuZS9DTwF5tPEy5gr2ARYD)
- mymind: files + `cards.csv`, no import — [mymind help](https://mymind.helpscoutdocs.com/article/18-can-i-export)
- NotebookLM: no bulk export; copy-paste and unofficial extensions — [Supademo](https://supademo.com/blog/how-to-export-notes-from-notebooklm); [nlmtools](https://www.nlmtools.com/blog/notebooklm-export-sources)
- Limitless: in-app export of all data added at acquisition, regional accounts deleted after 2025-12-19 — [limitless.ai](https://www.limitless.ai/); [TechInformed](https://techinformed.com/meta-acquires-limitless-pendant-users-moved-to-free-unlimited-plan/)
- Microsoft Recall: encrypted export only for EEA, one-time key — [The Register](https://theregister.com/2025/06/16/microsoft_snapshot_export_recall)

### Inferences
- "Exportable" rarely means "re-importable with structure": Heptabase whiteboards, Notion relations, Recall links and Tana graphs all degrade on export; SecondBrain's Markdown-vault-as-source-of-truth avoids the export step entirely.

### Gaps
- Export documentation for Evernote, Tana (official), Capacities (formats), Reflect Classic and Fabric not fetched.

---

## Key question: Which cite the exact passage behind an answer, versus a whole-document reference or nothing?

### Takeaway
Only NotebookLM/Gemini Notebook is documented as linking inline citation numbers to the specific passage in the source; Notion AI, Mem, Readwise Global Ghostreader and Tana cite sources at page/note/meeting level; mymind, Apple Notes, Craft and Recall have no documented citation behaviour.

### Cited Findings
- NotebookLM: "click any citation number to jump to the specific passage in the original source" — [Bowdoin KB](https://bowdoin.teamdynamix.com/TDClient/1814/Portal/KB/ArticleDet?ID=171371); Google: "inline citations now take you directly to supporting passages" — [Google blog](https://blog.google/technology/ai/notebooklm-goes-global-support-for-websites-slides-fact-check/); short sources may be cited whole — [CASRAI](https://www.casrai.org/guides/notebooklm-for-research-workflows); cited pages still need verification — [st-hakky](https://book.st-hakky.com/en/service/how-to-use-notebooklm-and-information-about-hallucination)
- Notion: "answers ... with citations", granularity unstated — [Notion AI FAQs](https://www.notion.com/help/notion-ai-faqs)
- Mem: "answers questions over your notes with source links" — [Tana blog](https://tana.inc/blog/best-second-brain-apps-2026)
- Readwise: Global Ghostreader answers "grounded in your saved content with citations" — [Readwise](https://readwise.io/reader/update-aug2026)
- Tana: chat answers "grounded in the source meeting" — [Tana blog](https://tana.inc/blog/best-second-brain-apps-2026)
- Evernote: a power user reports AI search returning "useless and irrelevant" results — [TidBITS Talk](https://talk.tidbits.com/t/evernote-2026-price-tiers-floored-me/33792)

### Inferences
- None of the products documents distinguishing "nothing found" from "retrieval degraded", nor pins citations to a document revision; SecondBrain's design would be unique on both counts if shipped.

### Gaps
- No product publishes citation-accuracy metrics; Notion/Readwise/Heptabase/Capacities granularity unconfirmed.

---

## Key question: Which allow a user-chosen or local model, versus a fixed vendor model?

### Takeaway
Bring-your-own-key is offered by Reflect Open (OpenAI/Anthropic/Google/OpenRouter), Saner.ai (external models) and Readwise (OpenAI key for custom Ghostreader prompts); vendor menus (no own key) are offered by Notion (credits), Tana, Heptabase (tier-gated), Recall (Max tier); fixed models: NotebookLM (Gemini), Evernote (OpenAI), Copilot Notebooks, Apple Notes, Mem, mymind, Craft. No cloud product offers a local model; Tana lists local models as "on the horizon".

### Cited Findings
- Reflect Open: own key for OpenAI, Anthropic, Google, OpenRouter — [Reflect blog](https://reflect.app/blog/reflect-open)
- Saner.ai: external GPT/Claude/Gemini "pay-as-you-go or through your own API key" — [Saner.ai pricing](https://www.saner.ai/pricing)
- Readwise: BYO OpenAI key, GPT-5 to GPT-5.6 selectable — [Readwise](https://readwise.io/reader/update-aug2026)
- Notion: switch models; premium models cost credits — [Notion AI FAQs](https://www.notion.com/help/notion-ai-faqs)
- Tana: Claude Sonnet/Opus 4.6, GPT-5.4 (default), Gemini 3.1 Pro; local models "On the horizon" — [Tana April 2026](https://outliner.tana.inc/blog/tana-current-april-2026); [Tana March 2026](https://outliner.tana.inc/blog/tana-current-march-2026)
- Heptabase: Gemini-only on Pro; GPT/Claude/Gemini on Premium — [Heptabase pricing](https://heptabase.com/pricing)
- Recall: model selection on Max; auto-selection on Plus — [Recall pricing](https://www.recall.it/pricing)
- Craft: GPT-4o mini vs GPT-4o by tier — [craft.do/pricing](https://craft.do/pricing)
- Evernote: AI Assistant built with OpenAI — [TechRadar](https://www.techradar.com/pro/evernote-unveils-its-biggest-update-yet-and-unsurprisingly-theres-ai-involved)
- Capacities: MCP connectors let the user's own AI client (incl. LM Studio) query the space — [Capacities docs](https://docs.capacities.io/reference/ai-chat-connectors)

### Inferences
- Capacities' LM Studio connector is the only route found to a fully local model over a commercial PKM, and it works by letting a local client read the cloud-stored notes, not by running the vendor's AI locally.
- SecondBrain's provider-agnostic roles with a vLLM reference and a transport-enforced `local_only` switch have no commercial equivalent in this set.

### Gaps
- Mem's and Heptabase's underlying providers not confirmed from primary sources.

---

## Key question: Which have official MCP servers or APIs so Claude/ChatGPT/Cursor can read and write them?

### Takeaway
Official hosted MCP with read+write: Notion, Readwise, Fabric, Heptabase, Capacities (AI Chat Connectors), Tana (remote + local), Evernote (read + create), Mem, Recall (free tier) and Granola; NotebookLM has none (enterprise alpha REST only); Reflect Classic has a write-only REST API and Reflect Open relies on files/CLI; Copilot Notebooks, Apple Notes, mymind and Craft have no official MCP.

### Cited Findings
- Notion — [developers.notion.com/docs/mcp](https://developers.notion.com/docs/mcp)
- Readwise `https://mcp2.readwise.io/mcp` — [readwise.io/mcp](https://readwise.io/mcp)
- Fabric `https://mcp.api.fabric.so/mcp` — [Fabric guide](https://fabric.so/guide/ai-tools/MCP-setup)
- Heptabase `https://api.heptabase.com/mcp` with read-only/write authorisation — [Heptabase support](https://support.heptabase.com/en/articles/12679581-how-to-use-heptabase-mcp)
- Capacities AI Chat Connectors (read/write, per-space) and REST API v2 — [Capacities docs](https://docs.capacities.io/reference/ai-chat-connectors); [developers.capacities.io](https://developers.capacities.io/)
- Tana: "Integrations and full MCP" on Pro — [Tana pricing](https://tana.inc/pricing); endpoints per third party — [ContextBolt](https://contextbolt.com/blog/tana-mcp/)
- Evernote MCP for all paid users (read, create) — [Evernote help](https://help.evernote.com/hc/en-us/articles/51596325640339-Evernote-MCP-Model-Context-Protocol)
- Mem REST API + MCP — [Mem guide](https://get.mem.ai/guides/mem-api-mcp-custom-workflows)
- Recall "API and MCP access" on Free — [Recall pricing](https://www.recall.it/pricing)
- Granola MCP (Feb 2026) and personal/enterprise APIs — [Reworked](https://www.reworked.co/digital-workplace/granola-raises-125m-launches-enterprise-context-tools/)
- NotebookLM: enterprise `v1alpha` API only; community MCPs scrape — [Google Cloud](https://cloud.google.com/agentspace/notebooklm-enterprise/docs/api-notebooks); [MCP.Directory](https://mcp.directory/blog/notebooklm-mcp-complete-guide-2026)
- Reflect Classic API write-only/append-only — [Reflect blog](https://reflect.app/blog/reflect-update-api)

### Inferences
- Write safety across MCP implementations is mostly "whatever the connected agent does": Fabric exposes delete tools, Capacities grants read+write per space, Tana already needed trash-restore for MCP deletions; only Heptabase (read-only mode) and Tana (proposal review) add guard rails. SecondBrain's proposal-only writes would be the strictest.

### Gaps
- Webhook support was not found documented for any product in this pass.

---

## Key question: Privacy policies on training, retention and third-party model providers

### Takeaway
Stated no-training policies: Google (Workspace/consumer, except feedback), Notion (no training; 30-day provider retention on non-Enterprise, zero retention on Enterprise), Mem (no training on integration queries); on-device by design: Microsoft Recall; promises without AI detail: mymind ("Nobody can see what you save"); unknown/undisclosed: Saner.ai, Heptabase, Capacities, Fabric, Recall, Evernote, Craft; Limitless users had to accept Meta-era terms.

### Cited Findings
- Google Workspace: not used to train "unless you provide feedback"; no human review; region settings do not apply to Gemini Notebook caches — [Google Workspace admin](https://knowledge.workspace.google.com/admin/generative-ai/notebooklm); Gemini-app notebooks follow the Gemini Apps notice incl. human review — [memx.app quoting Google](https://memx.app/blog/is-notebooklm-private/)
- Notion: no training; 30 days or fewer retention (non-Enterprise), zero retention (Enterprise); OpenAI embeddings in Turbopuffer; SOC 2/ISO 27001 — [Notion AI security](https://www.notion.com/help/notion-ai-security-practices)
- Mem: "We do not use integration queries to train AI models"; subprocessors at trust.mem.ai; policy revised 2026-08-13 — [Mem privacy](https://get.mem.ai/privacy-policy)
- Recall (Windows): local, encrypted, TPM/VBS, no cloud — [Microsoft Support](https://support.microsoft.com/en-us/windows/privacy-and-control-over-your-recall-experience-d404f672-7647-41e5-886c-a3c59680af15)
- mymind: no tracking/selling/ads, zero outside funding — [mymind pricing](https://access.mymind.com/pricing)
- Capacities: the connected AI tool's privacy policy governs data it pulls via MCP — [Capacities docs](https://docs.capacities.io/reference/ai-chat-connectors)
- Limitless: updated Privacy Policy and ToS required post-acquisition — [TechInformed](https://techinformed.com/meta-acquires-limitless-pendant-users-moved-to-free-unlimited-plan/)
- Reflect Open: Private notes never sent to AI; AI requests go directly to the user's chosen provider — [Reflect blog](https://reflect.app/blog/reflect-open)

### Inferences
- No commercial product discloses per-request data exposure to the model (SecondBrain's egress manifest) or caps it; Notion's 30-day provider retention is the most explicit statement of what leaves the vendor.

### Gaps
- Privacy policies for Heptabase, Capacities, Saner.ai (AI section), Fabric, Recall, Evernote, Readwise and Craft were not fetched.

---

## Key question: Shutdowns, pivots, price increases or acquisitions in 2024-2026 (continuity risk)

### Takeaway
Acquisitions: Limitless by Meta (2025-12-05; sales ended, regions shut, Rewind sunset), Bee by Amazon (agreed 2025-07-22). Pivots/renames: NotebookLM to Gemini Notebook (2026-07-16), Tana split and rename to Tana Outliner (Mar-May 2026), Reflect Open rewrite (2026-07-14), Mem 2.0 relaunch (2025-10-01), Evernote v11 under Bending Spoons (2026-01-19). Price/packaging changes: Notion AI add-on removed (May 2025) and credits (May 2026); Google AI Ultra $250 to $100/$200 (May 2026); Mem $12 flat to $29 "Pro" ladder (by Aug 2026); Tana $14 to $20 and a $120 tier (2026); Evernote Starter $99/Advanced $249.99 per year (July 2026); Capacities up then down (Feb/Apr 2026); Heptabase pay-as-you-go overage (Aug 2026).

### Cited Findings
- Limitless/Meta — [limitless.ai](https://www.limitless.ai/); [TechInformed](https://techinformed.com/meta-acquires-limitless-pendant-users-moved-to-free-unlimited-plan/)
- Bee/Amazon — [Entrepreneur](https://www.entrepreneur.com/business-news/amazon-acquires-bee-startup-behind-eavesdropping-wearable/494971)
- Gemini Notebook rename — [Google blog](https://blog.google/innovation-and-ai/products/gemini-notebook/notebooklm-gemini-notebook/); Ultra repricing — [Engadget](https://www.engadget.com/2176060/the-google-ai-ultra-plan-now-starts-at-100-a-month)
- Tana split/rename — [Tana March 2026](https://outliner.tana.inc/blog/tana-current-march-2026); [releases wk 15-20](https://outliner.tana.inc/releases/2026-wk-15-20); price changes — [CostBench](https://costbench.com/changelog/tana-price-increase-2026-02/)
- Reflect Open — [Reflect blog](https://reflect.app/blog/reflect-open)
- Mem 2.0 and repricing — [Mem blog](https://get.mem.ai/blog/mem-2-dot-0); [usagepricing](https://www.usagepricing.com/blueprint/mem)
- Notion add-on removal and credits — [coworker.ai](https://coworker.ai/blog/notion-ai-pricing); [Airframe](https://www.airframe.ai/product/notion-com-product-agents/analysis)
- Evernote repricing and user exodus — [TidBITS Talk](https://talk.tidbits.com/t/evernote-2026-price-tiers-floored-me/33792)
- Capacities — [CostBench](https://costbench.com/changelog/capacities-price-decrease-2026-04/); Heptabase overage — [usagepricing](https://usagepricing.com/blueprint/activity/heptabase-2026-08-28-packaging)
- Low-risk signals: Heptabase cash-flow positive with open-source pledge — [Heptabase support](https://support.heptabase.com/en/articles/10364279-how-sustainable-is-heptabase-can-i-easily-export-my-data-and-move-on); mymind zero outside funding — [mymind](https://access.mymind.com/pricing)

### Inferences
- Within 18 months every in-depth product changed name, owner, pricing model or core architecture at least once; a self-hosted vault's main continuity advantage is immunity to these events (at the cost of self-maintenance).

### Gaps
- Funding status 2025-26 for Mem, Capacities, Fabric, Recall, Readwise not found.

---

## Key question: How do they handle structured questions (dates, people, document types) versus similarity search?

### Takeaway
Only the object/outliner tools (Tana supertags/fields with `parent.field` search expressions; Capacities typed objects with properties and "smart queries"; Notion databases with Agent/Enterprise Search) have first-class structured properties an AI can filter on; NotebookLM, Mem, Readwise, Recall, Fabric, Evernote and the rest answer who/when/type questions by LLM reading or semantic similarity over text, and none document SQL-style pre-filtering before ranking.

### Cited Findings
- Tana: `parent.field` search expression works with multiple values (meetings/attendees); "Types" and "Attributes" in the new product — [Tana April 2026](https://outliner.tana.inc/blog/tana-current-april-2026); [Tana May 2026](https://outliner.tana.inc/blog/tana-current-may-2026)
- Capacities: unlimited custom object types; "smart queries" on Pro; MCP reads properties and body — [Capacities pricing](https://capacities.io/pricing); [Capacities docs](https://docs.capacities.io/reference/ai-chat-connectors)
- Notion: Agent creates/edits databases; a database-query incident meant "Notion AI may get less reliable/complete answers from databases" — [Notion AI FAQs](https://www.notion.com/help/notion-ai-faqs); [Pulsetic Notion incident log](https://pulsetic.com/status/notion/incidents/4891/)
- Mem: API search "by content, date, or meaning" — [Mem guide](https://get.mem.ai/guides/mem-api-mcp-custom-workflows)
- Fabric: "keyword and semantic similarity" search — [Fabric guide](https://fabric.so/guide/ai-tools/MCP-setup); Heptabase: `search_by_keyword` and `search_by_semantic` — [Heptabase support](https://support.heptabase.com/en/articles/12679581-how-to-use-heptabase-mcp)
- Evernote: Semantic Search example ("UK vacations" finds "London Trip") — [TechRadar](https://www.techradar.com/pro/evernote-unveils-its-biggest-update-yet-and-unsurprisingly-theres-ai-involved)

### Inferences
- SecondBrain's "filters before scoring, then RRF, optional rerank, degraded flag" is more explicit than anything disclosed here; the nearest commercial analogues are Tana and Capacities (typed data + MCP), neither of which documents its ranking.

### Gaps
- No vendor publishes retrieval architecture (chunking, embeddings, fusion); all retrieval claims above are feature-level.
