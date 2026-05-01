using System.Text;
using MediatR;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.FileProviders;
using Microsoft.IdentityModel.Tokens;
using Scalar.AspNetCore;
using Medytao.Infrastructure.Extensions;
using Medytao.Infrastructure.Persistence;
using Medytao.Api.Endpoints;
using Medytao.Api.Middleware;
using Medytao.Api.SignalR;
using Medytao.Application.Meditations.Commands;
using Medytao.Domain.Entities;
using Medytao.Domain.Enums;

var builder = WebApplication.CreateBuilder(args);

// ── Services ──────────────────────────────────────────────────────────────────

// Upload limit — 500 MB (Kestrel + multipart form)
const long MaxUploadBytes = 500L * 1024 * 1024;

builder.WebHost.ConfigureKestrel(options =>
    options.Limits.MaxRequestBodySize = MaxUploadBytes);

builder.Services.Configure<Microsoft.AspNetCore.Http.Features.FormOptions>(options =>
{
    options.MultipartBodyLengthLimit = MaxUploadBytes;
    options.ValueLengthLimit = int.MaxValue;
});

builder.Services.AddInfrastructure(builder.Configuration);

builder.Services.AddMediatR(cfg =>
    cfg.RegisterServicesFromAssembly(typeof(CreateMeditationCommand).Assembly));

builder.Services.AddAuthentication(JwtBearerDefaults.AuthenticationScheme)
    .AddJwtBearer(options =>
    {
        var jwtSettings = builder.Configuration.GetSection("Jwt");
        options.TokenValidationParameters = new TokenValidationParameters
        {
            ValidateIssuer = true,
            ValidateAudience = true,
            ValidateLifetime = true,
            ValidateIssuerSigningKey = true,
            ValidIssuer = jwtSettings["Issuer"],
            ValidAudience = jwtSettings["Audience"],
            IssuerSigningKey = new SymmetricSecurityKey(
                Encoding.UTF8.GetBytes(jwtSettings["Secret"]!))
        };

        // Allow SignalR to pass JWT via query string
        options.Events = new JwtBearerEvents
        {
            OnMessageReceived = ctx =>
            {
                var token = ctx.Request.Query["access_token"];
                if (!string.IsNullOrEmpty(token) &&
                    ctx.HttpContext.Request.Path.StartsWithSegments("/hubs"))
                {
                    ctx.Token = token;
                }
                return Task.CompletedTask;
            }
        };
    });

// Policies RBAC — hierarchiczne (Guru jest też Master). Logika "co najmniej rola X"
// żyje w ClaimsPrincipalExtensions.IsAtLeast; tu tylko nazywamy policy by można je
// wpinać przez .RequireAuthorization("RequireMaster") na endpointach.
//
// Endpoints które chcą "Master+" — używają RequireMaster.
// Endpoints które wymagają striktnie Guru — RequireGuru.
// Mieszane przypadki (np. "endpoint OK dla Master, ale gdy w body StartAtMs != null
// to wymaga Guru") obsługujemy ręcznie w handlerze/endpoint-lambda, bo policy nie
// widzi body requestu.
builder.Services.AddAuthorizationBuilder()
    .AddPolicy("RequireMaster", p => p.RequireAssertion(ctx => ctx.User.IsAtLeast(UserRole.Master)))
    .AddPolicy("RequireGuru", p => p.RequireAssertion(ctx => ctx.User.IsAtLeast(UserRole.Guru)))
    // Admin policy — strikt, nie hierarchical-or-above. Admin to operacyjna
    // rola separowana od content-roles; wszystkie endpointy user management
    // wymagają DOKŁADNIE Admin (nawet Guru ich nie dostaje, mimo że jest
    // niżej w intowanej hierarchii — IsAtLeast(Admin) i tak działa identycznie
    // bo jest to najwyższa wartość).
    .AddPolicy("RequireAdmin", p => p.RequireAssertion(ctx => ctx.User.IsAtLeast(UserRole.Admin)));
builder.Services.AddSignalR();
builder.Services.AddOpenApi();

builder.Services.AddCors(options =>
    options.AddDefaultPolicy(policy =>
        policy.WithOrigins(builder.Configuration["Cors:AllowedOrigins"]!.Split(','))
              .AllowAnyHeader()
              .AllowAnyMethod()
              .AllowCredentials()));

// ── Pipeline ──────────────────────────────────────────────────────────────────

var app = builder.Build();

