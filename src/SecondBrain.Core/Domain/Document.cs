using System.Collections.Frozen;
using System.Globalization;
using System.Text.Json;

namespace SecondBrain.Core.Domain;

/// <summary>
/// One logical document. Mutation methods must run under the publication coordinator's per-document lock (§8).
/// Immutable JSON values prevent callers from changing authored properties without advancing the revision.
/// </summary>
public sealed class Document
{
    private readonly List<DocumentRevision> _revisions = [];
    private DateTimeOffset? _sourceCreatedAt;
    private JsonElement _props;
    private JsonElement _propsOriginal;
    private string[] _tags;

    public Ulid Id { get; }
    public string SourceId => Occurrence.SourceId;
    public DocumentOccurrence Occurrence { get; private set; }
    public long Revision { get; private set; } = 1;
    public string Type { get; private set; }
    public TypeOrigin TypeOrigin { get; private set; }
    public double? TypeConfidence { get; private set; }
    public int SchemaVersion { get; private set; }
    public JsonElement Props => _props;
    public JsonElement PropsOriginal => _propsOriginal;
    public string Title { get; private set; }
    public string? Mime { get; private set; }
    public string ContentHash { get; private set; }
    public IReadOnlyList<string> Tags => Array.AsReadOnly(_tags);
    public OccurredTime? OccurredAt { get; private set; }
    public string? NaturalKey { get; private set; }
    public IReadOnlyList<DocumentPersonReference> People { get; private set; } = [];
    public IReadOnlyDictionary<string, JsonElement> IndexedProperties { get; private set; } = FrozenDictionary<string, JsonElement>.Empty;
    public DocumentStatus Status { get; private set; } = DocumentStatus.Pending;
    public PublishState PublishState { get; private set; } = PublishState.Publishing;
    public long? IndexedRevision { get; private set; }
    public DateTimeOffset CreatedAt { get; }
    public DateTimeOffset UpdatedAt { get; private set; }
    public DateTimeOffset? DeletedAt { get; private set; }
    public DocumentSuppression? Suppression { get; private set; }
    public IReadOnlyList<DocumentRevision> Revisions => _revisions.AsReadOnly();

    private Document(TypeRegistry registry, DocumentOccurrence occurrence, string type, JsonElement propsOriginal,
        string contentHash, DateTimeOffset admittedAt, TypeOrigin typeOrigin, double? typeConfidence,
        DateTimeOffset? sourceCreatedAt, string title, string? mime, IEnumerable<string>? tags)
    {
        ArgumentNullException.ThrowIfNull(registry);
        ArgumentNullException.ThrowIfNull(occurrence);
        ArgumentException.ThrowIfNullOrWhiteSpace(contentHash);
        ArgumentNullException.ThrowIfNull(title);
        ValidateClassification(typeOrigin, typeConfidence);
        var definition = registry.Get(type);
        _props = registry.DeriveEffectiveProperties(type, propsOriginal);
        _propsOriginal = propsOriginal.Clone();
        _tags = NormalizeTags(tags ?? []);
        _sourceCreatedAt = sourceCreatedAt;
        Id = Ulid.NewUlid();
        Occurrence = occurrence;
        Type = type;
        TypeOrigin = typeOrigin;
        TypeConfidence = typeConfidence;
        SchemaVersion = definition.SchemaVersion;
        Title = title;
        Mime = mime;
        ContentHash = contentHash;
        CreatedAt = admittedAt.ToUniversalTime();
        UpdatedAt = CreatedAt;
        ApplyDerivedMetadata(ComputeDerivedMetadata(definition, _props, Type));
        _revisions.Add(new DocumentRevision(Id, Revision, RevisionCause.Ingest, ContentHash, CreatedAt));
    }

    /// <summary>Admits a fresh identity even when another file has the same bytes or claims an id (§5.3).</summary>
    public static Document Admit(TypeRegistry registry, DocumentOccurrence occurrence, string type, JsonElement propsOriginal,
        string contentHash, DateTimeOffset admittedAt, TypeOrigin typeOrigin = TypeOrigin.FormatDefault,
        double? typeConfidence = null, DateTimeOffset? sourceCreatedAt = null, string title = "", string? mime = null,
        IEnumerable<string>? tags = null) =>
        new(registry, occurrence, type, propsOriginal, contentHash, admittedAt, typeOrigin, typeConfidence, sourceCreatedAt, title, mime, tags);

    /// <summary>Rehydrates authoritative state without assigning another identity or generating an ingest revision.</summary>
    public static Document Rehydrate(DocumentSnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        snapshot.Validate();
        return new Document(snapshot);
    }

