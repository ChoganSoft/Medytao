namespace Medytao.Domain.Entities;

public class User : BaseEntity
{
    public string Email { get; set; } = string.Empty;
    public string DisplayName { get; set; } = string.Empty;
    public string PasswordHash { get; set; } = string.Empty;

    public ICollection<Meditation> Meditations { get; set; } = [];
    public ICollection<Asset> Assets { get; set; } = [];
}
