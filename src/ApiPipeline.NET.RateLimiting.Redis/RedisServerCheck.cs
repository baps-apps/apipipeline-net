using System.Globalization;
using System.Net;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using StackExchange.Redis;

namespace ApiPipeline.NET.RateLimiting.Redis;

/// <summary>
/// Warns, once per process, about Redis servers that would silently break shared limits: older than
/// <see cref="MinimumVersion"/>, or able to evict rate-limit keys; and, outside Development, about a
/// connection to another host without TLS. Runs in the background after Redis first answers; never
/// fails a request or startup.
/// </summary>
internal sealed class RedisServerCheck
{
    /// <summary>Oldest Redis the scripts run on (<c>TIME</c>, <c>PEXPIREAT</c>).</summary>
    internal static readonly Version MinimumVersion = new(5, 0);

    private readonly ILogger<RedisServerCheck> _logger;
    private readonly bool _requireTls;
    private int _started;
    private Task? _running;

    /// <param name="logger">Gets the warnings.</param>
    /// <param name="environment">Development skips the TLS warning; without one, it applies.</param>
    public RedisServerCheck(ILogger<RedisServerCheck> logger, IHostEnvironment? environment = null)
    {
        _logger = logger;
        _requireTls = environment is null || !environment.IsDevelopment();
    }

    /// <summary>For tests: the running check; null before Redis answered.</summary>
    internal Task? Running => Volatile.Read(ref _running);

    /// <summary>Starts the check the first time Redis answers.</summary>
    public void RedisAnswered(RedisConnectionSource connection)
    {
        if (Volatile.Read(ref _started) != 0 || Interlocked.Exchange(ref _started, 1) != 0)
        {
            return;
        }

        Volatile.Write(ref _running, Task.Run(async () =>
        {
            try
            {
                await CheckAsync(await connection.GetMultiplexerAsync().ConfigureAwait(false)).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                _logger.RedisServerCheckSkipped(ex, "(connection)");
            }
        }));
    }

    /// <summary>Checks the connection's TLS, then every connected primary.</summary>
    internal async Task CheckAsync(IConnectionMultiplexer multiplexer)
    {
        if (_requireTls)
        {
            CheckTls(multiplexer);
        }

        foreach (var server in multiplexer.GetServers())
        {
            if (!server.IsConnected || server.IsReplica)
            {
                continue;
            }

            var endpoint = EndPointCollection.ToString(server.EndPoint);
            try
            {
                if (IsTooOld(server.Version))
                {
                    _logger.RedisVersionUnsupported(endpoint, server.Version.ToString(), MinimumVersion.ToString());
                }

                string? policy = null;
                long maxmemory = 0;
                foreach (var section in await server.InfoAsync("memory").ConfigureAwait(false))
                {
                    foreach (var (key, value) in section)
                    {
                        if (key == "maxmemory_policy")
                        {
                            policy = value;
                        }
                        else if (key == "maxmemory")
                        {
                            _ = long.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out maxmemory);
                        }
                    }
                }

                if (CanEvict(policy, maxmemory))
                {
                    _logger.RedisEvictsRateLimitKeys(endpoint, policy!);
                }
            }
            catch (Exception ex)
            {
                _logger.RedisServerCheckSkipped(ex, endpoint);
            }
        }
    }

    /// <summary>
    /// Warns when the connection reaches another host in plain text. Read from the connection's own
    /// configuration, so it covers every source. That string holds the password: only the endpoints
    /// are logged.
    /// </summary>
    private void CheckTls(IConnectionMultiplexer multiplexer)
    {
        ConfigurationOptions options;
        try
        {
            options = ConfigurationOptions.Parse(multiplexer.Configuration);
        }
        catch (Exception)
        {
            // Not logged: the parser's message may quote the configuration, password included.
            return;
        }

        if (SendsPlainTextOffHost(options))
        {
            _logger.RedisWithoutTls(string.Join(", ", options.EndPoints.Select(EndPointCollection.ToString)));
        }
    }

    /// <summary>Whether TLS is off and some endpoint is not loopback.</summary>
    internal static bool SendsPlainTextOffHost(ConfigurationOptions options) =>
        !options.Ssl && options.EndPoints.Any(endpoint => !IsLoopback(endpoint));

    private static bool IsLoopback(EndPoint endpoint) => endpoint switch
    {
        IPEndPoint ip => IPAddress.IsLoopback(ip.Address),
        DnsEndPoint dns => string.Equals(dns.Host, "localhost", StringComparison.OrdinalIgnoreCase),
        _ => false,
    };

    internal static bool IsTooOld(Version version) => version < MinimumVersion;

    /// <summary>Whether a server with a memory limit uses a policy other than <c>noeviction</c>; every key has a TTL, so <c>volatile-*</c> evicts them too.</summary>
    internal static bool CanEvict(string? policy, long maxmemory) =>
        maxmemory > 0 && policy is not null && !string.Equals(policy, "noeviction", StringComparison.OrdinalIgnoreCase);
}
