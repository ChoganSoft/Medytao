using Medytao.Domain.Enums;

namespace Medytao.Domain.Entities;

public class Asset : BaseEntity
{
    public Guid OwnerId { get; set; }
    public User Owner { get; set; } = null!;

    public string FileName { get; set; } = string.Empty;
    public string BlobKey { get; set; } = string.Empty;       // path in blob storage
    public string ContentType { get; set; } = string.Empty;   // audio/mpeg, text/plain, etc.
    public long SizeBytes { get; set; }
    public int? DurationMs { get; set; }                       // for audio assets
    public AssetType Type { get; set; }
    public string? Tags { get; set; }                          // comma-separated for simple filtering

    public ICollection<Track> Tracks { get; set; } = [];
}
