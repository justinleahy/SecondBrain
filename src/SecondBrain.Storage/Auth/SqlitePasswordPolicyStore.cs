using SecondBrain.Core.Auth;
using SecondBrain.Core.Storage;

namespace SecondBrain.Storage.Auth;

/// <summary>Reads the password policy from the state store's meta table.</summary>
public sealed class SqlitePasswordPolicyStore(IStateStore state) : IPasswordPolicyStore
{
    public async Task<string?> ReadPasswordParametersJsonAsync(CancellationToken cancellationToken = default)
    {
        await using var lease = await state.OpenReadConnectionAsync(cancellationToken);
        await using var command = lease.Connection.CreateCommand();
        command.CommandText = "SELECT value FROM meta WHERE key='password_parameters'";
        return await command.ExecuteScalarAsync(cancellationToken) as string;
    }
}