    private Document(DocumentSnapshot snapshot)
    {
        Id = snapshot.Id;
        Occurrence = snapshot.Occurrence;
        Revision = snapshot.Revision;
        Type = snapshot.Type;
        TypeOrigin = snapshot.TypeOrigin;
        TypeConfidence = snapshot.TypeConfidence;
        SchemaVersion = snapshot.SchemaVersion;
        _props = snapshot.Props.Clone();
        _propsOriginal = snapshot.PropsOriginal.Clone();
        _tags = NormalizeTags(snapshot.Tags);
        _sourceCreatedAt = snapshot.SourceCreatedAt;
        Title = snapshot.Title;
        Mime = snapshot.Mime;
        ContentHash = snapshot.ContentHash;
        OccurredAt = snapshot.OccurredAt;
        NaturalKey = snapshot.NaturalKey;
        People = Array.AsReadOnly(snapshot.People.ToArray());
        IndexedProperties = snapshot.IndexedProperties.ToFrozenDictionary(pair => pair.Key, pair => pair.Value.Clone(), StringComparer.Ordinal);
        Status = snapshot.Status;
        PublishState = snapshot.PublishState;
        IndexedRevision = snapshot.IndexedRevision;
        CreatedAt = snapshot.CreatedAt.ToUniversalTime();
        UpdatedAt = snapshot.UpdatedAt.ToUniversalTime();
        DeletedAt = snapshot.DeletedAt?.ToUniversalTime();
        Suppression = snapshot.Suppression;
        _revisions.AddRange(snapshot.Revisions.Select(revision => revision with { CreatedAt = revision.CreatedAt.ToUniversalTime() }));
    }

    public DocumentSnapshot ToSnapshot() => new()
    {
        Id = Id, Occurrence = Occurrence, Revision = Revision, Type = Type, TypeOrigin = TypeOrigin,
        TypeConfidence = TypeConfidence, SchemaVersion = SchemaVersion, Props = _props.Clone(), PropsOriginal = _propsOriginal.Clone(),
        Tags = Array.AsReadOnly(_tags.ToArray()), SourceCreatedAt = _sourceCreatedAt, Title = Title, Mime = Mime,
        ContentHash = ContentHash, OccurredAt = OccurredAt, NaturalKey = NaturalKey,
        People = Array.AsReadOnly(People.ToArray()),
        IndexedProperties = IndexedProperties.ToFrozenDictionary(pair => pair.Key, pair => pair.Value.Clone(), StringComparer.Ordinal),
        Status = Status, PublishState = PublishState, IndexedRevision = IndexedRevision, CreatedAt = CreatedAt,
        UpdatedAt = UpdatedAt, DeletedAt = DeletedAt, Suppression = Suppression, Revisions = Array.AsReadOnly(_revisions.ToArray()),
    };

    public bool IsCurrentRevision(long revision) => Revision == revision;

    public void RequireRevision(long expectedRevision)
    {
        if (!IsCurrentRevision(expectedRevision))
        {
            throw new RevisionMismatchException(expectedRevision, Revision);
        }
    }

    public bool ChangeContent(string contentHash, DateTimeOffset changedAt, RevisionCause cause = RevisionCause.Edit)
    {
        EnsureActive();
        ArgumentException.ThrowIfNullOrWhiteSpace(contentHash);
        if (cause is not (RevisionCause.Edit or RevisionCause.Enrichment))
        {
            throw new ArgumentException("Content changes use edit or enrichment causes.", nameof(cause));
        }
        if (ContentHash == contentHash)
        {
            return false;
        }
        ValidateMutationTime(changedAt);
        ContentHash = contentHash;
        AdvanceRevision(cause, changedAt);
        return true;
    }

    public bool ChangeProperties(TypeRegistry registry, JsonElement propsOriginal, DateTimeOffset changedAt)
    {
        EnsureActive();
        ArgumentNullException.ThrowIfNull(registry);
        var props = registry.DeriveEffectiveProperties(Type, propsOriginal);
        if (JsonElement.DeepEquals(_propsOriginal, propsOriginal) && JsonElement.DeepEquals(_props, props))
        {
            return false;
        }
        var definition = registry.Get(Type);
        var metadata = ComputeDerivedMetadata(definition, props, Type);
        ValidateMutationTime(changedAt);
        _propsOriginal = propsOriginal.Clone();
        _props = props;
        SchemaVersion = definition.SchemaVersion;
        ApplyDerivedMetadata(metadata);
        AdvanceRevision(RevisionCause.Patch, changedAt);
        return true;
    }

    public bool ChangeTags(IEnumerable<string> tags, DateTimeOffset changedAt)
    {
        EnsureActive();
        ArgumentNullException.ThrowIfNull(tags);
        var normalized = NormalizeTags(tags);
        if (_tags.SequenceEqual(normalized, StringComparer.Ordinal))
        {
            return false;
        }
        ValidateMutationTime(changedAt);
        _tags = normalized;
        AdvanceRevision(RevisionCause.Patch, changedAt);
        return true;
    }

