using SecondBrain.Server.Composition;

// Services can start in the vault directory. Published web assets belong to the
// installed application, never to the caller's working directory.
var builder = WebApplication.CreateBuilder(new WebApplicationOptions
{
    Args = args,
    ContentRootPath = AppContext.BaseDirectory
});

builder.Services.AddConfiguration();
builder.Services.AddKeyRing();
builder.Services.AddStorage();
builder.Services.AddDomain();
builder.Services.AddProviders();
builder.Services.AddPrivacy();
builder.Services.AddHttpHost();
builder.Services.AddAuth();
builder.Services.AddLimits();

var app = builder.Build();
app.MapGet("/health", () => Results.Ok());
app.Run();
