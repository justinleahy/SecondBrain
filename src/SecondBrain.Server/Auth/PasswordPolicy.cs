using System.Text.Json;
using SecondBrain.Core.Storage;

namespace SecondBrain.Server.Auth;

/// <summary>Uses the deployment host's policy recorded atomically by brain init.</summary>
public static class PasswordPolicy
{
    public static PasswordParameters Load(IStateStore state) => LoadAsync(state).GetAwaiter().GetResult();

    private static async Task<PasswordParameters> LoadAsync(IStateStore state)
    {
        await using var lease = await state.OpenReadConnectionAsync();
        await using var command = lease.Connection.CreateCommand();
        command.CommandText = "SELECT value FROM meta WHERE key='password_parameters'";
        var json = await command.ExecuteScalarAsync() as string;
        if (json is null) return new PasswordParameters();
        var policy = JsonSerializer.Deserialize<PasswordParameters>(json) ?? throw new InvalidDataException("Stored password policy is invalid.");
        if (policy.Version < 1 || policy.MemoryKiB is < 1024 or > 1048576 || policy.Iterations is < 1 or > 12 || policy.Lanes is < 1 or > 64)
            throw new InvalidDataException("Stored password policy is invalid.");
        return policy;
    }
}
