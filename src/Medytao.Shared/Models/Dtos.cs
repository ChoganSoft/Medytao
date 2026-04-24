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
// TracksByLayerType: klucz to nazwa LayerType ("Music", "Nature", "Text", "Fx"),
// wartość — liczba tracków w tej warstwie. Karta medytacji w widoku listy
// potrzebuje tych liczb na złotym pasku z ikonami, żeby pokazać ile jest
// dźwięków w każdej kategorii. Dict zamiast czterech osobnych pól, żeby
// nowy LayerType nie wymuszał zmiany kształtu DTO.
//
// CategoryId + CategoryName: opcjonalne (legacy medytacje mogą nie mieć
// kategorii, a usunięcie kategorii zeruje FK). UI renderuje badge tylko
// gdy CategoryName != null.
public record MeditationSummaryDto(
    Guid Id,
    string Title,
    string? Description,
    int DurationMs,
    string Status,
    DateTimeOffset CreatedAt,
    Dictionary<string, int> TracksByLayerType,
    Guid? CategoryId,
    string? CategoryName
);

public record MeditationDetailDto(
    Guid Id,
    string Title,
    string? Description,
    int DurationMs,
    string Status,
    DateTimeOffset CreatedAt,
    IEnumerable<LayerDto> Layers,
    Guid? CategoryId,
    string? CategoryName
);

// ── Program ────────────────────────────────────────────
// Program grupuje medytacje (M:N). Karta programu w widoku listy pokazuje
// tytuł/opis + listę nazw medytacji na złotym pasku (bez awatara — decyzja
// designu, program to "folder", nie sesja). Licznik jest pochodny od listy
// (MeditationTitles.Count) — nie duplikujemy go w DTO.
public record ProgramSummaryDto(
    Guid Id,
    string Name,
    string? Description,
    IReadOnlyList<string> MeditationTitles,
    DateTimeOffset CreatedAt
);

public record ProgramDetailDto(
    Guid Id,
    string Name,
    string? Description,
    DateTimeOffset CreatedAt,
    IEnumerable<MeditationSummaryDto> Meditations
);

// ── Category ───────────────────────────────────────────
// Prosty słownikowy typ dla dropdownu przy tworzeniu medytacji i dla strony
// zarządzania kategoriami. MeditationCount wystawiamy, żeby lista na stronie
// /categories pokazywała od razu ile medytacji ma daną kategorię (i żeby
// modal delete mógł ostrzec, że usunięcie zostawi N medytacji bez kategorii).
public record CategorySummaryDto(
    Guid Id,
    string Name,
    int MeditationCount
);

// ── Auth ───────────────────────────────────────────────
public record AuthTokenDto(string AccessToken, DateTimeOffset ExpiresAt, string DisplayName);