using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Dapper;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using SecondBrain.Core.Auth;
using SecondBrain.Core.Configuration;
using SecondBrain.Core.Security;
using SecondBrain.Infrastructure.Security;
using SecondBrain.Core.Storage;
using SecondBrain.Storage.Auth;
using SecondBrain.Storage;

namespace SecondBrain.Cli.Commands;

/// <summary>Local bootstrap and recovery run inside the command shell's exclusive data-root lock.</summary>
public sealed class LocalInitializationCommands(
    Func<string, FileKeyRing> openKeyRing,
    Func<string, CancellationToken, Task<string>> readPassword,
    Func<PasswordParameters> calibrate) : IInitializationCommands, IAccountEpochRevoker
{
    private SecondBrainOptions? configured;
    public void Configure(SecondBrainOptions options) => configured = options;

    public async Task<CliResult> InitializeAsync(SecondBrainOptions options, CancellationToken cancellationToken)
    {
        await using var services = OpenStorage(options);
        await services.GetRequiredService<IMigrationRunner>().MigrateAsync(cancellationToken);
        var repository = Repository(services);
        if (await repository.GetAccountAsync(cancellationToken) is not null)
        {
            await using var lease = await services.GetRequiredService<IStateStore>().OpenReadConnectionAsync(cancellationToken);
            var existingId = await lease.Connection.QuerySingleOrDefaultAsync<string>(new CommandDefinition(
                "SELECT value FROM meta WHERE key='initial_admin_credential_id'", cancellationToken: cancellationToken));
            existingId ??= await lease.Connection.QuerySingleOrDefaultAsync<string>(new CommandDefinition(
                "SELECT id FROM credentials WHERE kind='api_key' AND instr(',' || scopes || ',', ',admin,')>0 ORDER BY created_at,id LIMIT 1", cancellationToken: cancellationToken));
            if (existingId is null) throw new CliPreconditionException("The account exists without an initial admin credential; initialization will not overwrite existing state.");
            return CliResult.Ok("Stores and account are already initialized; the original admin key is not recoverable.", new { initialized = false, credentialId = existingId });
        }
        var password = await readPassword("initialize", cancellationToken);
        RequirePassword(password);
        var parameters = calibrate();
        using var ring = openKeyRing(options.DataRoot);
        IAdminCredentialFactory factory = new CredentialFactory(ring, new PasswordHasher(parameters), TimeProvider.System);
        var records = factory.CreateInitializationRecords(password);
        var verifier = Convert.FromBase64String(records.AdminCredential.Record.Verifier);
        StoreInitializationResult result;
        try
        {
            result = await services.GetRequiredService<IStoreInitializer>().InitializeAsync(new StoreInitializationRequest(
                records.AdminCredential.Record.Id, verifier, records.AdminCredential.Record.Kid,
                records.AdminCredential.Record.Scopes.Split(','), new PasswordHashRecord(records.Account.PasswordHash, records.Account.PasswordVersion),
                records.AdminCredential.Record.Name, options.Time.Zone, JsonSerializer.Serialize(parameters)), cancellationToken);
        }
        finally { CryptographicOperations.ZeroMemory(verifier); }
        return result.Initialized
            ? CliResult.Ok("Stores and account initialized. Save this admin key; it is shown once.",
                new InitialAdminResult(result.CredentialId, records.AdminCredential.Plaintext, parameters))
            : CliResult.Ok("Stores and account were already initialized; no new key was issued.", new { initialized = false, credentialId = result.CredentialId });
    }

    public async Task<CliResult> ResetPasswordAsync(SecondBrainOptions options, CancellationToken cancellationToken)
    {
        await using var services = OpenStorage(options);
        await services.GetRequiredService<IMigrationRunner>().MigrateAsync(cancellationToken);
        var repository = Repository(services);
        if (await repository.GetAccountAsync(cancellationToken) is null) throw new CliPreconditionException("The account is not initialized; run brain init first.");
        var password = await readPassword("reset", cancellationToken);
        RequirePassword(password);
        var parameters = calibrate();
        using var ring = openKeyRing(options.DataRoot);
        var account = new CredentialFactory(ring, new PasswordHasher(parameters), TimeProvider.System).CreateAccount(password);
        // Password, deployment policy and epoch are one transaction: no stale credential survives recovery.
        var epoch = await services.GetRequiredService<IStateStore>().QueueWriteAsync(async (connection, transaction, token) =>
        {
            var changed = await connection.ExecuteAsync(new CommandDefinition(
                "UPDATE account SET password_hash=@PasswordHash,password_version=@PasswordVersion,updated_at=@UpdatedAt WHERE id=1", account, transaction, cancellationToken: token));
            if (changed != 1) throw new CliPreconditionException("The account is not initialized.");
            await connection.ExecuteAsync(new CommandDefinition(
                "INSERT INTO meta(key,value) VALUES('password_parameters',@policy) ON CONFLICT(key) DO UPDATE SET value=excluded.value; UPDATE meta SET value=CAST(value AS INTEGER)+1 WHERE key='account_epoch'",
                new { policy = JsonSerializer.Serialize(parameters) }, transaction, cancellationToken: token));
            return await connection.QuerySingleAsync<long>(new CommandDefinition(
                "SELECT CAST(value AS INTEGER) FROM meta WHERE key='account_epoch'", transaction: transaction, cancellationToken: token));
        }, cancellationToken);
        return CliResult.Ok("Password reset; all existing credentials and sessions are invalidated.", new { accountEpoch = epoch, passwordParameters = parameters });
    }

    public async ValueTask BumpEpochAsync(CancellationToken cancellationToken = default)
    {
        var options = configured ?? throw new CliPreconditionException("Account recovery requires local configuration.");
        await using var services = OpenStorage(options);
        var repository = Repository(services);
        if (await repository.GetAccountAsync(cancellationToken) is null) throw new CliPreconditionException("The account is not initialized.");
        await repository.BumpEpochAsync(cancellationToken);
    }

    private static ServiceProvider OpenStorage(SecondBrainOptions options)
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton<IOptions<SecondBrainOptions>>(Options.Create(options));
        services.AddSecondBrainStorage();
        return services.BuildServiceProvider();
    }
    private static AuthRepository Repository(IServiceProvider services) => new(services.GetRequiredService<IStateStore>(), TimeProvider.System);
    private static void RequirePassword(string password)
    {
        if (string.IsNullOrEmpty(password) || Encoding.UTF8.GetByteCount(password) > 1024)
            throw new CliPreconditionException("A nonempty password of at most 1024 UTF-8 bytes is required.");
    }
}

