using Microsoft.Data.Sqlite;
using SecondBrain.Core.Auth;
using SecondBrain.Storage.Auth;
using Xunit;

namespace SecondBrain.Storage.Tests;

/// <summary>Characterizes the auth SQL relocated from the daemon into Storage, against a migrated real state store.</summary>
public sealed class AuthRepositoryTests
{
    private static readonly DateTimeOffset Start = new(2026, 10, 8, 0, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task FindAsyncReturnsNullForUnknownIdAndMapsEveryColumn()
    {
        await using var auth = await AuthStore.CreateAsync();
        Assert.Null(await auth.Repository.FindAsync("missing"));
        var credential = Credential("c1", kind: "session") with
        {
            Device = "Firefox on Linux", IdleExpiresAt = Format(Start.AddHours(6)), AbsoluteExpiresAt = Format(Start.AddDays(30)),
            SteppedUpAt = Format(Start), LastUsedAt = Format(Start.AddMinutes(1)), ExpiresAt = Format(Start.AddDays(90)),
        };
        await auth.Repository.AddAsync(credential.ToRecord());

        var found = await auth.Repository.FindAsync("c1");

        Assert.NotNull(found);
        Assert.Equal(credential, CredentialShape.From(found));
    }

    [Fact]
    public async Task ListPageAsyncFiltersByKindAndPagesByCreatedAtThenId()
    {
        await using var auth = await AuthStore.CreateAsync();
        await auth.Repository.AddAsync((Credential("b") with { CreatedAt = Format(Start.AddMinutes(2)) }).ToRecord());
        await auth.Repository.AddAsync((Credential("z") with { CreatedAt = Format(Start.AddMinutes(1)) }).ToRecord());
        await auth.Repository.AddAsync((Credential("a") with { CreatedAt = Format(Start.AddMinutes(2)) }).ToRecord());
        await auth.Repository.AddAsync((Credential("s", kind: "session") with { CreatedAt = Format(Start) }).ToRecord());

        var first = await auth.Repository.ListPageAsync("api_key", activeOnly: false, after: null, limit: 2);
        Assert.Equal(["z", "a"], first.Items.Select(value => value.Id));
        Assert.Equal(new CredentialCursor(Format(Start.AddMinutes(2)), "a"), first.Next);
        var second = await auth.Repository.ListPageAsync("api_key", activeOnly: false, first.Next, limit: 2);
        Assert.Equal(["b"], second.Items.Select(value => value.Id));
        Assert.Null(second.Next);
        var exact = await auth.Repository.ListPageAsync("api_key", activeOnly: false, after: null, limit: 3);
        Assert.Equal(["z", "a", "b"], exact.Items.Select(value => value.Id));
        Assert.Null(exact.Next);
        Assert.Equal(["s"], (await auth.Repository.ListPageAsync("session", activeOnly: false, after: null, limit: 10)).Items.Select(value => value.Id));
        Assert.Empty((await auth.Repository.ListPageAsync("cli", activeOnly: false, after: null, limit: 10)).Items);
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => auth.Repository.ListPageAsync("api_key", activeOnly: false, after: null, limit: 0));
    }

