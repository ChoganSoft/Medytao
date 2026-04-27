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
