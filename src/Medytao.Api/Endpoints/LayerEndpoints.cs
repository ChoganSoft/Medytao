using System.Security.Claims;
using MediatR;
using Medytao.Application.Layers.Commands;
using Medytao.Domain.Enums;

namespace Medytao.Api.Endpoints;

public static class LayerEndpoints
{
    public static void MapLayerEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/layers").WithTags("Layers");

        // Wszystkie operacje modyfikujące warstwy/tracki wymagają Master+
        // (Free i Apprentice nie tworzą sesji). Time-anchored (StartAtMs != null)
        // dodatkowo wymaga Guru — sprawdzane per-endpoint w lambda, bo to zależy
        // od body requestu, a policy pracuje na claimach a nie na payloadzie.

        // ── Layer ─────────────────────────────────────────────────────────────

        group.MapPut("/{layerId:guid}", async (Guid layerId, UpdateLayerRequest req, IMediator mediator) =>
        {
            var result = await mediator.Send(new UpdateLayerCommand(layerId, req.Volume, req.Muted));
            return Results.Ok(result);
        }).RequireAuthorization("RequireMaster");

        // ── Tracks ────────────────────────────────────────────────────────────

        group.MapPost("/{layerId:guid}/tracks", async (Guid layerId, AddTrackRequest req, ClaimsPrincipal user, IMediator mediator) =>
        {
            // StartAtMs != null oznacza tryb time-anchored — to feature Guru.
            // Master może tworzyć tracki tylko sekwencyjne (StartAtMs == null).
            if (req.StartAtMs.HasValue && !user.IsAtLeast(UserRole.Guru))
                return Results.Forbid();

            var result = await mediator.Send(new AddTrackCommand(
                layerId, req.AssetId,
                req.Volume, req.LoopCount,
                req.FadeInMs, req.FadeOutMs,
                req.StartOffsetMs, req.CrossfadeMs,
                req.PlaybackRate, req.ReverbMix,
                req.StartAtMs));
            return Results.Created($"/api/v1/layers/{layerId}/tracks/{result.Id}", result);
        }).RequireAuthorization("RequireMaster");

        group.MapPut("/{layerId:guid}/tracks/{trackId:guid}", async (Guid trackId, UpdateTrackRequest req, ClaimsPrincipal user, IMediator mediator) =>
        {
            // Patrz wyżej — Master nie może ustawiać time-anchored.
            if (req.StartAtMs.HasValue && !user.IsAtLeast(UserRole.Guru))
                return Results.Forbid();

            var result = await mediator.Send(new UpdateTrackCommand(
                trackId, req.Volume, req.LoopCount,
                req.FadeInMs, req.FadeOutMs,
                req.StartOffsetMs, req.CrossfadeMs,
                req.PlaybackRate, req.ReverbMix,
                req.StartAtMs));
            return Results.Ok(result);
        }).RequireAuthorization("RequireMaster");

        group.MapDelete("/{layerId:guid}/tracks/{trackId:guid}", async (Guid trackId, IMediator mediator) =>
        {
            await mediator.Send(new RemoveTrackCommand(trackId));
            return Results.NoContent();
        }).RequireAuthorization("RequireMaster");

        group.MapPut("/{layerId:guid}/tracks/reorder", async (Guid layerId, ReorderTracksRequest req, IMediator mediator) =>
        {
            await mediator.Send(new ReorderTracksCommand(layerId, req.OrderedTrackIds));
            return Results.NoContent();
        }).RequireAuthorization("RequireMaster");
    }
}

public record UpdateLayerRequest(float Volume, bool Muted);
public record AddTrackRequest(
    Guid AssetId,
    float Volume = 1f,
    int LoopCount = 1,
    int FadeInMs = 0,
    int FadeOutMs = 0,
    int StartOffsetMs = 0,
    int CrossfadeMs = 0,
    float PlaybackRate = 1f,
    float ReverbMix = 0f,
    int? StartAtMs = null);
public record UpdateTrackRequest(
    float Volume,
    int LoopCount,
    int FadeInMs,
    int FadeOutMs,
    int StartOffsetMs,
    int CrossfadeMs,
    float PlaybackRate,
    float ReverbMix,
    int? StartAtMs);
public record ReorderTracksRequest(IEnumerable<Guid> OrderedTrackIds);