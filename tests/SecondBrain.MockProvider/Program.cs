namespace SecondBrain.MockProvider;

/// <summary>A test host scaffold for the OpenAI-compatible mock in spec §20 / M0 item 7.</summary>
public sealed class Program
{
    public static void Main(string[] args)
    {
        var app = WebApplication.CreateBuilder(args).Build();
        app.MapGet("/health", () => Results.Ok());
        app.Run();
    }
}
