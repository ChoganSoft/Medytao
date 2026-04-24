using System.Security.Claims;
using MediatR;
using Medytao.Application.Meditations.Commands;
using Medytao.Application.Meditations.Queries;

namespace Medytao.Api.Endpoints;

public static class MeditationEndpoints
{
    public static void MapMeditationEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/meditations").WithTags("Meditations");

        group.MapGet("/", async (ClaimsPrincipal user, IMediator mediator) =>
        {
            var authorId = user.GetUserId();
            var result = await mediator.Send(new GetMeditationsByAuthorQuery(authorId));
            return Results.Ok(result);
        });

        group.MapGet("/{id:guid}", async (Guid id, IMediator mediator) =>
        {
            var result = await mediator.Send(new GetMeditationByIdQuery(id));
            return Results.Ok(result);
        });

        group.MapPost("/", async (CreateMeditationRequest req, ClaimsPrincipal user, IMediator mediator) =>
        {
            var result = await mediator.Send(new CreateMeditationCommand(user.GetUserId(), req.ProgramId, req.Title, req.Description));
            return Results.Created($"/api/v1/meditations/{result.Id}", result);
        });

        group.MapPut("/{id:guid}", async (Guid id, UpdateMeditationRequest req, IMediator mediator) =>
        {
            var result = await mediator.Send(new UpdateMeditationCommand(id, req.Title, req.Description, req.DurationMs));
            return Results.Ok(result);
        });

        group.MapPost("/{id:guid}/publish", async (Guid id, IMediator mediator) =>
        {
            var result = await mediator.Send(new PublishMeditationCommand(id));
            return Results.Ok(result);
        });

        group.MapDelete("/{id:guid}", async (Guid id, IMediator mediator) =>
        {
            await mediator.Send(new DeleteMeditationCommand(id));
            return Results.NoContent();
        });
    }
}

public record CreateMeditationRequest(Guid ProgramId, string Title, string? Description);
public record UpdateMeditationRequest(string Title, string? Description, int DurationMs);
