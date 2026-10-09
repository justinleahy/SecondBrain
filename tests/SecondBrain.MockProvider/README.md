# Mock provider

The M0 mock speaks the OpenAI-compatible wire protocol without a provider key.
It serves `mock-chat` and `mock-embed`; these are test identities, not product
defaults. Chat replies with `Mock response`. Embeddings have four dimensions by
default, with deterministic values `[0.1, 0.2, 0.3, 0.4]`. An explicit
`dimensions` value between 1 and 4096 repeats that pattern. Both float arrays
and the SDK's little-endian float32 `base64` encoding are supported.

For native tool-call fixtures, supply `tools` with function declarations and
`tool_choice: "required"` to select the first available function, or
`tool_choice: {"type":"function","function":{"name":"selected-name"}}` to
select an available function by name. Both normal and streaming chat return
one call with `{}` arguments and finish reason `tool_calls`. The call ID derives
from the response ID and stays unchanged across its stream. A forced choice
without a matching function returns 400. `auto` and `none` retain the normal
text response. The mock does not guarantee schema-constrained structured output.

Run standalone from the lane or merged checkout:

```sh
dotnet run --project tests/SecondBrain.MockProvider -- --urls http://127.0.0.1:8181
curl http://127.0.0.1:8181/v1/models
curl http://127.0.0.1:8181/v1/chat/completions \
  -H 'Content-Type: application/json' \
  -d '{"model":"mock-chat","messages":[{"role":"user","content":"hello"}],"stream":true}'
curl http://127.0.0.1:8181/v1/embeddings \
  -H 'Content-Type: application/json' \
  -d '{"model":"mock-embed","input":["one","two"],"encoding_format":"base64"}'
```

For one request, set `X-Mock-Fault` to `none`, `redirect`, `timeout`, `429`, or
`malformed`. The header overrides the server-wide fault, including `none`.
Faults apply to every `/v1` request, including model discovery. Redirect returns
307 with a public `https://example.com/mock-provider-redirect` location; the
mock itself never contacts that target. Timeout delays for 30 seconds, observes
request cancellation, and returns 504 if the caller remains connected. Rate
limiting returns an OpenAI-shaped 429 and `Retry-After: 1`; malformed returns
200 with deliberately invalid JSON.

```sh
curl http://127.0.0.1:8181/v1/models -H 'X-Mock-Fault: redirect' -i
curl http://127.0.0.1:8181/admin/fault \
  -H 'Content-Type: application/json' \
  -d '{"fault":"timeout","timeout_ms":30000}'
curl http://127.0.0.1:8181/admin/fault -X DELETE
curl http://127.0.0.1:8181/admin/state
curl http://127.0.0.1:8181/admin/reset -X POST
```

`POST /admin/fault` also accepts `redirect_target` for a controlled second
listener in redirect tests. `GET /admin/state` shows the fault configuration,
total provider requests and per-path counters. `POST /admin/reset` clears all
faults, restores default parameters, and resets counters. Admin endpoints are
test controls and have no authentication; bind this host only to the test
interface.

In-process tests can set `MockProviderState.ModelsResponse` to replace the
`/v1/models` response, for example with an oversized, chunked or slow body.
`Reset()` restores the standard list.

For in-process Kestrel, call
`MockProviderApplication.Build(["--urls", "http://127.0.0.1:0"])`, then
`StartAsync()`, and read the selected address from `app.Urls`. Obtain the
singleton `MockProviderState` through `app.Services` to set faults and inspect
`TotalRequests` or `GetRequestCount("/v1/models")`. The optional builder callback
supports `builder.WebHost.UseTestServer()` when the caller references
`Microsoft.AspNetCore.TestHost`. The public
`SecondBrain.MockProvider.Program` entry point also supports
`WebApplicationFactory<Program>`. Use Kestrel for policy transport tests so the
production `SocketsHttpHandler` and DNS pin checks are exercised.