    [Fact]
    public async Task ListPageAsyncActiveOnlyFiltersHistoricalRowsBeforeTheLimit()
    {
        await using var auth = await AuthStore.CreateAsync();
        var now = Format(Start);
        var past = Format(Start.AddMinutes(-1));
        var future = Format(Start.AddHours(1));
        // 1,100 inactive sessions older than every active one: revoked, an earlier epoch, idle/absolute/hard expiry exactly at or before now.
        await MigrationTestSql.WriteAsync(auth.State, $"""
            WITH RECURSIVE n(i) AS (SELECT 1 UNION ALL SELECT i+1 FROM n WHERE i<1100)
            INSERT INTO credentials (id,name,kind,verifier,scopes,generation,kid,account_epoch,idle_expires_at,absolute_expires_at,created_at,expires_at,revoked_at)
            SELECT printf('h%05d', i), 'old', 'session', 'v', '', 1, 'k', CASE i % 5 WHEN 1 THEN 0 ELSE 1 END,
              CASE i % 5 WHEN 2 THEN '{now}' ELSE '{future}' END, CASE i % 5 WHEN 3 THEN '{past}' ELSE '{future}' END,
              '{Format(Start.AddDays(-1))}', CASE i % 5 WHEN 4 THEN '{now}' END, CASE i % 5 WHEN 0 THEN '{past}' END FROM n;
            """);
        foreach (var id in new[] { "c", "a", "b" })
            await auth.Repository.AddAsync((Credential(id, kind: "session") with { IdleExpiresAt = future, AbsoluteExpiresAt = future }).ToRecord());

        var active = await auth.Repository.ListPageAsync("session", activeOnly: true, after: null, limit: 1000);
        Assert.Equal(["a", "b", "c"], active.Items.Select(value => value.Id));
        Assert.Null(active.Next);
        var history = new List<string>();
        CredentialCursor? cursor = null;
        do
        {
            var page = await auth.Repository.ListPageAsync("session", activeOnly: false, cursor, limit: 500);
            history.AddRange(page.Items.Select(value => value.Id));
            cursor = page.Next;
        } while (cursor is not null);
        Assert.Equal(1103, history.Distinct().Count());
        Assert.Equal(["a", "b", "c"], history[^3..]);
    }

    [Fact]
    public async Task GetEpochAsyncReadsTheMetaAccountEpoch()
    {
        await using var auth = await AuthStore.CreateAsync();
        Assert.Equal(1, await auth.Repository.GetEpochAsync());
        await MigrationTestSql.WriteAsync(auth.State, "UPDATE meta SET value='7' WHERE key='account_epoch';");
        Assert.Equal(7, await auth.Repository.GetEpochAsync());
    }

    [Fact]
    public async Task GetInstanceIdAsyncReadsTheMetaInstanceIdAndRefusesAnUninitializedStore()
    {
        await using var auth = await AuthStore.CreateAsync();
        await Assert.ThrowsAsync<InvalidDataException>(() => auth.Repository.GetInstanceIdAsync());
        await MigrationTestSql.WriteAsync(auth.State, "INSERT INTO meta(key, value) VALUES ('instance_id', '01J00000000000000000000000');");
        Assert.Equal("01J00000000000000000000000", await auth.Repository.GetInstanceIdAsync());
    }

    [Fact]
    public async Task GetAccountAsyncReturnsNullUntilTheAccountExists()
    {
        await using var auth = await AuthStore.CreateAsync();
        Assert.Null(await auth.Repository.GetAccountAsync());
        await auth.Repository.SaveAccountAsync(Account("hash-1", 1));

        var account = await auth.Repository.GetAccountAsync();

        Assert.NotNull(account);
        Assert.Equal((1, "hash-1", 1, Format(Start)), (account.Id, account.PasswordHash, account.PasswordVersion, account.UpdatedAt));
    }

    [Fact]
    public async Task SaveAccountAsyncUpsertsTheSingleAccountAndBumpsTheEpochOnlyWhenInvalidating()
    {
        await using var auth = await AuthStore.CreateAsync();
        await auth.Repository.SaveAccountAsync(Account("hash-1", 1));
        Assert.Equal(1, await auth.Repository.GetEpochAsync());

        await auth.Repository.SaveAccountAsync(Account("hash-2", 2, Start.AddMinutes(5)), invalidate: true);

        Assert.Equal(2, await auth.Repository.GetEpochAsync());
        var account = await auth.Repository.GetAccountAsync();
        Assert.Equal(("hash-2", 2, Format(Start.AddMinutes(5))), (account!.PasswordHash, account.PasswordVersion, account.UpdatedAt));
        Assert.Equal(1L, await MigrationTestSql.ScalarAsync(auth.State, "SELECT COUNT(*) FROM account;"));

        await auth.Repository.SaveAccountAsync(Account("hash-3", 2), invalidate: false);
        Assert.Equal(2, await auth.Repository.GetEpochAsync());
        Assert.Equal("hash-3", (await auth.Repository.GetAccountAsync())!.PasswordHash);
    }

