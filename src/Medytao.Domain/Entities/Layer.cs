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

    /// <summary>
    /// Preset reverbu dla całej warstwy. Off = bypass (graf nie wpina
    /// ConvolverNode-a). Room/Hall = syntetyczne IR generowane w kliencie.
    /// </summary>
    public LayerReverbPreset ReverbPreset { get; set; } = LayerReverbPreset.Off;

    /// <summary>
    /// Wet/dry mix reverbu, 0–1. 0 = tylko dry (efektywnie bypass nawet
    /// gdy Preset != Off), 1 = tylko wet. Typowo 0.2–0.4 dla naturalnego
    /// brzmienia. Stored osobno od Preset, żeby user mógł szybko ściszyć
    /// efekt bez gubienia wyboru typu pomieszczenia.
    /// </summary>
    public float ReverbMix { get; set; } = 0.0f;

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

    /// <summary>
    /// Tempo odtwarzania, 1.0 = normalna prędkość. Dziś używane wyłącznie
    /// w warstwie Text (lektor) — UI pokazuje slider 0.75–1.25× tylko przy
    /// trackach warstwy Text. Field jest jednak per-Track (a nie per-Layer),
    /// żeby user mógł różnym fragmentom narracji ustawić różne tempo.
    ///
    /// Backend nie wymusza zakresu — granice są UX-em po stronie Web (slider
    /// trzyma 0.75–1.25). Web Audio z preservesPitch radzi sobie dobrze
    /// w tym przedziale; ekstrema dają artefakty, ale to świadomy wybór usera.
    /// </summary>
    public float PlaybackRate { get; set; } = 1.0f;
}