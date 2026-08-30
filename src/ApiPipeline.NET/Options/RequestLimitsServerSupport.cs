using ApiPipeline.NET.Observability;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Server.HttpSys;
using Microsoft.Extensions.Options;

namespace ApiPipeline.NET;

/// <summary>
/// Which request limits each server applies. IIS in-process and HTTP.sys get the limits they have
/// options for; the rest are configured outside the app and named in a startup warning.
/// </summary>
internal static class RequestLimitsServerSupport
{
    /// <summary>The server, as far as request limits are concerned.</summary>
    internal enum Server
    {
        /// <summary>Kestrel, including behind IIS out-of-process.</summary>
        Kestrel,

        /// <summary>IIS in-process.</summary>
        Iis,

        /// <summary>HTTP.sys.</summary>
        HttpSys,

        /// <summary>Anything else (TestServer): not reported.</summary>
        Other,
    }

    /// <summary>
    /// Whether the runtime has IIS in-process's options type. False on source-built runtimes, whose
    /// <c>Microsoft.AspNetCore.Server.IIS</c> is an empty stub. A literal name, so trimming sees it.
    /// </summary>
    internal static readonly bool IisOptionsAvailable =
        Type.GetType("Microsoft.AspNetCore.Builder.IISServerOptions, Microsoft.AspNetCore.Server.IIS", throwOnError: false) is not null;

    /// <summary>Whether the runtime has HTTP.sys's options type; see <see cref="IisOptionsAvailable"/>.</summary>
    internal static readonly bool HttpSysOptionsAvailable =
        Type.GetType("Microsoft.AspNetCore.Server.HttpSys.HttpSysOptions, Microsoft.AspNetCore.Server.HttpSys", throwOnError: false) is not null;

    internal static Server Classify(string? serverAssembly) => serverAssembly switch
    {
        "Microsoft.AspNetCore.Server.Kestrel.Core" => Server.Kestrel,
        "Microsoft.AspNetCore.Server.IIS" => Server.Iis,
        "Microsoft.AspNetCore.Server.HttpSys" => Server.HttpSys,
        _ => Server.Other,
    };

    /// <summary>Settings that are set but <paramref name="server"/> cannot apply.</summary>
    internal static IReadOnlyList<string> Unenforced(Server server, ApiPipelineRequestLimitsOptions options)
    {
        if (!options.Enabled || server is Server.Kestrel or Server.Other)
        {
            return [];
        }

        var unenforced = new List<string>();
        void Add(bool isSet, string name)
        {
            if (isSet)
            {
                unenforced.Add(name);
            }
        }

        Add(options.MaxRequestHeadersTotalSize is not null, nameof(options.MaxRequestHeadersTotalSize));
        Add(options.MaxRequestHeaderCount is not null, nameof(options.MaxRequestHeaderCount));
        if (server == Server.Iis)
        {
            Add(options.RequestHeadersTimeout is not null, nameof(options.RequestHeadersTimeout));
            Add(options.KeepAliveTimeout is not null, nameof(options.KeepAliveTimeout));
            Add(options.MaxConcurrentConnections is not null, nameof(options.MaxConcurrentConnections));
        }

        Add(options.MinRequestBodyDataRateBytesPerSecond is not null, nameof(options.MinRequestBodyDataRateBytesPerSecond));
        return unenforced;
    }
}

/// <summary>Applies the body limit, IIS in-process's one limit set in code.</summary>
internal sealed class ConfigureIisServerOptions : IConfigureOptions<IISServerOptions>
{
    private readonly IOptions<ApiPipelineRequestLimitsOptions> _requestLimits;

    public ConfigureIisServerOptions(IOptions<ApiPipelineRequestLimitsOptions> requestLimits) => _requestLimits = requestLimits;

    public void Configure(IISServerOptions options)
    {
        var limits = _requestLimits.Value;
        if (limits.Enabled && limits.MaxRequestBodySize is { } maxBody)
        {
            options.MaxRequestBodySize = maxBody;
        }
    }
}

/// <summary>Applies body size, connections and both timeouts to HTTP.sys.</summary>
internal sealed class ConfigureHttpSysOptions : IConfigureOptions<HttpSysOptions>
{
    private readonly IOptions<ApiPipelineRequestLimitsOptions> _requestLimits;

    public ConfigureHttpSysOptions(IOptions<ApiPipelineRequestLimitsOptions> requestLimits) => _requestLimits = requestLimits;

    public void Configure(HttpSysOptions options)
    {
        var limits = _requestLimits.Value;
        if (!limits.Enabled)
        {
            return;
        }

        if (limits.MaxRequestBodySize is { } maxBody)
        {
            options.MaxRequestBodySize = maxBody;
        }

        if (limits.MaxConcurrentConnections is { } maxConnections)
        {
            options.MaxConnections = maxConnections;
        }

        if (limits.RequestHeadersTimeout is { } headersTimeout)
        {
            options.Timeouts.HeaderWait = headersTimeout;
        }

        if (limits.KeepAliveTimeout is { } keepAlive)
        {
            options.Timeouts.IdleConnection = keepAlive;
        }
    }
}

/// <summary>Warns under IIS in-process or HTTP.sys about limits set but not enforced.</summary>
internal sealed class RequestLimitsServerWarning : IApiPipelineStartupCheck
{
    private readonly IOptions<ApiPipelineRequestLimitsOptions> _settings;
    private readonly ILogger<RequestLimitsServerWarning> _logger;
    private readonly IServer? _server;

    public RequestLimitsServerWarning(
        IOptions<ApiPipelineRequestLimitsOptions> settings,
        ILogger<RequestLimitsServerWarning> logger,
        IServer? server = null)
    {
        _settings = settings;
        _logger = logger;
        _server = server;
    }

    public void Run()
    {
        if (_server is null)
        {
            return;
        }

        var server = RequestLimitsServerSupport.Classify(_server.GetType().Assembly.GetName().Name);
        var unenforced = RequestLimitsServerSupport.Unenforced(server, _settings.Value);
        if (unenforced.Count > 0)
        {
            _logger.RequestLimitsNotEnforcedByServer(
                server == RequestLimitsServerSupport.Server.Iis ? "IIS (in-process)" : "HTTP.sys",
                string.Join(", ", unenforced));
        }
    }
}
