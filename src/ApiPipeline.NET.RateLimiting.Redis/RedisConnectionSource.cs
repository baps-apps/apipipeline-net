using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using StackExchange.Redis;

namespace ApiPipeline.NET.RateLimiting.Redis;

/// <summary>
/// The Redis connection, from the first source that exists: a connection passed in code, the
/// <c>Configuration</c> string (owned, <c>abortConnect=false</c>), or an
/// <see cref="IConnectionMultiplexer"/> in DI. Connecting starts with the host but is never awaited
/// there, so Redis down does not fail startup. Only an owned connection is disposed.
/// </summary>
internal sealed class RedisConnectionSource : IAsyncDisposable, IDisposable
{
    public const string NoSourceMessage =
        "ApiPipeline:RateLimiting:Redis:Enabled is true, but no Redis connection is available. " +
        "Provide one of: a connection in code, AddApiPipelineRedisRateLimiting(configuration, sp => ...); " +
        "a connection string in ApiPipeline:RateLimiting:Redis:Configuration; or a registered " +
        "IConnectionMultiplexer. Or set ApiPipeline:RateLimiting:Redis:Enabled to false to keep per-pod limits.";

    private readonly IServiceProvider _services;
    private readonly Func<IServiceProvider, IConnectionMultiplexer>? _explicitConnection;
    private readonly string? _configuration;
    private readonly TimeSpan _commandTimeout;
    private readonly bool _hasRegisteredConnection;
    private readonly Lock _gate = new();
    private Task<IConnectionMultiplexer>? _connection;
    private ConnectionMultiplexer? _owned;
    private bool _connectionRequested;
    private bool _firstAttemptSettled;
    private bool _firstAttemptWaitedOut;
    private bool _disposed;

    public RedisConnectionSource(
        IServiceProvider services,
        IServiceProviderIsService isService,
        IOptions<ApiPipelineRedisRateLimitingOptions> options,
        ExplicitRedisConnection? explicitConnection = null)
    {
        _services = services;
        _explicitConnection = explicitConnection?.Factory;
        _configuration = string.IsNullOrWhiteSpace(options.Value.Configuration) ? null : options.Value.Configuration;
        _commandTimeout = options.Value.CommandTimeout;
        _hasRegisteredConnection = isService.IsService(typeof(IConnectionMultiplexer));
    }

    /// <summary>Whether a source exists. Checked at startup without connecting.</summary>
    public bool HasConnectionSource =>
        _explicitConnection is not null || _configuration is not null || _hasRegisteredConnection;

    /// <summary>For tests: whether a connection was ever asked for.</summary>
    internal bool ConnectionRequested => Volatile.Read(ref _connectionRequested);

    /// <summary>Whether the first connect is unsettled: waiting on it is starting up, not an outage.</summary>
    public bool IsFirstConnectPending => !Volatile.Read(ref _firstAttemptSettled);

    /// <summary>Whether a request already waited a whole command timeout on the first connect; later ones stop waiting.</summary>
    public bool IsFirstConnectSlow => IsFirstConnectPending && Volatile.Read(ref _firstAttemptWaitedOut);

    public void FirstConnectWaitedOut() => Volatile.Write(ref _firstAttemptWaitedOut, true);

    /// <summary>Starts connecting in the background. A failure is dropped; the next request retries.</summary>
    public void StartConnecting()
    {
        try
        {
            _ = GetConnectionAsync().ContinueWith(
                static t => _ = t.Exception,
                CancellationToken.None,
                TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously,
                TaskScheduler.Default);
        }
        catch (ObjectDisposedException)
        {
        }
    }

    /// <summary>The database, connecting first if needed. A failed connect is not cached.</summary>
    public async Task<IDatabase> GetDatabaseAsync()
    {
        var connection = await GetConnectionAsync().ConfigureAwait(false);
        return connection.GetDatabase();
    }

    public Task<IConnectionMultiplexer> GetMultiplexerAsync() => GetConnectionAsync();

    public void Dispose()
    {
        ConnectionMultiplexer? owned;
        lock (_gate)
        {
            _disposed = true;
            owned = _owned;
            _owned = null;
        }

        owned?.Dispose();
    }

    public async ValueTask DisposeAsync()
    {
        ConnectionMultiplexer? owned;
        lock (_gate)
        {
            _disposed = true;
            owned = _owned;
            _owned = null;
        }

        if (owned is not null)
        {
            await owned.DisposeAsync().ConfigureAwait(false);
        }
    }

    private Task<IConnectionMultiplexer> GetConnectionAsync()
    {
        var current = Volatile.Read(ref _connection);
        if (current is { IsFaulted: false, IsCanceled: false })
        {
            return current;
        }

        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);

            current = _connection;
            if (current is null || current.IsFaulted || current.IsCanceled)
            {
                Volatile.Write(ref _connectionRequested, true);

                // Off the request thread and lock: a factory may block for its connect timeout.
                var isFirstAttempt = current is null;
                current = Task.Run(ConnectAsync);
                Volatile.Write(ref _connection, current);
                if (isFirstAttempt)
                {
                    _ = current.ContinueWith(
                        _ => Volatile.Write(ref _firstAttemptSettled, true),
                        CancellationToken.None,
                        TaskContinuationOptions.ExecuteSynchronously,
                        TaskScheduler.Default);
                }
            }

            return current;
        }
    }

    private Task<IConnectionMultiplexer> ConnectAsync()
    {
        if (_explicitConnection is not null)
        {
            return Task.FromResult(_explicitConnection(_services) ?? throw new InvalidOperationException(
                "The connection factory passed to AddApiPipelineRedisRateLimiting(configuration, sp => ...) " +
                "returned null. Return the IConnectionMultiplexer to share rate limits through."));
        }

        if (_configuration is not null)
        {
            return ConnectOwnedAsync(_configuration);
        }

        return Task.FromResult(
            _services.GetService<IConnectionMultiplexer>() ?? throw new InvalidOperationException(NoSourceMessage));
    }

    /// <summary>
    /// Options for the owned connection. Caps the client's <c>AsyncTimeout</c> at the command timeout,
    /// so commands the limiter gave up on do not pile up in a brownout. Connections the application
    /// supplies are left as configured.
    /// </summary>
    internal static ConfigurationOptions CreateOwnedOptions(string configuration, TimeSpan commandTimeout)
    {
        var options = ConfigurationOptions.Parse(configuration);
        options.AbortOnConnectFail = false;
        options.AsyncTimeout = Math.Min(options.AsyncTimeout, (int)Math.Ceiling(commandTimeout.TotalMilliseconds));
        return options;
    }

    private async Task<IConnectionMultiplexer> ConnectOwnedAsync(string configuration)
    {
        var options = CreateOwnedOptions(configuration, _commandTimeout);
        var multiplexer = await ConnectionMultiplexer.ConnectAsync(options).ConfigureAwait(false);

        bool disposed;
        lock (_gate)
        {
            disposed = _disposed;
            if (!disposed)
            {
                _owned = multiplexer;
            }
        }

        if (disposed)
        {
            await multiplexer.DisposeAsync().ConfigureAwait(false);
            throw new ObjectDisposedException(nameof(RedisConnectionSource));
        }

        return multiplexer;
    }
}

/// <summary>A connection passed in code.</summary>
internal sealed class ExplicitRedisConnection(Func<IServiceProvider, IConnectionMultiplexer> factory)
{
    public Func<IServiceProvider, IConnectionMultiplexer> Factory { get; } = factory;
}
