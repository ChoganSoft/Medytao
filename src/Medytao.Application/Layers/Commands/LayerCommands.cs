using MediatR;
using Medytao.Domain.Entities;
using Medytao.Domain.Interfaces;
using Medytao.Shared.Models;

namespace Medytao.Application.Layers.Commands;

// ── Update layer master volume / mute ─────────────────────────────────────────
public record UpdateLayerCommand(Guid LayerId, float Volume, bool Muted) : IRequest<LayerDto>;

public class UpdateLayerHandler(ILayerRepository repo, IUnitOfWork uow, IStorageService storage)
    : IRequestHandler<UpdateLayerCommand, LayerDto>
{
    public async Task<LayerDto> Handle(UpdateLayerCommand cmd, CancellationToken ct)
    {
        var layer = await repo.GetByIdAsync(cmd.LayerId, ct)
            ?? throw new KeyNotFoundException($"Layer {cmd.LayerId} not found.");

        layer.Volume = cmd.Volume;
        layer.Muted = cmd.Muted;
        layer.UpdatedAt = DateTimeOffset.UtcNow;

        await repo.UpdateAsync(layer, ct);
        await uow.SaveChangesAsync(ct);
        return layer.ToDto(storage);
    }
}

// ── Add track to layer ─────────────────────────────────────────────────────────
public record AddTrackCommand(
    Guid LayerId, Guid AssetId,
    float Volume = 1f, int LoopCount = 1,
    int FadeInMs = 0, int FadeOutMs = 0,
    int StartOffsetMs = 0, int CrossfadeMs = 0
) : IRequest<TrackDto>;

public class AddTrackHandler(ILayerRepository layerRepo, ITrackRepository trackRepo, IAssetRepository assetRepo, IUnitOfWork uow, IStorageService storage)
    : IRequestHandler<AddTrackCommand, TrackDto>
{
    public async Task<TrackDto> Handle(AddTrackCommand cmd, CancellationToken ct)
    {
        var layer = await layerRepo.GetByIdAsync(cmd.LayerId, ct)
            ?? throw new KeyNotFoundException($"Layer {cmd.LayerId} not found.");

        var asset = await assetRepo.GetByIdAsync(cmd.AssetId, ct)
            ?? throw new KeyNotFoundException($"Asset {cmd.AssetId} not found.");

        // Asset musi pasować do warstwy — np. nie wkładamy MusicAsset do Text-warstwy.
        // Picker w UI i tak filtruje po LayerType, ale walidacja po stronie command
        // chroni przed podszytą requestem ID-zasobu z innej warstwy.
        if (asset.LayerType != layer.Type)
            throw new InvalidOperationException(
                $"Asset belongs to layer '{asset.LayerType}', cannot add to layer '{layer.Type}'.");

        var nextOrder = layer.Tracks.Any() ? layer.Tracks.Max(t => t.Order) + 1 : 0;

        var track = new Track
        {
            LayerId = cmd.LayerId,
            AssetId = cmd.AssetId,
            Order = nextOrder,
            Volume = cmd.Volume,
            LoopCount = cmd.LoopCount,
            FadeInMs = cmd.FadeInMs,
            FadeOutMs = cmd.FadeOutMs,
            StartOffsetMs = cmd.StartOffsetMs,
            CrossfadeMs = cmd.CrossfadeMs,
            Asset = asset
        };

        await trackRepo.AddAsync(track, ct);
        await uow.SaveChangesAsync(ct);
        return track.ToDto(storage);
    }
}

// ── Update track ───────────────────────────────────────────────────────────────
public record UpdateTrackCommand(
    Guid TrackId, float Volume, int LoopCount,
    int FadeInMs, int FadeOutMs, int StartOffsetMs, int CrossfadeMs
) : IRequest<TrackDto>;

public class UpdateTrackHandler(ITrackRepository repo, IUnitOfWork uow, IStorageService storage)
    : IRequestHandler<UpdateTrackCommand, TrackDto>
{
    public async Task<TrackDto> Handle(UpdateTrackCommand cmd, CancellationToken ct)
    {
        var track = await repo.GetByIdAsync(cmd.TrackId, ct)
            ?? throw new KeyNotFoundException($"Track {cmd.TrackId} not found.");

        track.Volume = cmd.Volume;
        track.LoopCount = cmd.LoopCount;
        track.FadeInMs = cmd.FadeInMs;
        track.FadeOutMs = cmd.FadeOutMs;
        track.StartOffsetMs = cmd.StartOffsetMs;
        track.CrossfadeMs = cmd.CrossfadeMs;
        track.UpdatedAt = DateTimeOffset.UtcNow;

        await repo.UpdateAsync(track, ct);
        await uow.SaveChangesAsync(ct);
        return track.ToDto(storage);
    }
}

// ── Remove track ───────────────────────────────────────────────────────────────
public record RemoveTrackCommand(Guid TrackId) : IRequest;

public class RemoveTrackHandler(ITrackRepository repo, IUnitOfWork uow)
    : IRequestHandler<RemoveTrackCommand>
{
    public async Task Handle(RemoveTrackCommand cmd, CancellationToken ct)
    {
        await repo.DeleteAsync(cmd.TrackId, ct);
        await uow.SaveChangesAsync(ct);
    }
}

// ── Reorder tracks ─────────────────────────────────────────────────────────────
public record ReorderTracksCommand(Guid LayerId, IEnumerable<Guid> OrderedTrackIds) : IRequest;

public class ReorderTracksHandler(ITrackRepository repo, IUnitOfWork uow)
    : IRequestHandler<ReorderTracksCommand>
{
    public async Task Handle(ReorderTracksCommand cmd, CancellationToken ct)
    {
        await repo.ReorderAsync(cmd.LayerId, cmd.OrderedTrackIds, ct);
        await uow.SaveChangesAsync(ct);
    }
}

// ── Mapping helpers ────────────────────────────────────────────────────────────
internal static class LayerMappings
{
    public static LayerDto ToDto(this Domain.Entities.Layer l, IStorageService storage) => new(
        l.Id, l.Type.ToString(), l.Volume, l.Muted,
        l.Tracks.OrderBy(t => t.Order).Select(t => t.ToDto(storage)));

    public static TrackDto ToDto(this Track t, IStorageService storage) => new(
        t.Id, t.Order, t.Volume, t.LoopCount,
        t.FadeInMs, t.FadeOutMs, t.StartOffsetMs, t.CrossfadeMs,
        // Tu DTO trafia w karty tracków w edytorze — toggle/kosz nie są
        // pokazywane, więc IsMine ustawiamy zachowawczo na false. IsShared
        // odzwierciedla rzeczywisty stan, na wypadek gdyby UI miał kiedyś
        // pokazywać badge "Shared" obok track-card.
        new AssetDto(
            t.Asset.Id, t.Asset.FileName, t.Asset.ContentType,
            t.Asset.SizeBytes, t.Asset.DurationMs,
            t.Asset.LayerType.ToString(),
            IsShared: t.Asset.IsShared || t.Asset.OwnerId is null,
            IsMine: false,
            t.Asset.Tags,
            storage.GetPublicUrl(t.Asset.BlobKey)
        ));
}