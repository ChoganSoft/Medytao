using MediatR;
using Medytao.Domain.Entities;
using Medytao.Domain.Interfaces;
using Medytao.Shared.Models;

namespace Medytao.Application.Meditations.Commands;

// ── Create ────────────────────────────────────────────────────────────────────
// ProgramId jest wymagane — medytacja MUSI należeć do programu (decyzja
// domenowa: program to "folder", orphaned medytacje nie mają prawa bytu).
// CategoryId opcjonalne — UI wymusza wybór przy tworzeniu nowej medytacji,
// ale legacy endpointy mogą pominąć. Handler weryfikuje, że program i
// (jeśli podana) kategoria należą do tego samego usera.
public record CreateMeditationCommand(Guid AuthorId, Guid ProgramId, string Title, string? Description, Guid? CategoryId)
    : IRequest<MeditationSummaryDto>;

public class CreateMeditationHandler(
    IMeditationRepository repo,
    IProgramRepository programRepo,
    ICategoryRepository categoryRepo,
    IUnitOfWork uow)
    : IRequestHandler<CreateMeditationCommand, MeditationSummaryDto>
{
    public async Task<MeditationSummaryDto> Handle(CreateMeditationCommand cmd, CancellationToken ct)
    {
        var program = await programRepo.GetByIdAsync(cmd.ProgramId, ct)
            ?? throw new KeyNotFoundException($"Program {cmd.ProgramId} not found.");

        if (program.OwnerId != cmd.AuthorId)
            throw new UnauthorizedAccessException("Program does not belong to the current user.");

        // Nazwa kategorii dla DTO — ładujemy tylko jeśli CategoryId podane,
        // żeby nie robić zbędnego round-tripu przy create bez kategorii.
        string? categoryName = null;
        if (cmd.CategoryId is { } categoryId)
        {
            var category = await categoryRepo.GetByIdAsync(categoryId, ct)
                ?? throw new KeyNotFoundException($"Category {categoryId} not found.");
            if (category.OwnerId != cmd.AuthorId)
                throw new UnauthorizedAccessException("Category does not belong to the current user.");
            categoryName = category.Name;
        }

        var meditation = Meditation.Create(cmd.AuthorId, cmd.Title, cmd.Description, cmd.CategoryId);
        program.Meditations.Add(meditation);

        await repo.AddAsync(meditation, ct);
        await uow.SaveChangesAsync(ct);

        // Na tym etapie meditation.Category nie jest załadowany (Include nie
        // był w AddAsync), ale CategoryName już znamy z wcześniejszego
        // GetByIdAsync — przekazujemy jawnie.
        return meditation.ToSummaryDto(categoryName);
    }
}

// ── Update ─────────────────────────────────────────────────────────────────────
// AuthorId domknięty w command — bez tego handler nie miał z czego sprawdzić
// własności i edycja cudzej medytacji była technicznie możliwa (klasyczne
// IDOR). Endpoint dorzuca user.GetUserId() przy mapowaniu requestu.
public record UpdateMeditationCommand(Guid Id, Guid AuthorId, string Title, string? Description, int DurationMs) : IRequest<MeditationSummaryDto>;

public class UpdateMeditationHandler(IMeditationRepository repo, IUnitOfWork uow)
    : IRequestHandler<UpdateMeditationCommand, MeditationSummaryDto>
{
    public async Task<MeditationSummaryDto> Handle(UpdateMeditationCommand cmd, CancellationToken ct)
    {
        var meditation = await repo.GetByIdAsync(cmd.Id, ct)
            ?? throw new KeyNotFoundException($"Meditation {cmd.Id} not found.");

        if (meditation.AuthorId != cmd.AuthorId)
            throw new UnauthorizedAccessException("Cannot update someone else's meditation.");

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
    // categoryNameOverride: dla świeżo utworzonych medytacji nav `Category`
    // nie jest załadowany (Include był w repo.GetByIdAsync, nie w AddAsync),
    // więc handler Create przekazuje nazwę ręcznie. Dla Update/Publish nav
    // jest załadowany (GetByIdAsync włącza .Include(m => m.Category)).
    public static MeditationSummaryDto ToSummaryDto(this Meditation m, string? categoryNameOverride = null) => new(
        m.Id, m.Title, m.Description, m.DurationMs, m.Status.ToString(), m.CreatedAt,
        // Encja Meditation zawsze ma 4 warstwy seed'owane w Create. Tracki w
        // Update/Publish są załadowane przez repo.GetByIdAsync z Include, a
        // w Create są pustymi listami — Count zwraca 0.
        m.Layers.ToDictionary(l => l.Type.ToString(), l => l.Tracks.Count),
        m.CategoryId,
        categoryNameOverride ?? m.Category?.Name);
}
