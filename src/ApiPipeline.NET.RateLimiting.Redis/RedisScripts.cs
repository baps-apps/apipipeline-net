namespace ApiPipeline.NET.RateLimiting.Redis;

/// <summary>
/// One Lua script per algorithm, so each decision is atomic. Acquire scripts take the permit count
/// last (<c>0</c> only asks) and return <c>{allowed, retryAfterMs}</c>. Window and bucket rejections
/// write nothing; concurrency always prunes expired slots first.
/// </summary>
internal static class RedisScripts
{
    /// <summary>
    /// Fixed window from first use. ARGV: permitLimit, windowMs, permitCount. A counter that lost its
    /// expiry gets a full window again, or it would reject forever.
    /// </summary>
    public const string FixedWindow = """
        local limit, window, n = tonumber(ARGV[1]), tonumber(ARGV[2]), tonumber(ARGV[3])
        local c = tonumber(redis.call('GET', KEYS[1]) or '0')
        if (n == 0 and c >= limit) or c + n > limit then
          local ttl = redis.call('PTTL', KEYS[1])
          if ttl == -1 then redis.call('PEXPIRE', KEYS[1], window); ttl = window end
          if ttl < 0 then ttl = 0 end
          return {0, ttl}
        end
        if n > 0 then
          if redis.call('INCRBY', KEYS[1], n) == n or redis.call('PTTL', KEYS[1]) == -1 then
            redis.call('PEXPIRE', KEYS[1], window)
          end
        end
        return {1, 0}
        """;

    /// <summary>Segmented counters in one hash. ARGV: permitLimit, windowMs, segments, permitCount.</summary>
    public const string SlidingWindow = """
        local t = redis.call('TIME')
        local now = tonumber(t[1]) * 1000 + math.floor(tonumber(t[2]) / 1000)
        local limit, window, segments, n = tonumber(ARGV[1]), tonumber(ARGV[2]), tonumber(ARGV[3]), tonumber(ARGV[4])
        local seg = math.floor(window / segments)
        local current = math.floor(now / seg)
        local oldest = current - segments + 1
        local fields = redis.call('HGETALL', KEYS[1])
        local total, earliest, stale = 0, nil, {}
        for i = 1, #fields, 2 do
          local idx, c = tonumber(fields[i]), tonumber(fields[i + 1])
          if idx < oldest then stale[#stale + 1] = fields[i]
          else total = total + c; if earliest == nil or idx < earliest then earliest = idx end end
        end
        if (n == 0 and total >= limit) or total + n > limit then
          if earliest == nil then return {0, seg} end
          return {0, (earliest + segments) * seg - now}
        end
        if n > 0 then
          if #stale > 0 then redis.call('HDEL', KEYS[1], unpack(stale)) end
          redis.call('HINCRBY', KEYS[1], current, n)
          redis.call('PEXPIRE', KEYS[1], window + seg)
        end
        return {1, 0}
        """;

    /// <summary>
    /// Token bucket replenished in whole periods, like the framework's. ARGV: tokenLimit,
    /// tokensPerPeriod, periodMs, permitCount. Writes only when it spends.
    /// </summary>
    public const string TokenBucket = """
        local t = redis.call('TIME')
        local now = tonumber(t[1]) * 1000 + math.floor(tonumber(t[2]) / 1000)
        local cap, rate, period, n = tonumber(ARGV[1]), tonumber(ARGV[2]), tonumber(ARGV[3]), tonumber(ARGV[4])
        local h = redis.call('HMGET', KEYS[1], 'tokens', 'ts')
        local tokens, ts = tonumber(h[1]) or cap, tonumber(h[2]) or now
        local periods = math.floor((now - ts) / period)
        if periods > 0 then tokens = math.min(cap, tokens + periods * rate); ts = ts + periods * period end
        local need = math.max(n, 1)
        if tokens < need then
          return {0, math.ceil((need - tokens) / rate) * period - (now - ts)}
        end
        if n > 0 then
          redis.call('HSET', KEYS[1], 'tokens', tokens - n, 'ts', ts)
          redis.call('PEXPIRE', KEYS[1], math.ceil(cap / rate) * period + period)
        end
        return {1, 0}
        """;

    /// <summary>
    /// In-flight permits in a sorted set scored by expiry, so a dead pod's slot frees itself. ARGV:
    /// permitLimit, leaseTimeoutMs, leaseId, permitCount. The key expires with its latest member, since
    /// pods may have different lease timeouts.
    /// </summary>
    public const string ConcurrencyAcquire = """
        local t = redis.call('TIME')
        local now = tonumber(t[1]) * 1000 + math.floor(tonumber(t[2]) / 1000)
        local limit, timeout, n = tonumber(ARGV[1]), tonumber(ARGV[2]), tonumber(ARGV[4])
        redis.call('ZREMRANGEBYSCORE', KEYS[1], '-inf', now)
        local count = redis.call('ZCARD', KEYS[1])
        if (n == 0 and count >= limit) or count + n > limit then return {0, 0} end
        for i = 1, n do redis.call('ZADD', KEYS[1], now + timeout, ARGV[3] .. ':' .. i) end
        if n > 0 then
          local last = redis.call('ZRANGE', KEYS[1], -1, -1, 'WITHSCORES')
          redis.call('PEXPIREAT', KEYS[1], math.ceil(tonumber(last[2])))
        end
        return {1, 0}
        """;

    /// <summary>Extends a held lease's members that still exist (<c>XX</c>). ARGV: leaseTimeoutMs, members.</summary>
    public const string ConcurrencyRenew = """
        local t = redis.call('TIME')
        local now = tonumber(t[1]) * 1000 + math.floor(tonumber(t[2]) / 1000)
        local timeout = tonumber(ARGV[1])
        local renewed = 0
        for i = 2, #ARGV do renewed = renewed + redis.call('ZADD', KEYS[1], 'XX', 'CH', now + timeout, ARGV[i]) end
        if renewed > 0 then
          local last = redis.call('ZRANGE', KEYS[1], -1, -1, 'WITHSCORES')
          redis.call('PEXPIREAT', KEYS[1], math.ceil(tonumber(last[2])))
        end
        return renewed
        """;
}
