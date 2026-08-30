namespace ApiPipeline.NET.Observability;

/// <summary>Telemetry names. Export the metrics with <c>.AddMeter(ApiPipelineTelemetry.MeterName)</c>.</summary>
/// <remarks>
/// The meter is per host. No spans are created; the correlation id is a tag on the request activity.
/// </remarks>
public static class ApiPipelineTelemetry
{
    /// <summary>The meter name.</summary>
    public const string MeterName = "ApiPipeline.NET";

    /// <summary>The activity tag holding the correlation id.</summary>
    public const string CorrelationIdTagName = "apipipeline.correlation_id";
}