    [Fact]
    public async Task RehashAsyncReplacesOnlyTheExpectedHashAtTheCurrentEpoch()
    {
        await using var auth = await AuthStore.CreateAsync();
        var original = Account("hash-1", 1);
        await auth.Repository.SaveAccountAsync(original);
        var replacement = Account("hash-2", 2, Start.AddMinutes(1));

        Assert.False(await auth.Repository.RehashAsync(Account("other", 1), replacement, 1));
        Assert.False(await auth.Repository.RehashAsync(Account("hash-1", 9), replacement, 1));
        Assert.False(await auth.Repository.RehashAsync(original, replacement, 2));
        Assert.Equal("hash-1", (await auth.Repository.GetAccountAsync())!.PasswordHash);

        Assert.True(await auth.Repository.RehashAsync(original, replacement, 1));
        var account = await auth.Repository.GetAccountAsync();
        Assert.Equal(("hash-2", 2, Format(Start.AddMinutes(1))), (account!.PasswordHash, account.PasswordVersion, account.UpdatedAt));
        Assert.Equal(1, await auth.Repository.GetEpochAsync());
    }

    [Fact]
    public async Task AddAsyncWithAStaleEpochThrowsAndInsertsNothing()
    {
        await using var auth = await AuthStore.CreateAsync();

        var error = await Assert.ThrowsAsync<AuthorityChangedException>(() => auth.Repository.AddAsync((Credential("stale") with { AccountEpoch = 0 }).ToRecord()));

        Assert.Equal("Credential authority changed while the action was being authorized.", error.Message);
        Assert.Equal(0L, await MigrationTestSql.ScalarAsync(auth.State, "SELECT COUNT(*) FROM credentials;"));
        await auth.Repository.AddAsync(Credential("current").ToRecord());
        Assert.Equal(1L, await MigrationTestSql.ScalarAsync(auth.State, "SELECT COUNT(*) FROM credentials;"));
    }

    [Fact]
    public async Task RotateAsyncRevokesTheOldCredentialAndInsertsTheReplacement()
    {
        await using var auth = await AuthStore.CreateAsync();
        await auth.Repository.AddAsync(Credential("old", kind: "session").ToRecord());
        auth.Clock.Advance(TimeSpan.FromMinutes(3));

        Assert.True(await auth.Repository.RotateAsync("old", 1, 1, Credential("new", kind: "session").ToRecord()));

        var old = await auth.Repository.FindAsync("old");
        Assert.Equal((2L, Format(Start.AddMinutes(3))), (old!.Generation, old.RevokedAt));
        var replacement = await auth.Repository.FindAsync("new");
        Assert.NotNull(replacement);
        Assert.Null(replacement.RevokedAt);
        Assert.False(await auth.Repository.RotateAsync("old", 2, 1, Credential("again", kind: "session").ToRecord()));
        Assert.Null(await auth.Repository.FindAsync("again"));
    }

    [Fact]
    public async Task RotateAsyncRollsBackTheRevocationWhenTheReplacementCannotBeInserted()
    {
        await using var auth = await AuthStore.CreateAsync();
        await auth.Repository.AddAsync(Credential("old", kind: "session").ToRecord());
        await auth.Repository.AddAsync(Credential("taken", kind: "session").ToRecord());

        await Assert.ThrowsAsync<SqliteException>(() => auth.Repository.RotateAsync("old", 1, 1, Credential("taken", kind: "session").ToRecord()));

        var old = await auth.Repository.FindAsync("old");
        Assert.Equal((1L, (string?)null), (old!.Generation, old.RevokedAt));
    }

    [Theory]
    [InlineData("generation")]
    [InlineData("epoch")]
    [InlineData("current-epoch")]
    [InlineData("revoked")]
    [InlineData("idle")]
    [InlineData("absolute")]
    public async Task RotateAsyncRefusesCredentialsThatAreNotCurrent(string reason)
    {
        await using var auth = await AuthStore.CreateAsync();
        var credential = Credential("old", kind: "session") with
        {
            IdleExpiresAt = reason == "idle" ? Format(Start) : null,
            AbsoluteExpiresAt = reason == "absolute" ? Format(Start) : null,
            RevokedAt = reason == "revoked" ? Format(Start) : null,
        };
        await auth.Repository.AddAsync(credential.ToRecord());
        if (reason == "current-epoch") await auth.Repository.BumpEpochAsync();

        var rotated = await auth.Repository.RotateAsync("old", reason == "generation" ? 2 : 1, reason == "epoch" ? 2 : 1,
            Credential("new", kind: "session", epoch: reason == "current-epoch" ? 2 : 1).ToRecord());

        Assert.False(rotated);
        Assert.Null(await auth.Repository.FindAsync("new"));
        Assert.Equal(credential, CredentialShape.From((await auth.Repository.FindAsync("old"))!));
    }

