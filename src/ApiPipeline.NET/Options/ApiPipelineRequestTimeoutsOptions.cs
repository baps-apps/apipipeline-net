using System.ComponentModel.DataAnnotations;

namespace ApiPipeline.NET;

/// <summary>
/// The default request timeout policy, section <c>ApiPipeline:RequestTimeouts</c>. Override per
/// endpoint with <c>WithRequestTimeout</c> or <c>DisableRequestTimeout</c>.
/// </summary>
public sealed class ApiPipelineRequestTimeoutsOptions : IValidatableObject
{
    /// <summary>Whether the timeout middleware is added. Default <c>false</c>.</summary>
    public bool Enabled { get; set; }

    /// <summary>The default timeout. Default 30 s; 1 s to 1 h.</summary>
    public TimeSpan DefaultTimeout { get; set; } = TimeSpan.FromSeconds(30);

    /// <summary>Status on timeout. Default <c>504</c>.</summary>
    [Range(400, 599)]
    public int TimeoutStatusCode { get; set; } = StatusCodes.Status504GatewayTimeout;

    /// <inheritdoc />
    public IEnumerable<ValidationResult> Validate(ValidationContext validationContext)
    {
        if (DurationRange.Check(DefaultTimeout, TimeSpan.FromSeconds(1), TimeSpan.FromHours(1), nameof(DefaultTimeout)) is { } failure)
        {
            yield return failure;
        }
    }
}
