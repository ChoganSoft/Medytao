using MediatR;
using Medytao.Domain.Entities;
using Medytao.Domain.Interfaces;
using Medytao.Shared.Models;

namespace Medytao.Application.Meditations.Commands;

// ── Create ────────────────────────────────────────────────────────────────────
// ProgramId jest wymagane — medytacja MUSI należeć do programu (decyzja
// domenowa: program to "folder", orphaned medytacje nie mają prawa bytu).
// Handler weryfikuje, że program istnieje i należy do tego samego usera,
// potem przypina medytację do programu po dodaniu.
public record CreateMeditationCommand(Guid AuthorId, Guid ProgramId, string Title, string? Description)
    : IRequest<MeditationSummaryDto>;

public class CreateMeditationHandler(
    IMeditationRepository repo,
    IProgramRepository programRepo,
    IUnitOfWork uow)
    : IRequestHandler<CreateMeditationCommand, MeditationSummaryDto>
{
    public async Task<MeditationSummaryDto> Handle(CreateMeditationCommand cmd, CancellationToken ct)
    {
        var program = await programRepo.GetByIdAsync(cmd.ProgramId, ct)
            ?? throw new KeyNotFoundException($"Program {cmd.ProgramId} not found.");

        if (program.OwnerId != cmd.AuthorId)
            throw new UnauthorizedAccessException("Program does not belong to the current user.");

        var meditation = Meditation.Create(cmd.AuthorId, cmd.Title, cmd.Description);
        program.Meditations.Add(meditation);

        await repo.AddAsync(meditation, ct);
        await uow.SaveChangesAsync(ct);
        return meditation.ToSummaryDto();
    }
}

// ── Update ─────────────────────────────────────────────────────────────────────
public record UpdateMeditationCommand(Guid Id, string Title, string? Description, int DurationMs) : IRequest<MeditationSummaryDto>;

public class UpdateMeditationHandler(IMeditationRepository repo, IUnitOfWork uow)
    : IRequestHandler<UpdateMeditationCommand, MeditationSummaryDto>
{
    public async Task<MeditationSummaryDto> Handle(UpdateMeditationCommand cmd, CancellationToken ct)
    {
        var meditation = await repo.GetByIdAsync(cmd.Id, ct)
            ?? throw new KeyNotFoundException($"Meditation {cmd.Id} not found.");

        meditation.Title = cmd.Title;
        meditation.Description = cmd.Description;
        meditation.DurationMs = cmd.DurationMs;
        meditation.UpdatedAt = DateTimeOffset.UtcNow;

        await repo.UpdateAsync(meditation, ct);
        await uow.SaveChangesAsync(ct);
        return meditation.ToSummaryDto();
    }
}

// ── Delete ─────────────────────────────────────────────────────────────────────
public record DeleteMeditationCommand(Guid Id) : IRequest;

public class DeleteMeditationHandler(IMeditationRepository repo, IUnitOfWork uow)
    : IRequestHandler<DeleteMeditationCommand>
{
    public async Task Handle(DeleteMeditationCommand cmd, CancellationToken ct)
    {
        await repo.DeleteAsync(cmd.Id, ct);
        await uow.SaveChangesAsync(ct);
    }
}

// ── Publish ────────────────────────────────────────────────────────────────────
public record PublishMeditationCommand(Guid Id) : IRequest<MeditationSummaryDto>;

public class PublishMeditationHandler(IMeditationRepository repo, IUnitOfWork uow)
    : IRequestHandler<PublishMeditationCommand, MeditationSummaryDto>
{
    public async Task<MeditationSummaryDto> Handle(PublishMeditationCommand cmd, CancellationToken ct)
    {
        var meditation = await repo.GetByIdAsync(cmd.Id, ct)
            ?? throw new KeyNotFoundException($"Meditation {cmd.Id} not found.");

        meditation.Status = Domain.Enums.MeditationStatus.Published;
        meditation.UpdatedAt = DateTimeOffset.UtcNow;

        await repo.UpdateAsync(meditation, ct);
        await uow.SaveChangesAsync(ct);
        return meditation.ToSummaryDto();
    }
}

// ── Mapping helpers ────────────────────────────────────────────────────────────
internal static class MeditationMappings
{
    public static MeditationSummaryDto ToSummaryDto(this Meditation m) => new(
        m.Id, m.Title, m.Description, m.DurationMs, m.Status.ToString(), m.CreatedAt,
        // Encja Meditation zawsze ma 4 warstwy seed'owane w Create. Tracki w
        // Update/Publish są załadowane przez repo.GetByIdAsync z Include, a
        // w Create są pustymi listami — Count zwraca 0.
        m.Layers.ToDictionary(l => l.Type.ToString(), l => l.Tracks.Count));
}
