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

            var assets = await assetRepo.GetVisibleForUserAsync(user.GetUserId(), parsedLayer);
            var dtos = assets.Select(a => new AssetDto(
                a.Id, a.FileName, a.ContentType, a.SizeBytes,
                a.DurationMs, a.LayerType.ToString(),
                a.OwnerId is null, a.Tags,
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

        group.MapDelete("/{id:guid}", async (Guid id, ClaimsPrincipal user, IMediator mediator) =>
        {
            await mediator.Send(new DeleteAssetCommand(id, user.GetUserId()));
            return Results.NoContent();
        });
    }
}
