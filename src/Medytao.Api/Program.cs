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

builder.Services.AddAuthorization();
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
api.MapMeditationEndpoints();
api.MapLayerEndpoints();
api.MapAssetEndpoints();

app.MapHub<PreviewHub>("/hubs/preview");

app.Run();
