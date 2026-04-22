using Medytao.Domain.Entities;

namespace Medytao.Domain.Interfaces;

public interface IMeditationRepository
{
    Task<Meditation?> GetByIdAsync(Guid id, CancellationToken ct = default);
    Task<IEnumerable<Meditation>> GetByAuthorAsync(Guid authorId, CancellationToken ct = default);
    Task AddAsync(Meditation meditation, CancellationToken ct = default);
    Task UpdateAsync(Meditation meditation, CancellationToken ct = default);
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
    Task<IEnumerable<Asset>> GetByOwnerAsync(Guid ownerId, CancellationToken ct = default);
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
