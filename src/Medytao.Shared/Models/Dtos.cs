namespace Medytao.Shared.Models;

// ── Asset ──────────────────────────────────────────────
// LayerType: nazwa warstwy do której zasób należy ("Music"/"Nature"/"Text"/"Fx").
// Wynika z konkretnej podklasy w domenie (TPH) i jednocześnie steruje filtrowaniem
// w UI — picker w warstwie X pokazuje tylko zasoby z LayerType == X.
//
// IsShared: true, jeśli zasób jest widoczny dla wszystkich userów. Pochodzi z
// flagi IsShared w bazie LUB z OwnerId == NULL (zasoby systemowe traktujemy
// jako z definicji shared — i tak nikt nie ma do nich własności).
// IsMine: true, jeśli zalogowany user jest autorem (może edytować widoczność,
// może usunąć). Dla cudzych shared/seedów = false. UI pokazuje toggle
// udostępniania i kosz wyłącznie gdy IsMine.
public record AssetDto(
    Guid Id,
    string FileName,
    string ContentType,
    long SizeBytes,
    int? DurationMs,
    string LayerType,
    bool IsShared,
    bool IsMine,
    string? Tags,
    string Url
);

// ── Track ──────────────────────────────────────────────
// PlaybackRate: 1.0 = normalna prędkość. UI rysuje slider 0.75–1.25× tylko
// dla tracków warstwy Text (decyzja produktowa: spowolnienie potrzebne tylko
// na lektorze). Field jednak jest per-Track, bo różne fragmenty narracji
// mogą wymagać różnego tempa. Default 1.0 zachowuje semantykę dla starych
// medytacji bez tej wartości.
//
// ReverbMix: 0..1 wet/dry pojedynczego sounda. 0 = bypass (graf nie wpina
// convolvera). Jeden zaszyty preset (Hall) — UI to slider 0–100% zawsze
// widoczny w expanded panelu tracka, niezależnie od warstwy.
public record TrackDto(
    Guid Id,
    int Order,
    float Volume,
    int LoopCount,
    int FadeInMs,
    int FadeOutMs,
    int StartOffsetMs,
    int CrossfadeMs,
    float PlaybackRate,
    float ReverbMix,
    AssetDto Asset
);

// ── Layer ──────────────────────────────────────────────
// Reverb wcześniej był per-Layer (Preset + Mix); refaktor "track-level reverb"
// przeniósł go na TrackDto.ReverbMix z jednym zaszytym presetem (Hall). LayerDto
// zostaje proste: tylko volume + mute + tracks.
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