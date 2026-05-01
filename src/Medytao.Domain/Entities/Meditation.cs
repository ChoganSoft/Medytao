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

    // Minimalna rola do widoczności w bibliotece, gdy Status == Published.
    // Default Free = każdy zalogowany user widzi. Publishing UI w MeditationEditor
    // pozwala autorowi (Master+) podnieść próg, jeśli sesja jest dla "wyższych ról".
    // Dla Status == Draft to pole nie ma znaczenia (sesja niewidoczna dla nikogo
    // poza autorem niezależnie od MinRoleRequired).
    public UserRole MinRoleRequired { get; set; } = UserRole.Free;

    // Kategoria medytacji — opcjonalna w schemacie (nullable FK). Powody:
    //  - legacy medytacje z przed feature'a nie mają przypisania,
    //  - przy usunięciu kategorii stawiamy FK na NULL (DeleteBehavior.SetNull),
    //    więc medytacja przeżywa i zostaje "Uncategorized".
    // UI wymusza wybór przy tworzeniu nowej medytacji (walidacja po stronie
    // formularza), ale na poziomie domeny dopuszczamy brak.
    public Guid? CategoryId { get; set; }
    public MeditationCategory? Category { get; set; }

    // One layer per type — always four, created on meditation creation
    public ICollection<Layer> Layers { get; set; } = [];

    // Many-to-many przez skip-navigation. Medytacja musi należeć do min.
    // jednego programu (walidujemy w command handlerach, nie na poziomie
    // schematu — EF M:N nie wyraża "co najmniej 1" natywnie). Cleanup
    // orphanów (medytacji bez programów po skasowaniu ostatniego programu,
    // w którym były) robi DeleteProgramCommand.
    public ICollection<MeditationProgram> Programs { get; set; } = [];

    public static Meditation Create(Guid authorId, string title, string? description = null, Guid? categoryId = null)
    {
        var meditation = new Meditation
        {
            AuthorId = authorId,
            Title = title,
            Description = description,
            CategoryId = categoryId
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
