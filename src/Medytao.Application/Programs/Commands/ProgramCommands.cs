using MediatR;
using Medytao.Domain.Entities;
using Medytao.Domain.Interfaces;
using Medytao.Shared.Models;

namespace Medytao.Application.Programs.Commands;

// ── Create ────────────────────────────────────────────────────────────────────
public record CreateProgramCommand(Guid OwnerId, string Name, string? Description) : IRequest<ProgramSummaryDto>;

public class CreateProgramHandler(IProgramRepository repo, IUnitOfWork uow)
    : IRequestHandler<CreateProgramCommand, ProgramSummaryDto>
{
    public async Task<ProgramSummaryDto> Handle(CreateProgramCommand cmd, CancellationToken ct)
    {
        var program = MeditationProgram.Create(cmd.OwnerId, cmd.Name, cmd.Description);
        await repo.AddAsync(program, ct);
        await uow.SaveChangesAsync(ct);
        return program.ToSummaryDto();
    }
}

// ── Update ────────────────────────────────────────────────────────────────────
public record UpdateProgramCommand(Guid Id, string Name, string? Description) : IRequest<ProgramSummaryDto>;

public class UpdateProgramHandler(IProgramRepository repo, IUnitOfWork uow)
    : IRequestHandler<UpdateProgramCommand, ProgramSummaryDto>
{
    public async Task<ProgramSummaryDto> Handle(UpdateProgramCommand cmd, CancellationToken ct)
    {
        var program = await repo.GetByIdAsync(cmd.Id, ct)
            ?? throw new KeyNotFoundException($"Program {cmd.Id} not found.");

        program.Name = cmd.Name;
        program.Description = cmd.Description;
        program.UpdatedAt = DateTimeOffset.UtcNow;

        await repo.UpdateAsync(program, ct);
        await uow.SaveChangesAsync(ct);
        return program.ToSummaryDto();
    }
}

// ── Delete ────────────────────────────────────────────────────────────────────
// Usunięcie programu kasuje wiersze w tabeli join (to robi EF sam, bo usuwamy
// encję z kolekcją `Meditations` załadowaną i EF widzi, że trzeba pousuwać
// powiązania). Dodatkowo sprawdzamy każdą z medytacji, które były w programie
// — jeśli po usunięciu nie należy już do żadnego innego programu, kasujemy
// też medytację (orphan cleanup). Założenie domenowe: medytacja MUSI należeć
// do min. 1 programu, więc "sama" nie ma prawa bytu.
public record DeleteProgramCommand(Guid Id) : IRequest;

public class DeleteProgramHandler(
    IProgramRepository programRepo,
    IMeditationRepository medRepo,
    IUnitOfWork uow)
    : IRequestHandler<DeleteProgramCommand>
{
    public async Task Handle(DeleteProgramCommand cmd, CancellationToken ct)
    {
        var program = await programRepo.GetByIdAsync(cmd.Id, ct)
            ?? throw new KeyNotFoundException($"Program {cmd.Id} not found.");

        // Snapshot listy medytacji przed usunięciem (po Remove kolekcja może
        // się zmienić przy track changes).
        var meditationIds = program.Meditations.Select(m => m.Id).ToList();

        await programRepo.DeleteAsync(cmd.Id, ct);
        await uow.SaveChangesAsync(ct);

        if (meditationIds.Count == 0) return;

        // Jedno zapytanie o wszystkie pozostałe programy usera — zamiast
        // pętli N×GetByOwnerAsync. Medytacja jest orphanem, jeśli jej Id
        // nie pojawia się w żadnym z tych programów.
        var remainingPrograms = await programRepo.GetByOwnerAsync(program.OwnerId, ct);
        var stillReferencedIds = remainingPrograms
            .SelectMany(p => p.Meditations.Select(m => m.Id))
            .ToHashSet();

        var orphanIds = meditationIds.Where(id => !stillReferencedIds.Contains(id)).ToList();
        foreach (var orphanId in orphanIds)
        {
            await medRepo.DeleteAsync(orphanId, ct);
        }

        if (orphanIds.Count > 0)
        {
            await uow.SaveChangesAsync(ct);
        }
    }
}

// ── Add meditation to program ─────────────────────────────────────────────────
// Używane np. kiedy user przerzuca medytację między programami albo kopiuje
// ją do dodatkowego programu. W iteracji 1 nie mamy UI do tego, ale command
// jest prosty, więc dokładam od razu — przyda się przy seedzie/migracji.
public record AddMeditationToProgramCommand(Guid ProgramId, Guid MeditationId) : IRequest;

public class AddMeditationToProgramHandler(
    IProgramRepository programRepo,
    IMeditationRepository medRepo,
    IUnitOfWork uow)
    : IRequestHandler<AddMeditationToProgramCommand>
{
    public async Task Handle(AddMeditationToProgramCommand cmd, CancellationToken ct)
    {
        var program = await programRepo.GetByIdAsync(cmd.ProgramId, ct)
            ?? throw new KeyNotFoundException($"Program {cmd.ProgramId} not found.");

        var meditation = await medRepo.GetByIdAsync(cmd.MeditationId, ct)
            ?? throw new KeyNotFoundException($"Meditation {cmd.MeditationId} not found.");

        if (!program.Meditations.Any(m => m.Id == meditation.Id))
        {
            program.Meditations.Add(meditation);
            await programRepo.UpdateAsync(program, ct);
            await uow.SaveChangesAsync(ct);
        }
    }
}

// ── Mapping helpers ───────────────────────────────────────────────────────────
internal static class ProgramMappings
{
    public static ProgramSummaryDto ToSummaryDto(this MeditationProgram p) => new(
        p.Id, p.Name, p.Description, p.Meditations?.Count ?? 0, p.CreatedAt);
}
