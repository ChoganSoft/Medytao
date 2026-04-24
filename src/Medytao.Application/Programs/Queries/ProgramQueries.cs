using MediatR;
using Medytao.Application.Programs.Commands;
using Medytao.Domain.Interfaces;
using Medytao.Shared.Models;

namespace Medytao.Application.Programs.Queries;

// ── Lista programów usera ─────────────────────────────────────────────────────
public record GetProgramsByOwnerQuery(Guid OwnerId) : IRequest<IEnumerable<ProgramSummaryDto>>;

public class GetProgramsByOwnerHandler(IProgramRepository repo)
    : IRequestHandler<GetProgramsByOwnerQuery, IEnumerable<ProgramSummaryDto>>
{
    public async Task<IEnumerable<ProgramSummaryDto>> Handle(GetProgramsByOwnerQuery q, CancellationToken ct)
    {
        // Mapowanie leci przez ToSummaryDto() z Commands/ProgramCommands.cs —
        // jeden punkt prawdy dla posortowanej listy tytułów medytacji, zamiast
        // duplikować logikę w każdym handlerze.
        var programs = await repo.GetByOwnerAsync(q.OwnerId, ct);
        return programs.Select(p => p.ToSummaryDto());
    }
}

// ── Szczegóły programu + jego medytacje ───────────────────────────────────────
public record GetProgramByIdQuery(Guid Id) : IRequest<ProgramDetailDto>;

public class GetProgramByIdHandler(IProgramRepository repo)
    : IRequestHandler<GetProgramByIdQuery, ProgramDetailDto>
{
    public async Task<ProgramDetailDto> Handle(GetProgramByIdQuery q, CancellationToken ct)
    {
        var p = await repo.GetByIdAsync(q.Id, ct)
            ?? throw new KeyNotFoundException($"Program {q.Id} not found.");

        // Medytacje sortujemy tak samo jak w GetMeditationsByAuthor — UpdatedAt
        // desc, żeby user widział ostatnio edytowane najwyżej.
        var meds = p.Meditations
            .OrderByDescending(m => m.UpdatedAt)
            .Select(m => new MeditationSummaryDto(
                m.Id, m.Title, m.Description, m.DurationMs, m.Status.ToString(), m.CreatedAt,
                m.Layers.ToDictionary(l => l.Type.ToString(), l => l.Tracks.Count)));

        return new ProgramDetailDto(p.Id, p.Name, p.Description, p.CreatedAt, meds);
    }
}