    public bool ChangeTitle(string title, DateTimeOffset changedAt)
    {
        EnsureActive();
        ArgumentNullException.ThrowIfNull(title);
        if (Title == title)
        {
            return false;
        }
        ValidateMutationTime(changedAt);
        Title = title;
        AdvanceRevision(RevisionCause.Patch, changedAt);
        return true;
    }

    /// <summary>Re-derives every metadata projection from preserved original props (§5.4).</summary>
    public bool Reclassify(TypeRegistry registry, string type, TypeOrigin origin, double? confidence, DateTimeOffset changedAt)
    {
        EnsureActive();
        ArgumentNullException.ThrowIfNull(registry);
        ValidateClassification(origin, confidence);
        if (TypeOrigin == TypeOrigin.Override && origin != TypeOrigin.Override)
        {
            return false;
        }
        var definition = registry.Get(type);
        var props = registry.DeriveEffectiveProperties(type, _propsOriginal);
        if (Type == type && TypeOrigin == origin && TypeConfidence == confidence && JsonElement.DeepEquals(_props, props))
        {
            return false;
        }
        var metadata = ComputeDerivedMetadata(definition, props, type);
        ValidateMutationTime(changedAt);
        Type = type;
        TypeOrigin = origin;
        TypeConfidence = confidence;
        SchemaVersion = definition.SchemaVersion;
        _props = props;
        ApplyDerivedMetadata(metadata);
        AdvanceRevision(RevisionCause.TypeChange, changedAt);
        return true;
    }

    /// <summary>Schema changes advance the revision only when effective props change; generations are external (§5.4).</summary>
    public bool ApplySchemaMigration(TypeRegistry registry, DateTimeOffset changedAt)
    {
        EnsureActive();
        ArgumentNullException.ThrowIfNull(registry);
        var definition = registry.Get(Type);
        var props = registry.DeriveEffectiveProperties(Type, _propsOriginal);
        var metadata = ComputeDerivedMetadata(definition, props, Type);
        var changed = !JsonElement.DeepEquals(_props, props);
        if (changed)
        {
            ValidateMutationTime(changedAt);
        }
        SchemaVersion = definition.SchemaVersion;
        _props = props;
        ApplyDerivedMetadata(metadata);
        if (changed)
        {
            AdvanceRevision(RevisionCause.SchemaMigration, changedAt);
        }
        return changed;
    }

    /// <summary>A reconciliation-confirmed move preserves identity and revision when bytes and authored metadata are unchanged.</summary>
    public void MoveTo(DocumentOccurrence occurrence)
    {
        EnsureActive();
        ArgumentNullException.ThrowIfNull(occurrence);
        if (occurrence.SourceId != SourceId || occurrence.ContainerDocumentId != Occurrence.ContainerDocumentId ||
            occurrence.MemberKey != Occurrence.MemberKey)
        {
            throw new ArgumentException("A move must retain its source and container-member identity.", nameof(occurrence));
        }
        Occurrence = occurrence;
    }

    public bool Delete(DateTimeOffset changedAt)
    {
        if (Status == DocumentStatus.Deleted)
        {
            return false;
        }
        EnsureActive();
        ValidateMutationTime(changedAt);
        DeletedAt = changedAt.ToUniversalTime();
        Suppression = new DocumentSuppression(Id, "user_delete", DeletedAt.Value);
        AdvanceRevision(RevisionCause.Delete, changedAt);
        Status = DocumentStatus.Deleted;
        return true;
    }

    public void Restore(DateTimeOffset changedAt)
    {
        if (Status != DocumentStatus.Deleted)
        {
            throw new InvalidOperationException("Only a deleted document can be restored.");
        }
        ValidateMutationTime(changedAt);
        DeletedAt = null;
        Suppression = null;
        AdvanceRevision(RevisionCause.Restore, changedAt);
    }

    /// <summary>
    /// Leaves an identity tombstone and advances its fence. The coordinator also redacts every linked copy (§15.9, SEC-26).
    /// Purge is the explicit exception to retaining original content properties.
    /// </summary>
    public bool Purge(DateTimeOffset changedAt)
    {
        if (Status == DocumentStatus.Purged)
        {
            return false;
        }
        ValidateMutationTime(changedAt);
        Suppression ??= new DocumentSuppression(Id, "user_delete", changedAt.ToUniversalTime());
        DeletedAt ??= changedAt.ToUniversalTime();
        _props = JsonSerializer.SerializeToElement(new Dictionary<string, object>());
        _propsOriginal = _props;
        _tags = [];
        Title = "[redacted]";
        Mime = null;
        ContentHash = "";
        _sourceCreatedAt = null;
        OccurredAt = null;
        NaturalKey = null;
        People = [];
        IndexedProperties = FrozenDictionary<string, JsonElement>.Empty;
        AdvanceRevision(RevisionCause.Purge, changedAt);
        Status = DocumentStatus.Purged;
        return true;
    }