public sealed record InitialAdminResult(string CredentialId, string AdminKey, PasswordParameters PasswordParameters) : IOneTimeSecret
{
    public bool Initialized => true;
    [JsonIgnore] public string Secret => AdminKey;
    public override string ToString() => $"InitialAdminResult({CredentialId}, redacted)";
}

public static class PasswordInput
{
    /// <summary>Bootstrap secrets may come from a short-lived environment variable; console input is never echoed.</summary>
    public static Task<string> ReadAsync(string purpose, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var value = purpose is "initialize" or "reset" ? Environment.GetEnvironmentVariable("SECONDBRAIN_BOOTSTRAP_PASSWORD") : null;
        value ??= Environment.GetEnvironmentVariable("SECONDBRAIN_PASSWORD");
        if (value is not null) return Task.FromResult(value);
        if (Console.IsInputRedirected)
        {
            var line = Console.ReadLine();
            return Task.FromResult(line ?? throw new CliPreconditionException("Supply the account password on stdin or through SECONDBRAIN_PASSWORD (SECONDBRAIN_BOOTSTRAP_PASSWORD for init)."));
        }
        Console.Error.Write(purpose is "initialize" or "reset" ? "New account password: " : "Account password: ");
        var password = new StringBuilder();
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var key = Console.ReadKey(intercept: true);
            if (key.Key == ConsoleKey.Enter) break;
            if (key.Key == ConsoleKey.Backspace) { if (password.Length > 0) password.Length--; }
            else if (!char.IsControl(key.KeyChar)) password.Append(key.KeyChar);
        }
        Console.Error.WriteLine();
        return Task.FromResult(password.ToString());
    }
}
