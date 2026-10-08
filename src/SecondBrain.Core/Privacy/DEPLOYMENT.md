# Provider-data and control-plane egress

SEC-17 requires the host/container rule as the privacy backstop. The systemd unit must
set `IPAddressDeny=any` and allow loopback, the configured trusted-service pinned
addresses, the DNS resolver, and the configured Access certificate endpoint. Compose
must provide an egress-restricted network with the same explicit destinations. With
`local_only` disabled, also allow the configured hosted provider endpoints.

DNS resolution and Cloudflare Access signing-key refresh are control-plane operations.
Their allowances never grant permission for provider data. Access refresh must keep
the last good cached key set on failure. The provider transport must never send a
provider request through those control-plane clients or treat a control-plane URL as
a trusted provider endpoint.

`EgressCanary.RunAsync` checks immediately on daemon startup and hourly using
`TimeProvider`; `RunOnceAsync` is available for `brain doctor` and deterministic tests.
Its TCP connect sends no application data and times out after two seconds. A successful
connection sets `Reachable`; `local_only` requests then fail with `egress-unverified`.
A later failed or timed-out check sets `Blocked` and clears that refusal. Before the
first enabled check, `Unknown` also refuses local-only calls. Explicitly disabling the
canary leaves the check disabled; readiness reports that configuration.

`IPrivacyReadiness.GetReadiness()` supplies the readiness/doctor contribution. The
canary target defaults to the configuration contract's `1.1.1.1:443`; ensure that target
does not overlap an allowed destination. A disabled canary must not be represented as
a successfully checked firewall. The browser's Cloudflare traffic is outside this
daemon egress guarantee.