    public bool BeginPublication(long revision)
    {
        if (!IsCurrentRevision(revision))
        {
            return false;
        }
        PublishState = PublishState.Publishing;
        return true;
    }

    public bool CompletePublication(long revision, bool partial = false)
    {
        if (!IsCurrentRevision(revision))
        {
            return false;
        }
        IndexedRevision = revision;
        PublishState = PublishState.Published;
        if (Status is not (DocumentStatus.Deleted or DocumentStatus.Purged))
        {
            Status = partial ? DocumentStatus.IndexedPartial : DocumentStatus.Indexed;
        }
        return true;
    }

    private void AdvanceRevision(RevisionCause cause, DateTimeOffset changedAt)
    {
        Revision = checked(Revision + 1);
        UpdatedAt = changedAt.ToUniversalTime();
        PublishState = PublishState.Publishing;
        Status = DocumentStatus.Pending;
        _revisions.Add(new DocumentRevision(Id, Revision, cause, ContentHash, UpdatedAt));
    }

    private DerivedMetadata ComputeDerivedMetadata(ContentTypeDefinition definition, JsonElement props, string type)
    {
        var basis = definition.Metadata.OccurredBasis;
        OccurredTime? occurredAt = basis == "created" && _sourceCreatedAt.HasValue
            ? new OccurredTime(_sourceCreatedAt.Value, basis, OccurredPrecision.DateTime)
            : basis is not null && props.TryGetProperty(basis, out var time) && time.ValueKind == JsonValueKind.String
                ? new OccurredTime(DateTimeOffset.Parse(time.GetString()!, CultureInfo.InvariantCulture), basis, OccurredPrecision.DateTime)
                : null;
        var people = new List<DocumentPersonReference>();
        foreach (var (property, role) in definition.Metadata.PeopleRoles)
        {
            if (!props.TryGetProperty(property, out var value))
            {
                continue;
            }
            var values = value.ValueKind == JsonValueKind.Array ? value.EnumerateArray().ToArray() : [value];
            foreach (var person in values)
            {
                if (person.ValueKind == JsonValueKind.String && !string.IsNullOrWhiteSpace(person.GetString()))
                {
                    var displayName = person.GetString()!.Trim();
                    people.Add(new DocumentPersonReference(role, displayName.ToLowerInvariant(), displayName));
                }
            }
        }
        var indexed = props.EnumerateObject().Where(property => definition.Metadata.IndexedProperties.Contains(property.Name))
            .ToFrozenDictionary(property => property.Name, property => property.Value.Clone(), StringComparer.Ordinal);
        var keys = definition.Metadata.NaturalKeyProperties;
        var naturalKey = keys.Count > 0 && props.TryGetProperty(keys[0], out _)
            ? $"{type}:{string.Join('/', keys.Where(key => props.TryGetProperty(key, out _)).Select(key => props.GetProperty(key).ToString()))}"
            : null;
        return new DerivedMetadata(occurredAt, Array.AsReadOnly(people.Distinct().ToArray()), indexed, naturalKey);
    }

    private void ApplyDerivedMetadata(DerivedMetadata metadata)
    {
        OccurredAt = metadata.OccurredAt;
        People = metadata.People;
        IndexedProperties = metadata.IndexedProperties;
        NaturalKey = metadata.NaturalKey;
    }

    private sealed record DerivedMetadata(OccurredTime? OccurredAt, IReadOnlyList<DocumentPersonReference> People,
        IReadOnlyDictionary<string, JsonElement> IndexedProperties, string? NaturalKey);

    private void EnsureActive()
    {
        if (Status is DocumentStatus.Deleted or DocumentStatus.Purged)
        {
            throw new InvalidOperationException("Deleted or purged documents cannot be edited.");
        }
    }

    private void ValidateMutationTime(DateTimeOffset changedAt)
    {
        // Check the fence increment before changing any authored fields.
        _ = checked(Revision + 1);
        if (changedAt < UpdatedAt)
        {
            throw new ArgumentOutOfRangeException(nameof(changedAt), "A document mutation cannot precede its previous mutation.");
        }
    }

    private static string[] NormalizeTags(IEnumerable<string> tags)
    {
        var values = tags.ToArray();
        foreach (var tag in values)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(tag);
        }
        return values.Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray();
    }

    private static void ValidateClassification(TypeOrigin origin, double? confidence)
    {
        if (!Enum.IsDefined(origin))
        {
            throw new ArgumentOutOfRangeException(nameof(origin));
        }
        if (confidence is { } number && (double.IsNaN(number) || number is < 0 or > 1))
        {
            throw new ArgumentOutOfRangeException(nameof(confidence), "Classification confidence must be between zero and one.");
        }
    }
}