    [Fact]
    public async Task TouchAsyncRecordsUseAndExtendsTheIdleExpiryOnlyWhenGiven()
    {
        await using var auth = await AuthStore.CreateAsync();
        await auth.Repository.AddAsync((Credential("c", kind: "session") with { IdleExpiresAt = Format(Start.AddHours(1)) }).ToRecord());

        Assert.True(await auth.Repository.TouchAsync("c", 1, 1, Start.AddMinutes(10), null));
        var touched = await auth.Repository.FindAsync("c");
        Assert.Equal((Format(Start.AddMinutes(10)), Format(Start.AddHours(1))), (touched!.LastUsedAt, touched.IdleExpiresAt));

        Assert.True(await auth.Repository.TouchAsync("c", 1, 1, Start.AddMinutes(20), Start.AddHours(2)));
        touched = await auth.Repository.FindAsync("c");
        Assert.Equal((Format(Start.AddMinutes(20)), Format(Start.AddHours(2))), (touched!.LastUsedAt, touched.IdleExpiresAt));

        Assert.False(await auth.Repository.TouchAsync("c", 2, 1, Start.AddMinutes(30), null));
        Assert.False(await auth.Repository.TouchAsync("c", 1, 2, Start.AddMinutes(30), null));
        Assert.Equal(Format(Start.AddMinutes(20)), (await auth.Repository.FindAsync("c"))!.LastUsedAt);
    }

    [Theory]
    [InlineData("expires")]
    [InlineData("idle")]
    [InlineData("absolute")]
    public async Task TouchAsyncHonoursEachExpiry(string expiry)
    {
        await using var auth = await AuthStore.CreateAsync();
        var deadline = Format(Start.AddHours(1));
        await auth.Repository.AddAsync((Credential("c", kind: "session") with
        {
            ExpiresAt = expiry == "expires" ? deadline : null,
            IdleExpiresAt = expiry == "idle" ? deadline : null,
            AbsoluteExpiresAt = expiry == "absolute" ? deadline : null,
        }).ToRecord());

        Assert.True(await auth.Repository.TouchAsync("c", 1, 1, Start.AddMinutes(59), null));
        Assert.False(await auth.Repository.TouchAsync("c", 1, 1, Start.AddHours(1), null));
        Assert.False(await auth.Repository.TouchAsync("c", 1, 1, Start.AddHours(2), Start.AddHours(3)));

        var row = await auth.Repository.FindAsync("c");
        Assert.Equal(Format(Start.AddMinutes(59)), row!.LastUsedAt);
        Assert.Equal(expiry == "idle" ? deadline : null, row.IdleExpiresAt);
    }

    [Fact]
    public async Task IsCurrentAsyncChecksGenerationEpochRevocationAndExpiries()
    {
        await using var auth = await AuthStore.CreateAsync();
        await auth.Repository.AddAsync((Credential("c") with { ExpiresAt = Format(Start.AddHours(1)) }).ToRecord());
        await auth.Repository.AddAsync((Credential("idle") with { IdleExpiresAt = Format(Start.AddHours(1)) }).ToRecord());
        await auth.Repository.AddAsync((Credential("absolute") with { AbsoluteExpiresAt = Format(Start.AddHours(1)) }).ToRecord());

        Assert.False(await auth.Repository.IsCurrentAsync("missing", 1, 1));
        Assert.True(await auth.Repository.IsCurrentAsync("c", 1, 1));
        Assert.False(await auth.Repository.IsCurrentAsync("c", 2, 1));
        Assert.False(await auth.Repository.IsCurrentAsync("c", 1, 2));

        auth.Clock.Advance(TimeSpan.FromHours(1));
        Assert.False(await auth.Repository.IsCurrentAsync("c", 1, 1));
        Assert.False(await auth.Repository.IsCurrentAsync("idle", 1, 1));
        Assert.False(await auth.Repository.IsCurrentAsync("absolute", 1, 1));

        await auth.Repository.AddAsync(Credential("fresh").ToRecord());
        Assert.True(await auth.Repository.IsCurrentAsync("fresh", 1, 1));
        await auth.Repository.BumpEpochAsync();
        Assert.False(await auth.Repository.IsCurrentAsync("fresh", 1, 1));

        await auth.Repository.AddAsync(Credential("revoked", epoch: 2).ToRecord());
        Assert.True(await auth.Repository.IsCurrentAsync("revoked", 1, 2));
        await auth.Repository.RevokeAsync("revoked");
        Assert.False(await auth.Repository.IsCurrentAsync("revoked", 1, 2));
        Assert.False(await auth.Repository.IsCurrentAsync("revoked", 2, 2));
    }

