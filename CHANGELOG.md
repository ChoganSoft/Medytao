# Changelog

Format oparty o [Keep a Changelog](https://keepachangelog.com/),
wersjonowanie [SemVer](https://semver.org/).

## [0.11.0] — 2026-04-22

### Changed
- `Track.Loop` (bool) zastąpiony przez `Track.LoopCount` (int):
  - `0` = loop forever (dawne `Loop = true`)
  - `1` = odtwórz raz (default)
  - `N` = odtwórz N razy
- API: endpointy create/update track przyjmują `loopCount` zamiast `loop`
- `TrackDto` i `TrackCard` zaktualizowane do `LoopCount`
- `LayerPanel` przekazuje `BlocksSequence` do `TrackCard` — ostrzeżenie gdy track z `LoopCount=0` blokuje kolejne tracki w sekwencji
- Schemat DB: kolumna `Loop` (bit) → `LoopCount` (int), domyślna wartość 1

### Migration notes
Breaking DB schema change — reset dev database before first run:

Database will be re-created on next API start via `EnsureCreated`.

After resetting the database, clear `auth_token` in browser localStorage
(DevTools → Application → Local Storage → remove `auth_token`) and register
a new account. Existing tokens reference users that no longer exist.

API clients must send `loopCount` instead of `loop` in track create/update requests.

## [0.10.2] — 2026-04-22

### Changed
- Dev database migrated from SQL Server LocalDB to SQL Server Express.
  Connection string in `appsettings.json` now points to `.\SQLEXPRESS` and
  database name changed from `Medytao` to `Medytao.Db`.

### Migration notes
Wymagania: zainstalowany lokalnie SQL Server (np. Express) z instancją `SQLEXPRESS`.
Stara baza w LocalDB (`Medytao`) nie jest już używana — można ją skasować lub zostawić.
Nowa baza `Medytao.Db` utworzy się automatycznie przez EnsureCreated przy pierwszym starcie API.

## [0.10.1] — 2026-04-19

### Fixed
- `AudioPreviewButton` rzucał `ReferenceError: arguments is not defined` przy kliknięciu.
  Zamieniony `eval("arguments[0].play()", ref)` na dedykowane helpery
  `medytaoAudio.play/pause/stop` w `window`.
- `audioPreview.js` przepisany — jeden wspólny namespace `medytaoAudio`, ze wszystkimi
  operacjami na elementach audio (`play`, `pause`, `stop`, `setVolume`, `pauseAll`).
  `meditationPlayer` pozostał jako alias dla kompatybilności wstecz.


## [0.10.0] — 2026-04-19

### Changed
- **Rebrand: MeditationBuilder → Medytao**
  - Solution: `MeditationBuilder.sln` → `Medytao.sln`
  - Wszystkie projekty: `MeditationBuilder.Api` → `Medytao.Api`, itd.
  - Wszystkie namespace'y: `MeditationBuilder.Domain` → `Medytao.Domain`, itd.
  - Nazwa bazy: `MeditationBuilder_Dev` → `Medytao_Dev`
  - JWT issuer/audience: `Medytao.Api` / `Medytao.Clients`
  - Brand w UI: "◎ MeditationBuilder" → "◎ Medytao"

### Migration notes
Po upgrade musisz:
1. Wykasować starą bazę w LocalDB: `sqllocaldb stop MSSQLLocalDB` → `sqllocaldb delete MSSQLLocalDB` → `sqllocaldb start MSSQLLocalDB` (nowa baza `Medytao_Dev` utworzy się automatycznie)
2. Wyczyścić auth_token w localStorage przeglądarki (DevTools → Application → Local Storage → usuń klucz `auth_token`)


## [0.9.0] — 2026-04-19

Pierwsza wersja działająca end-to-end — auth, CRUD medytacji, upload plików,
podsłuch pojedynczych tracków, full preview medytacji.

### Added
- `AudioPreviewButton` — reużywalny przycisk play/pause obok każdego assetu audio
  (w TrackCard, Assets, AssetPickerModal)
- `MeditationPlayer` — modalny player odtwarzający wszystkie 4 warstwy jednocześnie
  z poszanowaniem volume × mute × loop
- CORS headers dla plików statycznych pod `/files/*` + `Accept-Ranges`
  dla seeking w długich plikach audio
- Auth guard na wszystkich chronionych stronach (Index, Assets, MeditationEditor)
- `AuthService.IsAuthenticatedAsync()` + idempotentne `InitializeAsync()`
- Limit uploadu podniesiony do 500 MB (Kestrel + FormOptions + Blazor InputFile)
- Serwowanie plików z lokalnego folderu pod `/files`
- `LocalFileStorageService` — lokalny storage, bez Dockera
- SQL Server LocalDB zamiast Postgresa w kontenerze
- `EnsureCreated` zamiast migracji EF Core — start bez `dotnet ef`

### Changed
- Wymienione `Npgsql.EntityFrameworkCore.PostgreSQL` na `Microsoft.EntityFrameworkCore.SqlServer`
- Wymienione `Azure.Storage.Blobs` na `LocalFileStorageService`
- Usunięty `docker-compose.yml`

### Fixed
- Kolizja nazw `Login.Login()` i `Register.Register()` (C# uznawał to za konstruktory)
- Pusty string w `Storage:LocalPath` wysypywał `Directory.CreateDirectory`
- Brakujące usingi dla `Scalar.AspNetCore` i endpointów w `Program.cs`
- Zła wersja pakietu `Scalar.AspNetCore` (2.0.0 nie istniało → 2.9.0)

## Nieopublikowane — plany

### Na v0.10.0
- Sekwencyjne odtwarzanie tracków w warstwie
- Fade in / out i crossfade w playerze
- Drag-and-drop do reorderingu tracków

### Na v1.0.0
- .NET MAUI aplikacja mobilna
- Export medytacji do jednego MP3
- Publiczna biblioteka medytacji + udostępnianie
