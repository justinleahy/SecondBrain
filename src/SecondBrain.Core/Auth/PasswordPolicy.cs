using System.Text.Json;

namespace SecondBrain.Core.Auth;

/// <summary>Uses the deployment host's policy recorded atomically by brain init.</summary>
public static class PasswordPolicy
{
    public static PasswordParameters Load(IPasswordPolicyStore store) => LoadAsync(store).GetAwaiter().GetResult();

    private static async Task<PasswordParameters> LoadAsync(IPasswordPolicyStore store)
    {
        var json = await store.ReadPasswordParametersJsonAsync();
        if (json is null) return new PasswordParameters();
        var policy = JsonSerializer.Deserialize<PasswordParameters>(json) ?? throw new InvalidDataException("Stored password policy is invalid.");
        if (policy.Version < 1 || policy.MemoryKiB is < 1024 or > 1048576 || policy.Iterations is < 1 or > 12 || policy.Lanes is < 1 or > 64)
            throw new InvalidDataException("Stored password policy is invalid.");
        return policy;
    }
}
