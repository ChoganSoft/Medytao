using Microsoft.JSInterop;

namespace Medytao.Web.Services;

/// <summary>
/// Pomost między suwakami w UI a aktywną sesją odtwarzania w JS
/// (<c>window.medytaoPlayer</c>). <see cref="MeditationPlayer"/>
/// rejestruje tu sessionId po starcie sesji, a
/// <see cref="Components.Layers.LayerPanel"/> oraz
/// <see cref="Components.Layers.TrackCard"/> proszą o zmianę głośności
/// w czasie rzeczywistym — nie trzeba zatrzymywać playera, żeby zmiany
/// zadziałały.
/// </summary>
public sealed class PlaybackSessionService
{
    private readonly IJSRuntime _js;
    private string? _sessionId;

    public PlaybackSessionService(IJSRuntime js) => _js = js;

    public bool IsActive => _sessionId is not null;

    public void SetActiveSession(string? sessionId) => _sessionId = sessionId;

    public void ClearActiveSession() => _sessionId = null;

    public async Task SetLayerVolumeAsync(Guid layerId, float volume)
    {
        if (_sessionId is null) return;
        await SafeInvoke("medytaoPlayer.setLayerVolume", _sessionId, layerId.ToString(), volume);
    }

    public async Task SetLayerMutedAsync(Guid layerId, bool muted)
    {
        if (_sessionId is null) return;
        await SafeInvoke("medytaoPlayer.setLayerMuted", _sessionId, layerId.ToString(), muted);
    }

    public async Task SetTrackVolumeAsync(Guid layerId, Guid trackId, float volume)
    {
        if (_sessionId is null) return;
        await SafeInvoke("medytaoPlayer.setTrackVolume", _sessionId, layerId.ToString(), trackId.ToString(), volume);
    }

    private async Task SafeInvoke(string identifier, params object?[] args)
    {
        try
        {
            await _js.InvokeVoidAsync(identifier, args);
        }
        catch (Exception ex)
        {
            // Nie chcemy wywracać UI — ale też nie chcemy cicho zjeść błędu,
            // więc zgłaszamy w konsoli przeglądarki.
            try
            {
                await _js.InvokeVoidAsync("console.error", $"[PlaybackSession] {identifier} failed", ex.Message);
            }
            catch { }
        }
    }
}
