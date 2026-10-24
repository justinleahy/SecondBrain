# Conversation controls

Click a citation number to jump to its source card and open the excerpt. Each answer links to its own sources.

Enter sends a message; Shift+Enter inserts a new line. Empty input and repeated held keys do not send extra requests. Text composition retains its normal Enter behavior.

Each conversation has a delete button and an explicit confirmation. Deleting removes its transcript, replay events, source excerpts, summaries, and pending proposals. Notes already saved to the vault remain. A conversation cannot be deleted while its response or an approved write is still running.

The authenticated `DELETE /conversations/{id}` endpoint also has a `/v1` alias. It requires `write` scope and revalidates current credential authority and conversation ownership in the deletion transaction. Browser calls retain the existing Origin and antiforgery checks. API keys can delete only their own conversations; browser sessions share account conversations. Content-free token charges and deleted-request markers remain so deletion cannot reset usage limits or replay a deleted submission.

Both controls are deployed on the reference host as of 2026-10-24. Conversation deletion passed all 1,809 deterministic tests and live browser checks for confirmation, cancellation, removal after reload, pending-proposal removal, preserved documents, and the 390 px layout. The [deployment report](evidence/m2-conversation-delete-20261024/deployment-result.json) and [independent verification](evidence/m2-conversation-delete-20261024/installed-verification.json) confirm the installed 430-file release, healthy providers/stores, the delete API, and the Enter shortcut on loopback and LAN. Selected browser and database checks are in [the evidence directory](evidence/m2-conversation-delete-20261024).

## Full notes from sources

An expanded source card now offers **Open full note**. It loads the note inline with its title, tags, and revision, preserving the conversation and draft. The reader loads the current note; if its revision differs from the citation, a notice distinguishes it from the historical excerpt. **Close full note** collapses the reader, and **Download original** uses the existing authenticated download endpoint.

Opening a note revalidates the stored read permission and credential before and after loading. Deleted, purged, missing, and removed documents are unavailable. Text uses the existing safe formatter, and opening the reader does not add document content to a model turn.

The implementation passed 263 server tests and browser checks with a multi-section note whose last paragraph was absent from its citation excerpt. The checks covered the full text, preserved draft, updated revisions, the original download, deleted-note rejection, and 390 px layout. The release was deployed on 2026-10-24 at 18:39 UTC. The [deployment report](evidence/m2-full-note-20261024/deployment-result.json) and [independent verification](evidence/m2-full-note-20261024/installed-verification.json) confirm all 430 installed files, healthy services, preserved documents and credentials, authenticated chat endpoints, and matching assets on loopback and LAN. The deployed LAN page loaded without browser errors. Selected browser checks are in [the evidence directory](evidence/m2-full-note-20261024).

## Mobile layout

On screens up to 760 px wide and wide phones in short landscape viewports, a compact header provides **Chats** and **New conversation**. Chats expands a scrollable conversation list with access to approvals. Selecting a conversation or approvals closes the list and returns keyboard focus to its toggle; Escape closes it too. Desktop retains the sidebar.

Messages and full notes scroll above the composer. The layout follows the visible viewport when a software keyboard changes its height, respects safe-area insets, and preserves pinch zoom. Mobile inputs use 16 px text, primary controls have 44 px touch targets, and source cards wrap within the screen. Sign-in and approval controls follow the same sizing rules.

The changes passed 263 server regression tests and a temporary browser qualification with real local-model submission. Browser checks covered 320–1440 px widths, short landscape and reduced-height viewports, 25 saved conversations, deletion confirmation/cancellation, approvals, full notes, draft preservation, and Enter/Shift+Enter. No horizontal overflow or browser errors were found. Physical Safari keyboard and hardware safe-area behavior were not exercised. [Validation and screenshots](evidence/m2-mobile-20261024) record the checks. Deployed on 2026-10-24 at 18:51 UTC. The [deployment report](evidence/m2-mobile-20261024/deployment-result.json) and [independent verification](evidence/m2-mobile-20261024/installed-verification.json) confirm all 430 installed files, healthy services, preserved documents/configuration/credentials, and matching mobile assets on the live LAN site. The deployed phone-sized sign-in page loaded without browser errors.
