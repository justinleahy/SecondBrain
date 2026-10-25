# M5 Private email import and source privacy

Status: planned after M4 production acceptance. Researched 2026-10-25. This milestone turns the first deferred design in [spec.md](../../spec.md#appendix-f--deferred-designs) into a bounded implementation scope.

An administrator can import an email export, search messages by sender, recipient and date, follow threads and attachments, and ask questions with citations. A source marked `no_cloud` keeps its messages and derived content on approved local model bindings, even when other sources use hosted providers. The first release accepts local `.eml` and `.mbox` exports.

## Why this milestone follows M4

M4 supplies people roles, container reconciliation, recoverable publication, provider budgets, enrichment, encrypted recovery and release gates. Email uses those capabilities together and introduces the next privacy boundary required by the existing roadmap. Shipping email together with that boundary avoids importing a private mailbox before its downstream model policy exists.

Contacts, tasks and entity resolution remain later increments. IMAP, Microsoft Graph, Gmail and authenticated CalDAV synchronization require separate account authorization and connector lifecycle designs. OCR, transcription and approximate vector search remain separate features; an approximate index should follow measured retrieval limits rather than become a prerequisite by assumption.

## Research and package choice

Use a maintained MIME parser inside the existing offline extractor. The initial candidate is **MimeKitLite 4.18.1**, subject to a fresh dependency audit before implementation. The published package includes a .NET 10 target without additional package dependencies and omits the cryptography module. This fits export parsing without adding mail transport or decryption. [Published package and dependencies](https://www.nuget.org/packages/MimeKitLite/4.18.1).

The maintainer's current release notes identify 5.0 as unreleased and contain incompatible changes. Pin the selected stable package and inspect that version's source rather than implementing against examples from the master branch. The 4.18.1 notes include a TNEF overflow correction. M5 will preserve unsupported encrypted or TNEF parts as opaque attachments with an explicit extraction state. [Maintainer release notes](https://github.com/jstedfast/MimeKit/blob/master/ReleaseNotes.md).

`MimeParser` supports sequential mbox records. Persistent mode on a seekable stream can reference part bytes without copying them all into memory, so the backing descriptor must remain open while those parts are read. [Parser constructor](https://mimekit.net/docs/html/M_MimeKit_MimeParser__ctor.htm). `MaxMimeDepth` stops recursion by treating deeper content as a leaf; it does not itself establish that the complete message was extracted. The application must enforce and report its own completeness and byte/count/deadline limits. [Depth behavior](https://mimekit.net/docs/html/P_MimeKit_ParserOptions_MaxMimeDepth.htm).

Mailbox variants differ in separators, escaping and content-length handling. Qualify actual export variants and retain raw bytes instead of splitting messages on a naive `From ` search. [RFC 4155](https://www.rfc-editor.org/rfc/rfc4155). Use `Message-ID`, `In-Reply-To` and `References` as threading evidence, with missing and invalid identifiers handled explicitly. These headers do not authenticate the sender. [RFC 5322 section 3.6.4](https://www.rfc-editor.org/rfc/rfc5322#section-3.6.4).

## Contracts to define before implementation

### Email identity and representation

Add the `email` type with `message_id`, `thread_id`, `from`, `to`, `cc`, `subject`, `sent_at`, `in_reply_to`, `references` and `has_attachments`. Extend the people model with sender, recipient and copied roles. `occurred_at` follows a valid original sent date, with the original offset retained; invalid or missing dates stay explicit instead of becoming the import time.

An administrator chooses a stable mailbox/account namespace. Natural keys use that namespace and a parsed Message-ID. Incoming headers cannot choose another account's namespace. Exact repeats retain identity; different bytes claiming the same identifier produce an explicit collision outcome without silently replacing an existing message. Missing or invalid identifiers use a documented source-scoped byte-hash identity, and the UI reports that fallback.

Preserve original MIME bytes and lossless extracted text. Search text may exclude quoted replies and signatures only through a recorded deterministic transform. Citations map back to the retained message and revision; excluded text remains available in the viewer. A thread is a view over independently identified messages, with deterministic handling for missing parents, cycles and conflicting headers.

Each supported attachment becomes a child document with `parent_id`, an internal part locator and content hash. Supplied filenames are display metadata, never filesystem destinations. Repeated attachment extraction preserves identity. Children inherit source privacy and participate in suppression, absence, purge, export and encrypted restore. Remote images, external-body references and attachment links never initiate downloads.

### Containers and recovery

Generalize the current calendar-container machinery with explicit **additive** and **snapshot** import modes. Additive exports add or recognize records without interpreting omission as deletion. Only a fully accepted snapshot can mark missing members absent. A malformed tail, interrupted import or parser limit cannot retire messages from the prior accepted snapshot.

Persist bounded record checkpoints using the owned input hash, parser generation and verified record boundary. A restart must resume or safely replay the same input without duplicate messages or children. A changed input invalidates incompatible offsets. Membership must support one record appearing in several exports without purging the record while another accepted membership remains.

### Source privacy and inference

Add an administrator-controlled `no_cloud` restriction to sources. Document content and derived objects inherit the most restrictive contributing source policy. This includes attachments, chunks, embeddings, enrichment, batch jobs, citations, generated summaries, tool outputs and conversation context assembled from those objects.

Check the restriction at admission and immediately before each provider call, including fallback, retry, reranking, compaction and batch submission. Recheck current policy generations before publishing results. Tightening a source policy fences queued work and marks incompatible embeddings for controlled replacement. A policy cannot undo an earlier authorized disclosure; the UI must show that boundary when changing it.

Maintain a separate restricted embedding space on an approved local binding. Ordinary and restricted spaces keep their own fingerprint, dimensions, generation, coverage and lifecycle. Query each eligible space independently and fuse ranked results; never compare vectors from incompatible spaces directly. Hosted inference receives only eligible content. When useful local evidence is withheld from a hosted answer, report that exclusion without disclosing its contents. If no local route is available, restricted processing remains visibly pending or fails with a useful policy error.

Removing a restrictive source must not silently declassify retained records or earlier derivations. Relaxation requires an explicit administrator action and controlled reprocessing; previously derived content retains its recorded restriction until reviewed or regenerated.

Privacy must survive exports that retain source semantics, encrypted backup/restore, source removal, reimport, index rebuild and conversation replay. Portable plaintext exports remain explicitly initiated administrator actions with their own disclosure notice; automatic cloud inference permission does not follow from export permission.

## Implementation order

1. Define the email schema, account namespace, collision outcomes, attachment identities, container modes and privacy state transitions. Update the specification and compatibility contracts before schema work.
2. Implement source labels and derived provenance, generation fences and all provider-call checks. Add the restricted embedding-space lifecycle and query fusion before admitting restricted mailbox content.
3. Add bounded offline EML extraction, MIME text alternatives and attachment descriptors. Use existing bounded HTML-to-text handling; never treat a parser's HTML converter as a browser sanitizer.
4. Add sequential MBOX extraction and durable per-record recovery. Extend container publication and shared membership with failure and crash tests.
5. Implement sender/recipient/time predicates, thread views, message and attachment viewers, CLI/API/MCP contracts and cited assistant retrieval.
6. Extend retention, suppression and purge lineage, export, encrypted backup and offline restore to every new record and privacy label.
7. Qualify real approved exports, mixed privacy deployments and a synthetic 500,000-message corpus. Keep private mailbox bytes, addresses and answers outside committed evidence.
8. Run locked audits, deterministic and fuzz checks, phone/desktop workflows, hardened Compose and systemd qualification, signed release CI and upgrade verification on a disposable copy. Prepare a production handoff after those gates.

## Acceptance gates

| Gate | Required result |
|---|---|
| M5-01 | Real EML exports preserve sender/recipient/date, decoded text and original bytes across supported encodings, alternatives and missing fields. |
| M5-02 | At least two representative MBOX export variants import record by record with escaped separators and bounded memory; malformed/truncated tails report incomplete status. |
| M5-03 | Exact repeats preserve message/child identity; namespaces isolate accounts; conflicting or missing Message-IDs follow their explicit outcomes. |
| M5-04 | Additive omission never deletes membership; complete snapshots reconcile absence/reappearance; shared membership and user suppression remain correct. |
| M5-05 | Crashes at extraction, checkpoint, publication and attachment boundaries recover without duplicate or lost accepted records. |
| M5-06 | MIME depth, nested messages, huge headers, many parts, transfer decoding, HTML and attachment bombs stop within measured CPU/memory/output bounds. The extractor stays offline with no stores or secrets mounted. |
| M5-07 | Sender, recipient and copied-role filters, thread order and time predicates agree across REST, CLI, UI and MCP; cyclic or missing references terminate deterministically. |
| M5-08 | A real-export question returns a supported answer with current message/revision citations and attachment lineage; excluded quotes remain recoverable from lossless text. |
| M5-09 | Instrumented hosted peers receive zero restricted bytes through chat, embeddings, rerank, enrichment, batch, retry, summary, history replay or mixed-source tools. Both default-local and mixed hosted/local profiles are tested. |
| M5-10 | Tightening privacy while work is queued or in flight fences later disclosure/publication. Restrictive derivations retain labels through merge, cached results and policy changes. |
| M5-11 | Separate embedding spaces retain compatible coverage, rebuild and activation rules. Missing local models produce visible pending/withheld states; query fusion and assistant notices are correct. |
| M5-12 | Source purge, retained shared membership, child purge and conversation redaction preserve the documented retained classes without leaving live derived copies. |
| M5-13 | Encrypted recovery restores messages, attachments, labels, memberships and space descriptors; manifests verify during sustained edits, old credentials fail and search rebuilds. |
| M5-14 | A synthetic 500,000-message qualification records actual message/chunk/vector counts, latencies, memory, restart and recovery costs. Adopt additional indexing only if measured targets require it, with a separate design decision. |
| M5-15 | Desktop and phone import/recovery/privacy workflows pass, with understandable partial-import and withheld-result states. |
| M5-16 | Full regression, dependency/image scans, parser mutations, hardened deployments and signed provenance pass; a disposable upgrade preserves account/configuration and verifies its pre-upgrade backup. Production promotion remains a separate operator action. |

## Operator inputs and exclusions

Request suitable private EML/MBOX exports when the implementation is ready for qualification; synthetic fixtures cover ordinary CI. Do not assume permission to read a live mailbox or reuse M4's calendar URLs for another purpose. Live mail protocols, sending mail, automatic contact/entity merges, mailbox write-back, decryption and archive expansion are excluded from this milestone. A later connector should feed the same source/account contracts after separate authorization.

The new implementation goal should reference this plan and the updated specification, preserve M4's deployed release and user data, and keep acceptance exceptions explicit. M4's pricing waiver does not waive any M5 mail or privacy gate.
