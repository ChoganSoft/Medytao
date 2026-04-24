using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using System.Text;
using Microsoft.IdentityModel.Tokens;
using Medytao.Domain.Entities;
using Medytao.Domain.Interfaces;
using Medytao.Shared.Models;

namespace Medytao.Api.Endpoints;

public static class AuthEndpoints
{
    // Nazwa domyślnego programu tworzonego przy rejestracji i przy seed'zie
    // migracji (dla userów zarejestrowanych przed feature'em programów).
    // Trzymane jako const, żeby seed i auth używały tego samego stringa.
    public const string DefaultProgramName = "My meditations";

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

            var user = new User
            {
                Email = req.Email.ToLowerInvariant(),
                DisplayName = req.DisplayName,
                PasswordHash = BCrypt.Net.BCrypt.HashPassword(req.Password)
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

        group.MapPost("/login", async (LoginRequest req, IUserRepository users, IConfiguration cfg) =>
        {
            var user = await users.GetByEmailAsync(req.Email.ToLowerInvariant());
            if (user is null || !BCrypt.Net.BCrypt.Verify(req.Password, user.PasswordHash))
                return Results.Unauthorized();

            return Results.Ok(GenerateToken(user, cfg));
        });
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
                new Claim(ClaimTypes.Name, user.DisplayName)
            ],
            expires: expires.UtcDateTime,
            signingCredentials: new SigningCredentials(key, SecurityAlgorithms.HmacSha256)
        );

        return new AuthTokenDto(
            new JwtSecurityTokenHandler().WriteToken(token),
            expires,
            user.DisplayName
        );
    }
}

public record RegisterRequest(string Email, string DisplayName, string Password);
public record LoginRequest(string Email, string Password);
