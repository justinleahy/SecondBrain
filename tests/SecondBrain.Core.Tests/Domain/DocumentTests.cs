using System.Text.Json;
using SecondBrain.Core.Domain;
using Xunit;

namespace SecondBrain.Core.Tests.Domain;

public sealed class DocumentTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 8, 14, 0, 0, TimeSpan.Zero);
    private readonly TypeRegistry _registry = new();

    [Fact]
    public void IdenticalBytesAtDifferentPathsReceiveDifferentIdentities()
    {
        var first = Create("one.md");
        var second = Create("two.md");
        Assert.NotEqual(first.Id, second.Id);
        Assert.Equal(first.ContentHash, second.ContentHash);
        Assert.Equal(1, first.Revision);
        Assert.Equal(RevisionCause.Ingest, Assert.Single(first.Revisions).Cause);
    }

    [Fact]
    public void ReclassificationPreservesOriginalPropsAndReDerivesProjections()
    {
        var original = TypeRegistryTests.Parse("""
            {"author":" Sarah@Example.TEST ","published_at":"2026-10-07T10:00:00-04:00","site":"Example",
             "unknown":{"value":1.0}}
            """);
        var document = Document.Admit(_registry, new DocumentOccurrence("source", "story.pdf"), "article", original, "hash", Now);
        var originalText = document.PropsOriginal.GetRawText();
        Assert.Equal("published_at", document.OccurredAt!.Basis);
        Assert.Equal("sarah@example.test", Assert.Single(document.People).Identifier);
        Assert.Equal(2, document.IndexedProperties.Count);

        Assert.True(document.Reclassify(_registry, "note", TypeOrigin.Override, 1, Now.AddMinutes(1)));
        Assert.Empty(document.Props.EnumerateObject());
        Assert.Equal(originalText, document.PropsOriginal.GetRawText());
        Assert.Null(document.OccurredAt);
        Assert.Empty(document.People);
        Assert.Empty(document.IndexedProperties);

        Assert.True(document.Reclassify(_registry, "article", TypeOrigin.Override, 1, Now.AddMinutes(2)));
        Assert.Equal(" Sarah@Example.TEST ", document.Props.GetProperty("author").GetString());
        Assert.Equal(originalText, document.PropsOriginal.GetRawText());
        Assert.Equal("published_at", document.OccurredAt!.Basis);
        Assert.Single(document.People);
        Assert.Equal(3, document.Revision);
        Assert.Equal(RevisionCause.TypeChange, document.Revisions[^1].Cause);
    }

    [Fact]
    public void EveryAuthoredMutationAdvancesTheRevision()
    {
        var document = Create();
        Assert.True(document.ChangeContent("new hash", Now.AddMinutes(1)));
        Assert.True(document.ChangeProperties(_registry, TypeRegistryTests.Parse("""{"unknown":"retained original value"}"""), Now.AddMinutes(2)));
        Assert.True(document.ChangeTags(["decision"], Now.AddMinutes(3)));
        Assert.True(document.ChangeTitle("A title", Now.AddMinutes(4)));
        Assert.True(document.Reclassify(_registry, "file", TypeOrigin.Override, null, Now.AddMinutes(5)));
        Assert.Equal(6, document.Revision);
        Assert.Equal([1L, 2L, 3L, 4L, 5L, 6L], document.Revisions.Select(revision => revision.Revision));
        Assert.All(document.Revisions, revision => Assert.Equal(document.Id, revision.DocumentId));
    }

    [Fact]
    public void NoOpMutationsDoNotAdvanceRevision()
    {
        var document = Create();
        Assert.False(document.ChangeContent("hash", Now));
        Assert.False(document.ChangeProperties(_registry, TypeRegistryTests.Parse("{}"), Now));
        Assert.False(document.ChangeTags([], Now));
        Assert.False(document.ChangeTitle("", Now));
        Assert.False(document.Reclassify(_registry, "note", TypeOrigin.FormatDefault, null, Now));
        Assert.Equal(1, document.Revision);
    }

    [Fact]
    public void AutomaticReclassificationCannotOverwriteAUserOverride()
    {
        var document = Create();
        document.Reclassify(_registry, "article", TypeOrigin.Override, 1, Now);
        Assert.False(document.Reclassify(_registry, "file", TypeOrigin.Llm, 0.99, Now));
        Assert.Equal("article", document.Type);
        Assert.Equal(TypeOrigin.Override, document.TypeOrigin);
        Assert.Equal(2, document.Revision);
        Assert.True(document.Reclassify(_registry, "file", TypeOrigin.Override, null, Now));
        Assert.Equal(3, document.Revision);
    }

    [Fact]
    public void TagsHaveSetSemantics()
    {
        var document = Create();
        document.ChangeTags(["b", "a", "a"], Now);
        Assert.Equal(["a", "b"], document.Tags);
        Assert.False(document.ChangeTags(["a", "b"], Now));
        Assert.Equal(2, document.Revision);
    }

    [Fact]
    public void DeleteRestoreAndPurgeAdvanceRevisionAndFenceOldJobs()
    {
        var document = Create();
        var id = document.Id;
        Assert.True(document.CompletePublication(1));
        Assert.True(document.Delete(Now.AddMinutes(1)));
        Assert.Equal(2, document.Revision);
        Assert.Equal(DocumentStatus.Deleted, document.Status);
        Assert.Equal(id, document.Suppression!.DocumentId);
        Assert.False(document.CompletePublication(1));
        Assert.True(document.CompletePublication(2));
        Assert.Equal(DocumentStatus.Deleted, document.Status);

        document.Restore(Now.AddMinutes(2));
        Assert.Equal(3, document.Revision);
        Assert.Null(document.Suppression);
        Assert.Null(document.DeletedAt);
        Assert.Equal(DocumentStatus.Pending, document.Status);
        Assert.False(document.CompletePublication(2));

        Assert.True(document.Purge(Now.AddMinutes(3)));
        Assert.Equal(4, document.Revision);
        Assert.Equal(id, document.Id);
        Assert.Equal(DocumentStatus.Purged, document.Status);
        Assert.False(document.CompletePublication(3));
        Assert.Equal([RevisionCause.Ingest, RevisionCause.Delete, RevisionCause.Restore, RevisionCause.Purge],
            document.Revisions.Select(revision => revision.Cause));
    }

    [Fact]
    public void PurgeLeavesAScrubbedTombstoneAndCannotBeRestored()
    {
        var document = Document.Admit(_registry, new DocumentOccurrence("source", "story.pdf"), "article",
            TypeRegistryTests.Parse("""{"author":"Secret","published_at":"2026-10-07T10:00:00Z"}"""), "hash", Now,
            title: "Secret title", tags: ["secret"]);
        document.Purge(Now);
        Assert.Empty(document.Props.EnumerateObject());
        Assert.Empty(document.PropsOriginal.EnumerateObject());
        Assert.Empty(document.Tags);
        Assert.Empty(document.People);
        Assert.Empty(document.IndexedProperties);
        Assert.Equal("[redacted]", document.Title);
        Assert.Equal("", document.ContentHash);
        Assert.Null(document.OccurredAt);
        Assert.NotNull(document.Suppression);
        Assert.False(document.Purge(Now));
        Assert.Throws<InvalidOperationException>(() => document.Restore(Now));
        Assert.Throws<InvalidOperationException>(() => document.ChangeContent("other", Now));
    }

    [Fact]
    public void RepeatingDeleteDoesNotAdvanceRevision()
    {
        var document = Create();
        Assert.True(document.Delete(Now));
        Assert.False(document.Delete(Now));
        Assert.Equal(2, document.Revision);
        Assert.Throws<InvalidOperationException>(() => document.ChangeTags(["late"], Now));
    }

    [Fact]
    public void MovesPreserveIdentityAndRevision()
    {
        var document = Create("old.md");
        var id = document.Id;
        document.MoveTo(new DocumentOccurrence("source", "new.md", fileModifiedAt: Now, sizeBytes: 12));
        Assert.Equal(id, document.Id);
        Assert.Equal(1, document.Revision);
        Assert.Equal("new.md", document.Occurrence.Path);
        Assert.Throws<ArgumentException>(() => document.MoveTo(new DocumentOccurrence("other-source", "new.md")));
    }

    [Fact]
    public void OccurrencesRequireACompleteContainerMemberIdentity()
    {
        Assert.Throws<ArgumentException>(() => new DocumentOccurrence("source", "calendar.ics", Ulid.NewUlid()));
        Assert.Throws<ArgumentException>(() => new DocumentOccurrence("source", "calendar.ics", memberKey: "member"));
        Assert.Throws<ArgumentOutOfRangeException>(() => new DocumentOccurrence("source", "file", sizeBytes: -1));
        var occurrence = new DocumentOccurrence("source", "calendar.ics", Ulid.NewUlid(), "member");
        Assert.Equal("member", occurrence.MemberKey);
    }

    [Fact]
    public void PublicationUsesRevisionInsteadOfTimestamp()
    {
        var document = Create();
        document.ChangeContent("changed", Now);
        Assert.False(document.BeginPublication(1));
        Assert.False(document.CompletePublication(1));
        Assert.Null(document.IndexedRevision);
        Assert.True(document.BeginPublication(2));
        Assert.True(document.CompletePublication(2, partial: true));
        Assert.Equal(PublishState.Published, document.PublishState);
        Assert.Equal(DocumentStatus.IndexedPartial, document.Status);
        Assert.Equal(2L, document.IndexedRevision);
        var exception = Assert.Throws<RevisionMismatchException>(() => document.RequireRevision(1));
        Assert.Equal(2, exception.CurrentRevision);
        document.RequireRevision(2);
    }

    [Fact]
    public void IngestionTimeIsNeverSubstitutedForOccurrenceTime()
    {
        Assert.Null(Create().OccurredAt);
        var article = Document.Admit(_registry, new DocumentOccurrence("source", "article.pdf"), "article",
            TypeRegistryTests.Parse("{}"), "hash", Now, sourceCreatedAt: Now.AddDays(-2));
        Assert.Null(article.OccurredAt);
        var file = Document.Admit(_registry, new DocumentOccurrence("source", "file.bin"), "file",
            TypeRegistryTests.Parse("{}"), "hash", Now, sourceCreatedAt: Now.AddDays(-2));
        Assert.Null(file.OccurredAt);
    }

    [Fact]
    public void KnownCreationTimeHasCreatedBasisAndPreservesItsOffset()
    {
        var created = new DateTimeOffset(2026, 10, 7, 12, 30, 0, TimeSpan.FromHours(-4));
        var document = Document.Admit(_registry, new DocumentOccurrence("source", "note.md"), "note",
            TypeRegistryTests.Parse("{}"), "hash", Now, sourceCreatedAt: created);
        Assert.Equal(created.ToUniversalTime(), document.OccurredAt!.At);
        Assert.Equal(TimeSpan.Zero, document.OccurredAt.At.Offset);
        Assert.Equal(TimeSpan.FromHours(-4), document.OccurredAt.OriginalOffset);
        Assert.Equal("created", document.OccurredAt.Basis);
        Assert.Equal(OccurredPrecision.DateTime, document.OccurredAt.Precision);
    }

    [Fact]
    public void AllDaySourceValuesKeepDatePrecision()
    {
        var zone = TimeZoneInfo.CreateCustomTimeZone("test-zone", TimeSpan.FromHours(2), "test-zone", "test-zone");
        var time = OccurredTime.FromDate(new DateOnly(2026, 10, 8), "starts_at", zone);
        Assert.Equal(new DateTimeOffset(2026, 10, 7, 22, 0, 0, TimeSpan.Zero), time.At);
        Assert.Equal(OccurredPrecision.Date, time.Precision);
        Assert.Null(time.OriginalOffset);
    }

    [Fact]
    public void OriginalsRemainUsableAfterTheInputJsonDocumentIsDisposed()
    {
        Document document;
        using (var json = JsonDocument.Parse("""{"author":"Sarah","unknown":[1,2,3]}"""))
        {
            document = Document.Admit(_registry, new DocumentOccurrence("source", "article.pdf"), "article", json.RootElement, "hash", Now);
        }
        Assert.Equal(3, document.PropsOriginal.GetProperty("unknown").GetArrayLength());
        Assert.Equal("Sarah", document.Props.GetProperty("author").GetString());
    }

    [Fact]
    public void SchemaMigrationChangesPropsAndAdvancesRevisionWithoutChangingOriginals()
    {
        var document = Document.Admit(_registry, new DocumentOccurrence("source", "article.pdf"), "article",
            TypeRegistryTests.Parse("""{"author":"Sarah","site":"Example"}"""), "hash", Now);
        var original = document.PropsOriginal.GetRawText();
        var registry = RegistryWithArticleVersion(2, includeSite: false);
        Assert.True(document.ApplySchemaMigration(registry, Now.AddMinutes(1)));
        Assert.Equal(2, document.Revision);
        Assert.Equal(2, document.SchemaVersion);
        Assert.Equal(RevisionCause.SchemaMigration, document.Revisions[^1].Cause);
        Assert.False(document.Props.TryGetProperty("site", out _));
        Assert.Equal(original, document.PropsOriginal.GetRawText());
        Assert.False(document.ApplySchemaMigration(RegistryWithArticleVersion(3, includeSite: false), Now.AddMinutes(2)));
        Assert.Equal(2, document.Revision);
        Assert.Equal(3, document.SchemaVersion);
    }

    [Fact]
    public void NaturalKeysAreNamespacedAndReDerivedOnTypeChange()
    {
        var definition = new ContentTypeDefinition("custom", 1, """
            {"type":"object","properties":{"uid":{"type":"string"},"recurrence_id":{"type":"string"}},
             "additionalProperties":false,"x-natural-key":"uid+recurrence_id"}
            """);
        var registry = new TypeRegistry(_registry.Types.Append(definition));
        var document = Document.Admit(registry, new DocumentOccurrence("source", "calendar.ics", Ulid.NewUlid(), "entry"), "custom",
            TypeRegistryTests.Parse("""{"uid":"event","recurrence_id":"2026-10-08"}"""), "hash", Now);
        Assert.Equal("custom:event/2026-10-08", document.NaturalKey);
        document.Reclassify(registry, "file", TypeOrigin.Override, null, Now);
        Assert.Null(document.NaturalKey);
        Assert.Equal("event", document.PropsOriginal.GetProperty("uid").GetString());
    }

    [Fact]
    public void ValidationFailureLeavesTheAggregateUnchanged()
    {
        var document = Document.Admit(_registry, new DocumentOccurrence("source", "article.pdf"), "article",
            TypeRegistryTests.Parse("""{"author":"Sarah"}"""), "hash", Now);
        Assert.Throws<PropertyValidationException>(() => document.ChangeProperties(_registry, TypeRegistryTests.Parse("""{"author":5}"""), Now));
        Assert.Equal(1, document.Revision);
        Assert.Equal("Sarah", document.PropsOriginal.GetProperty("author").GetString());
        Assert.Throws<ArgumentOutOfRangeException>(() => document.ChangeContent("other", Now.AddMinutes(-1)));
        Assert.Equal("hash", document.ContentHash);
    }

    [Fact]
    public void SnapshotRehydrationPreservesIdentityHistoryAndPublicationState()
    {
        var document = Document.Admit(_registry, new DocumentOccurrence("source", "article.pdf"), "article",
            TypeRegistryTests.Parse("""{"author":"Sarah","unknown":{"preserved":true}}"""), "hash", Now);
        document.ChangeContent("edited", Now.AddMinutes(1));
        document.CompletePublication(document.Revision);
        var snapshot = document.ToSnapshot();
        var restored = Document.Rehydrate(snapshot);
        Assert.Equal(document.Id, restored.Id);
        Assert.Equal(2, restored.Revision);
        Assert.Equal(document.Revisions, restored.Revisions);
        Assert.Equal(PublishState.Published, restored.PublishState);
        Assert.Equal(DocumentStatus.Indexed, restored.Status);
        Assert.Equal(2L, restored.IndexedRevision);
        Assert.Equal(document.PropsOriginal.GetRawText(), restored.PropsOriginal.GetRawText());
        Assert.Equal("sarah", Assert.Single(restored.People).Identifier);
        restored.ChangeContent("next edit", Now.AddMinutes(2));
        Assert.Equal(3, restored.Revision);
        Assert.Equal(2, snapshot.Revision);
        Assert.Equal(2, document.Revision);
    }

    [Fact]
    public void RehydrationRejectsIncompleteOrStaleHistory()
    {
        var snapshot = Create().ToSnapshot();
        Assert.Throws<ArgumentException>(() => Document.Rehydrate(snapshot with { Revision = 2 }));
        Assert.Throws<ArgumentException>(() => Document.Rehydrate(snapshot with { Id = Ulid.NewUlid() }));
        Assert.Throws<ArgumentException>(() => Document.Rehydrate(snapshot with { UpdatedAt = Now.AddMinutes(1) }));
        Assert.Throws<ArgumentException>(() => Document.Rehydrate(snapshot with { IndexedRevision = 2 }));
    }

    [Fact]
    public void RehydrationPreservesARemovedCustomType()
    {
        var definition = new ContentTypeDefinition("custom", 1,
            """{"type":"object","properties":{"author":{"type":"string"}},"additionalProperties":false}""");
        var registry = new TypeRegistry(_registry.Types.Append(definition));
        var document = Document.Admit(registry, new DocumentOccurrence("source", "custom.md"), "custom",
            TypeRegistryTests.Parse("""{"author":"Sarah"}"""), "hash", Now);
        var restored = Document.Rehydrate(document.ToSnapshot());
        Assert.Equal("custom", restored.Type);
        Assert.Equal("Sarah", restored.Props.GetProperty("author").GetString());
        Assert.Equal("file", _registry.GetProcessingType(restored.Type).Key);
    }

    [Fact]
    public void RehydrationDoesNotResurrectPurgedContent()
    {
        var document = Document.Admit(_registry, new DocumentOccurrence("source", "article.pdf"), "article",
            TypeRegistryTests.Parse("""{"author":"Sarah"}"""), "hash", Now, title: "Secret", mime: "application/pdf", sourceCreatedAt: Now);
        document.Purge(Now);
        var snapshot = document.ToSnapshot();
        var restored = Document.Rehydrate(snapshot);
        Assert.Equal(document.Id, restored.Id);
        Assert.Equal(DocumentStatus.Purged, restored.Status);
        Assert.Empty(restored.PropsOriginal.EnumerateObject());
        Assert.Null(restored.Mime);
        Assert.Null(restored.OccurredAt);
        Assert.Throws<ArgumentException>(() => Document.Rehydrate(snapshot with { PropsOriginal = TypeRegistryTests.Parse("""{"secret":"resurrected"}""") }));
    }

    [Fact]
    public void ProjectionFailureDoesNotPartiallyMutatePropertiesOrType()
    {
        var definition = CustomTimeType(1);
        var registry = new TypeRegistry(_registry.Types.Append(definition));
        var document = Document.Admit(registry, new DocumentOccurrence("source", "file"), "custom",
            TypeRegistryTests.Parse("""{"time":"2026-10-08T12:00:00Z"}"""), "hash", Now);
        Assert.Throws<FormatException>(() => document.ChangeProperties(registry,
            TypeRegistryTests.Parse("""{"time":"not a time"}"""), Now.AddMinutes(1)));
        Assert.Equal("2026-10-08T12:00:00Z", document.Props.GetProperty("time").GetString());
        Assert.Equal("2026-10-08T12:00:00Z", document.PropsOriginal.GetProperty("time").GetString());
        Assert.Equal(1, document.Revision);
        Assert.Equal(Now, document.UpdatedAt);

        var note = Document.Admit(registry, new DocumentOccurrence("source", "note.md"), "note",
            TypeRegistryTests.Parse("""{"time":"not a time"}"""), "hash", Now);
        Assert.Throws<FormatException>(() => note.Reclassify(registry, "custom", TypeOrigin.Override, 1, Now.AddMinutes(1)));
        Assert.Equal("note", note.Type);
        Assert.Equal(TypeOrigin.FormatDefault, note.TypeOrigin);
        Assert.Null(note.TypeConfidence);
        Assert.Empty(note.Props.EnumerateObject());
        Assert.Equal(1, note.Revision);
    }

    [Fact]
    public void ProjectionFailureDoesNotPartiallyApplyASchemaMigration()
    {
        var definition = new ContentTypeDefinition("custom", 1,
            """{"type":"object","properties":{},"additionalProperties":false}""");
        var registry = new TypeRegistry(_registry.Types.Append(definition));
        var document = Document.Admit(registry, new DocumentOccurrence("source", "file"), "custom",
            TypeRegistryTests.Parse("""{"time":"not a time"}"""), "hash", Now);
        var migrated = new TypeRegistry(_registry.Types.Append(CustomTimeType(2)));
        Assert.Throws<FormatException>(() => document.ApplySchemaMigration(migrated, Now.AddMinutes(1)));
        Assert.Equal(1, document.SchemaVersion);
        Assert.Equal(1, document.Revision);
        Assert.Empty(document.Props.EnumerateObject());
        Assert.Equal("not a time", document.PropsOriginal.GetProperty("time").GetString());
        Assert.Null(document.OccurredAt);
    }

    [Theory]
    [InlineData(-0.1)]
    [InlineData(1.1)]
    [InlineData(double.NaN)]
    [InlineData(double.PositiveInfinity)]
    public void ClassificationConfidenceMustBeFiniteAndWithinBounds(double confidence)
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => Document.Admit(_registry, new DocumentOccurrence("source", "file"), "file",
            TypeRegistryTests.Parse("{}"), "hash", Now, typeConfidence: confidence));
    }

    private Document Create(string path = "note.md") => Document.Admit(_registry, new DocumentOccurrence("source", path), "note",
        TypeRegistryTests.Parse("{}"), "hash", Now);

    private TypeRegistry RegistryWithArticleVersion(int version, bool includeSite)
    {
        var properties = includeSite
            ? "\"author\":{\"type\":\"string\"},\"site\":{\"type\":\"string\"}"
            : "\"author\":{\"type\":\"string\"}";
        var schema = $"{{\"$id\":\"brain:type/article/{version}\",\"type\":\"object\",\"properties\":{{{properties}}},\"additionalProperties\":false}}";
        var article = new ContentTypeDefinition("article", version, schema, "article");
        return new TypeRegistry(_registry.Types.Where(type => type.Key != "article").Append(article));
    }

    private static ContentTypeDefinition CustomTimeType(int version) => new("custom", version,
        """{"type":"object","properties":{"time":{"type":"string"}},"additionalProperties":false,"x-occurred-basis":"time"}""");
}
