using MediatR;
using Medytao.Domain.Interfaces;
using Medytao.Shared.Models;

namespace Medytao.Application.Meditations.Queries;

public record GetMeditationByIdQuery(Guid Id) : IRequest<MeditationDetailDto>;

public class GetMeditationByIdHandler(IMeditationRepository repo, IStorageService storage)
    : IRequestHandler<GetMeditationByIdQuery, MeditationDetailDto>
{
    public async Task<MeditationDetailDto> Handle(GetMeditationByIdQuery query, CancellationToken ct)
    {
        var m = await repo.GetByIdAsync(query.Id, ct)
            ?? throw new KeyNotFoundException($"Meditation {query.Id} not found.");

        return new MeditationDetailDto(
            m.Id, m.Title, m.Description, m.DurationMs, m.Status.ToString(), m.CreatedAt,
            m.Layers.OrderBy(l => l.Type).Select(l => new LayerDto(
                l.Id, l.Type.ToString(), l.Volume, l.Muted,
                l.Tracks.OrderBy(t => t.Order).Select(t => new TrackDto(
                    t.Id, t.Order, t.Volume, t.LoopCount,
                    t.FadeInMs, t.FadeOutMs, t.StartOffsetMs, t.CrossfadeMs,
                    t.PlaybackRate, t.ReverbMix, t.StartAtMs,
                    new AssetDto(
                        t.Asset.Id, t.Asset.FileName, t.Asset.ContentType,
                        t.Asset.SizeBytes, t.Asset.DurationMs,
                        t.Asset.LayerType.ToString(),
                        IsShared: t.Asset.IsShared || t.Asset.OwnerId is null,
                        IsMine: false,
                        t.Asset.Tags,
                        storage.GetPublicUrl(t.Asset.BlobKey)
                    )
                ))
            )),
            m.CategoryId,
            m.Category?.Name
        );
    }
}

public record GetMeditationsByAuthorQuery(Guid AuthorId) : IRequest<IEnumerable<MeditationSummaryDto>>;

public class GetMeditationsByAuthorHandler(IMeditationRepository repo)
    : IRequestHandler<GetMeditationsByAuthorQuery, IEnumerable<MeditationSummaryDto>>
{
    public async Task<IEnumerable<MeditationSummaryDto>> Handle(GetMeditationsByAuthorQuery query, CancellationToken ct)
    {
        var meditations = await repo.GetByAuthorAsync(query.AuthorId, ct);
        return meditations.Select(m => new MeditationSummaryDto(
            m.Id, m.Title, m.Description, m.DurationMs, m.Status.ToString(), m.CreatedAt,
            m.Layers.ToDictionary(l => l.Type.ToString(), l => l.Tracks.Count),
            m.CategoryId,
            m.Category?.Name));
    }
}
