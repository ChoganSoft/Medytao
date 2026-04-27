using Medytao.Domain.Enums;

namespace Medytao.Domain.Entities;

/// <summary>
/// Bazowa, abstrakcyjna encja biblioteki zasobów. Konkretne podklasy
/// (<see cref="MusicAsset"/>, <see cref="NatureAsset"/>, <see cref="TextAsset"/>,
/// <see cref="FxAsset"/>) odpowiadają warstwom sesji medytacyjnej i będą
/// w przyszłości rozjeżdżać się parametrami — np. <see cref="TextAsset"/>
/// dostanie pola pod skrypty czytane przez ElevenLabs.
///
/// Mapowanie EF: TPH (Table Per Hierarchy) — wszystkie podklasy w jednej
/// tabeli <c>Assets</c>, dyskryminator = <see cref="LayerType"/>. Dzięki temu
/// dodanie pól do podklas to "tylko" dodanie nullable kolumn, bez ruszania
/// schemy <see cref="Track"/> (FK na bazową Asset jest polimorficzny).
///
/// <see cref="OwnerId"/> jest nullable — null oznacza zasób seedowany przez
/// system (np. preinstalowana biblioteka), bez konkretnego autora. Non-null =
/// zasób wgrany przez tego konkretnego usera. <see cref="IsShared"/> mówi, czy
/// zasób ma być widoczny dla wszystkich userów (true) czy tylko dla autora
/// (false). Łącznie:
///   - OwnerId = null              → zasób systemowy, traktowany jak shared
///   - OwnerId != null, IsShared=false → prywatny dla autora
///   - OwnerId != null, IsShared=true  → udostępniony przez autora innym
/// Tylko autor może przełączać <see cref="IsShared"/> swojego zasobu.
/// </summary>
public abstract class Asset : BaseEntity
{
    public Guid? OwnerId { get; set; }
    public User? Owner { get; set; }

    /// <summary>
    /// True = zasób widoczny dla wszystkich userów. False = tylko dla autora.
    /// Dla zasobów systemowych (OwnerId == null) wartość nie ma znaczenia —
    /// repo i tak włącza je do listy każdego usera; ustawiamy false dla
    /// porządku w bazie.
    /// </summary>
    public bool IsShared { get; set; }

    /// <summary>
    /// Warstwa, do której zasób pasuje. Jednocześnie pełni funkcję dyskryminatora
    /// TPH — EF zapisuje tu wartość enuma a podklasę dobiera automatycznie.
    /// Pole na Asset (a nie tylko discriminator z metadanych EF), żeby filtrowanie
    /// po LayerType działało bez sięgania do Set&lt;MusicAsset&gt;() itd.
    /// </summary>
    public LayerType LayerType { get; set; }

    public string FileName { get; set; } = string.Empty;
    public string BlobKey { get; set; } = string.Empty;       // path in blob storage
    public string ContentType { get; set; } = string.Empty;   // audio/mpeg, text/plain, etc.
    public long SizeBytes { get; set; }
    public int? DurationMs { get; set; }                       // for audio assets
    public string? Tags { get; set; }                          // comma-separated for simple filtering

    public ICollection<Track> Tracks { get; set; } = [];
}

/// <summary>Zasób warstwy muzycznej — utwory, podkłady, ambient.</summary>
public class MusicAsset : Asset
{
    public MusicAsset() => LayerType = LayerType.Music;
}

/// <summary>Zasób warstwy przyrody — szum lasu, fale, deszcz.</summary>
public class NatureAsset : Asset
{
    public NatureAsset() => LayerType = LayerType.Nature;
}

/// <summary>
/// Zasób warstwy tekstowej. Dziś trzyma plik audio (lektor nagrany przez
/// usera), w przyszłości dostanie pola na surowy skrypt + voice ID dla
/// ElevenLabs, żeby mogła być generowana dynamicznie.
/// </summary>
public class TextAsset : Asset
{
    public TextAsset() => LayerType = LayerType.Text;
}

/// <summary>Zasób warstwy efektowej — gongi, dzwonki, akcenty dźwiękowe.</summary>
public class FxAsset : Asset
{
    public FxAsset() => LayerType = LayerType.Fx;
}
