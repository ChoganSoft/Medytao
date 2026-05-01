using MediatR;
using Medytao.Domain.Enums;
using Medytao.Domain.Interfaces;
using Medytao.Shared.Models;

namespace Medytao.Application.Users.Commands;

// Zmiana roli usera przez Admina. Walidacje:
//   - Caller (AdminId) musi istnieć i mieć rolę Admin (sprawdzane policy
//     na endpoincie + redundantnie tutaj — defense in depth).
//   - Target nie może być sam Caller (no self-demotion — gdyby ostatni
//     Admin chciał zostać Free, nikt by go nie odzyskał poza developerem
//     przez RoleSeed w appsettings).
//   - Target nie może być innym Adminem (anti-eskalacja w drugą stronę:
//     Admin nie demotuje innego Admina; chcąc to zrobić, developer
//     edytuje appsettings i RoleSeed zaktualizuje rolę na login).
//   - NewRole nie może być Admin (anti-eskalacja: tylko developer przez
//     RoleSeed nadaje rolę Admin; UI zatrzymuje się na Guru).
public record UpdateUserRoleCommand(Guid TargetUserId, Guid AdminId, UserRole NewRole)
    : IRequest<UserDto>;

public class UpdateUserRoleHandler(IUserRepository repo, IUnitOfWork uow)
    : IRequestHandler<UpdateUserRoleCommand, UserDto>
{
    public async Task<UserDto> Handle(UpdateUserRoleCommand cmd, CancellationToken ct)
    {
        if (cmd.NewRole == UserRole.Admin)
            throw new InvalidOperationException("Promotion to Admin is not allowed via API. Use RoleSeed in appsettings.");

        if (cmd.TargetUserId == cmd.AdminId)
            throw new InvalidOperationException("Cannot change your own role.");

        var admin = await repo.GetByIdAsync(cmd.AdminId, ct)
            ?? throw new UnauthorizedAccessException("Caller not found.");
        if (admin.Role != UserRole.Admin)
            throw new UnauthorizedAccessException("Only Admin can change user roles.");

        var target = await repo.GetByIdAsync(cmd.TargetUserId, ct)
            ?? throw new KeyNotFoundException($"User {cmd.TargetUserId} not found.");
        if (target.Role == UserRole.Admin)
            throw new InvalidOperationException("Cannot change another admin's role through the UI.");

        target.Role = cmd.NewRole;
        target.UpdatedAt = DateTimeOffset.UtcNow;

        await repo.UpdateAsync(target, ct);
        await uow.SaveChangesAsync(ct);

        return new UserDto(target.Id, target.Email, target.DisplayName, target.Role.ToString(), target.CreatedAt);
    }
}
