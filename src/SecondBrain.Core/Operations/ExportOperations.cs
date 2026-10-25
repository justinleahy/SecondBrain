using SecondBrain.Core.Ingestion;

namespace SecondBrain.Core.Operations;

public sealed record ExportDownload(string Name, Stream Content, long Rows, long Files, long PartialFiles);

public interface IExportOperations
{
    Task<ExportDownload> ExportAsync(IngestionAuthority actor, CancellationToken token = default);
}
