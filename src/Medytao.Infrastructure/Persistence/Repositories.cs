using Microsoft.EntityFrameworkCore;
using Medytao.Domain.Entities;
using Medytao.Domain.Enums;
using Medytao.Domain.Interfaces;

namespace Medytao.Infrastructure.Persistence;

public class MeditationRepository(AppDbContext db) : IMeditationRepository
{
    public Task<Meditation?> GetByIdAsync(Guid id, CancellationToken ct = default) =>
        db.Meditations
            .Include(m => m.Category)
            .Include(m => m.Layers)
                .ThenInclude(l => l.Tracks)
                    .ThenInclude(t => t.Asset)
            .FirstOrDefaultAsync(m => m.Id == id, ct);

    public async Task<IEnumerable<Meditation>> GetByAuthorAsync(Guid authorId, CancellationToken ct = default) =>
        await db.Meditations
            // Include warstw + tracków — karta medytacji w widoku listy pokazuje
            // liczbę tracków per LayerType (Music/Nature/Text/Fx) na złotym pasku.
            // Bez tego Select na Layers rzuci navigation-not-loaded.
            // Category dołączamy, bo karta pokazuje też badge kategorii.
            .Include(m => m.Category)
            .Include(m => m.Layers)
                .ThenInclude(l => l.Tracks)
            .Where(m => m.AuthorId == authorId)
            .OrderByDescending(m => m.UpdatedAt)
            .ToListAsync(ct);

    public async Task<IEnumerable<Meditation>> GetByProgramAsync(Guid programId, CancellationToken ct = default) =>
        await db.Meditations
            .Include(m => m.Category)
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
        if (tracks.Count == 0) return;

        // Reorder w jednym batchu wybucha na unique-indeksie (LayerId, Order):
        // gdy chcemy zamienić tracki miejscami, każdy nowo przypisywany Order
        // chwilowo koliduje z istniejącą wartością innego wiersza, EF wykrywa
        // cykl w grafie operacji i rzuca InvalidOperationException
        // ("circular dependency was detected").
        //
        // Rozwiązanie dwufazowe: najpierw przerzucamy wszystkim Order na unikalne
        // ujemne sentinele (-1, -2, …), zapisujemy — żaden wiersz nie ma już
        // wartości w docelowym przedziale [0, N-1], więc faza druga (przypisanie
        // właściwych pozycji) nie napotyka konfliktów. Finalny save zostaje
        // w handlerze przez UoW, żeby zachować transakcyjność z resztą operacji.
        for (var i = 0; i < tracks.Count; i++)
            tracks[i].Order = -1 - i;
        await db.SaveChangesAsync(ct);

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

    public async Task<IEnumerable<Asset>> GetVisibleForUserAsync(Guid userId, LayerType layerType, CancellationToken ct = default) =>
        // Widzimy: własne (OwnerId == userId), systemowe (OwnerId IS NULL) i
        // udostępnione przez innych (IsShared == true). Filtr po LayerType
        // realnie schodzi do warunku na kolumnie dyskryminatora w SQL — EF
        // wybiera odpowiednią podklasę (MusicAsset/NatureAsset/...) automatycznie.
        await db.Assets
            .Where(a => a.LayerType == layerType
                     && (a.OwnerId == null || a.OwnerId == userId || a.IsShared))
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

public class CategoryRepository(AppDbContext db) : ICategoryRepository
{
    public Task<MeditationCategory?> GetByIdAsync(Guid id, CancellationToken ct = default) =>
        db.Categories.FirstOrDefaultAsync(c => c.Id == id, ct);

    public async Task<IEnumerable<MeditationCategory>> GetByOwnerAsync(Guid ownerId, CancellationToken ct = default) =>
        await db.Categories
            // Eager-load Meditations — strona /categories pokazuje licznik
            // medytacji per kategorię, a dropdown w modal-u "New meditation"
            // i tak chce samego Id+Name, więc to tylko koszt listy kategorii
            // (pojedynczy user: kilkanaście wierszy, OK).
            .Include(c => c.Meditations)
            .Where(c => c.OwnerId == ownerId)
            .OrderBy(c => c.Name)
            .ToListAsync(ct);

    public Task<bool> NameExistsAsync(Guid ownerId, string name, CancellationToken ct = default) =>
        db.Categories.AnyAsync(c => c.OwnerId == ownerId && c.Name == name, ct);

    public async Task AddAsync(MeditationCategory category, CancellationToken ct = default) =>
        await db.Categories.AddAsync(category, ct);

    public async Task DeleteAsync(Guid id, CancellationToken ct = default)
    {
        // Eager-load Meditations, żeby DeleteBehavior.ClientSetNull faktycznie
        // zadziałał — EF musi trackować wiersze, którym zerujemy FK. Bez
        // Include medytacje nie byłyby w change-trackerze i zostałby wiszący
        // FK w bazie (poza tym klasa DeleteBehavior.ClientSetNull rzuciłaby
        // wyjątek podczas SaveChanges).
        var category = await db.Categories
            .Include(c => c.Meditations)
            .FirstOrDefaultAsync(c => c.Id == id, ct);
        if (category is not null) db.Categories.Remove(category);
    }
}

public class ProgramRepository(AppDbContext db) : IProgramRepository
{
    public Task<MeditationProgram?> GetByIdAsync(Guid id, CancellationToken ct = default) =>
        db.Programs
            // Medytacje doładowujemy, żeby ProgramDetails od razu miało listę
            // do wyrenderowania. Tracki/layery ładujemy dopiero w edytorze.
            // Category — karta medytacji pokazuje badge kategorii.
            .Include(p => p.Meditations)
                .ThenInclude(m => m.Category)
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
