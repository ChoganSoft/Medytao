using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Medytao.Domain.Interfaces;
using Medytao.Infrastructure.Persistence;
using Medytao.Infrastructure.Storage;

namespace Medytao.Infrastructure.Extensions;

public static class InfrastructureServiceExtensions
{
    public static IServiceCollection AddInfrastructure(
        this IServiceCollection services,
        IConfiguration configuration)
    {
        // Baza — SQL Server (LocalDB lub pełny)
        services.AddDbContext<AppDbContext>(options =>
            options.UseSqlServer(
                configuration.GetConnectionString("SqlServer"),
                sql => sql.MigrationsAssembly(typeof(AppDbContext).Assembly.FullName)
            ));

        // Repozytoria
        services.AddScoped<IMeditationRepository, MeditationRepository>();
        services.AddScoped<IProgramRepository, ProgramRepository>();
        services.AddScoped<ICategoryRepository, CategoryRepository>();
        services.AddScoped<ILayerRepository, LayerRepository>();
        services.AddScoped<ITrackRepository, TrackRepository>();
        services.AddScoped<IAssetRepository, AssetRepository>();
        services.AddScoped<IUserRepository, UserRepository>();
        services.AddScoped<IUnitOfWork, UnitOfWork>();

        // Storage — lokalny folder na dysku
        var rootPath = configuration["Storage:LocalPath"];
        if (string.IsNullOrWhiteSpace(rootPath))
            rootPath = Path.Combine(AppContext.BaseDirectory, "uploads");

        var publicUrlPrefix = configuration["Storage:PublicUrlPrefix"];
        if (string.IsNullOrWhiteSpace(publicUrlPrefix))
            publicUrlPrefix = "https://localhost:7001/files";

        Directory.CreateDirectory(rootPath);

        services.AddSingleton<IStorageService>(_ =>
            new LocalFileStorageService(rootPath, publicUrlPrefix));

        return services;
    }
}
