using Microsoft.AspNetCore.Mvc;
using SecondBrain.Core.Problems;

namespace SecondBrain.Server.Http;

/// <summary>RFC 9457 responses shared by HTTP, authentication and admission.</summary>
public static class ProblemResponses
{
    public static Task WriteAsync(HttpContext context, int statusCode, string type, string title,
        string? detail = null, IDictionary<string, object?>? extensions = null)
    {
        var problem = new ProblemDetails
        {
            Status = statusCode, Type = type, Title = title, Detail = detail,
            Instance = context.Request.Path
        };
        if (extensions is not null)
            foreach (var entry in extensions) problem.Extensions[entry.Key] = entry.Value;
        context.Response.StatusCode = statusCode;
        context.Response.ContentType = "application/problem+json";
        return context.Response.WriteAsJsonAsync(problem, options: null,
            contentType: "application/problem+json", cancellationToken: context.RequestAborted);
    }

    public static string TypeForStatus(int status) => status switch
    {
        401 or 403 => ProblemTypes.ScopeDenied,
        429 => ProblemTypes.LimitExceeded,
        503 => ProblemTypes.Capacity,
        400 => ProblemTypes.InvalidRequest,
        404 => ProblemTypes.NotFound,
        405 => ProblemTypes.MethodNotAllowed,
        409 => ProblemTypes.Conflict,
        413 => ProblemTypes.RequestTooLarge,
        415 => ProblemTypes.UnsupportedMediaType,
        _ => ProblemTypes.InternalError
    };
}

/// <summary>Converts unhandled failures and empty framework errors to problem JSON.</summary>
public sealed class ProblemMiddleware(RequestDelegate next, ILogger<ProblemMiddleware> logger)
{
    public async Task InvokeAsync(HttpContext context)
    {
        try
        {
            await next(context);
            if (context.Response.StatusCode >= 400 && !context.Response.HasStarted &&
                context.Response.ContentLength is null or 0 && string.IsNullOrEmpty(context.Response.ContentType))
                await ProblemResponses.WriteAsync(context, context.Response.StatusCode,
                    ProblemResponses.TypeForStatus(context.Response.StatusCode), "Request failed");
        }
        catch (OperationCanceledException) when (context.RequestAborted.IsCancellationRequested) { }
        catch (BadHttpRequestException exception) when (!context.Response.HasStarted)
        {
            context.Response.Clear();
            await ProblemResponses.WriteAsync(context, exception.StatusCode,
                ProblemResponses.TypeForStatus(exception.StatusCode), "Invalid request");
        }
        catch (Auth.AuthorityChangedException) when (!context.Response.HasStarted)
        {
            context.Response.Clear();
            await ProblemResponses.WriteAsync(context, 409, ProblemResponses.TypeForStatus(409),
                "Credential authority changed; authenticate again");
        }
        catch (Core.Privacy.PrivacyPolicyException exception) when (!context.Response.HasStarted)
        {
            context.Response.Clear();
            await ProblemResponses.WriteAsync(context, exception.ProblemType == ProblemTypes.EgressUnverified ? 503 : 403,
                exception.ProblemType, "Provider request refused by privacy policy");
        }
        catch (Core.Providers.ProviderRequestException exception) when (!context.Response.HasStarted)
        {
            context.Response.Clear();
            await ProblemResponses.WriteAsync(context, (int?)exception.StatusCode ?? 502, exception.ProblemType, "Provider request failed");
        }
        catch (Exception exception) when (!context.Response.HasStarted)
        {
            logger.LogError(exception, "HTTP request failed for {Path}", context.Request.Path);
            context.Response.Clear();
            await ProblemResponses.WriteAsync(context, StatusCodes.Status500InternalServerError,
                ProblemResponses.TypeForStatus(500), "Internal server error");
        }
    }
}
