using System.Net;
using System.Net.Http.Json;
using Microsoft.Extensions.DependencyInjection;
using SecondBrain.Core.Auth;
using SecondBrain.Core.Authorization;
using SecondBrain.Server.Auth;
using SecondBrain.Server.Tests.Support;
using Xunit;

namespace SecondBrain.Server.Tests;

public sealed class AuthorityMutationTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task AdmittedAdminRevokedDuringAuthorizationCannotMutate(bool create)
    {
        var policy = new AdmissionPolicy();
        await using var factory = new LaneDWebFactory(configureServices: services => services.AddSingleton<IScopePolicy>(policy));
        var repository = factory.Services.GetRequiredService<IAuthRepository>();
        var actor = await factory.CreateKeyAsync(new HashSet<Scope> { Scope.Admin });
        var target = await factory.CreateKeyAsync(new HashSet<Scope> { Scope.Read });
        using var client = factory.CreatePrivateClient(false);
        client.DefaultRequestHeaders.Authorization = new("Bearer", actor.Plaintext);
        policy.AfterAdmission = () => repository.RevokeAsync(actor.Record.Id).GetAwaiter().GetResult();
        using var response = create
            ? await client.PostAsJsonAsync("/keys", new CreateKeyRequest("blocked", ["read"]))
            : await client.DeleteAsync("/keys/" + target.Record.Id);
        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        Assert.Null((await repository.FindAsync(target.Record.Id))!.RevokedAt);
        Assert.DoesNotContain((await repository.ListPageAsync("api_key", false, null, 100)).Items, row => row.Name == "blocked");
    }

    private sealed class AdmissionPolicy : IScopePolicy
    {
        private readonly M0ScopePolicy inner = new();
        public Action? AfterAdmission { get; set; }
        public ScopeMatrix Matrix => inner.Matrix;
        public bool IsAllowed(string operation, IReadOnlySet<Scope> scopes)
        {
            var allowed = inner.IsAllowed(operation, scopes);
            if (allowed && operation is "keys.create" or "keys.revoke") AfterAdmission?.Invoke();
            return allowed;
        }
    }
}
