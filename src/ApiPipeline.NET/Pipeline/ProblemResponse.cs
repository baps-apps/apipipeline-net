using System.Text.Json.Serialization;
using Microsoft.AspNetCore.Mvc;

namespace ApiPipeline.NET.Pipeline;

/// <summary>
/// Writes the RFC 9457 body of a response the pipeline produces (429, timeout): through the
/// application's <see cref="IProblemDetailsService"/> when registered, so its customizations apply,
/// else with source-generated serialization, which works under Native AOT.
/// </summary>
internal static class ProblemResponse
{
    private const string ContentType = "application/problem+json";

    /// <summary>Writes the body, unless the response started.</summary>
    /// <param name="context">The request.</param>
    /// <param name="statusCode">The status.</param>
    /// <param name="type">The type URI; the framework default when <c>null</c>.</param>
    /// <param name="title">The title; the framework default when <c>null</c>.</param>
    /// <param name="detail">What happened. Nothing internal.</param>
    public static async Task WriteAsync(HttpContext context, int statusCode, string? type, string? title, string detail)
    {
        if (context.Response.HasStarted)
        {
            return;
        }

        // TypedResults fills the framework's default type and title for the status.
        var problem = TypedResults.Problem(detail: detail, statusCode: statusCode, title: title, type: type).ProblemDetails;
        context.Response.StatusCode = statusCode;

        if (context.RequestServices.GetService<IProblemDetailsService>() is { } service
            && await service.TryWriteAsync(new ProblemDetailsContext { HttpContext = context, ProblemDetails = problem }))
        {
            return;
        }

        // Not the application's JSON options: without reflection (Native AOT) they may not know
        // ProblemDetails. No RequestAborted: a timed-out request's token is already cancelled.
        await context.Response.WriteAsJsonAsync(
            problem,
            ProblemResponseJsonContext.Default.ProblemDetails,
            ContentType,
            CancellationToken.None);
    }
}

/// <summary>Serialization metadata for <see cref="ProblemResponse"/>'s body.</summary>
[JsonSerializable(typeof(ProblemDetails))]
internal sealed partial class ProblemResponseJsonContext : JsonSerializerContext;
