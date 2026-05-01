using MediatR;
using Medytao.Domain.Enums;
using Medytao.Domain.Interfaces;
using Medytao.Shared.Models;

namespace Medytao.Application.Meditations.Queries;

// UserId + UserRole w query — handler sprawdza visibility:
//   - Właściciel widzi zawsze (każdy Status, każdy MinRoleRequired).
//   - Inny user widzi tylko gdy Status == Published AND MinRoleRequired <= role.
//   - Reszta → KeyNotFound (świadomie 404 zamiast 403, żeby nie wyciekać
//     informacji o istnieniu prywatnej sesji).
public record GetMeditationByIdQuery(Guid Id, Guid UserId, UserRole UserRole) : IRequest<MeditationDetailDto>;

public class GetMeditationByIdHandler(IMeditationRepository repo, IStorageService storage)
    : IRequestHandler<GetMeditationByIdQuery, MeditationDetailDto>
{
    public async Task<MeditationDetailDto> Handle(GetMeditationByIdQuery query, CancellationToken ct)
    {
        var m = await repo.GetByIdAsync(query.Id, ct)
            ?? throw new KeyNotFoundException($"Meditation {query.Id} not found.");

        var isOwner = m.AuthorId == query.UserId;
        var isPubliclyVisible = m.Status == MeditationStatus.Published
            && m.MinRoleRequired <= query.UserRole;
        if (!isOwner && !isPubliclyVisible)
            throw new KeyNotFoundException($"Meditation {query.Id} not found.");

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

// Library query — wszystkie sesje opublikowane przez innych userów, dla
// których bieżący user spełnia wymóg roli. UserRole jako parametr (nie
// pobierane z claim wewnątrz handlera) — handler nie ma dostępu do
// HttpContext, rola pochodzi z endpoint-a (ClaimsPrincipal.GetUserRole).
public record GetLibraryQuery(Guid UserId, UserRole UserRole) : IRequest<IEnumerable<MeditationSummaryDto>>;

public class GetLibraryHandler(IMeditationRepository repo)
    : IRequestHandler<GetLibraryQuery, IEnumerable<MeditationSummaryDto>>
{
    public async Task<IEnumerable<MeditationSummaryDto>> Handle(GetLibraryQuery query, CancellationToken ct)
    {
        var meditations = await repo.GetLibraryAsync(query.UserId, query.UserRole, ct);
        return meditations.Select(m => new MeditationSummaryDto(
            m.Id, m.Title, m.Description, m.DurationMs, m.Status.ToString(), m.CreatedAt,
            m.Layers.ToDictionary(l => l.Type.ToString(), l => l.Tracks.Count),
            m.CategoryId,
            m.Category?.Name));
    }
}
