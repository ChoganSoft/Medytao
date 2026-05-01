using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using System.Text;
using Microsoft.IdentityModel.Tokens;
using Medytao.Domain.Entities;
using Medytao.Domain.Enums;
using Medytao.Domain.Interfaces;
using Medytao.Shared.Models;

namespace Medytao.Api.Endpoints;

public static class AuthEndpoints
{
    // Nazwa domyślnego programu tworzonego przy rejestracji i przy seed'zie
    // migracji (dla userów zarejestrowanych przed feature'em programów).
    // Trzymane jako const, żeby seed i auth używały tego samego stringa.
    // Nazwa wyświetlana userowi — angielski label (UI-facing). Zaktualizowana
    // do "Default program" — neutralna nazwa folderu który user może później
    // sam przemianować przez edit-btn na karcie programu. Stare nazwy
    // ("My meditations", "My sessions") są migrowane przez RenameDefaultProgramsAsync
    // przy starcie (idempotentnie).
    public const string DefaultProgramName = "Default program";

    // Predefiniowane kategorie sesji medytacyjnych — wspólna lista dla
    // rejestracji i seeda migracyjnego (Program.cs → SeedDefaultCategoriesAsync).
    // User może potem dodawać/usuwać własne na stronie /categories; ta
    // tablica to jedynie starter pack. Nazwy zdefiniowane po polsku — to
    // dane domenowe, nie UI label, więc nie tłumaczymy.
    public static readonly string[] DefaultCategoryNames =
    [
        "Relaksacja",
        "Medytacja",
        "Autohipnoza",
        "Kontemplacja",
        "Sesja oddechowa",
        "Dekret afirmacyjny",
        "Modlitwa",
        "Synchronizacja półkul mózgowych",
        "Wizualizacja",
        "Praca z Kronikami Akaszy",
    ];

    public static void MapAuthEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/v1/auth").WithTags("Auth");

        group.MapPost("/register", async (
            RegisterRequest req,
            IUserRepository users,
            IProgramRepository programs,
            ICategoryRepository categories,
            IUnitOfWork uow,
            IConfiguration cfg) =>
        {
            if (await users.GetByEmailAsync(req.Email) is not null)
                return Results.Conflict("Email already registered.");

            var emailNormalized = req.Email.ToLowerInvariant();
            // RoleSeed sprawdzane przy rejestracji — bez tego nowy user
            // (np. admin@medytao.com) dostawałby Free i musiałby czekać na
            // restart aplikacji żeby SeedUserRolesAsync go promował. Lookup
            // case-insensitive po emailu — appsettings może mieć "Admin@Medytao.com",
            // baza i tak normalizuje do lowercase. null = "brak wpisu" → Free default.
            var seededRole = ResolveSeededRole(emailNormalized, cfg);

            var user = new User
            {
                Email = emailNormalized,
                DisplayName = req.DisplayName,
                PasswordHash = BCrypt.Net.BCrypt.HashPassword(req.Password),
                Role = seededRole ?? UserRole.Free
            };

            await users.AddAsync(user);

            // Auto-default program — user od razu po rejestracji ma gdzie
            // tworzyć medytacje (bez dodatkowego kroku "stwórz program").
            var defaultProgram = MeditationProgram.Create(user.Id, DefaultProgramName);
            await programs.AddAsync(defaultProgram);

            // Predefiniowane kategorie — te same 10 co w seedzie migracyjnym.
            foreach (var name in DefaultCategoryNames)
            {
                await categories.AddAsync(MeditationCategory.Create(user.Id, name));
            }

            await uow.SaveChangesAsync();
            return Results.Ok(GenerateToken(user, cfg));
        });

        group.MapPost("/login", async (LoginRequest req, IUserRepository users, IUnitOfWork uow, IConfiguration cfg) =>
        {
            var user = await users.GetByEmailAsync(req.Email.ToLowerInvariant());
            if (user is null || !BCrypt.Net.BCrypt.Verify(req.Password, user.PasswordHash))
                return Results.Unauthorized();

            // Lazy upgrade roli z RoleSeed — JEŚLI config ma email usera, używamy
            // wartości stamtąd jako "źródła prawdy". null = brak wpisu w configu,
            // wtedy zostawiamy rolę z bazy (która mogła zostać zmieniona przez
            // panel /users). Bez tego null-checka login każdego usera spoza
            // RoleSeed demotował go do Free — kasując efekty promocji UI.
            var seededRole = ResolveSeededRole(user.Email, cfg);
            if (seededRole.HasValue && seededRole.Value != user.Role)
            {
                user.Role = seededRole.Value;
                user.UpdatedAt = DateTimeOffset.UtcNow;
                await users.UpdateAsync(user);
                await uow.SaveChangesAsync();
            }

            return Results.Ok(GenerateToken(user, cfg));
        });
    }

    // RoleSeed lookup — wartości w appsettings są case-insensitive zarówno
    // po stronie klucza (email) jak i wartości (nazwa roli). Zwraca:
    //   - UserRole gdy jest wpis dla emaila i wartość poprawnie sparsowana
    //   - null gdy brak wpisu (caller decyduje co robić — Register: Free default,
    //     Login: zostaw rolę z bazy, żeby promocje przez UI nie były kasowane)
    //   - null gdy literówka roli (z log warning na startupie via SeedUserRolesAsync)
    // Klucze rozpoczynające się od "_" (np. _comment, _example_*) są pomijane —
    // umownie traktujemy je jako dokumentację w configu, nie wpisy.
    internal static UserRole? ResolveSeededRole(string emailNormalized, IConfiguration cfg)
    {
        var section = cfg.GetSection("RoleSeed");
        if (!section.Exists()) return null;
        foreach (var entry in section.GetChildren())
        {
            if (entry.Key.StartsWith('_')) continue;
            if (string.Equals(entry.Key, emailNormalized, StringComparison.OrdinalIgnoreCase))
            {
                return Enum.TryParse<UserRole>(entry.Value, ignoreCase: true, out var role)
                    ? role
                    : null;
            }
        }
        return null;
    }

    private static AuthTokenDto GenerateToken(User user, IConfiguration cfg)
    {
        var jwtSettings = cfg.GetSection("Jwt");
        var key = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(jwtSettings["Secret"]!));
        var expires = DateTimeOffset.UtcNow.AddHours(double.Parse(jwtSettings["ExpiryHours"] ?? "24"));

        var token = new JwtSecurityToken(
            issuer: jwtSettings["Issuer"],
            audience: jwtSettings["Audience"],
            claims: [
                new Claim(ClaimTypes.NameIdentifier, user.Id.ToString()),
                new Claim(ClaimTypes.Email, user.Email),
                new Claim(ClaimTypes.Name, user.DisplayName),
                // ClaimTypes.Role — standardowa nazwa, ASP.NET Core
                // [Authorize(Roles="...")] i policy assertions szukają właśnie tu.
                new Claim(ClaimTypes.Role, user.Role.ToString())
            ],
            expires: expires.UtcDateTime,
            signingCredentials: new SigningCredentials(key, SecurityAlgorithms.HmacSha256)
        );

        return new AuthTokenDto(
            new JwtSecurityTokenHandler().WriteToken(token),
            expires,
            user.DisplayName,
            user.Role.ToString()
        );
    }
}

public record RegisterRequest(string Email, string DisplayName, string Password);
public record LoginRequest(string Email, string Password);
