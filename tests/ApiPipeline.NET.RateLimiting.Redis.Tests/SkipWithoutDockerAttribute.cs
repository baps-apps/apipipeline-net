using System.Reflection;
using Xunit.v3;

namespace ApiPipeline.NET.RateLimiting.Redis.Tests;

/// <summary>
/// Skips a test, with <see cref="RedisFixture.UnavailableReason"/>, when the assembly's Redis
/// container could not start because Docker is unavailable. Applied to the whole assembly.
/// </summary>
[AttributeUsage(AttributeTargets.Assembly)]
public sealed class SkipWithoutDockerAttribute : BeforeAfterTestAttribute
{
    /// <inheritdoc />
    public override void Before(MethodInfo methodUnderTest, IXunitTest test)
    {
        if (RedisFixture.UnavailableReason is { } reason)
        {
            Assert.Skip(reason);
        }
    }
}