    [Fact]
    public async Task RevokeAsyncHonoursTheKindFilterAndRevokesOnce()
    {
        await using var auth = await AuthStore.CreateAsync();
        await auth.Repository.AddAsync(Credential("k").ToRecord());
        await auth.Repository.AddAsync(Credential("s", kind: "session").ToRecord());
        auth.Clock.Advance(TimeSpan.FromMinutes(4));

        Assert.False(await auth.Repository.RevokeAsync("missing"));
        Assert.False(await auth.Repository.RevokeAsync("k", "session"));
        Assert.Null((await auth.Repository.FindAsync("k"))!.RevokedAt);

        Assert.True(await auth.Repository.RevokeAsync("k", "api_key"));
        var revoked = await auth.Repository.FindAsync("k");
        Assert.Equal((2L, Format(Start.AddMinutes(4))), (revoked!.Generation, revoked.RevokedAt));
        Assert.False(await auth.Repository.RevokeAsync("k"));
        Assert.Equal(2L, (await auth.Repository.FindAsync("k"))!.Generation);

        Assert.True(await auth.Repository.RevokeAsync("s"));
    }

    [Fact]
    public async Task BumpEpochAsyncIncrementsAndReturnsTheNewEpoch()
    {
        await using var auth = await AuthStore.CreateAsync();
        Assert.Equal(2, await auth.Repository.BumpEpochAsync());
        Assert.Equal(3, await auth.Repository.BumpEpochAsync());
        Assert.Equal(3, await auth.Repository.GetEpochAsync());
        Assert.Equal("3", await MigrationTestSql.ScalarAsync(auth.State, "SELECT value FROM meta WHERE key='account_epoch';"));
    }

    [Fact]
    public async Task LoginAttemptsAsyncFiltersBySourceAndSinceInclusiveOrderedByTime()
    {
        await using var auth = await AuthStore.CreateAsync();
        await auth.Repository.RecordLoginAsync("10.0.0.1", Start.AddMinutes(3), success: false);
        await auth.Repository.RecordLoginAsync("10.0.0.1", Start.AddMinutes(1), success: false);
        await auth.Repository.RecordLoginAsync("10.0.0.1", Start, success: false);
        await auth.Repository.RecordLoginAsync("10.0.0.2", Start.AddMinutes(2), success: false);

        var attempts = await auth.Repository.LoginAttemptsAsync("10.0.0.1", Start.AddMinutes(1), limit: 1000);

        Assert.Equal([(Format(Start.AddMinutes(1)), false), (Format(Start.AddMinutes(3)), false)], attempts.Select(value => (value.At, value.Success)));
        Assert.All(attempts, value => Assert.Equal("10.0.0.1", value.Source));
        Assert.Empty(await auth.Repository.LoginAttemptsAsync("10.0.0.3", Start, limit: 1000));
    }