// Tworzenie bazy przy pierwszym starcie (dev) — bez potrzeby uruchamiania `dotnet ef`
if (app.Environment.IsDevelopment())
{
    using var scope = app.Services.CreateScope();
    var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
    await db.Database.EnsureCreatedAsync();

    // EnsureCreated nie dodaje brakujących kolumn do istniejących tabel
    // (to nie jest migracja). Dla istniejących baz dev manualnie ALTER TABLE,
    // idempotentnie. Po przejściu na formalne migracje EF te funkcje skasujemy.
    await EnsureUserRoleColumnAsync(db);
    await EnsureMeditationMinRoleColumnAsync(db);

    // Migracja danych dla feature'a programów: userzy zarejestrowani przed
    // wprowadzeniem programów nie mają żadnego programu. Tworzymy im domyślny
    // ("My meditations") i wpinamy do niego ich wszystkie medytacje. Bez tego
    // dotychczasowe medytacje byłyby niewidoczne z poziomu UI (lista teraz
    // startuje od programów, nie od medytacji).
    //
    // Idempotentne — sprawdzamy `Programs.Any()` per user, więc można to
    // odpalać na każdym starcie bez efektów ubocznych. Gdy przejdziemy na
    // formalne migracje EF, to się przeniesie do osobnego data-seed scriptu.
    await SeedDefaultProgramsAsync(db);

    // Analogicznie dla kategorii — userzy bez żadnej kategorii dostają
    // predefiniowaną dziesiątkę. Idempotentne (sprawdza Categories.Any()).
    await SeedDefaultCategoriesAsync(db);

    // Promocja userów do wyższych ról na podstawie konfiguracji w appsettings.
    // Idempotentne — można odpalać na każdym starcie. Userzy nieznajdujący się
    // w mapping pozostają z bieżącą rolą (default Free dla świeżo zarejestrowanych).
    await SeedUserRolesAsync(db, app.Configuration);

    app.MapOpenApi();
    app.MapScalarApiReference();
}

static async Task SeedDefaultProgramsAsync(AppDbContext db)
{
    var usersWithoutProgram = await Microsoft.EntityFrameworkCore.EntityFrameworkQueryableExtensions
        .ToListAsync(
            Microsoft.EntityFrameworkCore.EntityFrameworkQueryableExtensions.Include(db.Users, u => u.Meditations)
                .Where(u => !u.Programs.Any()));

    if (usersWithoutProgram.Count == 0) return;

    foreach (var user in usersWithoutProgram)
    {
        var program = MeditationProgram.Create(user.Id, AuthEndpoints.DefaultProgramName);
        // Przypinamy wszystkie istniejące medytacje usera do tego programu.
        foreach (var m in user.Meditations)
        {
            program.Meditations.Add(m);
        }
        db.Programs.Add(program);
    }

    await db.SaveChangesAsync();
}

static async Task SeedDefaultCategoriesAsync(AppDbContext db)
{
    // Bierzemy userów, którzy nie mają ŻADNEJ kategorii — nie chcemy dosypywać
    // predefiniowanych, jeśli user już coś u siebie poustawiał (np. skasował
    // pięć nieużywanych i zostawił pięć). To by dublowało wpisy i ignorowało
    // jego wybory. Idempotentność wystarcza na "u=ncleansed" userów.
    var usersWithoutCategory = await Microsoft.EntityFrameworkCore.EntityFrameworkQueryableExtensions
        .ToListAsync(db.Users.Where(u => !u.Categories.Any()));

    if (usersWithoutCategory.Count == 0) return;

    foreach (var user in usersWithoutCategory)
    {
        foreach (var name in AuthEndpoints.DefaultCategoryNames)
        {
            db.Categories.Add(MeditationCategory.Create(user.Id, name));
        }
    }

    await db.SaveChangesAsync();
}

// Idempotentne dodanie kolumny Role do tabeli Users dla istniejących baz.
// EnsureCreated tylko CREATE TABLE, ale nie ALTER. Skasujemy gdy przejdziemy
// na formalne migracje EF. SQL Server-specific (sys.columns / sys.tables).
static async Task EnsureUserRoleColumnAsync(AppDbContext db)
{
    const string sql = """
        IF EXISTS (SELECT 1 FROM sys.tables WHERE name = N'Users')
            AND NOT EXISTS (
                SELECT 1 FROM sys.columns
                WHERE Name = N'Role' AND Object_ID = Object_ID(N'Users')
            )
        BEGIN
            ALTER TABLE Users ADD Role INT NOT NULL DEFAULT 0;
        END
    """;
    await db.Database.ExecuteSqlRawAsync(sql);
}

