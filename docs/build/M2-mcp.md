# M2 MCP implementation

The current checkout exposes `POST /mcp` and provides `brain mcp` as a stdio-to-HTTP bridge. Both use the pinned official MCP C# SDK 2.2.0. The server uses `HttpServerSessionMode.Stateless`: current clients use the 2026-07-28 discovery protocol; older clients may initialize using 2025-11-25. Neither path creates transport sessions. The deprecated HTTP+SSE endpoint is absent.

The matching server and CLI builds are [deployed to the reference systemd service](M2-complete-deployment.md) as of 2026-10-24 at 19:57 UTC. The reference user's Codex connection is configured and its authenticated bridge is verified; see below and [current project status](../project-status.md).

## Client setup

Create a dedicated read-only credential through the existing login flow:

```sh
brain login http://YOUR-DAEMON:7171 --name bridge --scopes read
```

Configure an MCP client with the installed CLI and explicit daemon origin:

```json
{
  "mcpServers": {
    "secondbrain": {
      "command": "/path/to/brain",
      "args": ["--url", "http://YOUR-DAEMON:7171", "--credential-name", "bridge", "mcp"]
    }
  }
}
```

For a client that should capture or call the answering model, issue a separately named key with the intended scopes and explicitly select it with `mcp --credential <name>`. The default bridge refuses a key containing anything beyond `read`. It always refuses administrator keys and paired CLI sessions. It queries `/v1/auth/credential` to check the selected credential before forwarding protocol messages.

For a Cloudflare Access origin, store a service-token pair under the same origin and credential name. This command stores the Access token only; normal API-key login is a separate invocation.

```sh
# Supply SECONDBRAIN_ACCESS_CLIENT_SECRET through your environment/secret manager.
brain login https://YOUR-PUBLIC-ORIGIN --name bridge --access-client-id YOUR-SERVICE-TOKEN-ID
brain login https://YOUR-PUBLIC-ORIGIN --name bridge --scopes read
```

Access credentials use the existing OS credential store or private-file fallback, under a separate `.access` entry. They are injected on login, pairing, daemon commands and every bridge request. The CLI never prints the stored secret.

The Access application must authorize that token with a **Service Auth** policy. The bridge sends the token headers on every request rather than relying on an Access cookie, matching [Cloudflare's service-token contract](https://developers.cloudflare.com/cloudflare-one/access-controls/service-credentials/service-tokens/) and its [strict service-token authentication behavior](https://developers.cloudflare.com/changelog/2026-10-02-strict-service-token-authentication/). Local tests verify header injection; a live Cloudflare route has not been qualified in this change.

The bridge forwards each input JSON-RPC object unchanged to the fixed `/mcp` destination. It constructs the required version/method/name HTTP headers from the message, using the SDK header encoder; no client-provided HTTP destination or authorization metadata is forwarded. Redirects and ambient proxies remain disabled. It handles JSON and SSE responses, compacts responses to one JSON object per stdout line, and sends startup/errors to stderr. Notifications produce no fabricated reply. It never retries a write request automatically.

## Codex on the reference host

Configured and checked on **2026-10-24**. The reference user's `~/.codex/config.toml` contains this server entry, alongside the existing configuration:

```toml
[mcp_servers.secondbrain]
command = "/usr/local/lib/secondbrain/brain"
args = ["--url", "http://127.0.0.1:7171", "--credential-name", "codex", "mcp"]
env_vars = ["DBUS_SESSION_BUS_ADDRESS", "XDG_RUNTIME_DIR"]
enabled_tools = ["brain_search", "brain_query", "brain_get", "brain_list_recent", "brain_tags"]
startup_timeout_sec = 30
tool_timeout_sec = 60
```

The user completed login with a dedicated read-only credential named `codex`, stored in Linux Secret Service. No API key is embedded in the TOML. The forwarded session environment lets the bridge reach that store. The installed CLI path is absolute because a global `brain` command is not required for this configuration.

Only the five retrieval tools above are enabled for Codex. `brain_ask` needs `infer` as well as `read`; it is excluded here, along with the two write tools. Codex can use retrieved material in its own answers without invoking SecondBrain's answering model.

The exact configured command completed discovery, tool listing and an authenticated `brain_list_recent` call using both protocol generations. The [sanitized verification record](evidence/m2-codex-20261024/connection-verification.json) also records successful loopback/LAN health and readiness checks. No credential values, document contents or raw tool responses were retained.

Restart an already-running Codex session to load the entry. This verification launched the configured bridge directly; it does not prove that an existing Codex session reloaded its tool catalog. The setup is local to the reference host and its user account; other hosts need their own origin and stored credential.

## Server contracts

| Operation | Required scopes |
| --- | --- |
| Discovery, initialization, tool list, resource-template list, prompt list | Valid API key |
| Search, structured query, get, recent, tags, resource list/read, prompt get | `read` |
| Ask | `read`, `infer` |
| Remember, update proposal | `write` |

The server revalidates the stored credential and scopes in each handler, including the compatibility path. Browser and paired-session credentials cannot use the MCP transport. Host and Origin checks remain in the normal request pipeline. `subscriptions/listen` requires `read` before returning the explicitly unsupported-operation result; resource subscriptions are not advertised.

All eight tools are present: `brain_search`, `brain_query`, `brain_ask`, `brain_get`, `brain_remember`, `brain_update`, `brain_list_recent`, and `brain_tags`. Input schemas are generated from typed contracts with unknown fields rejected; shared retrieval filters, validation, repositories, provider policy and mutation services enforce the underlying rules. Unsupported transcript/calendar types report `unsupported_type` until M4 implements them.

Remember records `mcp:<stored credential name>` provenance, independent of claimed client metadata, and returns document identity/revision/path and projection state. Update creates an immutable proposal without inference, including for a write-only key. The proposal appears in the existing approval UI and paired CLI. It cannot execute through MCP. Stale revisions return a tool error with `status: 409` and `current_revision`; MCP transports the error as a tool result.

Resources are `brain://document/{id}` and `brain://note/{path}`; note paths are relative to the vault and cannot escape it. Reads return Markdown text. Large resources require paged `brain_get` calls; oversized full-resource reads fail explicitly. The prompts are `brain_daily_review` and `brain_topic_brief`.

Resource pagination wraps the existing signed, generation-bound query cursor in a credential/operation-bound envelope. Search pagination signs its query and result fingerprint and rejects a changed result set rather than silently shifting pages; search is capped at 200 ranked hits with explicit `truncated` state. Tag pagination computes vocabulary counts over available indexed documents and binds its cursor to corpus generation and credential. Cursors cannot be reused by another key or another operation.

Filtered asks persist the requested filters in the frozen turn policy. Initial retrieval, subsequent searches, structured queries, related-document expansion, and explicitly named document reads retain those constraints.

## Evidence

`McpTests` covers both protocol generations, per-method scope checks, the official SDK client, strict schemas, provenance, write-only proposals and interactive approval, text/resource reads, and signed pagination. `McpBridgeTests` covers protocol-only stdout, default read-only behavior, administrator-key rejection, credential/header replacement, Access storage, and legacy notifications. `CliConvergenceTests.Cli_McpBridgeUsesCurrentProtocolAgainstRealDaemon` exercises the real bridge with a temporary daemon. `ChatWorkflowTests.FilteredAskCannotReadAnExplicitlyNamedDocumentOutsideItsFilters` verifies the frozen filter boundary.

M2 implementation is verified, including retries, portable transcripts, slash commands and recorded answer-quality gates. See [the acceptance ledger](M2-acceptance.md).
