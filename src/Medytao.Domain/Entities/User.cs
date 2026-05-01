using Medytao.Domain.Enums;

namespace Medytao.Domain.Entities;

public class User : BaseEntity
{
    public string Email { get; set; } = string.Empty;
    public string DisplayName { get; set; } = string.Empty;
    public string PasswordHash { get; set; } = string.Empty;

    // RBAC — domyślnie Free (samo odsłuchiwanie). Promocja przez seed
    // (RoleSeed w appsettings) lub w przyszłości panel admina. Mapuje się
    // do JWT claim ClaimTypes.Role, frontend dekoduje i steruje UI.
    public UserRole Role { get; set; } = UserRole.Free;

    public ICollection<Meditation> Meditations { get; set; } = [];
    public ICollection<Asset> Assets { get; set; } = [];
    public ICollection<MeditationProgram> Programs { get; set; } = [];
    public ICollection<MeditationCategory> Categories { get; set; } = [];
}
