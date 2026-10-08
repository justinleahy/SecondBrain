# M0 extractor socket contract (version 1)

Run `SecondBrain.Extractor --socket /run/secondbrain/extractor.sock`. The directory must already exist and be writable by `secondbrain-extract`. Standalone mode creates a `0660` socket; use the daemon group as the process group in Compose. An existing path is refused rather than deleted. On Linux, matching `LISTEN_PID` and `LISTEN_FDS=1` adopt systemd fd 3 as a listening Unix stream socket (`Accept=no`). Only standalone sockets are removed on clean shutdown. `--request-timeout-ms` defaults to 10000 (maximum 120000); `--max-clients` defaults to 8 (maximum 64).

Each connection carries **one request and one response**, then closes. Each frame is a four-byte unsigned **big-endian** length followed by exactly that many UTF-8 JSON bytes. Empty frames and frames larger than **65536 bytes** are rejected before allocating the body. JSON has a maximum depth of 8, unknown fields are rejected, and request ids/operations are limited to 128 characters. Transport failures close the connection; valid frames with invalid requests receive a structured error.

```json
{"version":1,"id":"doctor-1","operation":"ping"}
{"id":"doctor-1","ok":true,"result":"pong","error":null,"version":1,"bytes_read":null,"sha256":null}
```

For descriptor-bearing requests, send **exactly one** `SCM_RIGHTS` descriptor with `sendmsg`, attached to the **first byte of the four-byte prefix**. Send no descriptors on subsequent prefix/body bytes. `recvmsg` receives and owns the duplicate, sets close-on-exec, rejects multiple/truncated control data, and closes received descriptors on success and failure. The sender retains its original descriptor. Responses contain no descriptors. Clients must validate the frame bound, response id, version, and expected shape before interpreting output.

The M0 spike operation is `probe_descriptor`: pass an open **read-only regular file** no larger than **65536 bytes**. The response contains `result: "descriptor_read"`, `bytes_read`, and the lowercase SHA-256 digest. Content is never echoed or logged. Positional reads preserve the sender's shared file offset. A test unlinks the file before sending it and proves that the receiver reads it solely through the passed descriptor. This operation defines the descriptor boundary; physical-format parsers and real extraction requests arrive in M1.

Errors include `invalid_request`, `invalid_json`, `unsupported_version`, `unexpected_descriptor`, `descriptor_required`, `invalid_descriptor`, `descriptor_read_failed`, `probe_size_exceeded`, and `not_implemented`. Ping accepts no descriptor. A received descriptor is never interpreted as a path. Frames and document data are not logged.

The small P/Invoke transport targets 64-bit Linux and macOS (x64 and arm64) and implements each platform's `msghdr`/`cmsghdr` layout and alignment. See the [Linux ancillary-data API](https://man7.org/linux/man-pages/man3/cmsg.3.html) and [Darwin recvmsg contract](https://developer.apple.com/library/archive/documentation/System/Conceptual/ManPages_iPhoneOS/man2/recvmsg.2.html). Deployment supplies the dedicated UID, scrubbed environment, network/store/secret isolation, and process resource limits. The daemon must enforce deadlines and revalidate M1 extraction output.

Before building the host, the executable removes inherited environment values except `LISTEN_PID`, `LISTEN_FDS`, `NOTIFY_SOCKET`, `TMPDIR`, and `PATH`; it sets `DOTNET_EnableDiagnostics=0`. Service/container launch configuration must also disable diagnostics before the runtime starts. Descriptor close-on-exec is read back with `F_GETFD`; the Darwin arm64 variadic `fcntl` setter uses an explicit stack argument matching the [Apple ARM64 ABI](https://developer.apple.com/documentation/xcode/writing-arm64-code-for-apple-platforms), verified against Clang output and a received-descriptor kernel-flag test.
