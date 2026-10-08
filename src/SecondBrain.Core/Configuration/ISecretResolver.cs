namespace SecondBrain.Core.Configuration;

/// <summary>Resolves the references retained by the frozen provider configuration contract.</summary>
public interface ISecretResolver
{
    string Resolve(string reference);
}
