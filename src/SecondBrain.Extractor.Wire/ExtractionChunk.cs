namespace SecondBrain.Extractor.Wire;

public sealed record ExtractionChunk(string Id, int Sequence, bool Final, string Data, int Version = 2);
