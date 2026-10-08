namespace SecondBrain.Core.Auth;

public interface IPasswordHasher
{
    PasswordParameters Parameters { get; }
    string Hash(string password);
    bool Verify(string encodedHash, string password);
    bool NeedsRehash(AccountRecord account);
}
