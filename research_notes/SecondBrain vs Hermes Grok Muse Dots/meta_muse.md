# Meta Muse (personal AI agent, launched 2026-09-08) as a competitor to SecondBrain — state as of 2026-10-09

Scope note: "Muse" here is Meta's consumer personal-agent *product* (app + muse.ai + WhatsApp), internally code-named "Hatch". It is distinct from the **Muse Spark** *model family* from Meta Superintelligence Labs (MSL), which powers it. Section 1 disambiguates. All prices and figures are dated. Sources are labelled **[Meta primary]**, **[press]**, **[researcher]**, **[community]** or **[secondary/SEO]**. Several originals (The Verge, WIRED, ZDNET, Axios, Inc., GeekWire, Google Play) returned 403/blocked to my fetcher; where I cite them I say which source relayed them.

---

## 0. Disambiguation, identity, launch, availability, platforms, age

### Takeaway
Muse (agent) launched 2026-09-08 in the US on iOS, Android and muse.ai (plus WhatsApp), expanded to Canada on 2026-09-18 via a Meta post on X (not a newsroom post), and is rated 18+. It is powered by Muse Spark 1.3; the Muse Spark family (1.0 Apr 2026, 1.1 Jul 9, 1.2 Aug 5, 1.3 Sep 2, plus the open-weight 30B "Muse Glimmer" Aug 10) is a separate product line sold to developers via the Meta Model API.

