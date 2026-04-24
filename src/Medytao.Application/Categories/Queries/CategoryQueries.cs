using MediatR;
using Medytao.Domain.Interfaces;
using Medytao.Shared.Models;

namespace Medytao.Application.Categories.Queries;

// Lista kategorii danego usera — używana i przez dropdown przy tworzeniu
// medytacji, i przez stronę /categories. MeditationCount liczony z
// eager-loaded nav (CategoryRepository.GetByOwnerAsync includuje Meditations).
public record GetCategoriesByOwnerQuery(Guid OwnerId) : IRequest<IEnumerable<CategorySummaryDto>>;

public class GetCategoriesByOwnerHandler(ICategoryRepository repo)
    : IRequestHandler<GetCategoriesByOwnerQuery, IEnumerable<CategorySummaryDto>>
{
    public async Task<IEnumerable<CategorySummaryDto>> Handle(GetCategoriesByOwnerQuery q, CancellationToken ct)
    {
        var categories = await repo.GetByOwnerAsync(q.OwnerId, ct);
        return categories.Select(c => new CategorySummaryDto(c.Id, c.Name, c.Meditations.Count));
    }
}
