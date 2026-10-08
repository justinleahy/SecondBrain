using System.Text.Json;
using SecondBrain.Core.Configuration;

namespace SecondBrain.Cli.Commands;

/// <summary>Exit statuses from specification §12.</summary>
public enum CliExitCode { Ok = 0, Error = 1, Usage = 2, DaemonUnreachable = 3, PreconditionFailed = 4 }

/// <summary>A result suitable for text and JSON output; payloads must contain no retained secrets.</summary>
public sealed record CliResult(CliExitCode ExitCode, string Code, string Message, object? Data = null)
{
    public static CliResult Ok(string message, object? data = null) => new(CliExitCode.Ok, "ok", message, data);
    public static CliResult Unbound(string component) => new(CliExitCode.PreconditionFailed,
        "integration-required", $"{component} is not implemented by this CLI adapter; bind the owning lane's service at convergence.");
}

/// <summary>Lane A store creation and lane D initial credential/password integration.</summary>
public interface IInitializationCommands
{
    Task<CliResult> InitializeAsync(SecondBrainOptions options, CancellationToken cancellationToken);
    Task<CliResult> ResetPasswordAsync(SecondBrainOptions options, CancellationToken cancellationToken);
}

/// <summary>Lane D key management. Creation may disclose a new key once; list/revoke must never return secrets.</summary>
public interface ICredentialCommands
{
    Task<CliResult> CreateAsync(IReadOnlyList<string> scopes, CancellationToken cancellationToken);
    Task<CliResult> ListAsync(CancellationToken cancellationToken);
    Task<CliResult> RevokeAsync(string id, CancellationToken cancellationToken);
}

/// <summary>Lane B provider listing and policy-owned provider health checking.</summary>
public interface IProviderCommands
{
    Task<CliResult> ListAsync(CancellationToken cancellationToken);
    Task<CliResult> TestAsync(string? name, CancellationToken cancellationToken);
}

/// <summary>Lane D session and account epoch management.</summary>
public interface ISessionCommands
{
    Task<CliResult> ListAsync(CancellationToken cancellationToken);
    Task<CliResult> RevokeAsync(string id, CancellationToken cancellationToken);
    Task<CliResult> RevokeAllAsync(CancellationToken cancellationToken);
}

/// <summary>Obtains a credential from lane D without exposing it to the CLI output pipeline.</summary>
public interface ILoginCommands
{
    Task<LoginCredential> AcquireAsync(Uri origin, string name, IReadOnlyList<string> scopes, CancellationToken cancellationToken);
}

/// <summary>The API key is deliberately excluded from JSON serialization and diagnostic stringification.</summary>
public sealed class LoginCredential(string apiKey, string? credentialId = null)
{
    [System.Text.Json.Serialization.JsonIgnore]
    public string ApiKey { get; } = apiKey;
    public string? CredentialId { get; } = credentialId;
    public override string ToString() => nameof(LoginCredential);
}

/// <summary>Lane A/B/D diagnostics that extend lane C's filesystem, listener, key-ring and socket checks.</summary>
public interface IDoctorExtension
{
    Task<IReadOnlyList<DoctorCheck>> CheckAsync(SecondBrainOptions options, CancellationToken cancellationToken);
}

public sealed record DoctorCheck(string Name, bool Passed, string Message);

/// <summary>Convergence hook for an in-process or installed daemon entrypoint.</summary>
public interface IDaemonCommands
{
    Task<int> ServeAsync(DaemonLaunchOptions options, CancellationToken cancellationToken);
}

public sealed record DaemonLaunchOptions(string ConfigPath, string DataRoot, string? SecretsDirectory, uint SyncUid, TextWriter Diagnostics);

public sealed class UnboundCommands : IInitializationCommands, ICredentialCommands, IProviderCommands, ISessionCommands, ILoginCommands
{
    public Task<CliResult> InitializeAsync(SecondBrainOptions options, CancellationToken cancellationToken) => Result("Store and account initialization (lanes A/D)");
    public Task<CliResult> ResetPasswordAsync(SecondBrainOptions options, CancellationToken cancellationToken) => Result("Password recovery (lane D)");
    public Task<CliResult> CreateAsync(IReadOnlyList<string> scopes, CancellationToken cancellationToken) => Result("Credential creation (lane D)");
    public Task<CliResult> ListAsync(CancellationToken cancellationToken) => Result("Daemon administration (lanes B/D)");
    public Task<CliResult> RevokeAsync(string id, CancellationToken cancellationToken) => Result("Credential/session revocation (lane D)");
    public Task<CliResult> RevokeAllAsync(CancellationToken cancellationToken) => Result("Session epoch revocation (lane D)");
    public Task<CliResult> TestAsync(string? name, CancellationToken cancellationToken) => Result("Provider health checking (lane B)");
    public Task<LoginCredential> AcquireAsync(Uri origin, string name, IReadOnlyList<string> scopes, CancellationToken cancellationToken) =>
        throw new CliPreconditionException("Login credential issuance (lane D) is not implemented by this CLI adapter; bind ILoginCommands at convergence.");
    private static Task<CliResult> Result(string owner) => Task.FromResult(CliResult.Unbound(owner));
}

public sealed class CliPreconditionException(string message) : Exception(message);
public sealed class CliUsageException(string message) : Exception(message);
public sealed class DaemonUnreachableException(string message) : Exception(message);

internal static class CliOutput
{
    internal static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web) { WriteIndented = false };
    internal static async Task<int> WriteAsync(CliResult result, bool json, TextWriter output, TextWriter error)
    {
        if (json) await output.WriteLineAsync(JsonSerializer.Serialize(result, JsonOptions));
        else await (result.ExitCode == CliExitCode.Ok ? output : error).WriteLineAsync(result.Message);
        return (int)result.ExitCode;
    }
}
