using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Medytao.Domain.Entities;

namespace Medytao.Infrastructure.Persistence.Configurations;

public class UserConfiguration : IEntityTypeConfiguration<User>
{
    public void Configure(EntityTypeBuilder<User> b)
    {
        b.HasKey(u => u.Id);
        b.Property(u => u.Email).IsRequired().HasMaxLength(256);
        b.Property(u => u.DisplayName).IsRequired().HasMaxLength(100);
        b.Property(u => u.PasswordHash).IsRequired();
        b.HasIndex(u => u.Email).IsUnique();

        b.HasMany(u => u.Meditations)
            .WithOne(m => m.Author)
            .HasForeignKey(m => m.AuthorId)
            .OnDelete(DeleteBehavior.Cascade);

        b.HasMany(u => u.Assets)
            .WithOne(a => a.Owner)
            .HasForeignKey(a => a.OwnerId)
            .OnDelete(DeleteBehavior.Cascade);
    }
}

public class MeditationConfiguration : IEntityTypeConfiguration<Meditation>
{
    public void Configure(EntityTypeBuilder<Meditation> b)
    {
        b.HasKey(m => m.Id);
        b.Property(m => m.Title).IsRequired().HasMaxLength(200);
        b.Property(m => m.Description).HasMaxLength(2000);
        b.Property(m => m.Status).HasConversion<string>();

        b.HasMany(m => m.Layers)
            .WithOne(l => l.Meditation)
            .HasForeignKey(l => l.MeditationId)
            .OnDelete(DeleteBehavior.Cascade);

        // Many-to-many Meditation <-> MeditationProgram. Strona "Programs"
        // encji Meditation jest skip-navigation; tabela pośrednicząca ma
        // domyślną nazwę "MeditationMeditationProgram" — nadpisujemy na
        // "MeditationPrograms" dla czytelności. Brak cascade po stronie
        // skip-nav — usunięcie programu kasuje tylko wiersze w tabeli
        // join (to robi EF automatycznie), samą medytację kasuje
        // DeleteProgramCommand jeśli została orphanem.
        b.HasMany(m => m.Programs)
            .WithMany(p => p.Meditations)
            .UsingEntity(join => join.ToTable("MeditationPrograms"));

        b.HasIndex(m => m.AuthorId);
        b.HasIndex(m => m.Status);
    }
}

public class MeditationProgramConfiguration : IEntityTypeConfiguration<MeditationProgram>
{
    public void Configure(EntityTypeBuilder<MeditationProgram> b)
    {
        b.HasKey(p => p.Id);
        b.Property(p => p.Name).IsRequired().HasMaxLength(200);
        b.Property(p => p.Description).HasMaxLength(2000);

        b.HasOne(p => p.Owner)
            .WithMany(u => u.Programs)
            .HasForeignKey(p => p.OwnerId)
            .OnDelete(DeleteBehavior.Cascade);

        // Relację M:N do Meditation konfigurujemy tylko po jednej stronie
        // (w MeditationConfiguration) — EF sam dopasuje drugi koniec.

        b.HasIndex(p => p.OwnerId);
        b.ToTable("Programs");
    }
}

public class LayerConfiguration : IEntityTypeConfiguration<Layer>
{
    public void Configure(EntityTypeBuilder<Layer> b)
    {
        b.HasKey(l => l.Id);
        b.Property(l => l.Type).HasConversion<string>();
        b.Property(l => l.Volume).HasDefaultValue(1.0f);

        b.HasMany(l => l.Tracks)
            .WithOne(t => t.Layer)
            .HasForeignKey(t => t.LayerId)
            .OnDelete(DeleteBehavior.Cascade);

        // Each meditation has exactly one layer per type
        b.HasIndex(l => new { l.MeditationId, l.Type }).IsUnique();
    }
}

public class TrackConfiguration : IEntityTypeConfiguration<Track>
{
    public void Configure(EntityTypeBuilder<Track> b)
    {
        b.HasKey(t => t.Id);
        b.Property(t => t.Volume).HasDefaultValue(1.0f);
        b.Property(t => t.LoopCount).HasDefaultValue(1);
        b.Property(t => t.FadeInMs).HasDefaultValue(0);
        b.Property(t => t.FadeOutMs).HasDefaultValue(0);
        b.Property(t => t.StartOffsetMs).HasDefaultValue(0);
        b.Property(t => t.CrossfadeMs).HasDefaultValue(0);

        b.HasOne(t => t.Asset)
            .WithMany(a => a.Tracks)
            .HasForeignKey(t => t.AssetId)
            .OnDelete(DeleteBehavior.Restrict); // don't cascade — asset may be reused

        // Unique so sequential playback is deterministic.
        b.HasIndex(t => new { t.LayerId, t.Order }).IsUnique();
    }
}

public class AssetConfiguration : IEntityTypeConfiguration<Asset>
{
    public void Configure(EntityTypeBuilder<Asset> b)
    {
        b.HasKey(a => a.Id);
        b.Property(a => a.FileName).IsRequired().HasMaxLength(500);
        b.Property(a => a.BlobKey).IsRequired().HasMaxLength(1000);
        b.Property(a => a.ContentType).IsRequired().HasMaxLength(100);
        b.Property(a => a.Type).HasConversion<string>();
        b.Property(a => a.Tags).HasMaxLength(500);

        b.HasIndex(a => a.OwnerId);
        b.HasIndex(a => a.Type);
    }
}