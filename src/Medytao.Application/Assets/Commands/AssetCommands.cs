using MediatR;
using Medytao.Domain.Entities;
using Medytao.Domain.Enums;
using Medytao.Domain.Interfaces;
using Medytao.Shared.Models;

namespace Medytao.Application.Assets.Commands;

// ── Upload asset ───────────────────────────────────────────────────────────────
// LayerType decyduje, którą podklasę Asset (Music/Nature/Text/Fx) tworzymy.
// Zwykły user uploaduje tylko dla siebie (OwnerId = jego id, IsShared = false).
// Wystawienie zasobu innym idzie osobnym command-em SetAssetSharingCommand,
// żeby świeży upload nie trafiał od razu do publicznej biblioteki przez
// przypadek (user musi świadomie kliknąć toggle).
public record UploadAssetCommand(
    Guid OwnerId,
    Stream FileStream,
    string FileName,
    string ContentType,
    long SizeBytes,
    int? DurationMs,
    LayerType LayerType,
    string? Tags
) : IRequest<AssetDto>;

public class UploadAssetHandler(IAssetRepository assetRepo, IStorageService storage, IUnitOfWork uow)
    : IRequestHandler<UploadAssetCommand, AssetDto>
{
    public async Task<AssetDto> Handle(UploadAssetCommand cmd, CancellationToken ct)
    {
        var blobKey = await storage.UploadAsync(cmd.FileStream, cmd.FileName, cmd.ContentType, ct);

        // Tworzymy konkretną podklasę zamiast bazowego Asset — TPH wymaga, żeby
        // EF wiedział z jakim typem ma do czynienia (dyskryminator LayerType
        // jest ustawiany w konstruktorze podklasy, więc i tak by się zgadzał,
        // ale `new Asset(...)` nie skompiluje się — Asset jest abstract).
        Asset asset = cmd.LayerType switch
        {
            LayerType.Music => new MusicAsset(),
            LayerType.Nature => new NatureAsset(),
            LayerType.Text => new TextAsset(),
            LayerType.Fx => new FxAsset(),
            _ => throw new ArgumentOutOfRangeException(nameof(cmd.LayerType), cmd.LayerType, "Unknown layer type.")
        };

        asset.OwnerId = cmd.OwnerId;
        asset.FileName = cmd.FileName;
        asset.BlobKey = blobKey;
        asset.ContentType = cmd.ContentType;
        asset.SizeBytes = cmd.SizeBytes;
        asset.DurationMs = cmd.DurationMs;
        asset.Tags = cmd.Tags;

        await assetRepo.AddAsync(asset, ct);
        await uow.SaveChangesAsync(ct);

        // Świeży upload: IsShared = false (asset.IsShared default), IsMine = true
        // (autor to wgrywający user). Toggle w UI pozwoli mu zmienić IsShared.
        return new AssetDto(
            asset.Id, asset.FileName, asset.ContentType,
            asset.SizeBytes, asset.DurationMs,
            asset.LayerType.ToString(),
            IsShared: asset.IsShared || asset.OwnerId is null,
            IsMine: true,
            asset.Tags,
            storage.GetPublicUrl(blobKey)
        );
    }
}

// ── Toggle asset sharing ───────────────────────────────────────────────────────
// Zmienia flagę IsShared zasobu. Tylko autor (OwnerId == RequesterId) może
// to zrobić. Seedów systemowych (OwnerId == null) nikt nie przełącza.
public record SetAssetSharingCommand(Guid AssetId, Guid RequesterId, bool IsShared) : IRequest<AssetDto>;

public class SetAssetSharingHandler(IAssetRepository assetRepo, IStorageService storage, IUnitOfWork uow)
    : IRequestHandler<SetAssetSharingCommand, AssetDto>
{
    public async Task<AssetDto> Handle(SetAssetSharingCommand cmd, CancellationToken ct)
    {
        var asset = await assetRepo.GetByIdAsync(cmd.AssetId, ct)
            ?? throw new KeyNotFoundException($"Asset {cmd.AssetId} not found.");

        if (asset.OwnerId is null)
            throw new UnauthorizedAccessException("System assets cannot be reshared by users.");

        if (asset.OwnerId != cmd.RequesterId)
            throw new UnauthorizedAccessException("You can only change sharing of your own assets.");

        asset.IsShared = cmd.IsShared;
        asset.UpdatedAt = DateTimeOffset.UtcNow;
        await uow.SaveChangesAsync(ct);

        return new AssetDto(
            asset.Id, asset.FileName, asset.ContentType,
            asset.SizeBytes, asset.DurationMs,
            asset.LayerType.ToString(),
            IsShared: asset.IsShared,
            IsMine: true,
            asset.Tags,
            storage.GetPublicUrl(asset.BlobKey)
        );
    }
}

// ── Set asset duration ─────────────────────────────────────────────────────────
// Wywoływane idempotentnie z klienta po wykryciu durationMs (lazy-fetch przy
// starcie sesji albo auto-detect przy uploadzie). Zapisanie wartości w bazie
// pozwala kolejnym sesjom uniknąć round-tripa przez ensureDurations — duration
// trafia do DTO bezpośrednio. No-op jeśli wartość już ustawiona LUB jeśli nowa
// jest <= 0 (sanity).
//
// Brak walidacji ownership: durationMs to fakt o pliku, nie własność. Każdy
// kto może odtworzyć asset (np. globalne lub shared) może też wykryć jego
// długość — nic to nie wnosi w kategorii security a oszczędza złożoności.
public record SetAssetDurationCommand(Guid AssetId, int DurationMs) : IRequest;

public class SetAssetDurationHandler(IAssetRepository assetRepo, IUnitOfWork uow)
    : IRequestHandler<SetAssetDurationCommand>
{
    public async Task Handle(SetAssetDurationCommand cmd, CancellationToken ct)
    {
        if (cmd.DurationMs <= 0) return;
        var asset = await assetRepo.GetByIdAsync(cmd.AssetId, ct);
        if (asset is null) return; // gone — JS callback po refresh, nie warto rzucać
        if (asset.DurationMs.HasValue) return; // już zapisane przez ktoś innego, nie nadpisujemy
        asset.DurationMs = cmd.DurationMs;
        asset.UpdatedAt = DateTimeOffset.UtcNow;
        await uow.SaveChangesAsync(ct);
    }
}

// ── Delete asset ───────────────────────────────────────────────────────────────
public record DeleteAssetCommand(Guid AssetId, Guid RequesterId) : IRequest;

public class DeleteAssetHandler(IAssetRepository assetRepo, IStorageService storage, IUnitOfWork uow)
    : IRequestHandler<DeleteAssetCommand>
{
    public async Task Handle(DeleteAssetCommand cmd, CancellationToken ct)
    {
        var asset = await assetRepo.GetByIdAsync(cmd.AssetId, ct)
            ?? throw new KeyNotFoundException($"Asset {cmd.AssetId} not found.");

        // Systemowe zasoby (OwnerId == null) nie należą do nikogo — zwykły user
        // ich nie kasuje. Prywatne i shared własne kasuje tylko autor.
        if (asset.OwnerId is null)
            throw new UnauthorizedAccessException("System assets cannot be deleted by users.");

        if (asset.OwnerId != cmd.RequesterId)
            throw new UnauthorizedAccessException("You do not own this asset.");

        await storage.DeleteAsync(asset.BlobKey, ct);
        await assetRepo.DeleteAsync(cmd.AssetId, ct);
        await uow.SaveChangesAsync(ct);
    }
}
