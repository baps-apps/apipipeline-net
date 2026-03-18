using System.Diagnostics;
using ApiPipeline.NET.Middleware;
using Asp.Versioning;
using Microsoft.AspNetCore.Mvc;

namespace ApiPipeline.NET.Sample.Controllers;

[ApiController]
[Route("api/v{version:apiVersion}/orders")]
[ApiVersion("1.0")]
[ApiVersion("2.0")]
public sealed class OrdersController : ControllerBase
{
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

