namespace Medytao.Domain.Entities;

// "Program" w domenie — czyli folder/grupa medytacji. Encja nazywa się
// MeditationProgram, żeby uniknąć kolizji nazw z top-level `Program`
// (Medytao.Api.Program, Medytao.Web.Program). Na warstwie UI/DTO mówimy
// po prostu "Program" (inne namespace'y, bez dwuznaczności).
//
// Relacja z Meditation jest many-to-many — jedna medytacja może należeć
// do wielu programów, a jeden program zawiera wiele medytacji.
// Właścicielem programu jest User (OwnerId) — cascade delete User → Program.
public class MeditationProgram : BaseEntity
{
    public Guid OwnerId { get; set; }
    public User Owner { get; set; } = null!;

    public string Name { get; set; } = string.Empty;
    public string? Description { get; set; }

    public ICollection<Meditation> Meditations { get; set; } = [];

    public static MeditationProgram Create(Guid ownerId, string name, string? description = null)
    {
        return new MeditationProgram
        {
            OwnerId = ownerId,
            Name = name,
            Description = description
        };
    }
}
