using System.Buffers.Binary;
using System.Text.Json;

namespace SecondBrain.MockProvider;

/// <summary>An OpenAI-compatible test server with no provider credentials or vendor defaults.</summary>
public static class MockProviderApplication
{
    public const string ChatModel = "mock-chat";
    public const string EmbeddingModel = "mock-embed";
    public const int EmbeddingDimensions = 4;
    public const string ChatResponse = "Mock response";

    /// <summary>
    /// Builds a standalone Kestrel host. Tests may configure the builder to use TestServer,
    /// or start Kestrel on http://127.0.0.1:0 to exercise the real socket transport.
    /// </summary>
    public static WebApplication Build(string[] args, Action<WebApplicationBuilder>? configureBuilder = null)
    {
        var builder = WebApplication.CreateBuilder(args);
        builder.Services.AddSingleton<MockProviderState>();
        configureBuilder?.Invoke(builder);
        var app = builder.Build();
        ConfigureEndpoints(app);
        return app;
    }

    /// <summary>Maps endpoints on a host whose services include MockProviderState.</summary>
    public static void ConfigureEndpoints(WebApplication app)
    {
        // Faults are deliberately applied before request bodies are read. Redirect tests can
        // therefore assert one provider request, without exposing any body to the public target.
        app.Use(async (context, next) =>
        {
            if (!context.Request.Path.StartsWithSegments("/v1"))
            {
                await next(context);
                return;
            }

            var state = context.RequestServices.GetRequiredService<MockProviderState>();
            state.Count(context.Request.Path.Value ?? "/v1");
            var fault = state.Fault;
            if (context.Request.Headers.TryGetValue("X-Mock-Fault", out var requestedFault) &&
                !MockProviderState.TryParseFault(requestedFault.ToString(), out fault))
            {
                await WriteErrorAsync(context, StatusCodes.Status400BadRequest, "Unknown mock fault.", "invalid_request_error");
                return;
            }

            switch (fault)
            {
                case MockFault.Redirect:
                    context.Response.StatusCode = StatusCodes.Status307TemporaryRedirect;
                    context.Response.Headers.Location = state.RedirectTarget.AbsoluteUri;
                    return;
                case MockFault.Timeout:
                    await Task.Delay(state.Timeout, context.RequestAborted);
                    await WriteErrorAsync(context, StatusCodes.Status504GatewayTimeout, "Mock response timeout.", "timeout");
                    return;
                case MockFault.RateLimit:
                    context.Response.Headers.RetryAfter = "1";
                    await WriteErrorAsync(context, StatusCodes.Status429TooManyRequests, "Mock rate limit.", "rate_limit_error");
                    return;
                case MockFault.Malformed:
                    context.Response.ContentType = "application/json";
                    await context.Response.WriteAsync("{malformed", context.RequestAborted);
                    return;
                default:
                    await next(context);
                    return;
            }
        });

        app.MapGet("/health", () => Results.Ok(new { status = "ok" }));
        app.MapGet("/v1/models", () => Results.Json(new
        {
            @object = "list",
            data = new[]
            {
                new { id = ChatModel, @object = "model", created = 0, owned_by = "secondbrain-mock" },
                new { id = EmbeddingModel, @object = "model", created = 0, owned_by = "secondbrain-mock" },
            },
        }));
        app.MapPost("/v1/chat/completions", ChatAsync);
        app.MapPost("/v1/embeddings", EmbeddingsAsync);

        app.MapGet("/admin/state", (MockProviderState state) => Results.Json(new
        {
            fault = state.Fault.ToString(),
            redirect_target = state.RedirectTarget.AbsoluteUri,
            timeout_ms = state.Timeout.TotalMilliseconds,
            total_requests = state.TotalRequests,
            request_counts = state.GetRequestCounts(),
        }));
        app.MapPost("/admin/fault", (MockFaultConfiguration request, MockProviderState state) =>
        {
            if (!MockProviderState.TryParseFault(request.Fault, out var fault))
            {
                return Results.BadRequest(new { error = "fault must be none, redirect, timeout, 429, or malformed" });
            }

            Uri? redirect = null;
            if (request.RedirectTarget is not null &&
                (!Uri.TryCreate(request.RedirectTarget, UriKind.Absolute, out redirect) ||
                 (redirect.Scheme != Uri.UriSchemeHttp && redirect.Scheme != Uri.UriSchemeHttps)))
            {
                return Results.BadRequest(new { error = "redirect_target must be an absolute HTTP(S) URI" });
            }

            if (request.TimeoutMilliseconds is <= 0 or > 3_600_000)
            {
                return Results.BadRequest(new { error = "timeout_ms must be between 1 and 3600000" });
            }

            if (redirect is not null)
            {
                state.RedirectTarget = redirect;
            }

            if (request.TimeoutMilliseconds is int timeout)
            {
                state.Timeout = TimeSpan.FromMilliseconds(timeout);
            }

            state.Fault = fault;
            return Results.Ok(new { fault = fault.ToString() });
        });
        app.MapDelete("/admin/fault", (MockProviderState state) =>
        {
            state.Fault = MockFault.None;
            return Results.NoContent();
        });
        app.MapPost("/admin/reset", (MockProviderState state) =>
        {
            state.Reset();
            return Results.NoContent();
        });
    }

