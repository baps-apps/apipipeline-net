namespace ApiPipeline.NET.Correlation;

/// <summary>Copies the request's correlation id onto outbound <see cref="HttpClient"/> requests.</summary>
internal sealed class CorrelationIdPropagationHandler : DelegatingHandler
{
    private readonly IHttpContextAccessor _httpContextAccessor;

    public CorrelationIdPropagationHandler(IHttpContextAccessor httpContextAccessor)
    {
        _httpContextAccessor = httpContextAccessor;
    }

    /// <inheritdoc />
    protected override Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        // A header the caller set wins; outside a request there is nothing to forward.
        if (!request.Headers.Contains(CorrelationHeaders.CorrelationId)
            && _httpContextAccessor.HttpContext?.GetCorrelationId() is { Length: > 0 } correlationId)
        {
            // Already validated by the middleware's allow-list.
            request.Headers.TryAddWithoutValidation(CorrelationHeaders.CorrelationId, correlationId);
        }

        return base.SendAsync(request, cancellationToken);
    }
}