// Idempotentne dodanie kolumny MinRoleRequired do tabeli Meditations.
// Default 0 = UserRole.Free → istniejące Published medytacje są widoczne
// dla wszystkich (nie chcemy ich nagle ukrywać po wprowadzeniu sharing systemu).
static async Task EnsureMeditationMinRoleColumnAsync(AppDbContext db)
{
    const string sql = """
        IF EXISTS (SELECT 1 FROM sys.tables WHERE name = N'Meditations')
            AND NOT EXISTS (
                SELECT 1 FROM sys.columns
                WHERE Name = N'MinRoleRequired' AND Object_ID = Object_ID(N'Meditations')
            )
        BEGIN
            ALTER TABLE Meditations ADD MinRoleRequired INT NOT NULL DEFAULT 0;
        END
    """;
    await db.Database.ExecuteSqlRawAsync(sql);
}

// Promocja userów do ról zdefiniowanych w appsettings:
//   "RoleSeed": { "user@example.com": "Master", "admin@example.com": "Guru" }
// Idempotentne — jeśli user nie istnieje w bazie (jeszcze się nie zarejestrował),
// po prostu pomijamy go. Przy każdym kolejnym starcie aplikacji próba zostanie
// powtórzona, więc gdy user się zarejestruje, dostanie podbicie rangi automatycznie.
// Niezdefiniowane / nieznane wartości stringa są ignorowane (log warn) — bezpiecznie.
static async Task SeedUserRolesAsync(AppDbContext db, IConfiguration cfg)
{
    var section = cfg.GetSection("RoleSeed");
    if (!section.Exists()) return;

    var changed = false;
    foreach (var entry in section.GetChildren())
    {
        // Klucze "_*" to umownie dokumentacja w configu (komentarze, przykłady) —
        // pomijamy zarówno tutaj jak i w ResolveSeededRole na hot-path Login/Register.
        if (entry.Key.StartsWith('_')) continue;
        var email = entry.Key.ToLowerInvariant();
        var roleStr = entry.Value;
        if (string.IsNullOrWhiteSpace(email) || string.IsNullOrWhiteSpace(roleStr)) continue;
        // ignoreCase:true — toleruje "guru"/"GURU"/"Guru". Nazwy ról
        // w appsettings są dla człowieka, niech literówka wielkości liter
        // nie wymagała czytania kodu źródłowego.
        if (!Enum.TryParse<UserRole>(roleStr, ignoreCase: true, out var role))
        {
            Console.WriteLine($"[RoleSeed] Unknown role '{roleStr}' for {email}; skipping.");
            continue;
        }

        var user = await Microsoft.EntityFrameworkCore.EntityFrameworkQueryableExtensions
            .FirstOrDefaultAsync(db.Users, u => u.Email == email);
        if (user is null) continue;
        if (user.Role == role) continue;

        user.Role = role;
        changed = true;
    }

    if (changed) await db.SaveChangesAsync();
}

app.UseExceptionHandler(new ExceptionHandlerOptions
{
    ExceptionHandler = GlobalExceptionHandler.Handle
});

app.UseHttpsRedirection();
app.UseCors();

// Serwowanie uploadowanych plików z lokalnego folderu pod /files
var uploadsPath = builder.Configuration["Storage:LocalPath"];
if (string.IsNullOrWhiteSpace(uploadsPath))
    uploadsPath = Path.Combine(AppContext.BaseDirectory, "uploads");
Directory.CreateDirectory(uploadsPath);

app.UseStaticFiles(new StaticFileOptions
{
    FileProvider = new PhysicalFileProvider(uploadsPath),
    RequestPath = "/files",
    ServeUnknownFileTypes = true, // m.in. .wav, .mp3
    OnPrepareResponse = ctx =>
    {
        // Pozwól Blazorowi (inny origin) odtwarzać pliki audio
        ctx.Context.Response.Headers.AccessControlAllowOrigin = "*";
        ctx.Context.Response.Headers.AcceptRanges = "bytes";
    }
});

app.UseAuthentication();
app.UseAuthorization();

// ── Route groups ──────────────────────────────────────────────────────────────
var api = app.MapGroup("/api/v1").RequireAuthorization();

app.MapAuthEndpoints();
api.MapProgramEndpoints();
api.MapCategoryEndpoints();
api.MapMeditationEndpoints();
api.MapLayerEndpoints();
api.MapAssetEndpoints();
api.MapUserEndpoints();

app.MapHub<PreviewHub>("/hubs/preview");

app.Run();
