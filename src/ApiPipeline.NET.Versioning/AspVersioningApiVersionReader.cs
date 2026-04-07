using ApiPipeline.NET.Versioning;
using Asp.Versioning;
using Microsoft.AspNetCore.Http;

namespace ApiPipeline.NET.Versioning.AspVersioning;

/// <summary>
/// Reads the requested API version using <c>Asp.Versioning.Mvc</c>'s
/// <see cref="HttpContextExtensions.GetRequestedApiVersion"/> extension.
/// </summary>
internal sealed class AspVersioningApiVersionReader : IApiVersionReader
{
    /// <inheritdoc />
    public string? ReadApiVersion(HttpContext context)
        => context.GetRequestedApiVersion()?.ToString();
}