    [Fact]
    public async Task LoginAttemptsAsyncStartsAtTheLatestSuccessInTheWindow()
    {
        await using var auth = await AuthStore.CreateAsync();
        await auth.Repository.RecordLoginAsync("10.0.0.1", Start, success: false);
        await auth.Repository.RecordLoginAsync("10.0.0.1", Start.AddMinutes(1), success: true);
        await auth.Repository.RecordLoginAsync("10.0.0.1", Start.AddMinutes(2), success: false);
        await auth.Repository.RecordLoginAsync("10.0.0.1", Start.AddMinutes(3), success: true);
        await auth.Repository.RecordLoginAsync("10.0.0.1", Start.AddMinutes(4), success: false);

        Assert.Equal([(Format(Start.AddMinutes(3)), true), (Format(Start.AddMinutes(4)), false)],
            (await auth.Repository.LoginAttemptsAsync("10.0.0.1", Start, limit: 1000)).Select(value => (value.At, value.Success)));
        // A success before the window does not start the tail.
        Assert.Equal([(Format(Start.AddMinutes(4)), false)],
            (await auth.Repository.LoginAttemptsAsync("10.0.0.1", Start.AddMinutes(4), limit: 1000)).Select(value => (value.At, value.Success)));
    }

    [Fact]
    public async Task LoginAttemptsAsyncBreaksIdenticalTimestampsByInsertionOrder()
    {
        await using var auth = await AuthStore.CreateAsync();
        var at = Format(Start);
        await MigrationTestSql.WriteAsync(auth.State, $"""
            INSERT INTO login_attempts(source,at,success) VALUES ('s','{at}',0);
            WITH RECURSIVE n(i) AS (SELECT 1 UNION ALL SELECT i+1 FROM n WHERE i<1500) INSERT INTO login_attempts(source,at,success) SELECT 's','{at}',1 FROM n;
            WITH RECURSIVE n(i) AS (SELECT 1 UNION ALL SELECT i+1 FROM n WHERE i<5) INSERT INTO login_attempts(source,at,success) SELECT 's','{at}',0 FROM n;
            """);

        var attempts = await auth.Repository.LoginAttemptsAsync("s", Start, limit: 1000);

        Assert.Equal([true, false, false, false, false, false], attempts.Select(value => value.Success));
    }

    [Fact]
    public async Task LoginAttemptsAsyncKeepsTheNewestRowsChronologicallyWhenTheTailExceedsTheLimit()
    {
        await using var auth = await AuthStore.CreateAsync();
        for (var i = 0; i < 10; i++) await auth.Repository.RecordLoginAsync("s", Start.AddMinutes(i), success: false);

        var attempts = await auth.Repository.LoginAttemptsAsync("s", Start, limit: 3);

        Assert.Equal([Format(Start.AddMinutes(7)), Format(Start.AddMinutes(8)), Format(Start.AddMinutes(9))], attempts.Select(value => value.At));
    }

    [Fact]
    public async Task RecordLoginAsyncPrunesAttemptsOlderThanOneDayAcrossSources()
    {
        await using var auth = await AuthStore.CreateAsync();
        await auth.Repository.RecordLoginAsync("a", Start.AddDays(-2), success: false);
        await auth.Repository.RecordLoginAsync("b", Start.AddDays(-1).AddTicks(-1), success: false);
        await auth.Repository.RecordLoginAsync("a", Start.AddDays(-1), success: true);

        await auth.Repository.RecordLoginAsync("b", Start, success: true);

        Assert.Equal(2L, await MigrationTestSql.ScalarAsync(auth.State, "SELECT COUNT(*) FROM login_attempts;"));
        Assert.Equal([Format(Start.AddDays(-1))], (await auth.Repository.LoginAttemptsAsync("a", DateTimeOffset.MinValue, limit: 1000)).Select(value => value.At));
        Assert.Equal([(Format(Start), true)], (await auth.Repository.LoginAttemptsAsync("b", DateTimeOffset.MinValue, limit: 1000)).Select(value => (value.At, value.Success)));
    }

    private static string Format(DateTimeOffset value) => AuthTime.Format(value);

    private static CredentialShape Credential(string id, string kind = "api_key", long epoch = 1) =>
        new(id, $"name-{id}", kind, $"verifier-{id}", "read,write", 1, "kid-1", epoch, null, null, null, null, Format(Start), null, null, null);

    private static AccountRecord Account(string hash, int version, DateTimeOffset? updated = null) =>
        new() { PasswordHash = hash, PasswordVersion = version, UpdatedAt = Format(updated ?? Start) };

