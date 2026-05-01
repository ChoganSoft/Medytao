namespace Medytao.Domain.Enums;

public enum LayerType
{
    Music = 1,
    Text = 2,
    Nature = 3,
    Fx = 4
}

// Stary enum AssetType (Audio/Text) zniknął razem z migracją na hierarchię
// per-warstwa (TPH). Reprezentacja danego asetu wynika z konkretnej podklasy
// (MusicAsset/NatureAsset/TextAsset/FxAsset) plus ContentType (MIME). Gdy
// TextAsset zacznie obsługiwać generowane TTS, jego treść odróżnimy nową
// kolumną na samej podklasie, a nie globalnym enumem.

public enum MeditationStatus
{
    Draft = 1,
    Published = 2,
    Archived = 3
}

// Reverb wcześniej był enumem per-warstwa (Off/Room/Hall); od refaktoru
// "track-level reverb" mamy jeden zaszyty preset (Hall) i sterowanie wet/dry
// per Track przez Track.ReverbMix. Enum zniknął, bo nie było już co przechowywać.

// Role użytkownika (RBAC) — hierarchiczne, każda kolejna rozszerza poprzednią.
// Wartości intowane (0..4) żeby wprost porównywać `>=` przez kod
// (np. requirement "Master+" = role >= UserRole.Master).
//
//   Free       — domyślna rola po rejestracji. Tylko odsłuch gotowych sesji
//                (sharing system: widzi sesje Published z MinRoleRequired=Free).
//   Apprentice — analogicznie jak Free + ewentualnie pakiety dla wyższego progu.
//   Master     — może tworzyć sesje, ale tylko w trybie sekwencyjnym
//                (bez StartAtMs na trackach).
//   Guru       — pełen dostęp do tworzenia treści, w tym tryb time-anchored.
//   Admin      — operacyjna rola "powyżej" content roli Guru. Auto-spełnia
//                wszystkie wymogi >=Master, >=Guru przez hierarchiczny int,
//                dodatkowo daje dostęp do user management (zmiana ról innych
//                userów). Promocja do Admin TYLKO przez RoleSeed (appsettings) —
//                inni Admini przez UI nie mogą promować do Admin (anti-eskalacja).
public enum UserRole
{
    Free = 0,
    Apprentice = 1,
    Master = 2,
    Guru = 3,
    Admin = 4
}

// Helpery semantyczne nad UserRole. Ekspozycja "co dana rola może" zamiast
// surowego `>= UserRole.Master` w wielu miejscach — jak będziemy zmieniać
// zakres uprawnień (np. Apprentice dostaje read-only assety), zmienimy
// w jednym miejscu, a wszystkie call-site'y zaktualizują się.
public static class UserRoleExtensions
{
    public static bool CanCreateSessions(this UserRole role) => role >= UserRole.Master;
    public static bool CanUseTimeAnchored(this UserRole role) => role >= UserRole.Guru;
    public static bool CanManageUsers(this UserRole role) => role >= UserRole.Admin;
}