    private static async Task ChatAsync(HttpContext context)
    {
        using var request = await ReadRequestAsync(context);
        if (request is null)
        {
            return;
        }

        var root = request.RootElement;
        if (!TryGetModel(root, ChatModel, out var model))
        {
            await WriteErrorAsync(context, StatusCodes.Status404NotFound, "Unknown chat model.", "model_not_found");
            return;
        }

        if (!TryGetForcedTool(root, out var toolName))
        {
            await WriteErrorAsync(context, StatusCodes.Status400BadRequest, "A forced tool choice must select an available function.", "invalid_request_error");
            return;
        }

        var created = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        var id = "chatcmpl-" + Guid.NewGuid().ToString("N");
        var toolCallId = "call_" + id["chatcmpl-".Length..];
        var finishReason = toolName is null ? "stop" : "tool_calls";
        if (!root.TryGetProperty("stream", out var stream) || stream.ValueKind != JsonValueKind.True)
        {
            object message = toolName is null
                ? new { role = "assistant", content = ChatResponse }
                : new
                {
                    role = "assistant",
                    content = (string?)null,
                    tool_calls = new[]
                    {
                        new { id = toolCallId, type = "function", function = new { name = toolName, arguments = "{}" } },
                    },
                };
            await context.Response.WriteAsJsonAsync(new
            {
                id,
                @object = "chat.completion",
                created,
                model,
                choices = new[]
                {
                    new { index = 0, message, finish_reason = finishReason },
                },
                usage = new { prompt_tokens = 1, completion_tokens = 2, total_tokens = 3 },
            }, context.RequestAborted);
            return;
        }

        context.Response.ContentType = "text/event-stream";
        context.Response.Headers.CacheControl = "no-cache";
        context.Response.Headers["X-Accel-Buffering"] = "no";
        object initialDelta = toolName is null
            ? new { role = "assistant", content = "Mock " }
            : new
            {
                role = "assistant",
                tool_calls = new[]
                {
                    new { index = 0, id = toolCallId, type = "function", function = new { name = toolName, arguments = "{}" } },
                },
            };
        await WriteEventAsync(context, new
        {
            id,
            @object = "chat.completion.chunk",
            created,
            model,
            choices = new[] { new { index = 0, delta = initialDelta, finish_reason = (string?)null } },
        });
        if (toolName is null)
        {
            await WriteEventAsync(context, new
            {
                id,
                @object = "chat.completion.chunk",
                created,
                model,
                choices = new[] { new { index = 0, delta = new { content = "response" }, finish_reason = (string?)null } },
            });
        }

        await WriteEventAsync(context, new
        {
            id,
            @object = "chat.completion.chunk",
            created,
            model,
            choices = new[] { new { index = 0, delta = new { }, finish_reason = finishReason } },
        });
        if (root.TryGetProperty("stream_options", out var streamOptions) &&
            streamOptions.ValueKind == JsonValueKind.Object &&
            streamOptions.TryGetProperty("include_usage", out var includeUsage) &&
            includeUsage.ValueKind == JsonValueKind.True)
        {
            await WriteEventAsync(context, new
            {
                id,
                @object = "chat.completion.chunk",
                created,
                model,
                choices = Array.Empty<object>(),
                usage = new { prompt_tokens = 1, completion_tokens = 2, total_tokens = 3 },
            });
        }

        await context.Response.WriteAsync("data: [DONE]\n\n", context.RequestAborted);
        await context.Response.Body.FlushAsync(context.RequestAborted);
    }