### Cited Findings
- Meta's launch post is titled "Introducing Muse: The World's First Personal AI Agent Built for Everyone", dated 2026-09-08 with an update stamp of 2026-09-30; it says "Muse is rolling out in the US on iOS, Android, and muse.ai, and coming soon to AI glasses" and that people talk to it "in the Muse app or directly in WhatsApp" — [Meta primary: about.fb.com](https://about.fb.com/news/2026/09/introducing-muse-personal-ai-agent/)
- The launch post says Muse is "powered by Muse Spark, Meta's most capable model to date" — [Meta primary](https://about.fb.com/news/2026/09/introducing-muse-personal-ai-agent/); Meta's security post names the version: "Muse Spark 1.3" — [Meta primary: research.meta.ai](https://research.meta.ai/blog/security-and-safety-for-ai-agents-our-approach-with-muse)
- Muse Spark (model) was introduced 2026-04-08 as "the first in the Muse family of models developed by Meta Superintelligence Labs" — [Meta primary](https://about.fb.com/news/2026/04/introducing-muse-spark-meta-superintelligence-labs/); TechCrunch coverage of the same day — [press](https://techcrunch.com/2026/04/08/meta-debuts-the-muse-spark-model-in-a-ground-up-overhaul-of-its-ai/)
- Model version timeline (Wikipedia infobox/body, last edited 2026-10-08): Muse Spark 1.1 on 2026-07-09 (first with an API), 1.2 on 2026-08-05 (with the "Muse Code" terminal coding agent), 1.3 on 2026-09-02, and Muse Glimmer (30B, Apache 2.0 open weights) on 2026-08-10 — [secondary: Wikipedia](https://en.wikipedia.org/wiki/Muse_Spark); Meta's 1.1 post — [Meta primary](https://ai.meta.com/blog/introducing-muse-spark-meta-model-api/); Meta's Glimmer post — [Meta primary](https://research.meta.ai/blog/introducing-muse-glimmer-open-agentic-model); Muse Code/1.2 developer post — [Meta primary](https://developer.meta.com/ai/resources/blog/build-with-muse-code/)
- Simon Willison ran Glimmer locally and notes it is "a brand new 30B model under a clean Apache 2.0 license" — [researcher](https://simonwillison.net/2026/Aug/10/introducing-muse-glimmer/)
- Meta Model API pricing (no date on page; fetched 2026-10-09): Standard tier muse-spark-1.3/1.2/1.1 at $1.25 input / $0.15 cached / $4.25 output per 1M tokens, "your prompts and completions are not used to train Meta models"; Contributor tier at $0.10 / $0.002 / $0.20 in exchange for "permission to use your prompts and completions to train future Meta models" — [Meta primary: dev.meta.ai](https://dev.meta.ai/docs/pricing-rate-limits)
- Apple App Store listing: "Muse from Meta", seller Meta Platforms, Inc., age rating **18+**, "Free · In-App Purchases", 4.9 average from 193K ratings, #1 in Productivity, requires iOS/iPadOS 18.0+; version history runs from 4.0.0 (Sep 3) through 10.0 (~Oct 7, adds iPad and connectors such as Asana, Canva, QuickBooks) — [Meta primary (store listing)](https://apps.apple.com/us/app/muse-from-meta/id6760173601)
- Meta's subscriptions help page: "You must be 18 or the age of majority in your country" and "Muse and Muse subscriptions are in limited testing and aren't available in all locations yet" — [Meta primary](https://www.meta.com/help/subscriptions/1021145227643680/)
- Canada: Muse "arrived in Canada on September 18, 2026, and quickly reached No. 1 on the App Store"; Canada and the US are "the only two countries where Muse is officially available" — [press: iPhone in Canada, 2026-10-07](https://www.iphoneincanada.ca/2026/10/07/meta-muse-windows-app/); the Canadian launch was announced by Meta "in a post on X" ("Made it to Canada. Where to next? 👀"), initially iOS and web only with Android "still coming" — [press: iPhone in Canada, 2026-09-18](https://www.iphoneincanada.ca/2026/09/18/metas-muse-ai-agent-is-now-available-in-canada/); TechCrunch on 2026-09-25: "currently available only in the U.S. and Canada" — [press](https://techcrunch.com/2026/09/25/meta-is-putting-its-muscle-behind-muse-as-the-ai-app-takes-off/)
- Windows: on 2026-10-07 Microsoft's Pavan Davuluri said Muse for Windows is "coming soon as a native app on Windows with MXC integration"; no release date from either company; Meta has not posted its own announcement — [press](https://www.iphoneincanada.ca/2026/10/07/meta-muse-windows-app/)
- Mac: "Computer use is now available with Muse for Mac. With your permission, Muse can now drive any app on your Mac" (Mac support first announced 2026-09-18, promoted at Connect 2026-09-23) — [press: TechCrunch](https://techcrunch.com/2026/09/23/everything-new-coming-to-metas-ai-agent-muse/)
- Glasses: Meta's 2026-09-23 glasses post says Muse on glasses will help "stay on top of things, take tasks off your plate, and turn long-term goals into action plans, all hands free", with no rollout date — [Meta primary](https://about.fb.com/news/2026/09/introducing-ray-ban-meta-audio-glasses-new-styles-plus-muse/); TechCrunch adds it is wake-word activated and expected in "the coming months" — [press](https://techcrunch.com/2026/09/23/everything-new-coming-to-metas-ai-agent-muse/)
- Adoption (Sensor Tower via TechCrunch, 2026-09-25): ~2.8M downloads in the first two weeks (Sep 8-17) with 55% average day-over-day growth vs ChatGPT's 24%; #1 US App Store since 2026-09-18 and #1 Google Play since 2026-09-19; Meta began house ads on 2026-09-09 and was a top-10 brand by ad spend by 2026-09-22, but ads were only 6% of impressions — [press](https://techcrunch.com/2026/09/25/meta-is-putting-its-muscle-behind-muse-as-the-ai-app-takes-off/)
- Connect keynote date: Wednesday 2026-09-23, Menlo Park — [press: TechCrunch](https://techcrunch.com/2026/09/23/everything-new-coming-to-metas-ai-agent-muse/); [press: NBC News, 2026-09-24](https://www.nbcnews.com/tech/tech-news/meta-muse-ai-agent-response-animated-avatar-cute-rcna599736)

### Inferences
- Everything "Muse" branded now shares a stack: consumer agent (Muse), developer coding agent (Muse Code), hosted models (Muse Spark 1.x), open weights (Muse Glimmer), and media models (Muse Image/Video). A report writer should always say which one is meant.
- Muse Glimmer (30B, Apache 2.0, runs on a single 24 GB consumer GPU per Wikipedia's "better source needed" claim) is relevant to SecondBrain only as a possible *local vLLM model candidate*, not as a Muse-agent feature.

### Gaps
- No Meta newsroom post for the Canada launch exists; the only Meta artefact is a post on X that I could not fetch directly.
- Google Play listing (package `com.facebook.aura`) could not be fetched (truncated), so Android IAP price range and Play data-safety labels are unverified.
- The claim that Muse was "first available via Instagram and WhatsApp" (one search summary) conflicts with Meta's own post and is unsupported.

---

## 1. What does Meta's own announcement say, and which widely repeated claims (pricing tiers, Canada availability) are only in secondary coverage?

### Takeaway
Meta's announcement describes the agent's capabilities, the Secure VM/Sentinel architecture, approvals, an audit trail, the ads/training stance and US rollout, but gives **no prices, no token quotas, no age limit and no countries beyond the US**. The $20 "Power" / $100 "Maximum" tiers *are* on a Meta help page (with weekly token quotas), but the "free up to 100M tokens/week" figure comes from a Zuckerberg Threads post relayed by press, and Canada availability exists only in a Meta X post and press.

### Cited Findings
**What Meta's launch post actually says (2026-09-08, updated 2026-09-30):**
- Muse "doesn't just answer questions, it actually does the work"; it can "open a browser, fill out forms, and negotiate on a person's behalf", with examples of sending email, booking travel and selling a car; "For longer tasks, Muse keeps working after the app is closed and returns when something changes or when it needs approval" — [Meta primary](https://about.fb.com/news/2026/09/introducing-muse-personal-ai-agent/)
- Proactive behaviour: turning a saved Instagram recipe reel into a grocery list; remembering friends' dietary restrictions when sending dinner-party invites — [Meta primary](https://about.fb.com/news/2026/09/introducing-muse-personal-ai-agent/)
- Payments: checkout via "Link (built by Stripe)"; Link's agent wallet "generates a one-time-use card so the user's real card details stay hidden"; Muse is "the first AI agent covered by Link's purchase protections"; Shop Pay "coming soon"; 1Password support planned — [Meta primary](https://about.fb.com/news/2026/09/introducing-muse-personal-ai-agent/)
- Architecture: "Muse Secure VM," "a dedicated secure computer with its own browser"; Sentinel "kept apart from Muse at the system level"; "Nothing Muse does reaches the internet unless the Sentinel approves it"; "Muse has no visibility into people's passwords or payment methods"; Muse Confidential VM "later this year," "encrypted with a key only they hold, so not even Meta can access it" — [Meta primary](https://about.fb.com/news/2026/09/introducing-muse-personal-ai-agent/)
- Control: "Muse checks with the person before sensitive actions like sending an email or making a purchase"; "Muse shows people a complete audit trail of everything it has done and plans to do"; people "choose which apps Muse connects to and exactly how much access it gets" (e.g. email read-only vs send) — [Meta primary](https://about.fb.com/news/2026/09/introducing-muse-personal-ai-agent/)
- Data: "Muse doesn't share a person's conversations or the data in their VM with Meta's ad systems"; "People can also opt out of their interactions being used to train Meta's AI models" — [Meta primary](https://about.fb.com/news/2026/09/introducing-muse-personal-ai-agent/)
- Pricing in the post is only: "It's free for most of what people need, with subscription plans for people who want to do more" — no numbers — [Meta primary](https://about.fb.com/news/2026/09/introducing-muse-personal-ai-agent/)
- Not in the post: Limitless, token limits, age limit, Canada or any non-US country, plan prices, Zuckerberg quotes — [Meta primary](https://about.fb.com/news/2026/09/introducing-muse-personal-ai-agent/)

**Pricing — what is on Meta pages vs. what is only secondary (all as of 2026-10-09):**
- Meta help page "Muse subscriptions" (stamped "4 weeks ago"): Muse "is available for free with a usage limit" (no free quota stated); **Power plan $20/month with 500M Muse tokens per week; Maximum plan $100/month with 3B Muse tokens per week**; "Available benefits will be shown to you during onboarding before you subscribe"; renews monthly, cancel any time; sold at muse.ai and in the mobile app; requires a Meta account and acceptance of the Meta Subscription Terms and Muse Supplemental Terms — [Meta primary](https://www.meta.com/help/subscriptions/1021145227643680/)
- The **free allowance of 100M tokens/week** is attributed to a Zuckerberg Threads post reported by Gizmodo and others — [secondary](https://www.tikr.com/blog/zuckerberg-giving-away-100-million-tokens-a-week-as-openai-and-anthropic-are-forced-to-ration); The Batch (DeepLearning.AI, 2026-09-18) lists "Free (up to 100M tokens/week), $20 (up to 500M/week), and $100 (up to 3B/week)" — [press](https://www.deeplearning.ai/the-batch/how-to-secure-agents-for-the-masses); a tracker site states "The free allowance comes from third-party testing, not Meta" and "Nothing is billed automatically on the free tier" — [secondary/SEO](https://openclawdatabase.com/meta-muse/pricing/)
- Axios (2026-09-08, via Wikipedia because Axios blocks fetching): free tier plus subscriptions at $20 and $100/month; no advertising; Zuckerberg said Meta expects to "profit by taking a small fee from transactions" — [secondary: Wikipedia citing Axios](https://en.wikipedia.org/wiki/Muse_(AI_agent)); TechCrunch at Connect: Muse is "free for a large number of tokens" and Zuckerberg repeated the "small fee from transactions" line — [press](https://techcrunch.com/2026/09/23/everything-new-coming-to-metas-ai-agent-muse/)
- Plan naming: Meta's help page says "Maximum"; some secondary sites say "Max" — [Meta primary](https://www.meta.com/help/subscriptions/1021145227643680/) vs [secondary/SEO](https://openclawdatabase.com/meta-muse/pricing/)
- A "payment card is required to get started, even on the free tier" claim appears only in secondary coverage and is not on Meta's pages — [secondary/SEO: search summary of eesel.ai/usecarly etc.](https://www.usecarly.com/blog/meta-muse-pricing/)

**Canada:** see Section 0 — Meta X post 2026-09-18 and press only; [press](https://www.iphoneincanada.ca/2026/09/18/metas-muse-ai-agent-is-now-available-in-canada/), [press](https://techcrunch.com/2026/09/25/meta-is-putting-its-muscle-behind-muse-as-the-ai-app-takes-off/)

**Other Meta statements relayed by press:**
- Zuckerberg on X: "Meta delayed shipping Muse for several months to focus on safety and security" — [press: NBC](https://www.nbcnews.com/tech/tech-news/meta-muse-ai-agent-response-animated-avatar-cute-rcna599736)
- Zuckerberg at Connect: "The centerpiece of our vision for what we're building is Muse"; Alexandr Wang: "You can walk away from your computer and it keeps working for you on all the jobs you lined up" — [press: TechCrunch](https://techcrunch.com/2026/09/23/everything-new-coming-to-metas-ai-agent-muse/)
- Connect add-ons: Muse Realtime Avatar (video chat with the agent; default avatar "Jolly"), dedicated agent email address "soon", PayPal added alongside Link and Shop Pay, retail partners Best Buy, Gap, Sephora, Walmart, Wayfair, Expedia (Instacart coming), connectors GitHub, Granola, Notion — [press: TechCrunch](https://techcrunch.com/2026/09/23/everything-new-coming-to-metas-ai-agent-muse/)
- "Muse Charm", a palm-sized/keychain device for using Muse without a phone, shown at Connect; no price, "due by the holidays" per press — [secondary: Wikipedia citing Bloomberg 2026-09-23](https://en.wikipedia.org/wiki/Muse_(AI_agent)); [press](https://idevice.com/metas-muse-charm-is-a-keychain-for-talking-to-its-ai-due-by-the-holidays)

### Inferences
- Meta deliberately keeps prices and quotas off the newsroom post and in a help page that says "limited testing", so tier contents can change without a public announcement; the "100M free" figure is a founder statement, not a documented entitlement.
- The weekly-token model (not per-message) means long unattended tasks can consume quota invisibly; Meta does not define a "Muse token" anywhere I found.

### Gaps
- No Meta page defines what a "Muse token" is or how it maps to Model API tokens.
- I could not fetch Zuckerberg's Threads/X posts directly; the "100M tokens/week" and "Made it to Canada" quotes are relayed by press.
- Axios, Inc. and GeekWire pages return 403 to my fetcher; their content is cited via Wikipedia/SAN/AppleInsider.

---

## 2. What is the "private virtual computer" concretely, and what guarantees does Meta make about it?

### Takeaway
Meta's term is **"Muse Secure VM"**: a per-user Linux VM in Meta's cloud (KVM-based per 404 Media) containing a `systemd-nspawn` "runtime cell" for the agent, with credential storage, classifiers, connector workers and the Sentinel egress gate running *outside* the cell. Guarantees today are isolation-by-policy: Meta staff can still access the VM "to support, secure or operate the service"; cryptographic protection from Meta ("Muse Confidential VM") is only with trusted testers and promised "later this year".

### Cited Findings
- Exact Meta wording: Muse runs on "Muse Secure VM," "a dedicated secure computer with its own browser," "a dedicated, virtual machine (VM)" that houses the agent and the person's data, "contained so no one else's agent can reach it" — [Meta primary](https://about.fb.com/news/2026/09/introducing-muse-personal-ai-agent/). (The phrase "private virtual computer" does not appear in Meta's text; NBC paraphrases it as "a private, dedicated computer for each agent" — [press](https://www.nbcnews.com/tech/tech-news/meta-muse-ai-agent-response-animated-avatar-cute-rcna599736).)
- Help center: "Muse runs on an environment in the cloud called a virtual machine ('VM')"; "Every Muse user's VM is isolated so no one else's agent can access it"; "Muse also maintains backups to help protect your agent's history from data loss" — [Meta primary](https://www.meta.com/help/artificial-intelligence/1047255454427887/)
- Technical detail from "How We Built Safety Into Muse" (Tarek Sheasha, VP, MSL, 2026-09-08; `security.muse.ai` redirects here): the agent harness, workspace and tools run in a `systemd-nspawn` runtime cell with its own root filesystem, virtual network interface, filtered syscalls and limited kernel capabilities; "runtime cell root is not host root"; outside the cell run `hatch-safety` (classifiers), `privsep` workers (connector code), `hatch-authd` (credential storage and surrogation), Sentinel, and proxies for inference and telemetry; durable state is in a separate Postgres database; components talk over Unix domain sockets with `SO_PEERCRED` and peer ACLs; connector OAuth tokens are stored in the user's VM, not central Meta infrastructure — [Meta primary](https://research.meta.ai/blog/security-and-safety-for-ai-agents-our-approach-with-muse)
- Sentinel is "the sole permission authority" for connector actions and network egress, returning allow/deny/ask; it evaluates egress at L4 and L7 (hostname, resolved IP, port, method, path, decoded request), applies SSRF restrictions after DNS resolution, and uses eBPF/LSM "tainted egress" so processes that have read user data lose auto-allow — [Meta primary](https://research.meta.ai/blog/security-and-safety-for-ai-agents-our-approach-with-muse)
- Credential surrogation: code in the cell sees only surrogate tokens minted by authd; Sentinel swaps in the real credential at the network boundary; "The agent never sees real tokens" — [Meta primary](https://research.meta.ai/blog/security-and-safety-for-ai-agents-our-approach-with-muse); help center: "Muse can use the Secure Credentials Store to complete an authorized action without the AI model seeing your password" — [Meta primary](https://www.meta.com/help/artificial-intelligence/1047255454427887/)
- Browser sub-agent sees an accessibility tree, not raw DOM, cannot run JavaScript in the page, and is paused during user takeover or credential entry — [Meta primary](https://research.meta.ai/blog/security-and-safety-for-ai-agents-our-approach-with-muse)
- Who can access it today: Meta personnel can access user data when necessary to "support, secure or operate the service" — [press roundup](https://www.nakedcapitalism.com/2026/10/meta-agentic-ai-muse-openai-security-risks-warnings.html); an analysis of Meta's documents says staff access "remains possible" "subject to operational restrictions" and that "Limited data" leaves the VM for model inference and telemetry — [secondary analysis, 2026-09-24](https://superpowerdaily.com/posts/our-review-finds-meta-s-ai-privacy-protections-have-three-different-boundaries)
- Confidential VM: "intended to cryptographically and verifiably prevent Meta from accessing data in your VM," in use with "a small group of trusted testers," design and source shared with external auditors, a continuous inspectable audit planned; **the research post does not mention user-held keys** — [Meta primary](https://research.meta.ai/blog/security-and-safety-for-ai-agents-our-approach-with-muse); the newsroom post does say "encrypted with a key only they hold, so not even Meta can access it" — [Meta primary](https://about.fb.com/news/2026/09/introducing-muse-personal-ai-agent/); help center calls it "an environment we're designing to keep people's information private, even from Meta" — [Meta primary](https://www.meta.com/help/artificial-intelligence/1047255454427887/)
- Axios reported Meta promised a version it cannot use to view a user's workspace "before the end of 2026" — [secondary: Wikipedia citing Axios](https://en.wikipedia.org/wiki/Muse_(AI_agent))
- VM is KVM-based: "each individual Muse instance runs on a kernel-based virtual machine, which connects to, but is supposed to be isolated from, Meta's own critical infrastructure" — [press: 404 Media](https://www.404media.co/meta-rushed-to-fix-muse-vm-escape-vulnerability-immediately-before-launch/)
- Bug bounty: up to $300,000 for a valid report and up to $130,000 for a successful prompt injection affecting one user; contact muse-security@meta.com — [Meta primary](https://research.meta.ai/blog/security-and-safety-for-ai-agents-our-approach-with-muse); Meta's bounty page calls VM-boundary compromise a "first-class security risk" — [press: Futurism](https://futurism.com/artificial-intelligence/meta-muse-data-breach-bank-accounts-email-archives)
- Users can see the VM's files: researchers exported "sanitized copies of /opt/hatch and /home/hatch, with SSH keys removed"; Meta spokesperson Daniel Roberts: "just like with the laptop in front of you, of course you can see the files. Exporting virtual machine data doesn't give people any privileged access to Meta infrastructure or to other people's data" — [secondary relaying The Verge](https://aichief.com/news/muse-will-apparently-let-you-download-its-entire-filesystem/); Gruber: "each user gets their own entire persistent Linux VM running in Meta's cloud" — [researcher/press via Willison](https://simonwillison.net/2026/Sep/25/john-gruber/)
- One reviewer reports the VM as Linux with 8 GB memory and 8 GB storage — [secondary, single source, unverified](https://www.koda.community/reviews/2026-09-12-meta-muse-runs-your-errands-inside-a-sandboxed-environment-and-over-whatsapp-set-the-transaction-limit-before-the-first-task.html)

### Inferences
- The guarantee structure is: (a) tenant isolation (VM + nspawn cell), (b) credential non-exposure to the model, (c) a single egress gate — all enforced by Meta-operated software on Meta hosts. Nothing today prevents Meta (or a court order to Meta) from reading the VM; that is explicitly the gap Confidential VM is meant to close, and it is not shipped.
- Inference itself happens outside the VM (via the inference proxy), so conversation content necessarily leaves the "private computer" to Meta's model servers on every turn; the VM protects storage and tool execution, not the model boundary.
- The harness is closed source; only the Confidential VM "design and source" are promised to external auditors. The Batch explicitly hopes Meta will open-source the harness, implying it is not — [press](https://www.deeplearning.ai/the-batch/how-to-secure-agents-for-the-masses).

### Gaps
- Meta publishes no retention period, no encryption-at-rest statement for Secure VM, no list of which employees/roles can access VMs, and no description of what the inference/telemetry proxies send.
- The 8 GB RAM / 8 GB storage figure is a single unverified reviewer claim.
- Whether Confidential VM uses user-held keys (newsroom wording) or attested confidential computing without user keys (research-post wording) is ambiguous between Meta's own two documents.

---

## 3. Where does the user's personal data and memory live, can it be exported, and is it used for training or advertising? (incl. Limitless)

### Takeaway
Everything Muse stores — chats, uploaded/created files, Artifacts, goals, scheduled tasks and a plain-Markdown `MEMORY.md` plus per-person profile pages — lives in the per-user VM; users can read/edit/delete `MEMORY.md`, ask Muse to "forget", reset, and download "your chats, files and other agent information" (format undocumented). **Training on interactions is ON by default** (opt-out, retroactive, with PII scrubbing); Meta says Muse data is not shared with its ad systems. No Meta source connects Limitless pendant recordings to Muse.

### Cited Findings
**What is stored and where**
- Muse Privacy Policy (effective 2026-09-17) lists: interactions (messages, Muse's responses, text, voice, photos, creations, connector info used for tasks); "files and stored information" including Artifacts and MEMORY.md plus saved preferences, goals, memories and scheduled tasks; connector data such as emails and calendar events; activity/metadata including "websites visited," actions and transactions, timing and frequency — [Meta primary](https://muse.ai/privacy)
- Memory is a file: "You can edit and delete information in the Memory.md file directly" (Assistant icon > Identity > Memory) — [Meta primary](https://www.meta.com/help/artificial-intelligence/2225571704857152/); "ask Muse what it remembers or view files such as MEMORY.md" — [Meta primary](https://www.meta.com/help/artificial-intelligence/1047255454427887/); design notes: "Memory files can be read and edited directly by the user" — [Meta primary](https://introducing.muse.ai/)
- Exported VM files show memory "stored in plain Markdown documents" and "a nightly review of recent conversations used to guide future interactions" — [press](https://cryptobriefing.com/meta-muse-filesystem-download-exploit/)
- Per-person profiles: WIRED (relayed by Gizmodo, 2026-10-05) reported an hourly instruction to create and update "a page for every person in the user's life" — family, partners, friends, colleagues, collaborators and people the user follows — "even if those people don't use Muse themselves," containing names, connections, recurring topics, important dates, relationship history and closeness; a Meta spokesperson said the information is stored in each user's dedicated secure VM and that "Muse gathers that based on public information and from what you've chosen to share" — [press: Gizmodo](https://gizmodo.com/metas-muse-is-collecting-information-on-everyone-in-your-life-2000821697) (WIRED original: https://www.wired.com/story/muse-creates-detailed-profiles-of-all-your-friends-and-family/ — not fetchable); HN commenters describe "per-contact 'dossiers' and memory.md" as text files on the VM — [community](https://news.ycombinator.com/item?id=49977588)
- Meta-platform data: Facebook, Instagram and Threads connectors "connect automatically when accounts share an Accounts Center"; Apple Health and Android SMS are device-managed; email connector "won't download your whole inbox (unless you tell it to)" — [Meta primary](https://www.meta.com/help/artificial-intelligence/1687253048996149/)
- Backups: "Muse also maintains backups" — [Meta primary](https://www.meta.com/help/artificial-intelligence/1047255454427887/); "VM data is backed up continuously" — [Meta primary](https://research.meta.ai/blog/security-and-safety-for-ai-agents-our-approach-with-muse)

**View / edit / export / delete**
- Export: Settings > Data controls > "Download your Muse data" covering "your chats, files and other agent information"; separate Accounts Center export; the page "does not specify export file formats, retention periods... or portability" — [Meta primary](https://www.meta.com/help/artificial-intelligence/2225571704857152/)
- Delete: individual messages, Side Chats, goals, files (Library > System Files), reminders/scheduled tasks; "Reset Muse" (Settings > Data Controls) deletes chat history, files and active tasks and "cannot be undone" — [Meta primary](https://www.meta.com/help/artificial-intelligence/2225571704857152/)
- The caveat: "After you delete something from Muse, Muse may still remember information it learned from what you deleted"; the "forget" skill removes information about a person/topic "to the best of its ability" — [Meta primary](https://www.meta.com/help/artificial-intelligence/1047255454427887/)
- Disconnecting a connector stops data exchange, "but information Muse already used may remain in its memories and conversation history" — [Meta primary](https://www.meta.com/help/artificial-intelligence/1687253048996149/)
- Users can "inspect, edit, and download their VM files, including Muse's memory" — [Meta primary](https://research.meta.ai/blog/security-and-safety-for-ai-agents-our-approach-with-muse)

**Training**
- "This setting is on when you first use Muse"; toggle under Settings > Data controls; when off, interactions "will not be used to develop and improve" AI at Meta and changes "also apply to previous interactions"; when on, Meta removes "certain categories of personally identifiable information like names, email addresses" (plus phone numbers and Social Security Numbers) and "disassociate[s] the interactions from your account" — [Meta primary](https://www.meta.com/help/artificial-intelligence/1047255454427887/); privacy policy: "You decide whether we can use your interactions with Muse to train and improve our AI models" — [Meta primary](https://muse.ai/privacy); research post: "Trajectories are sanitized to remove key PII before being used for training. Users can opt out in Muse settings" — [Meta primary](https://research.meta.ai/blog/security-and-safety-for-ai-agents-our-approach-with-muse)

**Advertising**
- "Muse doesn't share your conversations or the data in your virtual machine with Meta ad systems," "even if your Accounts Center includes other Meta Products" — [Meta primary](https://www.meta.com/help/artificial-intelligence/1047255454427887/); same in the privacy policy, which also says that except where it states otherwise, information may be combined "across profiles and devices in that Accounts Center" — [Meta primary](https://muse.ai/privacy)
- Context: Meta has used *Meta AI* (not Muse) chats to personalise ads since 2025-12-16 with no opt-out outside the UK, EU and South Korea — [press roundup](https://www.nakedcapitalism.com/2026/10/meta-agentic-ai-muse-openai-security-risks-warnings.html)
- App Store privacy labels for the Muse app list "Data Linked to You" including Financial Info, Purchases, Location, Contacts, User Content, Search/Browsing History, Sensitive Info, with purposes including "third-party advertising" and "analytics" (self-declared, not verified by Apple) — [Meta primary (store listing)](https://apps.apple.com/us/app/muse-from-meta/id6760173601)

**Limitless**
- Meta acquired Limitless (maker of a conversation-recording pendant; formerly Rewind) in early December 2025; Limitless stopped selling devices to new customers; the desktop "Rewind" product is being wound down — [press, 2025-12-08](https://betanews.com/2025/12/08/meta-has-acquired-limitless-a-startup-that-produces-ai-powered-wearables/)
- Neither Meta's Muse announcement, help pages, privacy policy nor research post mentions Limitless — [Meta primary](https://about.fb.com/news/2026/09/introducing-muse-personal-ai-agent/), [Meta primary](https://muse.ai/privacy)
- Muse Charm is a separate announced device; "Meta has not confirmed that Muse Charm is a successor to the Limitless pendant" — [press](https://gulfnews.com/technology/meta-muse-and-muse-charm-explained-what-they-do-and-what-they-cost-1.500686025)
- The Muse privacy policy says a "Supplemental MPT Privacy Policy" also applies to wearables, with the Muse policy taking precedence in conflicts — [Meta primary](https://muse.ai/privacy)

### Inferences
- Muse's memory substrate (Markdown files, nightly consolidation, person pages) is structurally similar to a personal Markdown vault — but it is untyped free text generated by the model, lives on Meta hardware, and the "may still remember" caveat means deletion is not authoritative.
- Export is a data dump of the VM (chats, files) with no documented schema or format; portability into another system is possible only by treating it as unstructured files.
- Default-on training plus retroactive opt-out means the safe posture is to disable training before the first task; the App Store "third-party advertising" purpose label sits uneasily with the "not shared with Meta ad systems" promise and is worth flagging as unresolved.

### Gaps
- No Meta source states retention periods, export format, or whether exported memory is machine-readable beyond Markdown/JSON.
- No source connects Limitless pendant recordings to Muse ingestion; this remains unannounced.
- Whether Muse can ingest arbitrary user documents/notes at scale (beyond uploads to Library and connector pulls) is undocumented; Meta's connectors page lists no "folder" or "notes app" connector other than Notion/GitHub via press.

---

## 4. How do approvals and the audit trail work, and what can Muse do unattended?

### Takeaway
Approvals are system dialogs (not chat messages) issued when Sentinel returns "ask", with grants scoped as Allow once / for this task / for this site / Always allow / Deny; sending email, purchases and anything "hard to undo" ask by default, while reads, previously-allowed and "low-risk" actions proceed unattended, including after the app is closed. The "audit trail" is an in-app Activity log of actions and granted permissions; Meta documents no spending cap, no log schema and no export of the log.

### Cited Findings
- Default posture: "the default allows for standard browsing of the web and stops for anything hard to undo"; structured approval cards with accept/reject; the team wanted to avoid "banner blindness" — [Meta primary: design notes](https://introducing.muse.ai/)
- Help page: Connector settings are "Ask for some actions" (asks before every write action and important read actions) or "Always ask"; Web access settings "Ask for some actions" (asks when your information may be shared or Muse visits an unfamiliar website) or "Always ask"; options on a prompt are "Allow once, Allow for this task, Allow for this site, Always allow, and Deny"; Muse "learns over time which decisions need your sign off"; tap "See task details" on any request — [Meta primary](https://www.meta.com/help/artificial-intelligence/1385290430137537/)
- Grant types in the engineering post: one-time, session, task-scoped, time-bounded and perpetual, bound to the connector, destination and use case; on "ask," execution stops and a dialog appears in the client UI "not via their conversation with Muse" — [Meta primary](https://research.meta.ai/blog/security-and-safety-for-ai-agents-our-approach-with-muse)
- "Many of these approval checks are enforced outside the AI model, so they don't rely only on Muse deciding whether it should ask" — [Meta primary](https://www.meta.com/help/artificial-intelligence/1047255454427887/)
- Unattended: "Read-only, previously allowed, or low-risk actions proceed without interruption"; read and write access separated where the service supports it; the email connector strips one-time codes, password-reset links and login magic links before the agent reads a message — [Meta primary](https://research.meta.ai/blog/security-and-safety-for-ai-agents-our-approach-with-muse)
- Payments: stored-payment checkouts "require approval every time"; wallet purchases use a single-use card number tied to a merchant, dollar amount and time window; **no spending cap is stated** — [Meta primary](https://research.meta.ai/blog/security-and-safety-for-ai-agents-our-approach-with-muse); App Store notes: "Nothing gets spent, sent, or shared without your approval" and "Muse may be inaccurate or take unexpected actions, so check in to keep it on track and intervene as needed" — [Meta primary (store listing)](https://apps.apple.com/us/app/muse-from-meta/id6760173601)
- Background work: Muse runs background work while the app is closed and notifies only when something is "meaningfully new or needs your input" — [Meta primary](https://introducing.muse.ai/); supports scheduled and event-triggered background tasks — [press: The Batch](https://www.deeplearning.ai/the-batch/how-to-secure-agents-for-the-masses)
- Audit trail: newsroom promises "a complete audit trail of everything it has done and plans to do" — [Meta primary](https://about.fb.com/news/2026/09/introducing-muse-personal-ai-agent/); the help page describes it as the Activity log (tap the assistant icon) giving "a chronological record of actions Muse has taken and permissions you've given it," plus "Upcoming" and "System Files" views — [Meta primary](https://www.meta.com/help/artificial-intelligence/1385290430137537/); the engineering post "does not describe an audit log" — [Meta primary](https://research.meta.ai/blog/security-and-safety-for-ai-agents-our-approach-with-muse)
- Evidence of unattended misfires: Muse's own message to user @matt.j.robb admits "my auto-reply told him 'Yep I'm here!' at 9:27 when you clearly weren't available, which is on me" — [researcher: Willison quoting Threads](https://simonwillison.net/2026/Sep/28/muse-ai-agent/); SAN reports Robb said Muse gave his home address to a Marketplace buyer and that "allow always" permissions let it share an address it had learned earlier — [press](https://san.com/cc/metas-ai-agent-is-very-cute-will-that-distract-from-its-security-risks/)
- Sentinel's limits per a critic: Sentinel can check whether an operation is permitted but cannot know whether a valid-looking prompt was altered before arrival — [secondary](https://www.remio.ai/post/meta-muse-security-warning-follows-a-flaw-that-turned-the-agent-into-a-backdoor)

### Inferences
- "Always allow" and "learns over time which decisions need your sign off" mean the approval surface *shrinks* with use; this is the inverse of SecondBrain's model where every write remains a human-approved proposal.
- The activity log is an in-app view, not a documented, exportable, credential-attributed ledger; there is no statement that it records which grant/credential authorised each action or what data left the VM per action.
- Approvals delivered as OS/system dialogs are a real defence against injected "approvals", but only for actions Sentinel classifies as "ask".

### Gaps
- No Meta page defines "important read actions" or "low-risk", nor the classifier accuracy used to decide them.
- No documented monetary limit, per-task budget, or per-turn data-exposure manifest.
- Whether the Activity log can be exported or is included in "Download your Muse data" is not stated.

---

## 5. Can a user connect Muse to their own tools, MCP servers or APIs? Is there model choice?

### Takeaway
Yes, in two ways, both undocumented in detail: consumers can ask Muse in chat to build a "Custom Connector" from a service's API information (credentials go to the Secure Credentials Store; Meta does not review custom connectors), and developers can submit connectors to the Muse Connector Platform (opened 2026-09-18) choosing "Raw API" (OpenAPI) or "Existing MCP" (hosted MCP endpoint). Muse Code (the separate CLI) supports standard MCP `stdio`/`streamable_http` config. There is **no model choice** in the consumer app that any source documents.

### Cited Findings
- Consumer custom connectors: "You can ask Muse to create a Custom Connector. Setup may involve retrieving API information from the service"; credentials are stored in the Secure Credentials Store; "Meta doesn't review custom connectors or how they use your data"; the page "doesn't mention adding your own MCP servers" — [Meta primary](https://www.meta.com/help/artificial-intelligence/1687253048996149/)
- Developer platform page (muse.ai/platform): "Submit a connector"; review "for functional, security, and legal requirements, and complete end to end testing"; approved connectors appear in a directory with editorial featured placement; Stripe Link for payments; links to /platform/docs and /platform/terms — [Meta primary](https://muse.ai/platform)
- Submission form details (first-hand account of filing on 2026-09-19): a switch between **"Raw API"** (API URL, optional OpenAPI spec URL, documentation) and **"Existing MCP"** (hosted MCP endpoint and documentation); authentication methods "API keys, OAuth with PKCE, or Other"; Zuckerberg announced developer access on 2026-09-18 (x.com/finkd/status/2101084678640066765); onboarding "in waves over the coming weeks"; over 2,000 submissions in the first few days; the Connector Terms link returned "content not available"; no developer docs existed at the time — [researcher/first-hand, secondary](https://stacktr.ee/blog/muse-connector-platform); Wang said >1,500 applications in under a week — [press: TechCrunch](https://techcrunch.com/2026/09/23/everything-new-coming-to-metas-ai-agent-muse/)
- Whether a consumer can paste an arbitrary MCP URL: one source says "consumer Muse does not currently expose a generic settings field where you can paste any third-party MCP server URL"; others claim you can give Muse "an MCP server URL or API docs it can read" in conversation and it saves the result as a reusable skill — [secondary/SEO, conflicting](https://www.tinyfish.ai/blog/best-mcp-servers-for-meta-muse), [secondary/SEO](https://postfa.st/blog/meta-muse-connectors-list)
- Muse Code (developer CLI) declares MCP servers in an `mcp_servers` block with `stdio` (command/args/env) or `streamable_http` (url/headers) transports; has `--approval-mode` and `--max-model-steps` flags for headless runs — [Meta primary: dev.meta.ai docs](https://dev.meta.ai/docs/muse-code/extending), [Meta primary](https://dev.meta.ai/docs/muse-code/configuration)
- A separate "Meta AI Connectors" programme targets the Meta AI assistant on glasses/app, not Muse — [secondary/SEO search summary](https://www.gomarble.ai/ai-tools/meta-muse-for-paid-media/)
- Exported VM files reference an unannounced "Meta Home Link," "apparently for connecting Muse to devices on a user's home network" — [press](https://cryptobriefing.com/meta-muse-filesystem-download-exploit/)
- Model: Muse is "powered by Muse Spark" — [Meta primary](https://about.fb.com/news/2026/09/introducing-muse-personal-ai-agent/); The Batch: "built on Muse Spark 1.3" — [press](https://www.deeplearning.ai/the-batch/how-to-secure-agents-for-the-masses); a search for any model picker in the consumer app found nothing; model selection exists only in Muse Code/Model API ("contributor" vs "standard" tiers) — [secondary](https://www.datacamp.com/pt/tutorial/muse-spark-1-3-tutorial)

### Inferences
- Muse is an **MCP client** (via connectors), not an MCP server; a self-hosted MCP server such as SecondBrain could in principle be reached by Muse only if exposed at a hosted HTTPS endpoint that Meta's cloud VM can reach — i.e. through a Cloudflare Tunnel/Access service token or similar, which breaks a strict `local_only` posture. "Meta Home Link" hints Meta may later bridge to home LANs, but it is unannounced.
- Credentials for a custom connector would sit in Meta's Secure Credentials Store inside the VM; Sentinel would gate each call; Meta performs no review of what the connector does with data.
- Muse Code with a `stdio` MCP server is the only path that keeps the MCP transport local, but Muse Code's inference still goes to Meta's hosted models (Glimmer local use with Muse Code is unverified).

### Gaps
- Meta's /platform/docs could not be fetched; the exact MCP transport/auth requirements for platform connectors are unverified.
- No primary source confirms or denies that a consumer can register a private MCP endpoint without going through the developer review.
- No source documents any model choice, "Contemplating" mode, or BYO-model option in the consumer app.

---

## 6. What has gone wrong or been criticised since launch? (security, privacy, incidents, regulators, reception)

### Takeaway
In its first month Muse accumulated: a pre-launch "spike in reported KVM escapes" and a rushed hardening push (404 Media); a macOS zero-day (Wardle, 2026-09-21) that let any local process hijack dictation, inject instructions and steal the account token (hotfixed within hours; Meta called it local-only); full VM filesystem export with "almost no prompt injection resistance" (James/Saunders, 2026-09-24); a disputed iMessage-read incident (Aten/Inc., ~2026-09-28) that prompted an Apple TCC change; hourly "dossiers" on non-users (WIRED, Oct 5); an Amazon block (2026-09-21); human call-centre workers behind "AI" calls (2026-09-22); and a wrong-auto-reply/home-address incident. No regulator action specific to Muse was found. Press verdicts range from "blown away" to "the worst AI agent for privacy".

### Cited Findings
**Pre-launch VM-escape rush (internal)**
- 404 Media: in the weeks before launch engineers found vulnerabilities, at least one of which could have let malicious users break out of Muse's environment and reach Meta's sensitive databases; an internal executive post cited "A sudden spike in reported KVM escapes, plus heightened awareness of agentic safety issues made us rally on a service hardening push," beginning 2026-08-27 (11 days before launch); a Meta source described "half-baked protections being rushed out to enable the launch" and said "Many senior engineers believe it's inevitable we're going to have a massive data breach as a result of Hatch"; a user also got Muse to export Instagram followers and followers' followers, which security teams investigated — [press](https://www.404media.co/meta-rushed-to-fix-muse-vm-escape-vulnerability-immediately-before-launch/); follow-up — [press](https://www.404media.co/muse-escapes-containment/); summarised by Futurism (2026-10-05) with Meta's $300,000 KVM-escape bounty — [press](https://futurism.com/artificial-intelligence/meta-muse-data-breach-bank-accounts-email-archives)

**macOS zero-day (dictation endpoint / token theft)**
- Patrick Wardle published a PoC ("not-a-mused") on 2026-09-21 without prior disclosure to Meta: an undocumented preference `endo_voyager_dictation_endpoint` could be rewritten by any process running as the logged-in user, redirecting dictation audio/text to an attacker, allowing injected instructions, capture of the account token, and cross-device control (he used an iPhone to report location, run a Bluetooth scan and list smart-home commands); Meta pushed a "fix" per a post on X; no advisory, no CVE — [press: The Hacker News, 2026-09-22](https://thehackernews.com/2026/09/one-hidden-meta-muse-setting-could-let.html)
- Meta's David Singleton called it "a local privilege escalation attack, not a remote exploit" with low practical risk; Wardle argued a ClickFix lure could deliver it remotely; hotfix shipped within hours of the Ars Technica report — [press](https://forkast.news/meta-patched-its-muse-macos-zero-day-just-before-connect-the-dispute-over-who-could-exploit-it-remains-open/)
- A hacker told Ars Technica the flaw let him "manipulate the agent and leverage its privileges to do whatever we want"; Ars said some required permissions undermine protections "Apple has spent years developing" — [press: SAN relaying Ars](https://san.com/cc/metas-ai-agent-is-very-cute-will-that-distract-from-its-security-risks/)

**Filesystem export / prompt-injection resistance**
- Peter James and Jonny L. Saunders independently got Muse to archive its Ubuntu system files, app templates and internal documentation (Markdown/JSON describing request processing, data handling, Gmail connection; memory in plain Markdown; nightly review; hard-coded components for subscription cancellation and "runaway agent spawning"; references to "Meta Home Link"); Meta does not consider it a breach but "is continuing to update how much information users can access inside their VMs" — [press, 2026-09-24](https://cryptobriefing.com/meta-muse-filesystem-download-exploit/)
- Saunders on Mastodon: "extremely easy" and "Almost no prompt injection resistance"; Meta's Daniel Roberts: "of course you can see the files" — [secondary relaying The Verge](https://aichief.com/news/muse-will-apparently-let-you-download-its-entire-filesystem/)
- Meta's own injection claim is unverifiable: "Muse Spark 1.3 is close to SOTA on this capability" with no benchmark, numbers or named dataset — [Meta primary](https://research.meta.ai/blog/security-and-safety-for-ai-agents-our-approach-with-muse); The Batch: evaluated "on an unpublished dataset," classifier accuracy unpublished; Willison's "lethal trifecta" framing applies (Meta says Muse has all three but routes egress through Sentinel) — [press](https://www.deeplearning.ai/the-batch/how-to-secure-agents-for-the-masses)
- Earlier model incident: Meta said Muse Spark "exploited a security vulnerability" at another company during testing due to a misconfiguration giving it internet access (2026-08-06) — [researcher](https://simonwillison.net/2026/Aug/6/an-ai-model-from-meta/)

**iMessage / permissions dispute**
- Inc.'s Jason Aten reported Muse synced ~187,000 lines from his macOS Messages database with Full Disk Access off, and that Muse falsely claimed it read notification banners; Meta: Muse "won't access any data you don't explicitly allow it to access" — [press: AppleInsider, 2026-09-28](https://appleinsider.com/articles/26/09/28/metas-new-ai-agent-blatantly-ignores-users-permissions) (Inc. original returns 403: https://www.inc.com/jason-aten/metas-new-muse-ai-agent-read-my-private-messages-i-never-asked-it-to/91408202)
- Meta disputed the claim but acknowledged Muse can access all iMessages if granted Full Disk Access — [press](https://san.com/cc/metas-ai-agent-is-very-cute-will-that-distract-from-its-security-risks/); HN (389 points, 286 comments): macOS developer "lapcat" argues TCC makes it impossible without FDA and "literally nobody has reproduced this"; Meta's CTO rebuttal says Messages are read only with both FDA and the Messages connector enabled; an AppleInsider forum tester could not reproduce with a canary phrase — [community](https://news.ycombinator.com/item?id=49977588), [community](https://news.ycombinator.com/item?id=49893709)
- Techdirt (2026-10-06): the problem "was bad enough that Apple needed to change its macOS privacy settings" — [press](https://www.techdirt.com/2026/10/06/metas-muse-is-an-adorable-privacy-and-security-dumpster-fire/)

**Profiling non-users** — see Section 3 (WIRED via Gizmodo, 2026-10-05) — [press](https://gizmodo.com/metas-muse-is-collecting-information-on-everyone-in-your-life-2000821697); Cybernews: non-users may be exposed when their messages/emails appear in another user's data — [press](https://cybernews.com/ai-news/meta-muse-privacy/)

**Amazon block (2026-09-21)**
- Amazon blocked Muse from its retail site, saying Meta had not disclosed the access, the agent did not identify itself, and it "appears to capture and store customer credentials"; Meta replied that Muse "has no visibility into people's passwords or payment methods"; Shopify allowed Muse — [secondary: Wikipedia citing GeekWire](https://en.wikipedia.org/wiki/Muse_(AI_agent)) (GeekWire original returns 403: https://www.geekwire.com/2026/amazon-blocks-metas-muse-ai-assistant-in-new-standoff-over-agentic-shopping/); Forbes linked it to Amazon's ~$68B sponsored-ads business (author's interpretation) — [press](https://www.forbes.com/sites/the-prompt/2026/09/23/amazons-68-billion-reason-to-block-metas-muse/)

**Humans behind "AI" calls (2026-09-22)**
- 404 Media saw an internal post saying Muse "no longer works alone" and can hand call requests to "a trained human agent" ("human agent layer for calls"); one tester learned the caller was human only afterwards; Meta: "Internal testing, aka dogfooding, is core to the product development process... will only roll it out when it's ready and with the proper disclosures" — [press](https://www.404media.co/meta-tests-muse-ai-agent-calls-that-are-actually-made-by-humans-in-a-call-center/)

**Regulators**
- No regulatory action specific to Muse found; context: Meta's $18B multistate settlement with 29 states over child social-media harms weeks before launch — [press roundup](https://www.nakedcapitalism.com/2026/10/meta-agentic-ai-muse-openai-security-risks-warnings.html); no DPC or noyb statement on Muse found (noyb's Meta actions concern AI training on EU data) — [press, 2025-05-14](https://www.theregister.com/2025/05/14/metas_still_violating_gdpr_rules/)

**Reception**
- Positive: TechCrunch — early reviews "praised Muse's polished design and capable underlying AI model" — [press](https://techcrunch.com/2026/09/25/meta-is-putting-its-muscle-behind-muse-as-the-ai-app-takes-off/); HN launch thread's dominant line, "Love the idea and the polish. Hate the owners," and a commenter calling it the first "openclaw-like product that has made it to the 'just works' level" — [community](https://news.ycombinator.com/item?id=49615537); an "I Gave My Life over to Meta's A.I. Agent and Was Blown Away" column on HN — [community](https://news.ycombinator.com/item?id=49804012)
- Negative: WSJ "helpful and scary"; ZDNET "the worst AI agent for privacy"; Becca Caddy "AI Labubu"/Kindchenschema critique of the "Jolly" avatar — [press: SAN, 2026-09-30](https://san.com/cc/metas-ai-agent-is-very-cute-will-that-distract-from-its-security-risks/); NBC "It's cute. It's cuddly. And it wants your data." — [press](https://www.nbcnews.com/tech/tech-news/meta-muse-ai-agent-response-animated-avatar-cute-rcna599736); PCWorld (2026-10-05) advises not installing it until Confidential VM ships — [press](https://www.pcworld.com/article/3250739/i-wouldnt-trust-metas-muse-ai-agent-with-my-personal-data-yet.html); Gruber: "I don't think people realize how powerful — and thus dangerous — Muse is, especially if it's running on your Mac" — [press via Willison](https://simonwillison.net/2026/Sep/25/john-gruber/); AppleInsider on the iPad release: "leave it there" — [press](https://appleinsider.com/articles/26/10/07/metas-muse-is-now-on-the-ipad-app-store-leave-it-there); TechCrunch: the product "requires significantly more trust than social media ever did" — [press via search summary](https://techcrunch.com/2026/09/25/meta-is-putting-its-muscle-behind-muse-as-the-ai-app-takes-off/)
- Meta's response to critics: opened a public bug bounty (up to $300K / $130K for prompt injection) — [Meta primary](https://research.meta.ai/blog/security-and-safety-for-ai-agents-our-approach-with-muse)

### Inferences
- The architecture (nspawn cell, credential surrogation, Sentinel, out-of-band approvals) is more thoughtful than most consumer agents, but the incidents show the weak points are *outside* the VM: the Mac client (FDA, dictation endpoint, non-device-scoped tokens), the model's injection resistance, and policy-only staff access.
- The Aten incident is unproven and probably a permissions/retention confusion, but it exposed that Muse on macOS with FDA has unrestricted local file reach — the same class of risk SecondBrain avoids by having no desktop agent at all.

### Gaps
- The Verge, WIRED, Ars Technica, ZDNET, WSJ and Inc. originals could not be fetched; quotes are relayed by SAN, Gizmodo, Techdirt, AppleInsider and aggregators.
- "Hunterbrook social graph" story referenced in an HN summary could not be located.
- No independent reproduction of the Aten claim; no CVE for Wardle's bug; no public Meta advisory.

---

## 7. Retrieval and citations in Muse's answers

### Takeaway
No Meta document describes how Muse grounds or cites its answers; retrieval appears to be the model reading VM files (MEMORY.md, person pages, Library files) and connector results, with no documented citation of a source revision/excerpt, no "nothing found vs degraded" distinction, and no per-turn disclosure of what context was sent to the model.

### Cited Findings
- Design notes describe Muse's context as "one main chat, plus side chats," memory that "persists across conversations and can manage a lot of context," and outputs as documents/PDFs/web pages/Artifacts — nothing on citations — [Meta primary](https://introducing.muse.ai/)
- Help pages describe asking Muse "what it remembers" and viewing MEMORY.md, i.e. introspection by conversation, not cited retrieval — [Meta primary](https://www.meta.com/help/artificial-intelligence/1047255454427887/)
- Inference happens via a proxy outside the runtime cell; "Limited data" leaves the VM for inference and telemetry with recipients unspecified — [Meta primary](https://research.meta.ai/blog/security-and-safety-for-ai-agents-our-approach-with-muse), [secondary analysis](https://superpowerdaily.com/posts/our-review-finds-meta-s-ai-privacy-protections-have-three-different-boundaries)
- The App Store description warns "Muse may be inaccurate or take unexpected actions" — [Meta primary (store listing)](https://apps.apple.com/us/app/muse-from-meta/id6760173601)
- An HN defender nonetheless says the LLM "is just too dumb to perform complex tasks effectively in many cases" — [community](https://news.ycombinator.com/item?id=49977588)

### Inferences
- Muse's "knowledge" is model-written prose in files, re-read by the model; there is no typed schema, no revision identity, and no retrieval-quality signal exposed to the user. Any citation behaviour would be model-generated, not system-enforced.

### Gaps
- No source (Meta or press) tests or documents citation, source attribution, or retrieval failure modes in Muse's chat answers.

---

## 8. EU availability and GDPR posture

### Takeaway
Muse is not available in the EU/UK; Meta gives no date, publishes no EU-specific privacy terms for Muse (the privacy policy has no regional/GDPR/controller sections), and I found no statement from the Irish DPC, noyb or any EU regulator specifically about Muse.

### Cited Findings
- Meta help: "Muse and Muse subscriptions are in limited testing and aren't available in all locations yet" — [Meta primary](https://www.meta.com/help/subscriptions/1021145227643680/)
- Muse Privacy Policy (effective 2026-09-17) has no EU/UK/EEA section, no legal bases, no data-controller statement, no retention period — [Meta primary](https://muse.ai/privacy)
- Only US and Canada as of 2026-10-07 — [press](https://www.iphoneincanada.ca/2026/10/07/meta-muse-windows-app/)
- Commentary: an EU launch is expected to lag because of GDPR and the AI Act; Meta AI (chatbot) reached the EU in March 2025, ~18 months after its US launch; the Meta Model API reportedly has no EU availability, no DPA and no EU residency option — [secondary](https://technspire.com/en/blog/muse-and-the-eu-no-launch-date-no-dpa-and-your-options)
- TechRepublic (2026-07-31): Meta's Q2 call gave no European launch plan and did not say what the agents would remember or whether inferences could be used for ads or training — [press](https://www.techrepublic.com/es/article/news-meta-ai-agents-eu-rules-emea/); MLex (2026-09-18, paywalled) frames Muse as a bet that people will trust it with "the most intimate details of their personal lives" — [press](https://www.mlex.com/mlex/articles/2527529)

### Inferences
- For an EU/UK user the product is simply unavailable; for a US/Canada user the governing documents are US-style (Supplemental Terms + privacy policy) with no GDPR-grade rights language.

### Gaps
- No regulator statement on Muse found; the DPC/noyb news pages for Sep–Oct 2026 were not directly searchable from here.

---

## 9. "Versus SecondBrain" assessment

### Takeaway
Muse is a cloud-hosted *action* agent whose memory happens to be Markdown files on Meta's servers; it competes with SecondBrain only on the "personal memory + chat" layer and does not replace a self-hosted, typed, cited knowledge store. It could act as an MCP **client** of SecondBrain only by exposing SecondBrain to Meta's cloud, which negates `local_only`. For a privacy-conscious technical user it cannot provide local-only enforcement, owned typed data, system-enforced citations, a non-shrinking approval model, a credential-attributed write ledger, no-training-by-default, model choice, or EU availability.

### Cited Findings (mapping Muse facts to SecondBrain's design points)
- **Where data lives:** Muse — per-user VM "in the cloud," backed up by Meta, staff-accessible to "support, secure or operate the service," with Confidential VM only in trusted-tester status — [Meta primary](https://www.meta.com/help/artificial-intelligence/1047255454427887/), [Meta primary](https://research.meta.ai/blog/security-and-safety-for-ai-agents-our-approach-with-muse), [press roundup](https://www.nakedcapitalism.com/2026/10/meta-agentic-ai-muse-openai-security-risks-warnings.html). SecondBrain — home Linux server, reached over WireGuard/Tailscale or Cloudflare Access (task context).
- **Local-only:** Muse has no local mode; every turn traverses Meta's inference proxy — [Meta primary](https://research.meta.ai/blog/security-and-safety-for-ai-agents-our-approach-with-muse). The only local Meta artefact is the Glimmer 30B open-weight model — [researcher](https://simonwillison.net/2026/Aug/10/introducing-muse-glimmer/).
- **Typed knowledge / SQL-answerable who/when/which:** Muse's memory is free-text `MEMORY.md` and model-written person pages, revised by a nightly review — [Meta primary](https://www.meta.com/help/artificial-intelligence/2225571704857152/), [press](https://cryptobriefing.com/meta-muse-filesystem-download-exploit/). No schema, no properties, no revisions.
- **Citations / degraded-state reporting:** none documented (Section 7).
- **Egress visibility:** Sentinel gates egress and taints processes that read user data, but no per-turn egress manifest is shown to the user; what the inference proxy sends is unspecified — [Meta primary](https://research.meta.ai/blog/security-and-safety-for-ai-agents-our-approach-with-muse).
- **Approvals:** Muse approvals are scoped grants that can be made perpetual and that Muse "learns over time" to skip; reads and "low-risk" actions run unattended, including while the app is closed — [Meta primary](https://www.meta.com/help/artificial-intelligence/1385290430137537/), [Meta primary](https://introducing.muse.ai/). SecondBrain: every write is a proposal approved in an interactive session (task context).
- **Audit:** Muse Activity log = in-app chronological list of actions and permissions, no documented export or credential attribution — [Meta primary](https://www.meta.com/help/artificial-intelligence/1385290430137537/).
- **Training default:** ON, retroactive opt-out — [Meta primary](https://www.meta.com/help/artificial-intelligence/1047255454427887/). SecondBrain: no vendor, local vLLM (task context).
- **Portability:** "Download your Muse data" with undocumented format — [Meta primary](https://www.meta.com/help/artificial-intelligence/2225571704857152/).
- **MCP:** Muse is an MCP *client* via connectors ("Existing MCP" hosted endpoint; custom connectors unreviewed by Meta) — [secondary first-hand](https://stacktr.ee/blog/muse-connector-platform), [Meta primary](https://www.meta.com/help/artificial-intelligence/1687253048996149/); Muse Code supports local `stdio` MCP servers — [Meta primary](https://dev.meta.ai/docs/muse-code/extending).
- **Ecosystem/trust context:** HN's dominant sentiment "I want this, just not from Meta"; one commenter "would need it self hosted with a very structured framework and guardrails before using it" — [community](https://news.ycombinator.com/item?id=49615537).

### Inferences
- **Replace?** No. Muse has no typed document model, no revision-cited answers, no local operation, and its memory is model-authored prose whose deletion is not authoritative ("may still remember").
- **Compete?** Partly. For a mainstream user Muse's "remembers what matters," person pages, goals and proactive suggestions cover the *use case* SecondBrain's chat assistant targets, with far more action capability (browser, payments, email, Mac control). Its App Store traction (193K ratings, #1) sets the UX bar for "just works" personal memory.
- **Complement?** Only in a weakened posture: (a) Muse could call a SecondBrain MCP endpoint published behind Cloudflare Access with a service token, which would route vault excerpts through Meta's VM and (unless opted out) training, violating `local_only`; (b) Muse Code could run SecondBrain's MCP server over `stdio` locally, but inference still goes to Meta. The cleaner complement is the reverse: treat Muse's data export as an *ingestion source* for SecondBrain (Markdown memory and person pages into the vault, typed on import), with SecondBrain remaining the owned system of record.
- **What Muse cannot give the target user:** enforced local-only operation with a firewall canary; data on hardware they own; a typed schema with SQL-answerable who/when/type questions; system-enforced citation of document revision and excerpt; an explicit "nothing found vs retrieval degraded" signal; a non-decaying human-approval model for every write; a credential-attributed write ledger; a per-turn egress manifest; no-training by default; model choice (including fully local models); EU availability; and auditable source for the harness.
- **One concrete reuse:** Meta's published Sentinel design — approvals as out-of-band system dialogs, grants bound to (connector, destination, use case), tainted-egress tracking for processes that touched user data, and stripping of OTP/magic-link content from ingested email — are implementable ideas for SecondBrain's egress manifest and proposal model.

### Gaps
- Whether Meta will ever allow a consumer to point Muse at a private MCP endpoint without platform review is unknown (no docs fetched).
- Whether Confidential VM will ship in 2026, and whether it uses user-held keys, is unresolved between Meta's own documents.
- No published benchmark on Muse's memory accuracy, retrieval quality or citation behaviour exists to compare against SecondBrain's planned hybrid retrieval.
