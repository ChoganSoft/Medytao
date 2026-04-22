using Medytao.Domain.Enums;

namespace Medytao.Domain.Entities;

public class Meditation : BaseEntity
{
    public Guid AuthorId { get; set; }
    public User Author { get; set; } = null!;

    public string Title { get; set; } = string.Empty;
    public string? Description { get; set; }
    public int DurationMs { get; set; }
    public MeditationStatus Status { get; set; } = MeditationStatus.Draft;

    // One layer per type — always four, created on meditation creation
    public ICollection<Layer> Layers { get; set; } = [];

    public static Meditation Create(Guid authorId, string title, string? description = null)
    {
        var meditation = new Meditation
        {
            AuthorId = authorId,
            Title = title,
            Description = description
        };

        // Seed all four layers immediately
        foreach (LayerType type in Enum.GetValues<LayerType>())
        {
            meditation.Layers.Add(new Layer
            {
                MeditationId = meditation.Id,
                Type = type,
                Volume = 1.0f
            });
        }

        return meditation;
    }
}