    /// <summary>Value-comparable view of <see cref="CredentialRecord"/>.</summary>
    private sealed record CredentialShape(string Id, string Name, string Kind, string Verifier, string Scopes, long Generation, string Kid,
        long AccountEpoch, string? Device, string? IdleExpiresAt, string? AbsoluteExpiresAt, string? SteppedUpAt, string CreatedAt,
        string? LastUsedAt, string? ExpiresAt, string? RevokedAt)
    {
        public static CredentialShape From(CredentialRecord value) => new(value.Id, value.Name, value.Kind, value.Verifier, value.Scopes,
            value.Generation, value.Kid, value.AccountEpoch, value.Device, value.IdleExpiresAt, value.AbsoluteExpiresAt, value.SteppedUpAt,
            value.CreatedAt, value.LastUsedAt, value.ExpiresAt, value.RevokedAt);

        public CredentialRecord ToRecord() => new()
        {
            Id = Id, Name = Name, Kind = Kind, Verifier = Verifier, Scopes = Scopes, Generation = Generation, Kid = Kid,
            AccountEpoch = AccountEpoch, Device = Device, IdleExpiresAt = IdleExpiresAt, AbsoluteExpiresAt = AbsoluteExpiresAt,
            SteppedUpAt = SteppedUpAt, CreatedAt = CreatedAt, LastUsedAt = LastUsedAt, ExpiresAt = ExpiresAt, RevokedAt = RevokedAt,
        };
    }

    [Theory]
    [InlineData("revoked")]
    [InlineData("rotated")]
    [InlineData("epoch")]
    [InlineData("expired")]
    [InlineData("step-up")]
    public async Task QueuedMutationRechecksActorBeforeLookingUpTarget(string invalidation)
    {
        await using var auth = await AuthStore.CreateAsync();
        var actor = (Credential("actor", "session") with { SteppedUpAt = Format(Start), ExpiresAt = Format(Start.AddHours(1)) }).ToRecord();
        await auth.Repository.AddAsync(actor);
        await auth.Repository.AddAsync(Credential("target", "session").ToRecord());
        var admitted = new AuthenticatedCredential(actor.Id, actor.Kind, actor.Generation, actor.AccountEpoch, actor.GrantedScopes, actor.SteppedUpAt);
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var blocker = auth.State.QueueWriteAsync(async (connection, transaction, token) =>
        {
            entered.SetResult();
            await release.Task;
            var sql = invalidation switch
            {
                "revoked" => "UPDATE credentials SET revoked_at='2026-10-08T00:00:00.0000000+00:00' WHERE id='actor'",
                "rotated" => "UPDATE credentials SET generation=generation+1 WHERE id='actor'",
                "epoch" => "UPDATE meta SET value='2' WHERE key='account_epoch'",
                _ => "SELECT 1",
            };
            using var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = sql;
            await command.ExecuteNonQueryAsync(token);
            return true;
        });
        await entered.Task;
        var mutation = auth.Repository.RevokeAuthorizedAsync("target", "session", admitted, TimeSpan.FromMinutes(10));
        var missing = auth.Repository.RevokeAuthorizedAsync("missing", "session", admitted, TimeSpan.FromMinutes(10));
        if (invalidation == "expired") auth.Clock.Advance(TimeSpan.FromHours(1));
        if (invalidation == "step-up") auth.Clock.Advance(TimeSpan.FromMinutes(10));
        release.SetResult();
        await blocker;
        await Assert.ThrowsAsync<AuthorityChangedException>(() => mutation);
        await Assert.ThrowsAsync<AuthorityChangedException>(() => missing);
        Assert.Null((await auth.Repository.FindAsync("target"))!.RevokedAt);
    }

