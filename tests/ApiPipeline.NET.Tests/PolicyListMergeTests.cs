using FluentAssertions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Options;

namespace ApiPipeline.NET.Tests;

/// <summary>
/// Configuration merges lists by position, not by name. An environment file whose
/// <c>Policies</c> list differs from the base file's does not replace it: entry 0 of the later file
/// renames entry 0 of the earlier one and inherits every key it leaves out, and the earlier file's
/// other entries stay in effect. A policy renamed by position fails startup.
/// </summary>
public sealed class PolicyListMergeTests
{
    private static readonly Dictionary<string, string?> Base = new()
    {
        ["ApiPipeline:RateLimiting:Enabled"] = "true",
        ["ApiPipeline:RateLimiting:DefaultPolicy"] = "strict",
        ["ApiPipeline:RateLimiting:Policies:0:Name"] = "strict",
        ["ApiPipeline:RateLimiting:Policies:0:PermitLimit"] = "20",
        ["ApiPipeline:RateLimiting:Policies:0:Window"] = "00:01:00",
        ["ApiPipeline:RateLimiting:Policies:1:Name"] = "permissive",
        ["ApiPipeline:RateLimiting:Policies:1:PermitLimit"] = "200",
        ["ApiPipeline:RateLimiting:Policies:1:Window"] = "00:01:00",
    };

    private static ApiPipelineRateLimitingOptions Bind(Dictionary<string, string?> environment)
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(Base)
            .AddInMemoryCollection(environment)
            .Build();
        var services = new ServiceCollection().AddLogging();
        services.AddApiPipelineRateLimiting(configuration);
        using var provider = services.BuildServiceProvider();
        return provider.GetRequiredService<IOptions<ApiPipelineRateLimitingOptions>>().Value;
    }

    /// <summary>
    /// The trap: Production lists one policy of its own, which actually renames <c>strict</c> and
    /// leaves <c>permissive</c> running. The message names both sources and both names.
    /// </summary>
    [Fact]
    public void PolicyRenamedByPosition_FailsStartup()
    {
        var act = () => Bind(new()
        {
            ["ApiPipeline:RateLimiting:DefaultPolicy"] = "per-user-burst",
            ["ApiPipeline:RateLimiting:Policies:0:Name"] = "per-user-burst",
            ["ApiPipeline:RateLimiting:Policies:0:PermitLimit"] = "100",
        });

        act.Should().Throw<OptionsValidationException>()
            .WithMessage("*ApiPipeline:RateLimiting:Policies:0*'strict'*'per-user-burst'*by position*");
    }

    /// <summary>Overriding a policy's values under its own name, at its own position, is the intended use.</summary>
    [Fact]
    public void SameNamesInTheSameOrder_Pass()
    {
        var options = Bind(new()
        {
            ["ApiPipeline:RateLimiting:Policies:0:Name"] = "strict",
            ["ApiPipeline:RateLimiting:Policies:0:PermitLimit"] = "100",
        });

        options.Policies.Select(p => (p.Name, p.PermitLimit))
            .Should().Equal(("strict", 100), ("permissive", 200));
    }

    /// <summary>Names compare ignoring case, as policy lookups do.</summary>
    [Fact]
    public void SameNameInAnotherCase_Passes()
    {
        var act = () => Bind(new() { ["ApiPipeline:RateLimiting:Policies:0:Name"] = "STRICT" });

        act.Should().NotThrow();
    }
}
