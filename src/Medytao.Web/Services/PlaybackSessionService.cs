using Medytao.Shared.Models;
using Microsoft.JSInterop;

namespace Medytao.Web.Services;

/// <summary>
/// Właściciel aktywnej sesji <c>window.medytaoPlayer</c> i źródło prawdy
/// o stanie odtwarzania w UI. Komponenty:
/// <list type="bullet">
///   <item><c>MeditationEditor</c> woła <see cref="StartAsync"/> / <see cref="StopAsync"/> z przycisku Play.</item>
///   <item><c>LayerPanel</c> i <c>TrackCard</c> zmieniają głośność w czasie rzeczywistym przez setLayer/SetTrack.</item>
///   <item><c>LayerPanel</c> dodatkowo subskrybuje <see cref="OnChanged"/> żeby odświeżać swój pasek postępu.</item>
/// </list>
/// Scoped w Blazor WASM = efektywnie singleton na całą sesję użytkownika.
/// </summary>
public sealed class PlaybackSessionService : IAsyncDisposable
{
    private readonly IJSRuntime _js;
    private string? _sessionId;
    private Guid? _meditationId;
    private System.Timers.Timer? _progressTimer;
    private Dictionary<Guid, LayerProgress> _progressByLayer = new();

    public PlaybackSessionService(IJSRuntime js) => _js = js;

    /// <summary>Sygnalizuje zmianę stanu (start / stop / tick progressu).</summary>
    public event Action? OnChanged;

    public bool IsActive => _sessionId is not null;

    /// <summary>Czy w ogóle coś gra (alias dla <see cref="IsActive"/> — nazwa dla czytelności w UI).</summary>
    public bool IsPlaying => _sessionId is not null;

    /// <summary>
    /// Id medytacji, która aktualnie leci — null gdy brak aktywnej sesji.
    /// Używane przez listę medytacji żeby wiedzieć, którą kartę podświetlić jako "grająca".
    /// </summary>
    public Guid? CurrentMeditationId => _meditationId;

    public LayerProgress? GetProgress(Guid layerId) =>
        _progressByLayer.TryGetValue(layerId, out var p) ? p : null;

    /// <summary>
    /// Zagregowany postęp całej medytacji — max(CurrentTime) i max(Duration) po warstwach.
    /// Loopy warstw powodują, że pełna ścisłość jest niemożliwa bez master clocka w JS,
    /// ale "jak daleko jesteśmy względem najdłuższej warstwy" to dobry intuicyjny wskaźnik
    /// dla karty medytacji. Gdy brak progressów — zwraca zera.
    /// </summary>
    public (double Current, double Total, bool Finished) GetOverallProgress()
    {
        if (_progressByLayer.Count == 0) return (0, 0, false);
        double maxCurrent = 0, maxDuration = 0;
        bool allFinished = true;
        foreach (var p in _progressByLayer.Values)
        {
            if (p.CurrentTime > maxCurrent) maxCurrent = p.CurrentTime;
            if (p.Duration > maxDuration) maxDuration = p.Duration;
            if (!p.Finished) allFinished = false;
        }
        return (maxCurrent, maxDuration, allFinished);
    }

    public async Task StartAsync(MeditationDetailDto meditation)
    {
        // Idempotent: jeśli coś leciało, zatrzymaj pierwsze.
        if (_sessionId is not null) await StopAsync();

        var layers = meditation.Layers
            .Where(l => l.Tracks.Any())
            .Select(l => new
            {
                id = l.Id,
                volume = l.Volume,
                muted = l.Muted,
                tracks = l.Tracks.OrderBy(t => t.Order).Select(t => new
                {
                    trackId = t.Id,
                    url = t.Asset.Url,
                    volume = t.Volume,
                    loopCount = t.LoopCount,
                    // PlaybackRate = 1.0 oznacza "graj normalnie" — JS i tak
                    // ustawia preservesPitch=true, więc dla 1.0 nie ma efektu.
                    playbackRate = t.PlaybackRate
                }).ToArray()
            }).ToArray();

        if (layers.Length == 0)
        {
            // Nic do grania — nie zakładamy sesji.
            return;
        }

        try
        {
            _sessionId = await _js.InvokeAsync<string>("medytaoPlayer.startSession", (object)layers);
            _meditationId = meditation.Id;
        }
        catch (Exception ex)
        {
            _sessionId = null;
            _meditationId = null;
            try { await _js.InvokeVoidAsync("console.error", "[PlaybackSession] startSession failed", ex.Message); } catch { }
            return;
        }

        StartProgressTimer();
        OnChanged?.Invoke();
    }

