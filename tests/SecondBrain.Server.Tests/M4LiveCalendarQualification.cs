using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using Dapper;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Options;
using SecondBrain.Core.Authorization;
using SecondBrain.Core.Configuration;
using SecondBrain.Core.Extraction;
using SecondBrain.Core.Ingestion;
using SecondBrain.Core.Privacy;
using SecondBrain.Core.Sources;
using SecondBrain.Infrastructure.Extraction;
using SecondBrain.Infrastructure.Network;
using SecondBrain.Server.Sources;
using SecondBrain.Server.Tests.Support;
using SecondBrain.Storage;
using Xunit;

namespace SecondBrain.Server.Tests;

/// <summary>Explicit live qualification: link arrives over a private local socket, never argv, environment or files.</summary>
public sealed class M4LiveCalendarQualification
{
    [Fact]
    [Trait("Category", "Qualification")]
    public async Task ImportAndConditionalRefresh()
    {
        Assert.Equal("1", Environment.GetEnvironmentVariable("SECONDBRAIN_M4_LIVE_CALENDAR"));
        var directory = Environment.GetEnvironmentVariable("SECONDBRAIN_M4_LIVE_OUTPUT") ?? throw new InvalidOperationException("Set the private qualification directory.");
        Directory.CreateDirectory(directory);
        if (!OperatingSystem.IsWindows()) File.SetUnixFileMode(directory, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        using var deadline = new CancellationTokenSource(TimeSpan.FromMinutes(4));
        var token = deadline.Token;
        var url = await ReadPrivateLink(directory, token);
        var link = CalendarLink.Parse(url);
        await using var factory = new LaneDWebFactory(options =>
        {
            options.Time.Zone = "America/New_York";
            options.Sources.CalendarAllowedOrigins = [link.Origin];
        }, services =>
        {
            services.Replace(ServiceDescriptor.Singleton<IExtractorClient>(provider =>
                new DescriptorExtractorClient(provider.GetRequiredService<IOptionsMonitor<SecondBrainOptions>>().CurrentValue.Extractor.SocketPath)));
            services.AddSingleton<ObservedTransport>();
            services.Replace(ServiceDescriptor.Singleton<ICalendarDownloadTransport>(provider => provider.GetRequiredService<ObservedTransport>()));
        });
        var key = await factory.CreateKeyAsync(new HashSet<Scope> { Scope.Admin, Scope.Read, Scope.Write }, cancellationToken: token);
        var worker = factory.Services.GetRequiredService<CalendarSubscriptionOrchestrator>();
        await worker.StopAsync(token);
        var repository = factory.Services.GetRequiredService<ICalendarSubscriptionRepository>();
        var actor = new IngestionAuthority(key.Record.Id, key.Record.Generation, key.Record.AccountEpoch, "Live calendar qualification", "api_key");
        var source = await factory.Services.GetRequiredService<CalendarSubscriptionService>().CreateAsync("Live qualification", url, 3600, true, actor, token);
        var first = Assert.Single(await repository.ClaimDueAsync(token: token));
        await worker.ProcessAsync(first, token);
        var accepted = (await repository.GetAsync(source.Id, token))!;
        var initialSucceeded = accepted.LastSuccessAt is not null;
        long members = 0, occurrences = 0, firstRevision = 0, lastRevision = 0;
        bool? conditionalSucceeded = null;
        bool? bytesChanged = null;
        string? conditionalError = null;
        if (initialSucceeded)
        {
            await using (var state = await factory.Store.OpenReadConnectionAsync(token))
            {
                members = await state.Connection.ExecuteScalarAsync<long>(new CommandDefinition("SELECT COUNT(*) FROM calendar_members WHERE container_id=@id", new { id = first.ContainerId }, cancellationToken: token));
                firstRevision = await state.Connection.ExecuteScalarAsync<long>(new CommandDefinition("SELECT SUM(revision) FROM documents WHERE source_id=@id", new { id = source.Id }, cancellationToken: token));
            }
            string? hashBefore;
            await using (var state = await factory.Store.OpenReadConnectionAsync(token))
                hashBefore = await state.Connection.ExecuteScalarAsync<string>(new CommandDefinition("SELECT snapshot_hash FROM calendar_subscriptions WHERE source_id=@id", new { id = source.Id }, cancellationToken: token));
            await using (var index = await factory.Services.GetRequiredService<IIndexStore>().OpenReadConnectionAsync(token))
                occurrences = await index.Connection.ExecuteScalarAsync<long>(new CommandDefinition("SELECT COUNT(*) FROM event_occurrences WHERE document_id IN (SELECT document_id FROM indexed_calendar_members WHERE container_id=@id)", new { id = first.ContainerId }, cancellationToken: token));
            await repository.QueueAsync(source.Id, false, actor, token);
            await worker.ProcessAsync(Assert.Single(await repository.ClaimDueAsync(token: token)), token);
            var checkedAgain = (await repository.GetAsync(source.Id, token))!;
            conditionalSucceeded = checkedAgain.Error is null; conditionalError = checkedAgain.Error;
            await using var stateAgain = await factory.Store.OpenReadConnectionAsync(token);
            lastRevision = await stateAgain.Connection.ExecuteScalarAsync<long>(new CommandDefinition("SELECT SUM(revision) FROM documents WHERE source_id=@id", new { id = source.Id }, cancellationToken: token));
            var hashAfter = await stateAgain.Connection.ExecuteScalarAsync<string>(new CommandDefinition("SELECT snapshot_hash FROM calendar_subscriptions WHERE source_id=@id", new { id = source.Id }, cancellationToken: token));
            bytesChanged = hashBefore != hashAfter;
        }
        await File.WriteAllTextAsync(Path.Combine(directory, "result.json"), JsonSerializer.Serialize(new
        {
            initialSucceeded, error = accepted.Error, members, occurrences, conditionalSucceeded, conditionalError, bytesChanged,
            unchangedRevisions = initialSucceeded && firstRevision == lastRevision,
            transport = factory.Services.GetRequiredService<ObservedTransport>().Results,
            realTransport = true, realExtractor = true, isolatedTemporaryStore = true, hardenedDeployment = false,
            localOnly = factory.Options.CurrentValue.Privacy.LocalOnly
        }, new JsonSerializerOptions { WriteIndented = true }), token);
        Assert.True(initialSucceeded, "Live feed failed; the sanitized result contains its failure code.");
        Assert.True(conditionalSucceeded, "The follow-up live check failed; inspect its sanitized result.");
        Assert.True(bytesChanged == true || firstRevision == lastRevision, "An unchanged live snapshot created document revisions.");
    }

    private sealed class ObservedTransport(IOptionsMonitor<SecondBrainOptions> options, IDnsResolver dns, ICalendarFetchAuthority authority, IInputStager staging) : ICalendarDownloadTransport
    {
        private readonly CalendarDownloadTransport transport = new(options, dns, authority, staging);
        internal List<object> Results { get; } = [];
        public async ValueTask<CalendarDownloadResult> DownloadAsync(CalendarDownloadRequest request, CancellationToken cancellationToken = default)
        {
            var result = await transport.DownloadAsync(request, cancellationToken);
            Results.Add(new { result.Status, result.Error, conditional = request.ETag is not null || request.Modified is not null,
                acceptedETag = result.ETag is not null, acceptedLastModified = result.Modified is not null });
            return result;
        }
    }

    private static async Task<string> ReadPrivateLink(string directory, CancellationToken token)
    {
        var path = Path.Combine(directory, "input.sock");
        using var listener = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
        listener.Bind(new UnixDomainSocketEndPoint(path));
        if (!OperatingSystem.IsWindows()) File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite);
        listener.Listen(1);
        try
        {
            using var connection = await listener.AcceptAsync(token);
            await using var stream = new NetworkStream(connection, ownsSocket: false);
            using var reader = new StreamReader(stream, Encoding.UTF8);
            var chars = new char[8193]; var length = 0;
            while (length < chars.Length)
            {
                var read = await reader.ReadAsync(chars.AsMemory(length), token);
                if (read == 0) return new string(chars, 0, length);
                length += read;
            }
            throw new InvalidDataException("Private calendar link is too long.");
        }
        finally { File.Delete(path); }
    }
}
