using System.Security.Claims;
using System.Text.Encodings.Web;
using Microsoft.AspNetCore.Authentication;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace ApiPipeline.NET.Tests;

/// <summary>
/// A minimal authentication handler used in tests.
/// Returns a successful authentication result when the request carries
/// <c>Authorization: Bearer valid-token</c>, and <see cref="AuthenticateResult.NoResult"/>
/// otherwise, causing authorization to issue a 401 challenge.
/// </summary>
internal sealed class TestAuthHandler : AuthenticationHandler<AuthenticationSchemeOptions>
{
    /// <summary>
    /// Initializes a new instance of <see cref="TestAuthHandler"/>.
    /// </summary>
    public TestAuthHandler(
        IOptionsMonitor<AuthenticationSchemeOptions> options,
        ILoggerFactory logger,
        UrlEncoder encoder)
        : base(options, logger, encoder)
    {
    }

    /// <inheritdoc />
    protected override Task<AuthenticateResult> HandleAuthenticateAsync()
    {
        // Succeed only when the caller presents the well-known test bearer token.
        if (Request.Headers.Authorization.ToString() == "Bearer valid-token")
        {
            var identity = new ClaimsIdentity(
                [new Claim(ClaimTypes.Name, "test-user")],
                Scheme.Name);
            var ticket = new AuthenticationTicket(new System.Security.Principal.GenericPrincipal(identity, null), Scheme.Name);
            return Task.FromResult(AuthenticateResult.Success(ticket));
        }

        // No token — treat as unauthenticated so authorization issues a 401 challenge.
        return Task.FromResult(AuthenticateResult.NoResult());
    }
}
