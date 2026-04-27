using MediatR;
using Medytao.Application.Layers.Commands;

namespace Medytao.Api.Endpoints;

public static class LayerEndpoints
{
    public static void MapLayerEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/layers").WithTags("Layers");

        // ── Layer ─────────────────────────────────────────────────────────────

        group.MapPut("/{layerId:guid}", async (Guid layerId, UpdateLayerRequest req, IMediator mediator) =>
        {
            var result = await mediator.Send(new UpdateLayerCommand(layerId, req.Volume, req.Muted));
            return Results.Ok(result);
        });

        // ── Tracks ────────────────────────────────────────────────────────────

        group.MapPost("/{layerId:guid}/tracks", async (Guid layerId, AddTrackRequest req, IMediator mediator) =>
        {
            var result = await mediator.Send(new AddTrackCommand(
                layerId, req.AssetId,
                req.Volume, req.LoopCount,
                req.FadeInMs, req.FadeOutMs,
                req.StartOffsetMs, req.CrossfadeMs,
                req.PlaybackRate));
            return Results.Created($"/api/v1/layers/{layerId}/tracks/{result.Id}", result);
        });

        group.MapPut("/{layerId:guid}/tracks/{trackId:guid}", async (Guid trackId, UpdateTrackRequest req, IMediator mediator) =>
        {
            var result = await mediator.Send(new UpdateTrackCommand(
                trackId, req.Volume, req.LoopCount,
                req.FadeInMs, req.FadeOutMs,
                req.StartOffsetMs, req.CrossfadeMs,
                req.PlaybackRate));
            return Results.Ok(result);
        });

        group.MapDelete("/{layerId:guid}/tracks/{trackId:guid}", async (Guid trackId, IMediator mediator) =>
        {
            await mediator.Send(new RemoveTrackCommand(trackId));
            return Results.NoContent();
        });

        group.MapPut("/{layerId:guid}/tracks/reorder", async (Guid layerId, ReorderTracksRequest req, IMediator mediator) =>
        {
            await mediator.Send(new ReorderTracksCommand(layerId, req.OrderedTrackIds));
            return Results.NoContent();
        });
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
    float PlaybackRate = 1f);
public record UpdateTrackRequest(
    float Volume,
    int LoopCount,
    int FadeInMs,
    int FadeOutMs,
    int StartOffsetMs,
    int CrossfadeMs,
    float PlaybackRate);
public record ReorderTracksRequest(IEnumerable<Guid> OrderedTrackIds);