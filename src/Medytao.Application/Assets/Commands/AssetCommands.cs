using MediatR;
using Medytao.Domain.Entities;
using Medytao.Domain.Enums;
using Medytao.Domain.Interfaces;
using Medytao.Shared.Models;

namespace Medytao.Application.Assets.Commands;

// ── Upload asset ───────────────────────────────────────────────────────────────
public record UploadAssetCommand(
    Guid OwnerId,
    Stream FileStream,
    string FileName,
    string ContentType,
    long SizeBytes,
    int? DurationMs,
    AssetType Type,
    string? Tags
) : IRequest<AssetDto>;

public class UploadAssetHandler(IAssetRepository assetRepo, IStorageService storage, IUnitOfWork uow)
    : IRequestHandler<UploadAssetCommand, AssetDto>
{
    public async Task<AssetDto> Handle(UploadAssetCommand cmd, CancellationToken ct)
    {
        var blobKey = await storage.UploadAsync(cmd.FileStream, cmd.FileName, cmd.ContentType, ct);

        var asset = new Asset
        {
            OwnerId = cmd.OwnerId,
            FileName = cmd.FileName,
            BlobKey = blobKey,
            ContentType = cmd.ContentType,
            SizeBytes = cmd.SizeBytes,
            DurationMs = cmd.DurationMs,
            Type = cmd.Type,
            Tags = cmd.Tags
        };

        await assetRepo.AddAsync(asset, ct);
        await uow.SaveChangesAsync(ct);

        return new AssetDto(
            asset.Id, asset.FileName, asset.ContentType,
            asset.SizeBytes, asset.DurationMs,
            asset.Type.ToString(), asset.Tags,
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

        if (asset.OwnerId != cmd.RequesterId)
            throw new UnauthorizedAccessException("You do not own this asset.");

        await storage.DeleteAsync(asset.BlobKey, ct);
        await assetRepo.DeleteAsync(cmd.AssetId, ct);
        await uow.SaveChangesAsync(ct);
    }
}
