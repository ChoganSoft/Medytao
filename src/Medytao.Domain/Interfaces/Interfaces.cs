using Medytao.Domain.Entities;
using Medytao.Domain.Enums;

namespace Medytao.Domain.Interfaces;

public interface IMeditationRepository
{
    Task<Meditation?> GetByIdAsync(Guid id, CancellationToken ct = default);
    Task<IEnumerable<Meditation>> GetByAuthorAsync(Guid authorId, CancellationToken ct = default);
    Task<IEnumerable<Meditation>> GetByProgramAsync(Guid programId, CancellationToken ct = default);
    Task AddAsync(Meditation meditation, CancellationToken ct = default);
    Task UpdateAsync(Meditation meditation, CancellationToken ct = default);
    Task DeleteAsync(Guid id, CancellationToken ct = default);
}

public interface ICategoryRepository
{
    Task<MeditationCategory?> GetByIdAsync(Guid id, CancellationToken ct = default);
    // Lista per-user — zwraca kategorie zalogowanego usera, posortowane po
    // nazwie (pokazujemy je w dropdownie i na stronie zarządzania).
    Task<IEnumerable<MeditationCategory>> GetByOwnerAsync(Guid ownerId, CancellationToken ct = default);
    // Sprawdza konflikt nazwy w obrębie tego samego usera — używane zanim
    // dodamy nową kategorię, żeby nie tworzyć duplikatów.
    Task<bool> NameExistsAsync(Guid ownerId, string name, CancellationToken ct = default);
    Task AddAsync(MeditationCategory category, CancellationToken ct = default);
    Task DeleteAsync(Guid id, CancellationToken ct = default);
}

public interface IProgramRepository
{
    // Zwraca program wraz z kolekcją medytacji (ale bez layerów/tracków —
    // te są ładowane lazy przy wejściu do edytora).
    Task<MeditationProgram?> GetByIdAsync(Guid id, CancellationToken ct = default);
    Task<IEnumerable<MeditationProgram>> GetByOwnerAsync(Guid ownerId, CancellationToken ct = default);
    Task AddAsync(MeditationProgram program, CancellationToken ct = default);
    Task UpdateAsync(MeditationProgram program, CancellationToken ct = default);
    Task DeleteAsync(Guid id, CancellationToken ct = default);
}

public interface ILayerRepository
{
    Task<Layer?> GetByIdAsync(Guid id, CancellationToken ct = default);
    Task<IEnumerable<Layer>> GetByMeditationAsync(Guid meditationId, CancellationToken ct = default);
    Task UpdateAsync(Layer layer, CancellationToken ct = default);
}

public interface ITrackRepository
{
    Task<Track?> GetByIdAsync(Guid id, CancellationToken ct = default);
    Task AddAsync(Track track, CancellationToken ct = default);
    Task UpdateAsync(Track track, CancellationToken ct = default);
    Task DeleteAsync(Guid id, CancellationToken ct = default);
    Task ReorderAsync(Guid layerId, IEnumerable<Guid> orderedTrackIds, CancellationToken ct = default);
}

public interface IAssetRepository
{
    Task<Asset?> GetByIdAsync(Guid id, CancellationToken ct = default);

    /// <summary>
    /// Zwraca zasoby widoczne dla danego usera w zadanej warstwie:
    /// własne (OwnerId = userId) plus globalne (OwnerId IS NULL). To jest
    /// główny entry point listingu w UI — picker, panel zarządzania assetami.
    /// </summary>
    Task<IEnumerable<Asset>> GetVisibleForUserAsync(Guid userId, LayerType layerType, CancellationToken ct = default);

    Task AddAsync(Asset asset, CancellationToken ct = default);
    Task DeleteAsync(Guid id, CancellationToken ct = default);
}

public interface IUserRepository
{
    Task<User?> GetByIdAsync(Guid id, CancellationToken ct = default);
    Task<User?> GetByEmailAsync(string email, CancellationToken ct = default);
    Task AddAsync(User user, CancellationToken ct = default);
    Task UpdateAsync(User user, CancellationToken ct = default);
}

public interface IUnitOfWork
{
    Task<int> SaveChangesAsync(CancellationToken ct = default);
}

public interface IStorageService
{
    Task<string> UploadAsync(Stream stream, string fileName, string contentType, CancellationToken ct = default);
    Task<Stream> DownloadAsync(string blobKey, CancellationToken ct = default);
    Task DeleteAsync(string blobKey, CancellationToken ct = default);
    string GetPublicUrl(string blobKey);
}
