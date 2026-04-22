using Microsoft.EntityFrameworkCore;
using Medytao.Domain.Entities;

namespace Medytao.Infrastructure.Persistence;

public class AppDbContext(DbContextOptions<AppDbContext> options) : DbContext(options)
{
    public DbSet<User> Users => Set<User>();
    public DbSet<Meditation> Meditations => Set<Meditation>();
    public DbSet<Layer> Layers => Set<Layer>();
    public DbSet<Track> Tracks => Set<Track>();
    public DbSet<Asset> Assets => Set<Asset>();

    protected override void OnModelCreating(ModelBuilder mb)
    {
        mb.ApplyConfigurationsFromAssembly(typeof(AppDbContext).Assembly);
        base.OnModelCreating(mb);
    }

    public override Task<int> SaveChangesAsync(CancellationToken ct = default)
    {
        foreach (var entry in ChangeTracker.Entries<BaseEntity>()
            .Where(e => e.State == EntityState.Modified))
        {
            entry.Entity.UpdatedAt = DateTimeOffset.UtcNow;
        }
        return base.SaveChangesAsync(ct);
    }
}
