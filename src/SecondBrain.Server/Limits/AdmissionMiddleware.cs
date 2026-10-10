using System.Globalization;
using SecondBrain.Core.Limits;
using SecondBrain.Core.Problems;

namespace SecondBrain.Server.Limits;

public sealed class AdmissionMiddleware(RequestDelegate next)
{
    public const string ReservationItem = "SecondBrain.AdmissionReservation";

    public async Task InvokeAsync(HttpContext context, IAdmissionController admission)
    {
        var id = context.User.FindFirst("credential_id")?.Value;
        if (context.User.Identity?.IsAuthenticated != true || string.IsNullOrEmpty(id))
        {
            await next(context);
            return;
        }

        var policy = context.GetEndpoint()?.Metadata.GetMetadata<AdmissionPolicy>() ?? new();
        if (policy.BytesFromContentLength)
        {
            if (context.Request.ContentLength is not long length)
            {
                context.Response.Headers.RetryAfter = "5";
                await Results.Problem(statusCode: 503, type: ProblemTypes.Capacity, title: "Capacity unavailable", detail: "Admission requires a bounded content length.").ExecuteAsync(context);
                return;
            }

            policy = policy with { AdmittedBytes = Math.Max(policy.AdmittedBytes, length) };
        }

        // The request's budget is reserved first, so a rejected request never reaches persistent corpus accounting.
        var decision = admission.TryReserve(id, policy);
        if (!decision.Accepted)
        {
            await RejectAsync(context, decision);
            return;
        }

        using (decision.Reservation)
        {
            if (context.RequestServices?.GetService<IPersistentAdmissionAccounting>() is { } accounting)
            {
                await accounting.RefreshAsync(context.RequestAborted);
                if (admission.ConfirmCapacity() is { } exceeded)
                {
                    await RejectAsync(context, exceeded);
                    return;
                }
            }

            context.Items[ReservationItem] = decision.Reservation;
            try
            {
                await next(context);
            }
            finally
            {
                context.Items.Remove(ReservationItem);
            }
        }
    }

    private static Task RejectAsync(HttpContext context, AdmissionDecision decision)
    {
        context.Response.Headers.RetryAfter = decision.RetryAfterSeconds.ToString(CultureInfo.InvariantCulture);
        return Results.Problem(
            statusCode: decision.StatusCode,
            type: decision.StatusCode == 429 ? ProblemTypes.LimitExceeded : ProblemTypes.Capacity,
            title: decision.StatusCode == 429 ? "Credential limit exceeded" : "Capacity unavailable",
            detail: decision.Detail).ExecuteAsync(context);
    }
}
