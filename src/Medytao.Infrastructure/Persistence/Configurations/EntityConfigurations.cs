using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Medytao.Domain.Entities;
using Medytao.Domain.Enums;

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
        // "MeditationPrograms" dla czytelności.
        //
        // Kaskady: domyślnie EF ustawia Cascade po obu stronach join-a, ale
        // wtedy SQL Server wykrywa dwie ścieżki kaskady przy usunięciu User:
        //   Users → Meditations (CASCADE) → MeditationPrograms (CASCADE)
        //   Users → Programs     (CASCADE) → MeditationPrograms (CASCADE)
        // i odrzuca schemat ("may cause cycles or multiple cascade paths").
        //
        // Rozwiązanie: strona Program → join ustawiona na ClientCascade —
        // baza nie dopisuje FK CASCADE (cykl zniknął), a EF i tak kasuje
        // wiersze join-a kiedy trackuje program z załadowaną kolekcją
        // Meditations (tak właśnie działa DeleteProgramCommand: GetByIdAsync
        // robi eager-load Meditations, potem DeleteAsync → SaveChanges).
        // Strona Meditation → join zostaje na Cascade, więc przy kasowaniu
        // medytacji (albo jej User-owner-cascade) baza sama czyści join.
        b.HasMany(m => m.Programs)
            .WithMany(p => p.Meditations)
            .UsingEntity<Dictionary<string, object>>(
                "MeditationPrograms",
                j => j.HasOne<MeditationProgram>()
                      .WithMany()
                      .OnDelete(DeleteBehavior.ClientCascade),
                j => j.HasOne<Meditation>()
                      .WithMany()
                      .OnDelete(DeleteBehavior.Cascade));

        // Kategoria — nullable FK. SetNull przy usunięciu kategorii: medytacje
        // zostają, po prostu tracą przypisanie (stają się "Uncategorized").
        // ClientSetNull zamiast SetNull na bazie, bo User → Meditations cascade
        // i User → Categories cascade stworzyłyby drugą ścieżkę kaskady
        // (User → Categories → Meditations SET NULL plus User → Meditations
        // CASCADE) — analogiczny problem jak z tabelą MeditationPrograms.
        // ClientSetNull: EF odpina medytację w trackerze przy usuwaniu
        // kategorii, baza nie dopisuje FK ON DELETE SET NULL.
        b.HasOne(m => m.Category)
            .WithMany(c => c.Meditations)
            .HasForeignKey(m => m.CategoryId)
            .OnDelete(DeleteBehavior.ClientSetNull);

        b.HasIndex(m => m.AuthorId);
        b.HasIndex(m => m.Status);
        b.HasIndex(m => m.CategoryId);
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

public class MeditationCategoryConfiguration : IEntityTypeConfiguration<MeditationCategory>
{
    public void Configure(EntityTypeBuilder<MeditationCategory> b)
    {
        b.HasKey(c => c.Id);
        b.Property(c => c.Name).IsRequired().HasMaxLength(100);

        b.HasOne(c => c.Owner)
            .WithMany(u => u.Categories)
            .HasForeignKey(c => c.OwnerId)
            .OnDelete(DeleteBehavior.Cascade);

        // Unikalna nazwa w obrębie jednego usera — dwóch userów może mieć
        // osobno "Medytacja", ale u jednego nie dublujemy. Walidacja po
        // stronie handlera przed INSERT — indeks to backup.
        b.HasIndex(c => new { c.OwnerId, c.Name }).IsUnique();

        b.ToTable("Categories");
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
        // PlaybackRate: stare wiersze (przed migracją) nie mają wartości — default
        // 1.0 zachowuje semantykę "graj normalnie", więc istniejące tracki nie
        // zmieniają zachowania po dodaniu kolumny.
        b.Property(t => t.PlaybackRate).HasDefaultValue(1.0f);
        // ReverbMix: 0 = brak efektu (graf audio bypass'uje convolver), więc
        // stare tracki bez tej kolumny brzmią identycznie jak przed dodaniem.
        b.Property(t => t.ReverbMix).HasDefaultValue(0.0f);

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
        b.Property(a => a.Tags).HasMaxLength(500);

        // TPH: jedna tabela Assets, kolumna LayerType pełni rolę dyskryminatora.
        // Wartość trzymana jako string (czytelnie w SQL i odporne na renumerację
        // enuma) — analogicznie do innych enumów w schemie.
        b.Property(a => a.LayerType).HasConversion<string>().HasMaxLength(20);
        b.HasDiscriminator(a => a.LayerType)
            .HasValue<MusicAsset>(LayerType.Music)
            .HasValue<NatureAsset>(LayerType.Nature)
            .HasValue<TextAsset>(LayerType.Text)
            .HasValue<FxAsset>(LayerType.Fx);

        // OwnerId nullable: NULL = zasób globalny (widoczny dla wszystkich),
        // non-null = prywatny zasób tego usera. Composite index pod typowy
        // pattern listingu: "daj mi zasoby warstwy X widoczne dla usera Y"
        // (czyli WHERE LayerType = ? AND (OwnerId IS NULL OR OwnerId = ?)).
        b.HasIndex(a => new { a.LayerType, a.OwnerId });
    }
}