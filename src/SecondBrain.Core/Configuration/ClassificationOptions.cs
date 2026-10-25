using YamlDotNet.Serialization;

namespace SecondBrain.Core.Configuration;

public sealed class ClassificationOptions
{
    [YamlMember(Alias = "llm")] public bool Llm { get; set; } = true;
    [YamlMember(Alias = "min_confidence")] public double MinConfidence { get; set; } = .8;
    public void Validate()
    {
        if (!double.IsFinite(MinConfidence) || MinConfidence is < 0 or > 1)
            throw new ConfigurationException("Classification confidence must be between zero and one.");
    }
}
