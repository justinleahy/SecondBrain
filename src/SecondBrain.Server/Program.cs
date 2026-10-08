using SecondBrain.Server.Composition;

var builder = WebApplication.CreateBuilder(args);

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
