namespace SecondBrain.Extractor.Wire;

public sealed record ExtractorResponse(
    string Id,
    bool Ok,
    string? Result = null,
    string? Error = null,
    int Version = ExtractorProtocol.Version,
    int? BytesRead = null,
    string? Sha256 = null);
