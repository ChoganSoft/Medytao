namespace Medytao.Domain.Entities;

// Słownikowa kategoria sesji medytacyjnej (np. "Relaksacja", "Autohipnoza").
// Per-user: każdy user ma własny słownik — seedujemy 10 domyślnych przy
// rejestracji (AuthEndpoints.DefaultCategoryNames), ale user może dodawać
// i usuwać swoje. Globalnych kategorii nie robimy, bo aplikacja nie ma
// roli admina i nie chcemy, żeby zmiana u jednego usera dotykała innych.
//
// Relacja z Meditation jest 1:N z opcjonalnym FK (CategoryId nullable):
// usunięcie kategorii ustawia meditation.CategoryId = NULL (DeleteBehavior
// .SetNull), więc medytacje nie znikają razem z kategorią — po prostu
// stają się "Uncategorized" do czasu gdy user przypisze im nową.
public class MeditationCategory : BaseEntity
{
    public Guid OwnerId { get; set; }
    public User Owner { get; set; } = null!;

    public string Name { get; set; } = string.Empty;

    public ICollection<Meditation> Meditations { get; set; } = [];

    public static MeditationCategory Create(Guid ownerId, string name) =>
        new() { OwnerId = ownerId, Name = name };
}
