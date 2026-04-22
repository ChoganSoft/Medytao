using Medytao.Domain.Enums;

namespace Medytao.Domain.Entities;

/// <summary>
/// One of the four audio layers within a meditation (Music, Text, Nature, Fx).
/// </summary>
public class Layer : BaseEntity
{
    public Guid MeditationId { get; set; }
    public Meditation Meditation { get; set; } = null!;

    public LayerType Type { get; set; }
    public float Volume { get; set; } = 1.0f;   // master volume 0.0–1.0
    public bool Muted { get; set; } = false;

    public ICollection<Track> Tracks { get; set; } = [];
}

/// <summary>
/// A single uploadable element inside a layer.
/// Tracks within a layer play sequentially in <see cref="Order"/>.
/// </summary>
public class Track : BaseEntity
{
    public Guid LayerId { get; set; }
    public Layer Layer { get; set; } = null!;

    public Guid AssetId { get; set; }
    public Asset Asset { get; set; } = null!;

    public int Order { get; set; }              // playback sequence within the layer

    public float Volume { get; set; } = 1.0f;  // per-track volume override

    /// <summary>
    /// How many times the track should play before the sequence advances.
    /// 1 = play once (default). N = play N times. 0 = loop forever (next tracks in the layer will never play).
    /// </summary>
    public int LoopCount { get; set; } = 1;

    public int FadeInMs { get; set; } = 0;
    public int FadeOutMs { get; set; } = 0;
    public int StartOffsetMs { get; set; } = 0;
    public int CrossfadeMs { get; set; } = 0;  // crossfade to the next track
}