    private static bool TryGetForcedTool(JsonElement root, out string? toolName)
    {
        toolName = null;
        if (!root.TryGetProperty("tool_choice", out var choice))
        {
            return true;
        }

        string? selectedName = null;
        if (choice.ValueKind == JsonValueKind.String)
        {
            // The mock never autonomously calls tools: only an explicit forced choice does so.
            if (choice.GetString() != "required")
            {
                return true;
            }
        }
        else if (choice.ValueKind == JsonValueKind.Object &&
                 choice.TryGetProperty("function", out var selectedFunction) && selectedFunction.ValueKind == JsonValueKind.Object &&
                 selectedFunction.TryGetProperty("name", out var selectedNameValue) && selectedNameValue.ValueKind == JsonValueKind.String &&
                 !string.IsNullOrWhiteSpace(selectedNameValue.GetString()))
        {
            selectedName = selectedNameValue.GetString();
        }
        else
        {
            return false;
        }

        if (!root.TryGetProperty("tools", out var tools) || tools.ValueKind != JsonValueKind.Array)
        {
            return false;
        }

        foreach (var tool in tools.EnumerateArray())
        {
            if (tool.ValueKind != JsonValueKind.Object ||
                !tool.TryGetProperty("type", out var type) || type.ValueKind != JsonValueKind.String || type.GetString() != "function" ||
                !tool.TryGetProperty("function", out var function) || function.ValueKind != JsonValueKind.Object ||
                !function.TryGetProperty("name", out var name) || name.ValueKind != JsonValueKind.String ||
                string.IsNullOrWhiteSpace(name.GetString()))
            {
                continue;
            }

            var availableName = name.GetString();
            if (selectedName is null || string.Equals(selectedName, availableName, StringComparison.Ordinal))
            {
                // The first available function makes "required" repeatable; a named choice
                // matches exactly. Empty arguments support the empty-schema test fixture.
                toolName = availableName;
                return true;
            }
        }

        return false;
    }

