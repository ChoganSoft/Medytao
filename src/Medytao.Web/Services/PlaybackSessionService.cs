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
    private readonly AssetService _assets;
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

    // Pozycja w master-clocku w momencie ostatniej pauzy. Null = nie spauzowane
    // (sesja gra normalnie albo brak sesji). Stan pauzy jest ortogonalny do
    // _sessionId — przy pauzie robimy pełny teardown w JS (bo to MVP wariant
    // "restart from elapsed"), ale C# pamięta _pausedAtMs i _lastMeditation,
    // żeby ResumeAsync mógł wystartować JS od zapamiętanej pozycji.
    private double? _pausedAtMs;

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
    // sesji). Klucz = ASSET id (nie trackId), bo duration to property pliku
    // a nie referencji do niego — dwa tracki używające tego samego asset-u
    // dzielą metadane. UI używa tego jako fallback gdy Asset.DurationMs
    // w DTO jest NULL.
    private readonly Dictionary<Guid, int> _fetchedDurations = new();

    // DotNetObjectReference przekazywany do JS — strona JS używa go do
    // wywołania ReportAssetDuration po pobraniu metadanych. Trzymamy
    // referencję żeby ją dispose'ować przy stop/dispose service.
    private DotNetObjectReference<PlaybackSessionService>? _dotNetRef;

    // Pamięć assetId-ów już zapersistowanych do bazy w tej sesji service-a —
    // żeby setDuration HTTP call nie strzelał wielokrotnie dla tego samego
    // asset-u (np. ten sam asset w kilku trackach, ten sam track w wielu
    // sesjach Play). Backend i tak ignoruje duplikaty (handler sprawdza
    // czy duration już zapisany), ale po co generować zbędne requesty.
    private readonly HashSet<Guid> _persistedAssets = new();

    public PlaybackSessionService(IJSRuntime js, AssetService assets)
    {
        _js = js;
        _assets = assets;
    }

    /// <summary>Sygnalizuje zmianę stanu (start / stop / tick progressu).</summary>
    public event Action? OnChanged;

    /// <summary>
    /// Czy aktualnie gra dźwięk (sesja JS żywa, nie spauzowana). False zarówno
    /// gdy nic nie startowało, jak i gdy user spauzował (wtedy <see cref="IsPaused"/>=true).
    /// </summary>
    public bool IsPlaying => _sessionId is not null;

    /// <summary>
    /// Czy sesja jest spauzowana. JS-owej sesji nie ma (zrobiliśmy teardown
    /// żeby zatrzymać dźwięk), ale C# pamięta pozycję i medytację, więc
    /// <see cref="ResumeAsync"/> wznowi od tego miejsca.
    /// </summary>
    public bool IsPaused => _pausedAtMs is not null;

    /// <summary>
    /// Czy karta medytacji powinna być traktowana jako "trwa odtwarzanie"
    /// (gra ALBO spauzowana). Ten fakt steruje np. enabled-state przycisku
    /// Stop oraz podświetleniem karty na liście medytacji.
    /// </summary>
    public bool IsActive => IsPlaying || IsPaused;

    /// <summary>
    /// Id medytacji, której aktualnie dotyczy sesja (gra lub spauzowana) —
    /// null gdy brak aktywnej sesji. Używane przez listę medytacji żeby wiedzieć,
    /// którą kartę podświetlić jako "grająca".
    /// </summary>
    public Guid? CurrentMeditationId => _meditationId;

    public LayerProgress? GetProgress(Guid layerId) =>
        _progressByLayer.TryGetValue(layerId, out var p) ? p : null;

    /// <summary>
    /// Zwraca durationMs assetu odkryte przez JS w bieżącej sesji, lub null
    /// jeśli nie było jeszcze pobrane (albo fetch się nie udał). Używane
    /// przez UI do liczenia skali timeline-a, gdy Asset.DurationMs w DTO
    /// jest NULL.
    /// </summary>
    public int? KnownDuration(Guid assetId) =>
        _fetchedDurations.TryGetValue(assetId, out var ms) ? ms : null;

    /// <summary>
    /// Wywoływane z JS (ensureDurations / preloadDurations) gdy uda się
    /// pobrać durationMs dla assetu bez metadanych. Aktualizuje cache,
    /// emituje OnChanged dla UI i fire-and-forget persistuje wartość
    /// w bazie przez PATCH /assets/{id}/duration. Persist robi się tylko
    /// raz per assetId per service-instance (kolejne raporty są no-op
    /// dla HTTP, cache i tak idempotentny).
    /// JSInvokable musi być publiczny i instance method.
    /// </summary>
    [JSInvokable]
    public void ReportAssetDuration(Guid assetId, int ms)
    {
        if (ms <= 0) return;
        _fetchedDurations[assetId] = ms;
        OnChanged?.Invoke();
        if (_persistedAssets.Add(assetId))
        {
            // Fire-and-forget: failure persistencji nie blokuje playbacku.
            _ = SafePersistAssetDuration(assetId, ms);
        }
    }

    private async Task SafePersistAssetDuration(Guid assetId, int ms)
    {
        try
        {
            await _assets.SetDurationAsync(assetId, ms);
        }
        catch (Exception ex)
        {
            try { await _js.InvokeVoidAsync("console.warn", "[PlaybackSession] persist duration failed", assetId, ex.Message); } catch { }
        }
    }

    /// <summary>
    /// Master-clock medytacji: ile ms minęło od pozycji "ms 0" tej sesji.
    /// Po seeku wartość natychmiast skacze na nową pozycję, bo
    /// _sessionStartedAt jest cofnięty o startFromMs. Podczas pauzy zwraca
    /// zamrożoną pozycję (<c>_pausedAtMs</c>) — UI może wtedy nadal pokazywać
    /// suwak na właściwym miejscu, mimo że JS-owa sesja została zdjęta.
    /// Zwraca 0 gdy nic nie gra i nie jest spauzowane.
    /// </summary>
    public double ElapsedMs
    {
        get
        {
            if (_pausedAtMs is not null) return _pausedAtMs.Value;
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

        // Świeży Play kasuje stan pauzy — bez tego po starcie mielibyśmy
        // równocześnie IsPlaying i IsPaused, a ElapsedMs zwracałby zamrożoną
        // wartość zamiast prawdziwego master-clocka. ResumeAsync zeruje to
        // samo zanim tu wejdzie, ale przy bezpośrednim StartAsync (np. user
        // kliknął kartę innej medytacji w pauzie) potrzebujemy guard'a tutaj.
        _pausedAtMs = null;

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
                    // assetId potrzebne JS-owi do callback-u ReportAssetDuration
                    // (cache po assetId — duration to fakt o pliku, nie o tracku).
                    assetId = t.Asset.Id,
                    url = t.Asset.Url,
                    volume = t.Volume,
                    loopCount = t.LoopCount,
                    // durationMs potrzebny po stronie JS dla fast-forward przy seeku.
                    // Bierzemy z DTO, w razie braku z lokalnego cache (po lazy-fetch
                    // lub preload). Bez tego JS musiałby refetch-ować nawet po
                    // udanym preload, bo _lastMeditation w service-ie i tak ma
                    // stare DTO (Asset.DurationMs=null) z OnInitializedAsync.
                    durationMs = t.Asset.DurationMs ?? KnownDuration(t.Asset.Id) ?? 0,
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
    /// Skacze do podanej pozycji medytacji. Gdy gra — restart sesji JS z nowym
    /// startFromMs (krótka cisza przy rebuilcie grafu, ale zachowuje wszystkie
    /// ustawienia volume/reverb/rate, bo lecimy po tych samych DTO). Gdy sesja
    /// jest spauzowana — tylko aktualizujemy zamrożoną pozycję, bez wznawiania:
    /// user może scrub-ować po pasku w pauzie i kliknąć Resume żeby ruszyć
    /// z nowego miejsca, dokładnie jak w odtwarzaczu mp3. Brak _lastMeditation
    /// (sesja jeszcze nie startowała) = no-op.
    /// </summary>
    public async Task SeekAsync(double targetMs)
    {
        if (_lastMeditation is null) return;
        if (targetMs < 0) targetMs = 0;
        if (_pausedAtMs is not null)
        {
            _pausedAtMs = targetMs;
            OnChanged?.Invoke();
            return;
        }
        await StartAsync(_lastMeditation, targetMs);
    }

    /// <summary>
    /// Zamraża sesję na bieżącej pozycji. MVP wariant: pełny teardown JS-owej
    /// sesji (audio, scheduled timers, fade timers) + zapis <c>_pausedAtMs</c>
    /// w C#. <see cref="ResumeAsync"/> wystartuje JS-a od nowa z tej pozycji
    /// używając istniejącego mechanizmu fast-forward w <c>startSession</c>.
    /// Cena: ~200ms cisza przy resume (dispose + nowy load). Plus: zero ryzyka
    /// race-conditions z RAF rampami i scheduled timeoutami.
    /// No-op gdy nic nie gra albo już spauzowane.
    /// </summary>
    public async Task PauseAsync()
    {
        if (_sessionId is null || _pausedAtMs is not null) return;
        var elapsed = ElapsedMs;
        await TeardownJsSessionAsync();
        // _meditationId zostawiamy świadomie — karta na liście medytacji ma
        // dalej być podświetlona jako "ta jest aktualnie wybrana", a UI
        // edytora odróżnia "gra" od "spauzowana" po IsPaused.
        _pausedAtMs = elapsed;
        OnChanged?.Invoke();
    }

    /// <summary>
    /// Wznawia z zapamiętanej pozycji. Używa <see cref="StartAsync"/> z
    /// <c>startFromMs = _pausedAtMs</c> — to ten sam mechanizm fast-forward
    /// co przy seeku. No-op gdy nie ma czego wznawiać (brak pauzy lub brak
    /// referencji do medytacji).
    /// </summary>
    public async Task ResumeAsync()
    {
        if (_pausedAtMs is null || _lastMeditation is null) return;
        var resumeFrom = _pausedAtMs.Value;
        _pausedAtMs = null;
        // StartAsync sam emituje OnChanged po udanym starcie — nie duplikujemy.
        await StartAsync(_lastMeditation, resumeFrom);
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
        // Deduplikujemy po assetId (kilka tracków może wskazywać ten sam asset)
        // i pomijamy te z już znaną duration (z DTO lub z cache po assetId).
        var seen = new HashSet<Guid>();
        var assets = new List<object>();
        foreach (var t in meditation.Layers.SelectMany(l => l.Tracks))
        {
            if (t.Asset.DurationMs.HasValue) continue;
            if (_fetchedDurations.ContainsKey(t.Asset.Id)) continue;
            if (!seen.Add(t.Asset.Id)) continue;
            assets.Add(new { assetId = t.Asset.Id, url = t.Asset.Url });
        }
        if (assets.Count == 0) return;

        // Lazy-tworzenie DotNetObjectReference jeśli sesja jeszcze nie
        // startowała. ReportAssetDuration potrzebuje go do callback-u z JS.
        if (_dotNetRef is null) _dotNetRef = DotNetObjectReference.Create(this);

        try
        {
            await _js.InvokeVoidAsync("medytaoPlayer.preloadDurations", (object)assets, _dotNetRef);
        }
        catch (Exception ex)
        {
            try { await _js.InvokeVoidAsync("console.error", "[PlaybackSession] preloadDurations failed", ex.Message); } catch { }
        }
    }

    public async Task StopAsync()
    {
        await TeardownJsSessionAsync();
        _meditationId = null;
        _pausedAtMs = null;
        // _lastMeditation świadomie zostaje — żeby user po Stop mógł znowu
        // kliknąć Play albo seekować bez ponownego ładowania DTO.
        OnChanged?.Invoke();
    }

    /// <summary>
    /// Zatrzymuje JS-ową sesję (audio, timers) i zeruje stan progress'u +
    /// master-clock'u — ale NIE rusza <c>_meditationId</c>, <c>_pausedAtMs</c>
    /// ani <c>_lastMeditation</c>. Współdzielone między <see cref="StopAsync"/>
    /// (potem zerujemy też powyższe pola) i <see cref="PauseAsync"/> (chcemy
    /// zachować info, "co" zostało spauzowane i "gdzie"). Bez OnChanged —
    /// caller decyduje, kiedy wyemitować, żeby UI zobaczył tylko jeden
    /// spójny stan po zmianie.
    /// </summary>
    private async Task TeardownJsSessionAsync()
    {
        StopProgressTimer();
        var sid = _sessionId;
        _sessionId = null;
        _sessionStartedAt = null;
        _sessionRestartedAt = null;
        _progressByLayer = new();

        if (sid is not null)
        {
            try { await _js.InvokeVoidAsync("medytaoPlayer.stopSession", sid); } catch { }
        }
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