    [Fact]
    public async Task CurrentActorsCanManageKeysAndSelfLogoutWithoutStepUp()
    {
        await using var auth = await AuthStore.CreateAsync();
        var key = (Credential("admin") with { Scopes = "admin" }).ToRecord();
        await auth.Repository.AddAsync(key);
        var admin = new AuthenticatedCredential(key.Id, key.Kind, key.Generation, key.AccountEpoch, key.GrantedScopes, null);
        await auth.Repository.AddAuthorizedAsync(Credential("new-key").ToRecord(), admin, TimeSpan.FromMinutes(10));
        Assert.True(await auth.Repository.RevokeAuthorizedAsync("new-key", "api_key", admin, TimeSpan.FromMinutes(10)));
        var session = Credential("self", "session").ToRecord();
        await auth.Repository.AddAsync(session);
        var identity = new AuthenticatedCredential(session.Id, session.Kind, session.Generation, session.AccountEpoch, session.GrantedScopes, null);
        Assert.True(await auth.Repository.RevokeAuthorizedAsync("self", "session", identity, TimeSpan.FromMinutes(10)));
    }

    [Fact]
    public async Task FreshSessionCanMutateButExpiredAuthorityCannotIssueOrLogoutAll()
    {
        await using var auth = await AuthStore.CreateAsync();
        var row = (Credential("actor", "session") with { Scopes = "admin", SteppedUpAt = Format(Start) }).ToRecord();
        await auth.Repository.AddAsync(row);
        var actor = new AuthenticatedCredential(row.Id, row.Kind, row.Generation, row.AccountEpoch, row.GrantedScopes, row.SteppedUpAt);
        await auth.Repository.AddAuthorizedAsync(Credential("key").ToRecord(), actor, TimeSpan.FromMinutes(10));
        Assert.True(await auth.Repository.RevokeAuthorizedAsync("key", "api_key", actor, TimeSpan.FromMinutes(10)));
        auth.Clock.Advance(TimeSpan.FromMinutes(10));
        await Assert.ThrowsAsync<AuthorityChangedException>(() => auth.Repository.AddAuthorizedAsync(Credential("blocked").ToRecord(), actor, TimeSpan.FromMinutes(10)));
        await Assert.ThrowsAsync<AuthorityChangedException>(() => auth.Repository.BumpEpochAuthorizedAsync(actor, TimeSpan.FromMinutes(10)));
        Assert.Equal(1, await auth.Repository.GetEpochAsync());
        Assert.Null(await auth.Repository.FindAsync("blocked"));
    }

    [Fact]
    public async Task RotationRejectsHardExpiryAndReplacementEpochMismatch()
    {
        await using var auth = await AuthStore.CreateAsync();
        await auth.Repository.AddAsync((Credential("actor", "session") with { ExpiresAt = Format(Start) }).ToRecord());
        Assert.False(await auth.Repository.RotateAsync("actor", 1, 1, Credential("replacement", "session").ToRecord()));
        await auth.Repository.AddAsync(Credential("current", "session").ToRecord());
        Assert.False(await auth.Repository.RotateAsync("current", 1, 1, (Credential("replacement", "session") with { AccountEpoch = 2 }).ToRecord()));
        Assert.Null((await auth.Repository.FindAsync("current"))!.RevokedAt);
    }

    private sealed class ManualClock(DateTimeOffset now) : TimeProvider
    {
        private DateTimeOffset now = now;
        public override DateTimeOffset GetUtcNow() => now;
        public void Advance(TimeSpan delta) => now += delta;
    }

    /// <summary>A migrated state store seeded with <c>meta.account_epoch='1'</c>, exactly as LaneDWebFactory.CreateHost does.</summary>
    private sealed class AuthStore : IAsyncDisposable
    {
        private readonly MigrationTestDataRoot root;
        private readonly MigrationTestStores stores;

        private AuthStore(MigrationTestDataRoot root, MigrationTestStores stores)
        {
            this.root = root;
            this.stores = stores;
            Repository = new AuthRepository(stores.State, Clock);
        }

        public ManualClock Clock { get; } = new(Start);
        public AuthRepository Repository { get; }
        public SqliteStateStore State => stores.State;

        public static async Task<AuthStore> CreateAsync()
        {
            var root = new MigrationTestDataRoot();
            var stores = new MigrationTestStores(root.Path);
            await stores.Runner.MigrateAsync();
            await MigrationTestSql.WriteAsync(stores.State, "INSERT INTO meta(key, value) VALUES ('account_epoch', '1') ON CONFLICT(key) DO NOTHING;");
            return new AuthStore(root, stores);
        }

        public async ValueTask DisposeAsync()
        {
            await stores.DisposeAsync();
            root.Dispose();
        }
    }
}
