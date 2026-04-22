# Medytao

Tworzenie medytacji z czterech warstw audio: **Music · Text · Nature · FX**.
Każda warstwa to uporządkowana lista uploadowanych ścieżek z własnym wolumenem, zapętleniem, fade in/out i crossfade.

## Stack (zero Dockera, zero narzędzi zewnętrznych)

| Warstwa | Technologia |
|---|---|
| API | ASP.NET Core 9 (minimal API + MediatR + EF Core) |
| Web | Blazor WebAssembly 9 |
| Mobile (później) | .NET MAUI |
| Baza | SQL Server LocalDB |
| Pliki | Lokalny folder `uploads/` obok API |
| Realtime | SignalR |
| Auth | JWT Bearer + BCrypt |

## Uruchomienie — 2 komendy

### Wymagania

- **.NET 9 SDK** — `dotnet --version` musi pokazać 9.x
- **SQL Server LocalDB** — jest z Visual Studio. Jeśli nie masz, zainstaluj [SQL Server Express LocalDB](https://www.microsoft.com/en-us/sql-server/sql-server-downloads) (~60MB).
  - Sprawdzenie: `sqllocaldb info MSSQLLocalDB`
  - Start (jeśli nie działa): `sqllocaldb start MSSQLLocalDB`

### Krok 1: Odpal API

```bash
cd src/Medytao.Api
dotnet run
```

Baza danych zostanie **automatycznie utworzona** przy pierwszym starcie — nie musisz uruchamiać żadnych migracji ani `dotnet ef`.

API startuje na **https://localhost:7001**. Dokumentacja Scalar: https://localhost:7001/scalar

### Krok 2: Odpal Blazor Web (w nowym terminalu)

```bash
cd src/Medytao.Web
dotnet run
```

Otwórz **https://localhost:5001**, zarejestruj konto, stwórz medytację i zacznij budować.

## Gdzie są dane?

- **Baza**: SQL Server LocalDB, baza nazywa się `Medytao_Dev` (możesz przeglądać w SSMS, Azure Data Studio lub zakładce SQL Server w Visual Studio)
- **Pliki**: `src/Medytao.Api/bin/Debug/net9.0/uploads/` — każdy upload dostaje swój GUID-owy podfolder
- **URL do pliku w przeglądarce**: `https://localhost:7001/files/{guid}/{filename}`

## Reset (jeśli chcesz zacząć od nowa)

Jeśli zmienisz model i chcesz wyczyścić bazę:

```bash
sqllocaldb stop MSSQLLocalDB
sqllocaldb delete MSSQLLocalDB
sqllocaldb start MSSQLLocalDB
```

Przy następnym `dotnet run` zostanie utworzona świeża baza.

**Uwaga**: `EnsureCreated` nie obsługuje zmian schematu — służy tylko do pierwszego utworzenia. Jeśli zmienisz modele encji, musisz zresetować bazę albo przejść na migracje (patrz niżej).

## Przejście na migracje EF Core (opcjonalne, później)

Kiedy schemat się ustabilizuje, możesz przełączyć się na migracje:

1. W `Program.cs` zmień `EnsureCreatedAsync()` na `MigrateAsync()`
2. Wygeneruj pierwszą migrację z **katalogu głównego solucji** (nie z API):

```bash
cd Medytao
dotnet ef migrations add InitialCreate --project src/Medytao.Infrastructure --startup-project src/Medytao.Api
```

## Struktura solucji

```
Medytao/
├── Medytao.sln
├── src/
│   ├── Medytao.Domain          # Encje, enumy, interfejsy repo
│   ├── Medytao.Application     # MediatR commands/queries
│   ├── Medytao.Infrastructure  # EF Core + SQL Server + LocalFileStorage
│   ├── Medytao.Shared          # DTOs
│   ├── Medytao.Api             # ASP.NET Core API
│   └── Medytao.Web             # Blazor WebAssembly
└── tests/
```

## Model czterech warstw

Każda medytacja ma zawsze cztery warstwy, tworzone automatycznie przy tworzeniu medytacji:

| Warstwa | Cel | Typowe assety |
|---|---|---|
| Music | Podkład muzyczny | Ambientowe MP3 |
| Text | Narracja / guidance | Nagrania głosowe WAV |
| Nature | Dźwięki natury | Deszcz, las, ocean (loopy) |
| FX | Akcenty i przejścia | Dzwonki, chimes, single hits |

Każda warstwa obsługuje **volume, mute, listę tracków**.
Każdy track obsługuje **volume, loop, fade in, fade out, start offset, crossfade**.

## Endpointy API

| Metoda | Ścieżka | Opis |
|---|---|---|
| POST | /api/v1/auth/register | Rejestracja |
| POST | /api/v1/auth/login | Login + JWT |
| GET | /api/v1/meditations | Lista medytacji |
| POST | /api/v1/meditations | Utwórz (tworzy 4 warstwy) |
| GET | /api/v1/meditations/{id} | Szczegóły |
| PUT | /api/v1/meditations/{id} | Aktualizuj |
| POST | /api/v1/meditations/{id}/publish | Publikuj |
| DELETE | /api/v1/meditations/{id} | Usuń |
| PUT | /api/v1/layers/{layerId} | Volume/mute warstwy |
| POST | /api/v1/layers/{layerId}/tracks | Dodaj track |
| PUT | /api/v1/layers/{layerId}/tracks/{trackId} | Aktualizuj track |
| DELETE | /api/v1/layers/{layerId}/tracks/{trackId} | Usuń track |
| PUT | /api/v1/layers/{layerId}/tracks/reorder | Zmień kolejność |
| GET | /api/v1/assets | Lista assetów |
| POST | /api/v1/assets/upload | Upload (multipart) |
| DELETE | /api/v1/assets/{id} | Usuń asset |
| GET | /files/{blobKey} | Pobierz uploadowany plik |

SignalR hub: `/hubs/preview` — broadcast zmian warstwy w czasie rzeczywistym.

## Troubleshooting

**"Cannot connect to (localdb)\\MSSQLLocalDB"** → `sqllocaldb start MSSQLLocalDB`. Jeśli komenda nie istnieje, zainstaluj SQL Server Express LocalDB.

**Pełny SQL Server zamiast LocalDB** → zmień connection string w `appsettings.Development.json`:
```
"Server=localhost;Database=Medytao;User Id=sa;Password=TwojeHaslo;TrustServerCertificate=True"
```

**CORS error w Blazor** → sprawdź, że `Cors:AllowedOrigins` w `appsettings.Development.json` zawiera `https://localhost:5001`.

**Audio się nie ładuje w przeglądarce** → sprawdź w DevTools zakładkę Network. URL powinien być `https://localhost:7001/files/...`. Jeśli 404, sprawdź że folder `uploads` istnieje i plik tam jest.

**HTTPS certificate warning** → `dotnet dev-certs https --trust` raz na maszynie.
