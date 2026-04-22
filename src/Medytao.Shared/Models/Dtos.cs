namespace Medytao.Shared.Models;

// ── Asset ──────────────────────────────────────────────
public record AssetDto(
    Guid Id,
    string FileName,
    string ContentType,
    long SizeBytes,
    int? DurationMs,
    string Type,
    string? Tags,
    string Url
);

// ── Track ──────────────────────────────────────────────
public record TrackDto(
    Guid Id,
    int Order,
    float Volume,
    int LoopCount,
    int FadeInMs,
    int FadeOutMs,
    int StartOffsetMs,
    int CrossfadeMs,
    AssetDto Asset
);

// ── Layer ──────────────────────────────────────────────
public record LayerDto(
    Guid Id,
    string Type,
    float Volume,
    bool Muted,
    IEnumerable<TrackDto> Tracks
);

// ── Meditation ─────────────────────────────────────────
public record MeditationSummaryDto(
    Guid Id,
    string Title,
    string? Description,
    int DurationMs,
    string Status,
    DateTimeOffset CreatedAt
);

public record MeditationDetailDto(
    Guid Id,
    string Title,
    string? Description,
    int DurationMs,
    string Status,
    DateTimeOffset CreatedAt,
    IEnumerable<LayerDto> Layers
);

// ── Auth ───────────────────────────────────────────────
public record AuthTokenDto(string AccessToken, DateTimeOffset ExpiresAt, string DisplayName);