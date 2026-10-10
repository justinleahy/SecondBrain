namespace SecondBrain.Extractor.Wire;

public sealed record ExtractorRequest(string Id, string Operation, int Version = ExtractorProtocol.Version, string? FormatHint = null);
