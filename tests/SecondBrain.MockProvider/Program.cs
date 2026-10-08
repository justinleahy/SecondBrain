namespace SecondBrain.MockProvider;

/// <summary>The standalone entry point, also discoverable by WebApplicationFactory.</summary>
public sealed class Program
{
    public static void Main(string[] args)
    {
        MockProviderApplication.Build(args).Run();
    }
}
