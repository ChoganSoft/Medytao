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

        // Library — sesje opublikowane przez innych userów, filtr po MinRoleRequired.
        // Każdy zalogowany user może czytać; visibility per-sesja jest egzekwowana
        // w handlerze (tylko Status==Published, MinRoleRequired<=user.Role, AuthorId!=user).
        // Endpoint świadomie przed /{id:guid}, żeby route matching nie próbował
        // sparsować "library" jako Guid.
        group.MapGet("/library", async (ClaimsPrincipal user, IMediator mediator) =>
        {
            var result = await mediator.Send(new GetLibraryQuery(user.GetUserId(), user.GetUserRole()));
            return Results.Ok(result);
        });

        group.MapGet("/{id:guid}", async (Guid id, ClaimsPrincipal user, IMediator mediator) =>
        {
            var result = await mediator.Send(new GetMeditationByIdQuery(id, user.GetUserId(), user.GetUserRole()));
            return Results.Ok(result);
        });

        // Tworzenie/edycja/duplikowanie/publikacja/usuwanie — wymaga roli Master+.
        // Free i Apprentice mogą tylko odsłuchiwać gotowe sesje (GET endpointy
        // powyżej zostają bez policy, dostępne dla każdego zalogowanego usera).
        group.MapPost("/", async (CreateMeditationRequest req, ClaimsPrincipal user, IMediator mediator) =>
        {
            var result = await mediator.Send(new CreateMeditationCommand(user.GetUserId(), req.ProgramId, req.Title, req.Description, req.CategoryId));
            return Results.Created($"/api/v1/meditations/{result.Id}", result);
        }).RequireAuthorization("RequireMaster");

        group.MapPut("/{id:guid}", async (Guid id, UpdateMeditationRequest req, ClaimsPrincipal user, IMediator mediator) =>
        {
            var result = await mediator.Send(new UpdateMeditationCommand(id, user.GetUserId(), req.Title, req.Description, req.DurationMs));
            return Results.Ok(result);
        }).RequireAuthorization("RequireMaster");

        group.MapPost("/{id:guid}/publish", async (Guid id, IMediator mediator) =>
        {
            var result = await mediator.Send(new PublishMeditationCommand(id));
            return Results.Ok(result);
        }).RequireAuthorization("RequireMaster");

        // Duplicate — zwraca DTO nowej medytacji, frontend odświeża listę po wywołaniu.
        // 404 gdy source nie istnieje, 401 gdy źródło należy do innego usera (handler
        // rzuca UnauthorizedAccessException → middleware zamapuje na 401).
        group.MapPost("/{id:guid}/duplicate", async (Guid id, ClaimsPrincipal user, IMediator mediator) =>
        {
            var result = await mediator.Send(new DuplicateMeditationCommand(id, user.GetUserId()));
            return result is null ? Results.NotFound() : Results.Created($"/api/v1/meditations/{result.Id}", result);
        }).RequireAuthorization("RequireMaster");

        group.MapDelete("/{id:guid}", async (Guid id, IMediator mediator) =>
        {
            await mediator.Send(new DeleteMeditationCommand(id));
            return Results.NoContent();
        }).RequireAuthorization("RequireMaster");
    }
}

public record CreateMeditationRequest(Guid ProgramId, string Title, string? Description, Guid? CategoryId);
public record UpdateMeditationRequest(string Title, string? Description, int DurationMs);
