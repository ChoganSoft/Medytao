using System.Security.Claims;
using MediatR;
using Medytao.Application.Programs.Commands;
using Medytao.Application.Programs.Queries;

namespace Medytao.Api.Endpoints;

public static class ProgramEndpoints
{
    public static void MapProgramEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/programs").WithTags("Programs");

        group.MapGet("/", async (ClaimsPrincipal user, IMediator mediator) =>
        {
            var ownerId = user.GetUserId();
            var result = await mediator.Send(new GetProgramsByOwnerQuery(ownerId));
            return Results.Ok(result);
        });

        group.MapGet("/{id:guid}", async (Guid id, IMediator mediator) =>
        {
            var result = await mediator.Send(new GetProgramByIdQuery(id));
            return Results.Ok(result);
        });

        group.MapPost("/", async (CreateProgramRequest req, ClaimsPrincipal user, IMediator mediator) =>
        {
            var result = await mediator.Send(new CreateProgramCommand(user.GetUserId(), req.Name, req.Description));
            return Results.Created($"/api/v1/programs/{result.Id}", result);
        });

        group.MapPut("/{id:guid}", async (Guid id, UpdateProgramRequest req, IMediator mediator) =>
        {
            var result = await mediator.Send(new UpdateProgramCommand(id, req.Name, req.Description));
            return Results.Ok(result);
        });

        group.MapDelete("/{id:guid}", async (Guid id, IMediator mediator) =>
        {
            await mediator.Send(new DeleteProgramCommand(id));
            return Results.NoContent();
        });
    }
}

public record CreateProgramRequest(string Name, string? Description);
public record UpdateProgramRequest(string Name, string? Description);
