using ApiPipeline.NET.Correlation;
using Asp.Versioning;
using Microsoft.AspNetCore.Mvc;

namespace ApiPipeline.NET.Sample.Controllers;

/// <summary>
/// Sample orders controller with a deprecated v1 and a current v2, so the <c>Deprecation</c> and
/// <c>Sunset</c> headers can be seen on v1 responses.
/// </summary>
[ApiController]
[Route("api/v{version:apiVersion}/orders")]
// Deprecated = true drives api-deprecated-versions; the ApiPipeline:VersionDeprecation section
// supplies the dates for the Deprecation and Sunset headers. Use both.
[ApiVersion("1.0", Deprecated = true)]
[ApiVersion("2.0")]
public sealed class OrdersController : ControllerBase
{
    /// <summary>
    /// Returns a list of orders using the v1 schema.
    /// This version is deprecated — callers should migrate to <see cref="GetV2"/>.
    /// The response includes the resolved correlation and trace IDs for request tracing.
    /// </summary>
    /// <returns>A 200 OK response containing the v1 orders payload and tracing IDs.</returns>
    // GET /api/v1/orders
    [HttpGet]
    [MapToApiVersion("1.0")]
    public IActionResult GetV1()
    {
        // Null only if the pipeline has no WithCorrelationId().
        var correlationId = HttpContext.GetCorrelationId();
        var traceId = HttpContext.GetTraceId();

        return Ok(new
        {
            ApiVersion = "1.0",
            Message = "Deprecated orders API. Please migrate to v2.",
            CorrelationId = correlationId,
            TraceId = traceId,
            Orders = new[]
            {
                new { Id = "ORD-1001", Status = "Processing" },
                new { Id = "ORD-1002", Status = "Shipped" }
            }
        });
    }

    /// <summary>
    /// Returns a list of orders using the current v2 schema, which includes order totals.
    /// The response includes the resolved correlation and trace IDs for request tracing.
    /// </summary>
    /// <returns>A 200 OK response containing the v2 orders payload and tracing IDs.</returns>
    // GET /api/v2/orders
    [HttpGet]
    [MapToApiVersion("2.0")]
    public IActionResult GetV2()
    {
        // Null only if the pipeline has no WithCorrelationId().
        var correlationId = HttpContext.GetCorrelationId();
        var traceId = HttpContext.GetTraceId();

        return Ok(new
        {
            ApiVersion = "2.0",
            Message = "Current orders API.",
            CorrelationId = correlationId,
            TraceId = traceId,
            Orders = new[]
            {
                new { Id = "ORD-2001", Status = "Processing", Total = 125.50m },
                new { Id = "ORD-2002", Status = "Delivered", Total = 89.99m }
            }
        });
    }
}

