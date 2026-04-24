using System.Security.Claims;
using MediatR;
using Medytao.Application.Categories.Commands;
using Medytao.Application.Categories.Queries;

namespace Medytao.Api.Endpoints;

public static class CategoryEndpoints
{
    public static void MapCategoryEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/categories").WithTags("Categories");

        // Lista kategorii zalogowanego usera — posortowana po nazwie (sort w
        // repo), z licznikiem medytacji per kategoria.
        group.MapGet("/", async (ClaimsPrincipal user, IMediator mediator) =>
        {
            var ownerId = user.GetUserId();
            var result = await mediator.Send(new GetCategoriesByOwnerQuery(ownerId));
            return Results.Ok(result);
        });

        // Tworzenie — konflikt nazwy w obrębie usera rzuca
        // InvalidOperationException, mapujemy na 409 Conflict. Pusta nazwa →
        // 400 Bad Request (ArgumentException). Global handler łapie i tak,
        // ale tu dajemy jawne Results.Conflict dla lepszej narracji.
        group.MapPost("/", async (CreateCategoryRequest req, ClaimsPrincipal user, IMediator mediator) =>
        {
            try
            {
                var result = await mediator.Send(new CreateCategoryCommand(user.GetUserId(), req.Name));
                return Results.Created($"/api/v1/categories/{result.Id}", result);
            }
            catch (InvalidOperationException ex)
            {
                return Results.Conflict(ex.Message);
            }
            catch (ArgumentException ex)
            {
                return Results.BadRequest(ex.Message);
            }
        });

        group.MapDelete("/{id:guid}", async (Guid id, IMediator mediator) =>
        {
            await mediator.Send(new DeleteCategoryCommand(id));
            return Results.NoContent();
        });
    }
}

public record CreateCategoryRequest(string Name);
