using System.Diagnostics;
using ApiPipeline.NET.Middleware;
using Asp.Versioning;
using Microsoft.AspNetCore.Mvc;

namespace ApiPipeline.NET.Sample.Controllers;

/// <summary>
/// Sample orders controller demonstrating API versioning with ApiPipeline.NET.
/// Exposes v1 (deprecated) and v2 (current) endpoints so that the
/// <c>Sunset</c> / <c>Deprecation</c> headers added by <c>UseApiVersionDeprecation</c>
/// can be observed in responses to v1 requests.
/// </summary>
[ApiController]
[Route("api/v{version:apiVersion}/orders")]
[ApiVersion("1.0")]
[ApiVersion("2.0")]
public sealed class OrdersController : ControllerBase
{
    /// <summary>
    /// Returns a list of orders using the v1 schema.
    /// This version is deprecated — callers should migrate to <see cref="GetV2"/>.
    /// The response includes the resolved <c>X-Correlation-Id</c> for request tracing.
    /// </summary>
    /// <returns>A 200 OK response containing the v1 orders payload and correlation ID.</returns>
    // GET /api/v1/orders
    [HttpGet]
    [MapToApiVersion("1.0")]
    public IActionResult GetV1()
    {
        var correlationId = HttpContext.Items[CorrelationIdMiddleware.HeaderName] as string
                            ?? Activity.Current?.TraceId.ToString()
                            ?? Guid.NewGuid().ToString("N");

        return Ok(new
        {
            ApiVersion = "1.0",
            Message = "Deprecated orders API. Please migrate to v2.",
            CorrelationId = correlationId,
            Orders = new[]
            {
                new { Id = "ORD-1001", Status = "Processing" },
                new { Id = "ORD-1002", Status = "Shipped" }
            }
        });
    }

    /// <summary>
    /// Returns a list of orders using the current v2 schema, which includes order totals.
    /// The response includes the resolved <c>X-Correlation-Id</c> for request tracing.
    /// </summary>
    /// <returns>A 200 OK response containing the v2 orders payload and correlation ID.</returns>
    // GET /api/v2/orders
    [HttpGet]
    [MapToApiVersion("2.0")]
    public IActionResult GetV2()
    {
        var correlationId = HttpContext.Items[CorrelationIdMiddleware.HeaderName] as string
                            ?? Activity.Current?.TraceId.ToString()
                            ?? Guid.NewGuid().ToString("N");

        return Ok(new
        {
            ApiVersion = "2.0",
            Message = "Current orders API.",
            CorrelationId = correlationId,
            Orders = new[]
            {
                new { Id = "ORD-2001", Status = "Processing", Total = 125.50m },
                new { Id = "ORD-2002", Status = "Delivered", Total = 89.99m }
            }
        });
    }
}

