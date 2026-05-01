using System.Security.Claims;
using MediatR;
using Medytao.Application.Users.Commands;
using Medytao.Application.Users.Queries;
using Medytao.Domain.Enums;

namespace Medytao.Api.Endpoints;

public static class UserEndpoints
{
    public static void MapUserEndpoints(this IEndpointRouteBuilder app)
    {
        // Cała grupa wymaga roli Admin — zarządzanie userami nie jest dla
        // Guru/Master/Apprentice/Free. Każdy endpoint dziedziczy policy.
        var group = app.MapGroup("/users").WithTags("Users").RequireAuthorization("RequireAdmin");

        group.MapGet("/", async (IMediator mediator) =>
        {
            var result = await mediator.Send(new GetAllUsersQuery());
            return Results.Ok(result);
        });

        // PUT /users/{id}/role — body { role: "Free|Apprentice|Master|Guru" }.
        // 400 gdy nazwa roli nieparsowalna albo Admin (nie pozwalamy promować
        // przez API), 403 gdy self-demotion albo target jest Admin (handler
        // rzuca InvalidOperationException → 400).
        group.MapPut("/{id:guid}/role", async (Guid id, UpdateUserRoleRequest req, ClaimsPrincipal caller, IMediator mediator) =>
        {
            if (!Enum.TryParse<UserRole>(req.Role, ignoreCase: true, out var newRole))
                return Results.BadRequest($"Unknown role '{req.Role}'.");

            try
            {
                var dto = await mediator.Send(new UpdateUserRoleCommand(id, caller.GetUserId(), newRole));
                return Results.Ok(dto);
            }
            catch (InvalidOperationException ex)
            {
                return Results.BadRequest(ex.Message);
            }
        });
    }
}

public record UpdateUserRoleRequest(string Role);
