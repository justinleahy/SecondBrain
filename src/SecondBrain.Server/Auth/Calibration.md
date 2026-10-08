# Argon2 calibration (lane D, 2026-10-08)

Measured locally using .NET SDK 10.0.105, shared runtime 10.0.5, and Isopoh.Cryptography.Argon2 2.0.0 on macOS 27.0.1; Arm64; 10 CPUs.

The routine warms five hashes before collecting three timed samples and reporting the median. It starts with 64 MiB, three iterations, one lane, then measures the lower and upper iteration candidates and selects the one closest to 250 ms. Salt is 16 random bytes and output is 32 bytes, with Argon2id version 19.

- Starting parameters: 65,536 KiB, 3 iterations, 1 lane; median **253.74 ms**.
- Selected parameters: 65,536 KiB, 3 iterations, 1 lane; median **244.53 ms**.
- Stored application password parameter version: 1.

These are this macOS development host's measurements. Run `PasswordHasher.Calibrate()` (in `SecondBrain.Infrastructure.Security`) on the deployment host during brain init and bind its returned `PasswordParameters` (in `SecondBrain.Core.Auth`) for the credential factory and daemon. The encoded password hash stores the Argon2 parameters; account.password_version stores the application policy version. Changing the registered policy triggers a compare-and-set rehash after a successful login. Password reset must use `SecondBrain.Core.Auth.IAuthRepository.SaveAccountAsync(account, invalidate: true)` (implemented in `SecondBrain.Storage.Auth`) or atomically bump account_epoch alongside the new hash when lane A writes initialization/recovery records.

The test fixture deliberately uses 1 MiB/1 iteration to exercise HTTP behavior quickly; the calibration test explicitly constructs production parameters. Cold managed-code JIT measurements were discarded because they do not describe steady-state verification cost.

The final full server suite measured these values. An isolated calibration run selected the same 64 MiB/3/1 parameters and measured 217.98 ms starting / 214.98 ms selected; variation reflects local CPU/JIT load.