    private static async Task EmbeddingsAsync(HttpContext context)
    {
        using var request = await ReadRequestAsync(context);
        if (request is null)
        {
            return;
        }

        var root = request.RootElement;
        if (!TryGetModel(root, EmbeddingModel, out var model))
        {
            await WriteErrorAsync(context, StatusCodes.Status404NotFound, "Unknown embedding model.", "model_not_found");
            return;
        }

        if (!root.TryGetProperty("input", out var input) || !TryGetInputCount(input, out var count))
        {
            await WriteErrorAsync(context, StatusCodes.Status400BadRequest, "input must be a string or a nonempty text/token array.", "invalid_request_error");
            return;
        }

        var dimensions = EmbeddingDimensions;
        if (root.TryGetProperty("dimensions", out var requestedDimensions) &&
            (requestedDimensions.ValueKind != JsonValueKind.Number ||
             !requestedDimensions.TryGetInt32(out dimensions) || dimensions is < 1 or > 4096))
        {
            await WriteErrorAsync(context, StatusCodes.Status400BadRequest, "dimensions must be between 1 and 4096.", "invalid_request_error");
            return;
        }

        var base64 = root.TryGetProperty("encoding_format", out var encoding) &&
            encoding.ValueKind == JsonValueKind.String && encoding.GetString() == "base64";
        var vector = Enumerable.Range(0, dimensions).Select(index => (index % 4 + 1) / 10f).ToArray();
        object encodedVector = vector;
        if (base64)
        {
            var bytes = new byte[vector.Length * sizeof(float)];
            for (var index = 0; index < vector.Length; index++)
            {
                BinaryPrimitives.WriteSingleLittleEndian(bytes.AsSpan(index * sizeof(float), sizeof(float)), vector[index]);
            }

            encodedVector = Convert.ToBase64String(bytes);
        }

        await context.Response.WriteAsJsonAsync(new
        {
            @object = "list",
            data = Enumerable.Range(0, count).Select(index => new { @object = "embedding", index, embedding = encodedVector }),
            model,
            usage = new { prompt_tokens = count, total_tokens = count },
        }, context.RequestAborted);
    }

    private static bool TryGetModel(JsonElement root, string expected, out string model)
    {
        model = expected;
        return root.TryGetProperty("model", out var value) && value.ValueKind == JsonValueKind.String &&
               string.Equals(value.GetString(), expected, StringComparison.Ordinal);
    }

    private static bool TryGetInputCount(JsonElement input, out int count)
    {
        count = 0;
        if (input.ValueKind == JsonValueKind.String)
        {
            count = 1;
            return true;
        }

        if (input.ValueKind != JsonValueKind.Array || input.GetArrayLength() == 0)
        {
            return false;
        }

        // A flat token array is one input; text arrays and nested token arrays are batches.
        var firstKind = input[0].ValueKind;
        if (firstKind == JsonValueKind.Number)
        {
            count = 1;
            return input.EnumerateArray().All(token => token.ValueKind == JsonValueKind.Number && token.TryGetInt32(out _));
        }

        if (firstKind == JsonValueKind.String)
        {
            count = input.GetArrayLength();
            return input.EnumerateArray().All(value => value.ValueKind == JsonValueKind.String);
        }

        if (firstKind == JsonValueKind.Array)
        {
            count = input.GetArrayLength();
            return input.EnumerateArray().All(value => value.ValueKind == JsonValueKind.Array &&
                value.GetArrayLength() > 0 && value.EnumerateArray().All(token =>
                    token.ValueKind == JsonValueKind.Number && token.TryGetInt32(out _)));
        }

        return false;
    }

    private static async Task<JsonDocument?> ReadRequestAsync(HttpContext context)
    {
        try
        {
            var document = await JsonDocument.ParseAsync(context.Request.Body, cancellationToken: context.RequestAborted);
            if (document.RootElement.ValueKind == JsonValueKind.Object)
            {
                return document;
            }

            document.Dispose();
        }
        catch (JsonException)
        {
            // Invalid inputs receive a provider-shaped error; intentional malformed responses
            // are produced only by the explicit fault switch above.
        }

        await WriteErrorAsync(context, StatusCodes.Status400BadRequest, "Expected a JSON object.", "invalid_request_error");
        return null;
    }

    private static async Task WriteEventAsync(HttpContext context, object value)
    {
        await context.Response.WriteAsync("data: " + JsonSerializer.Serialize(value) + "\n\n", context.RequestAborted);
        await context.Response.Body.FlushAsync(context.RequestAborted);
    }

    private static Task WriteErrorAsync(HttpContext context, int status, string message, string code)
    {
        context.Response.StatusCode = status;
        return context.Response.WriteAsJsonAsync(new
        {
            error = new { message, type = code, param = (string?)null, code },
        }, context.RequestAborted);
    }
}
