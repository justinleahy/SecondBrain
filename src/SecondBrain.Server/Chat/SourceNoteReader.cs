using SecondBrain.Core.Auth;
using SecondBrain.Core.Authorization;
using SecondBrain.Core.Chat;
using SecondBrain.Core.Ingestion;

namespace SecondBrain.Server.Chat;

/// <summary>Loads current document text for the browser without adding it to a model turn.</summary>
public static class SourceNoteReader
{
    public static async Task<DocumentRecord> LoadAsync(string id, AuthenticatedCredential actor,
        IAuthRepository auth, IDocumentRepository documents, CancellationToken token = default)
    {
        await CheckReadAsync();
        var metadata = await documents.GetAsync(id, false, token);
        if (metadata is null || DocumentStatusNames.IsUnavailable(metadata.Status)) throw new ChatNotFoundException();
        var document = await documents.GetAsync(id, true, token);
        if (document is null || DocumentStatusNames.IsUnavailable(document.Status)) throw new ChatNotFoundException();
        await CheckReadAsync();
        return document;

        async Task CheckReadAsync()
        {
            var credential = await auth.FindAsync(actor.Id, token);
            if (credential is null || credential.Kind != actor.Kind || !credential.GrantedScopes.Contains(Scope.Read)
                || !await auth.IsCurrentAsync(actor.Id, actor.Generation, actor.AccountEpoch, token))
                throw new AuthorityChangedException();
        }
    }
}