    public async Task StopAsync()
    {
        StopProgressTimer();
        var sid = _sessionId;
        _sessionId = null;
        _meditationId = null;
        _progressByLayer = new();

        if (sid is not null)
        {
            try { await _js.InvokeVoidAsync("medytaoPlayer.stopSession", sid); } catch { }
        }
        OnChanged?.Invoke();
    }

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

    /// <summary>
    /// Live-update tempa odtwarzania pojedynczego tracka. Jeśli track akurat
    /// gra w swojej warstwie — efekt słychać natychmiast (HTMLMediaElement
    /// .playbackRate aplikuje się on-the-fly z preservesPitch). Jeśli nie —
    /// silnik zapamięta wartość i użyje jej, gdy track wejdzie w sekwencję.
    /// </summary>
    public async Task SetTrackPlaybackRateAsync(Guid layerId, Guid trackId, float rate)
    {
        if (_sessionId is null) return;
        await SafeInvoke("medytaoPlayer.setTrackPlaybackRate", _sessionId, layerId.ToString(), trackId.ToString(), rate);
    }

    private void StartProgressTimer()
    {
        StopProgressTimer();
        _progressTimer = new System.Timers.Timer(250) { AutoReset = true };
        _progressTimer.Elapsed += OnProgressTick;
        _progressTimer.Start();
    }

    private void StopProgressTimer()
    {
        if (_progressTimer is null) return;
        _progressTimer.Elapsed -= OnProgressTick;
        _progressTimer.Stop();
        _progressTimer.Dispose();
        _progressTimer = null;
    }

    private async void OnProgressTick(object? sender, System.Timers.ElapsedEventArgs e)
    {
        var sid = _sessionId;
        if (sid is null) return;
        try
        {
            var items = await _js.InvokeAsync<LayerProgress[]>("medytaoPlayer.getProgress", sid);
            if (items is null) return;
            var map = new Dictionary<Guid, LayerProgress>(items.Length);
            foreach (var p in items)
            {
                if (p.LayerId != Guid.Empty) map[p.LayerId] = p;
            }
            _progressByLayer = map;

            // Gdy wszystkie warstwy dobiegły końca — zatrzymaj sesję samoczynnie.
            // Dzięki temu ring na karcie medytacji i w edytorze wraca do stanu spoczynku
            // bez ręcznego stopu. Sprawdzamy sid żeby nie wyścigać się z ręcznym StopAsync.
            if (items.Length > 0 && sid == _sessionId && items.All(p => p.Finished))
            {
                await StopAsync();
                return;
            }

            OnChanged?.Invoke();
        }
        catch
        {
            // Komponent może być już zdisposowany lub sesja zakończona.
        }
    }

    private async Task SafeInvoke(string identifier, params object?[] args)
    {
        try
        {
            await _js.InvokeVoidAsync(identifier, args);
        }
        catch (Exception ex)
        {
            try
            {
                await _js.InvokeVoidAsync("console.error", $"[PlaybackSession] {identifier} failed", ex.Message);
            }
            catch { }
        }
    }

    public async ValueTask DisposeAsync()
    {
        StopProgressTimer();
        if (_sessionId is not null)
        {
            try { await _js.InvokeVoidAsync("medytaoPlayer.stopSession", _sessionId); } catch { }
            _sessionId = null;
        }
    }

    public sealed class LayerProgress
    {
        public int LayerIndex { get; set; }
        public Guid LayerId { get; set; }
        public int TrackIndex { get; set; }
        public int TrackCount { get; set; }
        public double CurrentTime { get; set; }
        public double Duration { get; set; }
        public bool Finished { get; set; }
    }
}
