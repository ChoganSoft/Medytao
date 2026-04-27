using System.Security.Claims;
using MediatR;
using Medytao.Application.Assets.Commands;
using Medytao.Domain.Enums;
using Medytao.Domain.Interfaces;
using Medytao.Shared.Models;

namespace Medytao.Api.Endpoints;

public static class AssetEndpoints
{
    public static void MapAssetEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/assets").WithTags("Assets");

        // GET /assets?layerType=Music — listuje zasoby widoczne dla zalogowanego
        // usera w danej warstwie: jego własne plus globalne (OwnerId IS NULL).
        // LayerType jest wymagany — UI zarządzania assetami i picker zawsze
        // operują w kontekście jednej warstwy.
        group.MapGet("/", async (
            string layerType,
            ClaimsPrincipal user,
            IAssetRepository assetRepo,
            IStorageService storage) =>
        {
            if (!Enum.TryParse<LayerType>(layerType, ignoreCase: true, out var parsedLayer))
                return Results.BadRequest($"Unknown layer type '{layerType}'.");

            var userId = user.GetUserId();
            var assets = await assetRepo.GetVisibleForUserAsync(userId, parsedLayer);
            var dtos = assets.Select(a => new AssetDto(
                a.Id, a.FileName, a.ContentType, a.SizeBytes,
                a.DurationMs, a.LayerType.ToString(),
                IsShared: a.IsShared || a.OwnerId is null,
                IsMine: a.OwnerId == userId,
                a.Tags,
                storage.GetPublicUrl(a.BlobKey)));
            return Results.Ok(dtos);
        });

        group.MapPost("/upload", async (
            HttpRequest request,
            ClaimsPrincipal user,
            IMediator mediator) =>
        {
            if (!request.HasFormContentType || !request.Form.Files.Any())
                return Results.BadRequest("No file provided.");

            var file = request.Form.Files[0];

            // LayerType jest obowiązkowy w formie — bez niego nie wiemy, którą
            // podklasę Asset utworzyć (MusicAsset/NatureAsset/TextAsset/FxAsset).
            if (!request.Form.TryGetValue("layerType", out var layerTypeStr)
                || !Enum.TryParse<LayerType>(layerTypeStr, ignoreCase: true, out var layerType))
            {
                return Results.BadRequest("Missing or invalid 'layerType' form field.");
            }

            var tags = request.Form.TryGetValue("tags", out var t) ? t.ToString() : null;
            var duration = request.Form.TryGetValue("durationMs", out var d)
                && int.TryParse(d, out var ms) ? ms : (int?)null;

            await using var stream = file.OpenReadStream();

            var result = await mediator.Send(new UploadAssetCommand(
                user.GetUserId(),
                stream,
                file.FileName,
                file.ContentType ?? "application/octet-stream",
                file.Length,
                duration,
                layerType,
                tags));

            return Results.Created($"/api/v1/assets/{result.Id}", result);
        }).DisableAntiforgery();

        // PATCH /assets/{id}/sharing — autor zmienia widoczność swojego zasobu.
        // Body: { "isShared": true|false }. Zwraca zaktualizowany AssetDto, żeby
        // UI mogło bez refetcha podmienić wpis na liście.
        group.MapPatch("/{id:guid}/sharing", async (
            Guid id,
            SetSharingRequest body,
            ClaimsPrincipal user,
            IMediator mediator) =>
        {
            var dto = await mediator.Send(new SetAssetSharingCommand(id, user.GetUserId(), body.IsShared));
            return Results.Ok(dto);
        });

        group.MapDelete("/{id:guid}", async (Guid id, ClaimsPrincipal user, IMediator mediator) =>
        {
            await mediator.Send(new DeleteAssetCommand(id, user.GetUserId()));
            return Results.NoContent();
        });
    }

    // Lokalny request DTO — body PATCH-a. Nie idzie do Shared, bo to detal
    // protokołu HTTP konkretnego endpointu, nie kontrakt domenowy.
    private record SetSharingRequest(bool IsShared);
}
