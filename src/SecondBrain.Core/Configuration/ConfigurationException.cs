namespace SecondBrain.Core.Configuration;

/// <summary>A configuration failure safe to include in logs (never contains source values).</summary>
public sealed class ConfigurationException(string message) : Exception(message);
