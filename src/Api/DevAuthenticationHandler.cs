using Microsoft.AspNetCore.Authentication;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using System.Security.Claims;
using System.Text.Encodings.Web;

namespace RideSharing.Api;

/// <summary>
/// Development-only authentication handler for DisableAuthentication: true.
/// Allows any request through with a dev user identity.
/// DO NOT USE IN PRODUCTION.
/// </summary>
public class DevAuthenticationHandler : AuthenticationHandler<DevAuthenticationSchemeOptions>
{
    public DevAuthenticationHandler(
        IOptionsMonitor<DevAuthenticationSchemeOptions> options,
        ILoggerFactory logger,
        UrlEncoder encoder) : base(options, logger, encoder)
    {
    }

    protected override Task<AuthenticateResult> HandleAuthenticateAsync()
    {
        // Create dev claims
        var claims = new[]
        {
            new Claim(ClaimTypes.NameIdentifier, "dev-user-001"),
            new Claim(ClaimTypes.Name, "Development User"),
            new Claim(ClaimTypes.Role, "Admin"),
            new Claim("sub", "dev-user-001")
        };

        var identity = new ClaimsIdentity(claims, Scheme.Name);
        var principal = new ClaimsPrincipal(identity);
        var ticket = new AuthenticationTicket(principal, Scheme.Name);

        return Task.FromResult(AuthenticateResult.Success(ticket));
    }
}

public class DevAuthenticationSchemeOptions : AuthenticationSchemeOptions
{
}
