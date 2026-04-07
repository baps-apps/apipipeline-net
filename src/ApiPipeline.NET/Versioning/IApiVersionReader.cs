namespace ApiPipeline.NET.Versioning;

/// <summary>
/// Reads the requested API version string from an HTTP context.
/// Register an implementation via <c>services.AddSingleton&lt;IApiVersionReader, YourImpl&gt;()</c>
/// or use the <c>ApiPipeline.NET.Versioning</c> satellite package which provides an implementation
/// backed by <c>Asp.Versioning.Mvc</c>.
/// </summary>
public interface IApiVersionReader
{
    /// <summary>
    /// Returns the requested API version string, or <c>null</c> if no version was specified.
    /// </summary>
    string? ReadApiVersion(HttpContext context);
}
