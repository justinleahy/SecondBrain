using Microsoft.Win32.SafeHandles;

namespace SecondBrain.Extractor.Wire;

/// <summary>Owns the received descriptor; disposing the frame closes it on every response/error path.</summary>
public sealed class ExtractorFrame(byte[] json, SafeFileHandle? descriptor) : IDisposable
{
    public byte[] Json { get; } = json;
    public SafeFileHandle? Descriptor { get; } = descriptor;
    public void Dispose() => Descriptor?.Dispose();
}
