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

        group.MapGet("/", async (ClaimsPrincipal user, IAssetRepository assetRepo, IStorageService storage) =>
        {
            var assets = await assetRepo.GetByOwnerAsync(user.GetUserId());
            var dtos = assets.Select(a => new AssetDto(
                a.Id, a.FileName, a.ContentType, a.SizeBytes,
                a.DurationMs, a.Type.ToString(), a.Tags,
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
            var assetType = request.Form.TryGetValue("type", out var typeStr)
                && Enum.TryParse<AssetType>(typeStr, true, out var parsed)
                ? parsed : AssetType.Audio;

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
                assetType,
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
