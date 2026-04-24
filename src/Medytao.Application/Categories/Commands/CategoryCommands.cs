using MediatR;
using Medytao.Domain.Entities;
using Medytao.Domain.Interfaces;
using Medytao.Shared.Models;

namespace Medytao.Application.Categories.Commands;

// ── Create ────────────────────────────────────────────────────────────────────
// Nazwa jest trymowana i walidowana na "nie pusta" + "nie duplikat u tego
// samego usera". Konflikt rzuca InvalidOperationException — front-end mapuje
// na komunikat w modalu.
public record CreateCategoryCommand(Guid OwnerId, string Name) : IRequest<CategorySummaryDto>;

public class CreateCategoryHandler(ICategoryRepository repo, IUnitOfWork uow)
    : IRequestHandler<CreateCategoryCommand, CategorySummaryDto>
{
    public async Task<CategorySummaryDto> Handle(CreateCategoryCommand cmd, CancellationToken ct)
    {
        var name = (cmd.Name ?? string.Empty).Trim();
        if (name.Length == 0)
            throw new ArgumentException("Category name cannot be empty.", nameof(cmd.Name));

        if (await repo.NameExistsAsync(cmd.OwnerId, name, ct))
            throw new InvalidOperationException($"Category '{name}' already exists.");

        var category = MeditationCategory.Create(cmd.OwnerId, name);
        await repo.AddAsync(category, ct);
        await uow.SaveChangesAsync(ct);

        // Świeża kategoria — zero medytacji.
        return new CategorySummaryDto(category.Id, category.Name, 0);
    }
}

// ── Delete ────────────────────────────────────────────────────────────────────
// Medytacjom przypisanym do kasowanej kategorii EF zeruje FK (ClientSetNull
// w konfiguracji), więc one przetrwają. SaveChanges musi objąć oba zmiany —
// samo usunięcie wiersza kategorii i update medytacji.
public record DeleteCategoryCommand(Guid Id) : IRequest;

public class DeleteCategoryHandler(ICategoryRepository repo, IUnitOfWork uow)
    : IRequestHandler<DeleteCategoryCommand>
{
    public async Task Handle(DeleteCategoryCommand cmd, CancellationToken ct)
    {
        await repo.DeleteAsync(cmd.Id, ct);
        await uow.SaveChangesAsync(ct);
    }
}
