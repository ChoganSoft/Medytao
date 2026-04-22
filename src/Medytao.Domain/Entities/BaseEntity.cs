namespace Medytao.Domain.Entities;

public abstract class BaseEntity
{
    public Guid Id { get; init; } = Guid.NewGuid();
    public DateTimeOffset CreatedAt { get; init; } = DateTimeOffset.UtcNow;
    public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.UtcNow;
}
