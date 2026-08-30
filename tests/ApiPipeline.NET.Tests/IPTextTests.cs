using FluentAssertions;

namespace ApiPipeline.NET.Tests;

/// <summary>
/// The strict parser behind every configured trust and probe list. The framework's own parser reads
/// shorthand, octal and hex IPv4 as other addresses; these must be rejected, and only these.
/// </summary>
public sealed class IPTextTests
{
    [Theory]
    [InlineData("10.0.10.5", "10.0.10.5")]
    [InlineData(" 10.0.10.5 ", "10.0.10.5")]
    [InlineData("0.0.0.0", "0.0.0.0")]
    [InlineData("255.255.255.255", "255.255.255.255")]
    [InlineData("fd00::5", "fd00::5")]
    [InlineData("::1", "::1")]
    [InlineData("::ffff:10.0.0.1", "::ffff:10.0.0.1")]
    public void Address_Valid_IsParsed(string text, string expected)
    {
        IPText.TryParseAddress(text, out var address).Should().BeTrue();
        address!.ToString().Should().Be(expected);
    }

    [Theory]
    [InlineData("10.0.10")]       // 10.0.0.10
    [InlineData("10")]            // 0.0.0.10
    [InlineData("010.0.0.1")]     // octal: 8.0.0.1
    [InlineData("0x0A.0.0.1")]    // hex: 10.0.0.1
    [InlineData("10.0.0.256")]
    [InlineData("10.0.0.0/8")]
    [InlineData("not-an-ip")]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData(null)]
    public void Address_InvalidOrAmbiguous_IsRejected(string? text) =>
        IPText.TryParseAddress(text, out _).Should().BeFalse();

    [Theory]
    [InlineData("10.0.0.0/8", "10.0.0.0/8")]
    [InlineData(" 10.0.0.0/8 ", "10.0.0.0/8")]
    [InlineData("10.1.2.3/8", "10.0.0.0/8")]      // host bits masked, as the framework does
    [InlineData("0.0.0.0/0", "0.0.0.0/0")]
    [InlineData("fd00::/8", "fd00::/8")]
    public void Network_Valid_IsParsed(string text, string expected)
    {
        IPText.TryParseNetwork(text, out var network).Should().BeTrue();
        network.ToString().Should().Be(expected);
    }

    [Theory]
    [InlineData("10/8")]          // 0.0.0.0/8
    [InlineData("10.0/16")]       // 10.0.0.0/16 by luck; still shorthand
    [InlineData("010.0.0.0/8")]   // octal: 8.0.0.0/8
    [InlineData("10.0.0.0/33")]
    [InlineData("10.0.0.0/-1")]
    [InlineData("10.0.0.0")]
    [InlineData("/8")]
    [InlineData("")]
    [InlineData(null)]
    public void Network_InvalidOrAmbiguous_IsRejected(string? text) =>
        IPText.TryParseNetwork(text, out _).Should().BeFalse();
}
