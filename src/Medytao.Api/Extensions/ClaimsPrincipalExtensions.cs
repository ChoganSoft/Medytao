using System.Security.Claims;

namespace Medytao.Api.Endpoints;

public static class ClaimsPrincipalExtensions
{
    public static Guid GetUserId(this ClaimsPrincipal user)
    {
        var id = user.FindFirstValue(ClaimTypes.NameIdentifier)
            ?? throw new UnauthorizedAccessException("User ID claim not found.");
        return Guid.Parse(id);
    }
}
