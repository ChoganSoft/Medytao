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

// Preset reverbu nakładanego na całą warstwę. Off = brak efektu (graf
// audio omija ConvolverNode), Room = krótkie, naturalne odbicia, Hall =
// dłuższe, przestrzenne. Faktyczne IR (impulse response) generujemy
// syntetycznie po stronie klienta — zob. wwwroot/js/audioPreview.js.
// Liczba presetów świadomie mała: medytacja nie potrzebuje studyjnej
// gradacji, a każdy dodatkowy preset = kolejny algorytmiczny IR.
public enum LayerReverbPreset
{
    Off = 0,
    Room = 1,
    Hall = 2
}
