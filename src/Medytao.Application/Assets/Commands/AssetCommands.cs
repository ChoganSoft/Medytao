using MediatR;
using Medytao.Domain.Entities;
using Medytao.Domain.Enums;
using Medytao.Domain.Interfaces;
using Medytao.Shared.Models;

namespace Medytao.Application.Assets.Commands;

// ── Upload asset ───────────────────────────────────────────────────────────────
// LayerType decyduje, którą podklasę Asset (Music/Nature/Text/Fx) tworzymy.
// Zwykły user uploaduje tylko dla siebie (OwnerId = jego id); globalne zasoby
// seedujemy admin-flow albo bezpośrednim INSERT-em — endpoint nie wystawia
// flagi "IsGlobal" celowo, żeby nie dało się przez przypadek zasypać biblioteki.
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

        return new AssetDto(
            asset.Id, asset.FileName, asset.ContentType,
            asset.SizeBytes, asset.DurationMs,
            asset.LayerType.ToString(),
            asset.OwnerId is null,
            asset.Tags,
            storage.GetPublicUrl(blobKey)
        );
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

        // Globalne zasoby (OwnerId == null) nie należą do nikogo — zwykły user
        // ich nie kasuje. Prywatne kasuje tylko właściciel.
        if (asset.OwnerId is null)
            throw new UnauthorizedAccessException("Global assets cannot be deleted by users.");

        if (asset.OwnerId != cmd.RequesterId)
            throw new UnauthorizedAccessException("You do not own this asset.");

        await storage.DeleteAsync(asset.BlobKey, ct);
        await assetRepo.DeleteAsync(cmd.AssetId, ct);
        await uow.SaveChangesAsync(ct);
    }
}
