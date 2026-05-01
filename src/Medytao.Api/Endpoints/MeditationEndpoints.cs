using System.Security.Claims;
using MediatR;
using Medytao.Application.Meditations.Commands;
using Medytao.Application.Meditations.Queries;
using Medytao.Domain.Enums;

namespace Medytao.Api.Endpoints;

public static class MeditationEndpoints
{
    // Tolerancyjny parser dla MinRoleRequired w body PublishRequest.
    // Brak/niepoprawna wartość → Free (najmniej restrykcyjna), bezpieczne
    // domyślne dla "publikuj bez konfiguracji". Case-insensitive.
    private static UserRole ParseRoleOrFree(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return UserRole.Free;
        return Enum.TryParse<UserRole>(raw, ignoreCase: true, out var role)
            ? role
            : UserRole.Free;
    }

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

        // Publish — body przyjmuje MinRoleRequired (string nazwy enum). Brak body
        // lub niepoprawna wartość → fallback Free (najmniej restrykcyjna widoczność,
        // bezpieczna dla "publikuj bez konfiguracji"). Powtórne publish na już-Published
        // sesji zmienia tylko MinRoleRequired (nie potrzeba unpublish/publish ping-pong).
        group.MapPost("/{id:guid}/publish", async (Guid id, PublishRequest? req, ClaimsPrincipal user, IMediator mediator) =>
        {
            var minRole = ParseRoleOrFree(req?.MinRoleRequired);
            var result = await mediator.Send(new PublishMeditationCommand(id, user.GetUserId(), minRole));
            return Results.Ok(result);
        }).RequireAuthorization("RequireMaster");

        // Unpublish — przywraca Status=Draft, sesja znika z biblioteki.
        // Bez body, bez parametrów (poza id i userem z JWT).
        group.MapPost("/{id:guid}/unpublish", async (Guid id, ClaimsPrincipal user, IMediator mediator) =>
        {
            var result = await mediator.Send(new UnpublishMeditationCommand(id, user.GetUserId()));
            return Results.Ok(result);
        }).RequireAuthorization("RequireMaster");

        // Duplicate — zwraca DTO nowej medytacji, frontend odświeża listę po wywołaniu.
        // 404 gdy source nie istnieje, 401 gdy źródło jest cudzą Draft / brak roli
        // (handler rzuca UnauthorizedAccessException → middleware zamapuje na 401).
        // UserRole z JWT przekazujemy do handlera — duplikowanie cudzej Published
        // sesji wymaga roli >= source.MinRoleRequired (te same warunki co library).
        group.MapPost("/{id:guid}/duplicate", async (Guid id, ClaimsPrincipal user, IMediator mediator) =>
        {
            var result = await mediator.Send(new DuplicateMeditationCommand(id, user.GetUserId(), user.GetUserRole()));
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
public record PublishRequest(string? MinRoleRequired);
