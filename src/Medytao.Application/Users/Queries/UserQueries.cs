using MediatR;
using Medytao.Domain.Interfaces;
using Medytao.Shared.Models;

namespace Medytao.Application.Users.Queries;

// Lista wszystkich userów dla panelu /users. Endpoint wymaga roli Admin
// (RequireAdmin policy) — handler nie powtarza authorization, bo nie ma
// dostępu do ClaimsPrincipal (lista jest taka sama dla każdego Admina).
public record GetAllUsersQuery : IRequest<IEnumerable<UserDto>>;

public class GetAllUsersHandler(IUserRepository repo)
    : IRequestHandler<GetAllUsersQuery, IEnumerable<UserDto>>
{
    public async Task<IEnumerable<UserDto>> Handle(GetAllUsersQuery query, CancellationToken ct)
    {
        var users = await repo.GetAllAsync(ct);
        return users.Select(u => new UserDto(
            u.Id,
            u.Email,
            u.DisplayName,
            u.Role.ToString(),
            u.CreatedAt));
    }
}
