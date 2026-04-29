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

    // Master clock sesji: moment, w którym sesja efektywnie wystartowała,
    // skorygowany o startFromMs przy seeku (czyli "where ms 0 of the
    // meditation happened in wall-clock time"). ElapsedMs = now - tego.
    // Zapisywany dopiero po udanym medytaoPlayer.startSession, żeby zegar
    // nie ruszał gdy JS rzucił wyjątek.
    private DateTimeOffset? _sessionStartedAt;

    // Wall-clock moment ostatniego restartu sesji (start lub seek). Auto-stop
    // (gdy wszystkie warstwy raportują Finished) świadomie się wyciszasz przez
    // pierwsze ~2s — chroni przed scenariuszem "user kliknął suwak poza
    // wszystkimi triggerami, getProgress od razu zwraca Finished, sesja
    // by się sama zabiła zanim user zdąży kliknąć Stop". W takim przypadku
    // zegar tika dalej (sesja jest w pamięci, audio milczy), user może
    // jeszcze przesunąć suwak gdzieś indziej.
    private DateTimeOffset? _sessionRestartedAt;

    // Ostatnia załadowana medytacja — potrzebna SeekAsync, który restartuje
    // sesję z nową pozycją. Trzymamy referencję do DTO bezpośrednio (bo
    // sesja po refetchu mogłaby już nie istnieć w tej formie); Blazor i tak
    // disposuje service razem z całym tabem.
    private MeditationDetailDto? _lastMeditation;

    // Cache durationMs odkrytych przez JS (lazy-fetch metadanych przy starcie
    // sesji, bo backend ich nie zapisuje). Klucz = trackId, wartość = ms.
    // UI używa tego jako fallback gdy Asset.DurationMs w DTO jest NULL —
    // bez tego skala suwaka byłaby niedoszacowana i suwak osiągałby 100%
    // zanim audio dojdzie do końca.
    private readonly Dictionary<Guid, int> _fetchedDurations = new();

    // DotNetObjectReference przekazywany do JS — strona JS używa go do
    // wywołania ReportTrackDuration po pobraniu metadanych. Trzymamy
    // referencję żeby ją dispose'ować przy stop/dispose service.
    private DotNetObjectReference<PlaybackSessionService>? _dotNetRef;

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
    /// Zwraca durationMs tracka odkryte przez JS w bieżącej sesji, lub null
    /// jeśli nie było jeszcze pobrane (albo fetch się nie udał). Używane
    /// przez UI do liczenia skali timeline-a, gdy Asset.DurationMs w DTO
    /// jest NULL (typowy stan, bo upload nie zapisuje metadanych).
    /// </summary>
    public int? KnownDuration(Guid trackId) =>
        _fetchedDurations.TryGetValue(trackId, out var ms) ? ms : null;

    /// <summary>
    /// Wywoływane z JS (medytaoPlayer.ensureDurations) gdy uda się pobrać
    /// durationMs dla tracka bez metadanych. Aktualizujemy cache i emitujemy
    /// OnChanged żeby UI mogło natychmiast przeliczyć skalę suwaka.
    /// JSInvokable musi być publiczny i instance method.
    /// </summary>
    [JSInvokable]
    public void ReportTrackDuration(Guid trackId, int ms)
    {
        if (ms <= 0) return;
        _fetchedDurations[trackId] = ms;
        OnChanged?.Invoke();
    }

    /// <summary>
    /// Master-clock medytacji: ile ms minęło od pozycji "ms 0" tej sesji.
    /// Po seeku wartość natychmiast skacze na nową pozycję, bo
    /// _sessionStartedAt jest cofnięty o startFromMs. Zwraca 0 gdy nic
    /// nie gra.
    /// </summary>
    public double ElapsedMs
    {
        get
        {
            if (_sessionStartedAt is null) return 0;
            var ms = (DateTimeOffset.UtcNow - _sessionStartedAt.Value).TotalMilliseconds;
            return ms < 0 ? 0 : ms;
        }
    }

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

    public async Task StartAsync(MeditationDetailDto meditation, double startFromMs = 0)
    {
        // Idempotent: jeśli coś leciało, zatrzymaj pierwsze.
        if (_sessionId is not null) await StopAsync();

        if (startFromMs < 0) startFromMs = 0;

        // Zapamiętujemy referencję żeby SeekAsync mógł re-startować z nową pozycją
        // bez konieczności przeładowywania DTO przez API.
        _lastMeditation = meditation;

        // Świeży DotNetObjectReference per restart sesji — stary mógł być
        // już dispose'owany w StopAsync. Pojedyncza referencja na sesję,
        // JS używa jej w ensureDurations do raportowania pobranych metadanych.
        _dotNetRef?.Dispose();
        _dotNetRef = DotNetObjectReference.Create(this);

        var layers = meditation.Layers
            .Where(l => l.Tracks.Any())
            .Select(l => new
            {
                id = l.Id,
                // type ("Music"/"Nature"/"Text"/"Fx") steruje semantyką
                // time-anchored triggerów po stronie JS — Text/Fx jako overlay,
                // Music/Nature na razie ignored (wsparcie w Etapie 3).
                type = l.Type,
                volume = l.Volume,
                muted = l.Muted,
                tracks = l.Tracks.OrderBy(t => t.Order).Select(t => new
                {
                    trackId = t.Id,
                    url = t.Asset.Url,
                    volume = t.Volume,
                    loopCount = t.LoopCount,
                    // durationMs potrzebny po stronie JS dla fast-forward przy seeku —
                    // engine musi wiedzieć ile trwa każdy track żeby znaleźć właściwy
                    // sample na danej pozycji master clocka.
                    durationMs = t.Asset.DurationMs ?? 0,
                    // PlaybackRate = 1.0 oznacza "graj normalnie" — JS i tak
                    // ustawia preservesPitch=true, więc dla 1.0 nie ma efektu.
                    playbackRate = t.PlaybackRate,
                    // ReverbMix = 0 oznacza "bez efektu" — graf bypassuje convolver,
                    // koszt CPU zerowy. >0 wpina sample do współdzielonego
                    // ConvolverNode-a warstwy (Hall IR).
                    reverbMix = t.ReverbMix,
                    // null = sekwencyjny (gra wg Order); int = time-anchored
                    // (silnik użyje setTimeout z (startAtMs - startFromMs) jako
                    // delay, w trigger-ze odpali overlay <audio>).
                    startAtMs = t.StartAtMs,
                    // Fade i crossfade — silnik triggerCrossfade dla Music/Nature
                    // używa tych wartości żeby decydować jak długo ramp-ować
                    // volume. Bez przekazania ich do JS triggerCrossfade
                    // fallback-ował na DEFAULT_CROSSFADE_MS niezależnie od
                    // user settings.
                    fadeInMs = t.FadeInMs,
                    fadeOutMs = t.FadeOutMs,
                    crossfadeMs = t.CrossfadeMs
                }).ToArray()
            }).ToArray();

        if (layers.Length == 0)
        {
            // Nic do grania — nie zakładamy sesji.
            return;
        }

        try
        {
            _sessionId = await _js.InvokeAsync<string>("medytaoPlayer.startSession", (object)layers, startFromMs, _dotNetRef);
            _meditationId = meditation.Id;
            // Cofnij _sessionStartedAt o startFromMs, żeby ElapsedMs zwracał
            // od razu właściwą pozycję (a nie "od kliknięcia, którego user
            // nie zrobił"). Dla normalnego Play startFromMs=0 → start = teraz.
            _sessionStartedAt = DateTimeOffset.UtcNow.AddMilliseconds(-startFromMs);
            _sessionRestartedAt = DateTimeOffset.UtcNow;
        }
        catch (Exception ex)
        {
            _sessionId = null;
            _meditationId = null;
            _sessionStartedAt = null;
            _sessionRestartedAt = null;
            try { await _js.InvokeVoidAsync("console.error", "[PlaybackSession] startSession failed", ex.Message); } catch { }
            return;
        }

        StartProgressTimer();
        OnChanged?.Invoke();
    }

    /// <summary>
    /// Skacze do podanej pozycji medytacji. Najprostsza implementacja:
    /// stop + start z nowym startFromMs. Krótka cisza (~kilka ms) przy
    /// rebuilcie grafu audio, ale zachowuje wszystkie ustawienia (volume,
    /// reverb, rate) bo lecimy po tych samych DTO. Brak _lastMeditation
    /// (sesja jeszcze nie startowała) = no-op.
    /// </summary>
    public async Task SeekAsync(double targetMs)
    {
        if (_lastMeditation is null) return;
        await StartAsync(_lastMeditation, targetMs);
    }

    /// <summary>
    /// Pobiera durationMs dla wszystkich tracków medytacji bez startowania
    /// sesji. Wywoływane z MeditationEditor.OnInitializedAsync, żeby skala
    /// timeline-a była poprawna od razu po wejściu na stronę — bez czekania
    /// na pierwszy Play. Skipuje tracki, dla których duration już znamy
    /// (Asset.DurationMs z DTO lub poprzedni cache).
    /// </summary>
    public async Task PreloadDurationsAsync(MeditationDetailDto meditation)
    {
        var tracks = meditation.Layers
            .SelectMany(l => l.Tracks)
            .Where(t => !t.Asset.DurationMs.HasValue && !_fetchedDurations.ContainsKey(t.Id))
            .Select(t => new { trackId = t.Id, url = t.Asset.Url })
            .ToArray();
        if (tracks.Length == 0) return;

        // Lazy-tworzenie DotNetObjectReference jeśli sesja jeszcze nie
        // startowała. ReportTrackDuration potrzebuje go do callback-u z JS.
        if (_dotNetRef is null) _dotNetRef = DotNetObjectReference.Create(this);

        try
        {
            await _js.InvokeVoidAsync("medytaoPlayer.preloadDurations", (object)tracks, _dotNetRef);
        }
        catch (Exception ex)
        {
            try { await _js.InvokeVoidAsync("console.error", "[PlaybackSession] preloadDurations failed", ex.Message); } catch { }
        }
    }

    public async Task StopAsync()
    {
        StopProgressTimer();
        var sid = _sessionId;
        _sessionId = null;
        _meditationId = null;
        _sessionStartedAt = null;
        _sessionRestartedAt = null;
        _progressByLayer = new();
        // _lastMeditation świadomie zostaje — żeby user po Stop mógł znowu
        // kliknąć Play albo seekować bez ponownego ładowania DTO.

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

    /// <summary>
    /// Live-update wet/dry mixu reverbu pojedynczego tracka. Mutuje stan
    /// silnika, a jeśli track akurat gra — zmienia gain-y w grafie audio
    /// natychmiast. Współdzielony ConvolverNode (per-warstwa, Hall IR)
    /// jest tworzony lazy przy pierwszym mix > 0 w danej warstwie.
    /// </summary>
    public async Task SetTrackReverbMixAsync(Guid layerId, Guid trackId, float mix)
    {
        if (_sessionId is null) return;
        await SafeInvoke("medytaoPlayer.setTrackReverbMix", _sessionId, layerId.ToString(), trackId.ToString(), mix);
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
            //
            // Guard: nie auto-stop'uj sesji w pierwszych 2s od restartu. Po seeku
            // poza wszystkie scheduled triggery getProgress od razu zwraca
            // Finished=true, co bez tego guard-u kasowałoby sesję natychmiast,
            // suwak wracał do 0% i user widział "wraca do 0% i nic nie odtwarza".
            // 2s daje też user-owi szansę na kolejny seek, jeśli kliknął w pustkę.
            var sinceRestart = _sessionRestartedAt.HasValue
                ? (DateTimeOffset.UtcNow - _sessionRestartedAt.Value).TotalMilliseconds
                : double.MaxValue;
            if (items.Length > 0 && sid == _sessionId && items.All(p => p.Finished) && sinceRestart > 2000)
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
        _dotNetRef?.Dispose();
        _dotNetRef = null;
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
