# Conversation controls

Click a citation number to jump to its source card and open the excerpt. Each answer links to its own sources.

Enter sends a message; Shift+Enter inserts a new line. Empty input and repeated held keys do not send extra requests. Text composition retains its normal Enter behavior.

Each conversation has a delete button and an explicit confirmation. Deleting removes its transcript, replay events, source excerpts, summaries, and pending proposals. Notes already saved to the vault remain. A conversation cannot be deleted while its response or an approved write is still running.

The authenticated `DELETE /conversations/{id}` endpoint also has a `/v1` alias. It requires `write` scope and revalidates current credential authority and conversation ownership in the deletion transaction. Browser calls retain the existing Origin and antiforgery checks. API keys can delete only their own conversations; browser sessions share account conversations. Content-free token charges and deleted-request markers remain so deletion cannot reset usage limits or replay a deleted submission.

Both controls are deployed on the reference host as of 2026-10-24. Conversation deletion passed all 1,809 deterministic tests and live browser checks for confirmation, cancellation, removal after reload, pending-proposal removal, preserved documents, and the 390 px layout. The [deployment report](evidence/m2-conversation-delete-20261024/deployment-result.json) and [independent verification](evidence/m2-conversation-delete-20261024/installed-verification.json) confirm the installed 430-file release, healthy providers/stores, the delete API, and the Enter shortcut on loopback and LAN. Selected browser and database checks are in [the evidence directory](evidence/m2-conversation-delete-20261024).
