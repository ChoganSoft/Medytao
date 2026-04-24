using Microsoft.EntityFrameworkCore;
using Medytao.Domain.Entities;
using Medytao.Domain.Interfaces;

namespace Medytao.Infrastructure.Persistence;

public class MeditationRepository(AppDbContext db) : IMeditationRepository
{
    public Task<Meditation?> GetByIdAsync(Guid id, CancellationToken ct = default) =>
        db.Meditations
            .Include(m => m.Layers)
                .ThenInclude(l => l.Tracks)
                    .ThenInclude(t => t.Asset)
            .FirstOrDefaultAsync(m => m.Id == id, ct);

    public async Task<IEnumerable<Meditation>> GetByAuthorAsync(Guid authorId, CancellationToken ct = default) =>
        await db.Meditations
            // Include warstw + tracków — karta medytacji w widoku listy pokazuje
            // liczbę tracków per LayerType (Music/Nature/Text/Fx) na złotym pasku.
            // Bez tego Select na Layers rzuci navigation-not-loaded.
            .Include(m => m.Layers)
                .ThenInclude(l => l.Tracks)
            .Where(m => m.AuthorId == authorId)
            .OrderByDescending(m => m.UpdatedAt)
            .ToListAsync(ct);

    public async Task<IEnumerable<Meditation>> GetByProgramAsync(Guid programId, CancellationToken ct = default) =>
        await db.Meditations
            .Include(m => m.Layers)
                .ThenInclude(l => l.Tracks)
            .Where(m => m.Programs.Any(p => p.Id == programId))
            .OrderByDescending(m => m.UpdatedAt)
            .ToListAsync(ct);

    public async Task AddAsync(Meditation meditation, CancellationToken ct = default) =>
        await db.Meditations.AddAsync(meditation, ct);

    public Task UpdateAsync(Meditation meditation, CancellationToken ct = default)
    {
        db.Meditations.Update(meditation);
        return Task.CompletedTask;
    }

    public async Task DeleteAsync(Guid id, CancellationToken ct = default)
    {
        var meditation = await db.Meditations.FindAsync([id], ct);
        if (meditation is not null) db.Meditations.Remove(meditation);
    }
}

public class LayerRepository(AppDbContext db) : ILayerRepository
{
    public Task<Layer?> GetByIdAsync(Guid id, CancellationToken ct = default) =>
        db.Layers
            .Include(l => l.Tracks).ThenInclude(t => t.Asset)
            .FirstOrDefaultAsync(l => l.Id == id, ct);

    public async Task<IEnumerable<Layer>> GetByMeditationAsync(Guid meditationId, CancellationToken ct = default) =>
        await db.Layers
            .Include(l => l.Tracks).ThenInclude(t => t.Asset)
            .Where(l => l.MeditationId == meditationId)
            .ToListAsync(ct);

    public Task UpdateAsync(Layer layer, CancellationToken ct = default)
    {
        db.Layers.Update(layer);
        return Task.CompletedTask;
    }
}

public class TrackRepository(AppDbContext db) : ITrackRepository
{
    public Task<Track?> GetByIdAsync(Guid id, CancellationToken ct = default) =>
        db.Tracks.Include(t => t.Asset).FirstOrDefaultAsync(t => t.Id == id, ct);

    public async Task AddAsync(Track track, CancellationToken ct = default) =>
        await db.Tracks.AddAsync(track, ct);

    public Task UpdateAsync(Track track, CancellationToken ct = default)
    {
        db.Tracks.Update(track);
        return Task.CompletedTask;
    }

    public async Task DeleteAsync(Guid id, CancellationToken ct = default)
    {
        var track = await db.Tracks.FindAsync([id], ct);
        if (track is not null) db.Tracks.Remove(track);
    }

    public async Task ReorderAsync(Guid layerId, IEnumerable<Guid> orderedTrackIds, CancellationToken ct = default)
    {
        var tracks = await db.Tracks
            .Where(t => t.LayerId == layerId)
            .ToListAsync(ct);

        var orderList = orderedTrackIds.ToList();
        foreach (var track in tracks)
        {
            var index = orderList.IndexOf(track.Id);
            if (index >= 0) track.Order = index;
        }
    }
}

public class AssetRepository(AppDbContext db) : IAssetRepository
{
    public Task<Asset?> GetByIdAsync(Guid id, CancellationToken ct = default) =>
        db.Assets.FirstOrDefaultAsync(a => a.Id == id, ct);

    public async Task<IEnumerable<Asset>> GetByOwnerAsync(Guid ownerId, CancellationToken ct = default) =>
        await db.Assets
            .Where(a => a.OwnerId == ownerId)
            .OrderByDescending(a => a.CreatedAt)
            .ToListAsync(ct);

    public async Task AddAsync(Asset asset, CancellationToken ct = default) =>
        await db.Assets.AddAsync(asset, ct);

    public async Task DeleteAsync(Guid id, CancellationToken ct = default)
    {
        var asset = await db.Assets.FindAsync([id], ct);
        if (asset is not null) db.Assets.Remove(asset);
    }
}

public class UserRepository(AppDbContext db) : IUserRepository
{
    public Task<User?> GetByIdAsync(Guid id, CancellationToken ct = default) =>
        db.Users.FirstOrDefaultAsync(u => u.Id == id, ct);

    public Task<User?> GetByEmailAsync(string email, CancellationToken ct = default) =>
        db.Users.FirstOrDefaultAsync(u => u.Email == email, ct);

    public async Task AddAsync(User user, CancellationToken ct = default) =>
        await db.Users.AddAsync(user, ct);

    public Task UpdateAsync(User user, CancellationToken ct = default)
    {
        db.Users.Update(user);
        return Task.CompletedTask;
    }
}

public class ProgramRepository(AppDbContext db) : IProgramRepository
{
    public Task<MeditationProgram?> GetByIdAsync(Guid id, CancellationToken ct = default) =>
        db.Programs
            // Medytacje doładowujemy, żeby ProgramDetails od razu miało listę
            // do wyrenderowania. Tracki/layery ładujemy dopiero w edytorze.
            .Include(p => p.Meditations)
                .ThenInclude(m => m.Layers)
                    .ThenInclude(l => l.Tracks)
            .FirstOrDefaultAsync(p => p.Id == id, ct);

    public async Task<IEnumerable<MeditationProgram>> GetByOwnerAsync(Guid ownerId, CancellationToken ct = default) =>
        await db.Programs
            // Tylko same Meditations (bez warstw) — listę programów renderujemy
            // z liczbą medytacji per program, więc .Count() na navigation wystarczy.
            .Include(p => p.Meditations)
            .Where(p => p.OwnerId == ownerId)
            .OrderBy(p => p.CreatedAt)
            .ToListAsync(ct);

    public async Task AddAsync(MeditationProgram program, CancellationToken ct = default) =>
        await db.Programs.AddAsync(program, ct);

    public Task UpdateAsync(MeditationProgram program, CancellationToken ct = default)
    {
        db.Programs.Update(program);
        return Task.CompletedTask;
    }

    public async Task DeleteAsync(Guid id, CancellationToken ct = default)
    {
        var program = await db.Programs.FindAsync([id], ct);
        if (program is not null) db.Programs.Remove(program);
    }
}

public class UnitOfWork(AppDbContext db) : IUnitOfWork
{
    public Task<int> SaveChangesAsync(CancellationToken ct = default) =>
        db.SaveChangesAsync(ct);
}
