using Microsoft.AspNetCore.Antiforgery;
using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.Options;
using SecondBrain.Core.Authorization;
using SecondBrain.Core.Configuration;
using SecondBrain.Core.Problems;
using SecondBrain.Server.Http;

namespace SecondBrain.Server.Auth;

public sealed class RequestAuthorizationMiddleware(RequestDelegate next)
{
    public async Task InvokeAsync(HttpContext context, IScopePolicy scopePolicy, IOptionsMonitor<SecondBrainOptions> options, TimeProvider clock)
    {
        var policy = context.GetEndpoint()?.Metadata.GetMetadata<EndpointPolicy>();
        var identity = context.Items[typeof(AuthenticatedCredential)] as AuthenticatedCredential;
        if (policy is not null)
        {
            if (identity is null) { await ProblemResponses.WriteAsync(context, 401, ProblemTypes.AuthenticationRequired, "A valid credential is required."); return; }
            if (!scopePolicy.IsAllowed(policy.Operation, identity.Scopes) || policy.SessionOnly && identity.Kind != "session")
            { await ProblemResponses.WriteAsync(context, 403, ProblemTypes.ScopeDenied, "The credential cannot perform this operation."); return; }
            if (policy.StepUp && identity.Kind == "session" && (identity.SteppedUpAt is null || AuthTime.Parse(identity.SteppedUpAt).AddMinutes(options.CurrentValue.Auth.StepUpMinutes) <= clock.GetUtcNow()))
            { await ProblemResponses.WriteAsync(context, 403, ProblemTypes.StepUpRequired, "Re-authenticate before this action."); return; }
        }
        var unsafeMethod = !HttpMethods.IsGet(context.Request.Method) && !HttpMethods.IsHead(context.Request.Method) && !HttpMethods.IsOptions(context.Request.Method);
        // SignalR transport negotiation carries no domain mutation; its exact Origin is checked by the outer policy.
        var componentTransport = context.GetEndpoint()?.Metadata.GetMetadata<HubMetadata>()?.HubType.FullName == "Microsoft.AspNetCore.Components.Server.ComponentHub";
        if (!componentTransport && identity?.Kind == "session" && unsafeMethod && !context.Request.Headers.Authorization.ToString().StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase))
        {
            try { await context.RequestServices.GetRequiredService<IAntiforgery>().ValidateRequestAsync(context); }
            catch (AntiforgeryValidationException) { await ProblemResponses.WriteAsync(context, 400, ProblemTypes.AntiforgeryRejected, "The antiforgery token is missing or invalid."); return; }
        }
        await next(context);
    }
}
