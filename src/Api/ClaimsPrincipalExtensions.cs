namespace RideSharing.Api;

using System.Security.Claims;

public static class ClaimsPrincipalExtensions
{
    public static string GetUserId(this ClaimsPrincipal principal)
    {
        var userId = principal.FindFirstValue(ClaimTypes.NameIdentifier)
            ?? principal.FindFirstValue("sub");
        if (string.IsNullOrWhiteSpace(userId))
            throw new UnauthorizedAccessException("User ID claim not found in token.");
        return userId;
    }
}