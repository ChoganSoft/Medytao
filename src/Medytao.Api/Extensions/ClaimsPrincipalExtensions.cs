using System.Security.Claims;
using Medytao.Domain.Enums;

namespace Medytao.Api.Endpoints;

public static class ClaimsPrincipalExtensions
{
    public static Guid GetUserId(this ClaimsPrincipal user)
    {
        var id = user.FindFirstValue(ClaimTypes.NameIdentifier)
            ?? throw new UnauthorizedAccessException("User ID claim not found.");
        return Guid.Parse(id);
    }

    // Role z JWT-a: string z claim ClaimTypes.Role, parsowany z powrotem
    // do enuma. Brak claim albo nieznana wartość → fallback Free (najmniej
    // uprzywilejowana rola; jeśli token był poprawny ale nieczytelny, nie
    // promujemy usera przypadkowo).
    public static UserRole GetUserRole(this ClaimsPrincipal user)
    {
        var roleStr = user.FindFirstValue(ClaimTypes.Role);
        return Enum.TryParse<UserRole>(roleStr, ignoreCase: false, out var role)
            ? role
            : UserRole.Free;
    }

    // Hierarchical check — Master jest "co najmniej Apprentice", Guru jest
    // "co najmniej Master" itd. Standardowy [Authorize(Roles="Master,Guru")]
    // tego nie robi (każda rola listowana jawnie), więc trzymamy własną
    // logikę przez integer compare na enum.
    public static bool IsAtLeast(this ClaimsPrincipal user, UserRole min) =>
        user.GetUserRole() >= min;
}
