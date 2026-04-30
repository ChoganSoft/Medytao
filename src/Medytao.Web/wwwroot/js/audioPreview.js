// Silnik audio dla Medytao.
// Dwa namespace'y:
//   window.medytaoAudio  — proste helpery nad pojedynczym <audio> (preview button).
//   window.medytaoPlayer — odtwarzacz sesji: wiele warstw grających równolegle,
//                          każda warstwa to sekwencja tracków (LoopCount).
// Oba systemy współdzielą jeden AudioContext i jeden Hall IR-buffer
// (zob. _sharedCtx / _sharedHallIR poniżej).

// ── Współdzielone helpery audio (nad-namespace) ───────────────────────────────
// Globalny singleton AudioContext. Chrome wymaga user gesture na pierwszy
// resume(), więc tworzymy lazy przy pierwszym żądaniu (po kliknięciu Play
// albo Preview). Sentinel `false` = "próba się nie udała", nie spamujemy logiem.
let _sharedCtx = null;

function getSharedAudioCtx() {
    if (_sharedCtx === null) {
        try {
            const Ctor = window.AudioContext || window.webkitAudioContext;
            _sharedCtx = new Ctor();
        } catch (e) {
            console.warn('[medytao audio] AudioContext unavailable', e);
            _sharedCtx = false;
        }
    }
    if (_sharedCtx && _sharedCtx.state === 'suspended') {
        _sharedCtx.resume().catch(() => { /* idempotent */ });
    }
    return _sharedCtx || null;
}

// Hall IR — generowany raz, współdzielony między medytaoPlayer (graph
// sesji) a medytaoAudio (graph preview-a). Decay 2.2s, lekki LP-smoothing,
// stereo decorrelated. Power(2.5) envelope = naturalne gasnięcie bez
// długich szumiących "ogonów".
let _sharedHallIR = null;

function getSharedHallIR(ctx) {
    if (_sharedHallIR) return _sharedHallIR;
    const decaySec = 2.2;
    const smoothFactor = 0.35;
    const gain = 0.7;
    const sr = ctx.sampleRate;
    const length = Math.max(1, Math.floor(decaySec * sr));
    const ir = ctx.createBuffer(2, length, sr);
    for (let ch = 0; ch < 2; ch++) {
        const data = ir.getChannelData(ch);
        let prev = 0;
        for (let i = 0; i < length; i++) {
            const t = i / length;
            const env = Math.pow(1 - t, 2.5);
            const noise = (Math.random() * 2 - 1) * env * gain;
            const out = prev * (1 - smoothFactor) + noise * smoothFactor;
            data[i] = out;
            prev = out;
        }
    }
    _sharedHallIR = ir;
    return ir;
}

// ── Upload-time duration detection ────────────────────────────────────────────
// Pomocnik dla Blazor InputFile: czyta File-object z natywnego <input type=file>
// (pobranego przez ElementReference), ładuje go do temp <audio>, czeka na
// loadedmetadata, zwraca duration w ms. Wywoływane PRZED uploadem do API,
// żeby asset trafił do bazy z DurationMs od razu — bez round-tripa przez
// preloadDurations w editor-ze.
//
// fileIndex pozwala obsłużyć multi-select (InputFile w Assets.razor pozwala
// na 10 plików naraz). Zwraca null gdy file nie istnieje, format nieczytelny
// albo timeout 5s.
window.medytaoUpload = {
    async readDurationMs(inputEl, fileIndex) {
        if (!inputEl || !inputEl.files) return null;
        const idx = (typeof fileIndex === 'number') ? fileIndex : 0;
        const file = inputEl.files[idx];
        if (!file) return null;

        const url = URL.createObjectURL(file);
        const audio = new Audio();
        audio.preload = 'metadata';
        audio.src = url;

        return new Promise(resolve => {
            const cleanup = () => {
                try { URL.revokeObjectURL(url); } catch { }
                try { audio.src = ''; } catch { }
            };
            const timer = setTimeout(() => { cleanup(); resolve(null); }, 5000);
            audio.addEventListener('loadedmetadata', () => {
                clearTimeout(timer);
                const ms = isFinite(audio.duration) && audio.duration > 0
                    ? Math.round(audio.duration * 1000)
                    : null;
                cleanup();
                resolve(ms);
            });
            audio.addEventListener('error', () => {
                clearTimeout(timer);
                cleanup();
                resolve(null);
            });
        });
    }
};

// preservesPitch: spec-y wszystkich obecnych przeglądarek (Chrome/Edge/Firefox/Safari)
// już dawno wspierają standardową nazwę. Stary `mozPreservesPitch`/`webkitPreservesPitch`
// ustawiamy z czysto defensywnych powodów — koszt zerowy, a chroni przed dziwnymi WebView.
function applyRateToEl(audioEl, rate) {
    const r = (typeof rate === 'number' && isFinite(rate) && rate > 0)
        ? Math.max(0.5, Math.min(2.0, rate))
        : 1.0;
    try { audioEl.preservesPitch = true; } catch { }
    try { audioEl.mozPreservesPitch = true; } catch { }
    try { audioEl.webkitPreservesPitch = true; } catch { }
    try { audioEl.playbackRate = r; } catch (e) {
        console.warn('medytao audio: playbackRate set failed', e);
    }
}

function clamp01_shared(v) {
    if (typeof v !== 'number' || !isFinite(v)) return 0;
    return Math.max(0, Math.min(1, v));
}

// ── medytaoAudio: graf reverbu dla preview-a ──────────────────────────────────
// WeakMap audioEl → { src, dryGain, wetGain, convolver, wetConnected }.
// WeakMap = automatic cleanup gdy element zniknie z DOM-u (Blazor disposuje
// AudioPreviewButton). createMediaElementSource można utworzyć tylko RAZ
// na element, więc graph cache'ujemy i reusujemy między klikami Play.
const _previewGraphs = new WeakMap();

function ensurePreviewGraph(audioEl) {
    if (!audioEl) return null;
    let g = _previewGraphs.get(audioEl);
    if (g) return g;
    const ctx = getSharedAudioCtx();
    if (!ctx) return null;
    try {
        const src = ctx.createMediaElementSource(audioEl);
        const dryGain = ctx.createGain();
        const wetGain = ctx.createGain();
        // Start: 100% dry, 0% wet — preview bez reverbu zachowuje się tak
        // samo jak przed dodaniem grafu (audio.volume nadal działa pre-graph).
        dryGain.gain.value = 1;
        wetGain.gain.value = 0;
        src.connect(dryGain);
        dryGain.connect(ctx.destination);
        // Wet path zostaje niepodłączony do convolvera dopóki mix nie urośnie >0
        // — żaden FFT cost dla preview-ów dry tracków.
        g = { src, dryGain, wetGain, convolver: null, wetConnected: false };
        _previewGraphs.set(audioEl, g);
        return g;
    } catch (e) {
        // createMediaElementSource rzuca, jeśli element już został wpięty —
        // teoretycznie WeakMap to wyłapie, ale defensywnie cache'ujemy null.
        console.warn('medytao audio: ensurePreviewGraph failed', e);
        return null;
    }
}

function applyPreviewReverb(audioEl, mix) {
    const m = clamp01_shared(mix);
    // Lazy: jeśli mix=0 i graf nie istnieje, nie tworzymy go w ogóle —
    // preview bez reverbu ma zerowy narzut Web Audio.
    if (m === 0 && !_previewGraphs.has(audioEl)) return;
    const g = ensurePreviewGraph(audioEl);
    if (!g) return;
    g.dryGain.gain.value = 1 - m;
    g.wetGain.gain.value = m;
    if (m > 0 && !g.wetConnected) {
        try {
            const ctx = getSharedAudioCtx();
            if (!ctx) return;
            // Convolver tworzymy lazy per element (a nie współdzielony między
            // wielu elementami), bo przy wielu jednoczesnych preview-ach
            // każdy musi mieć osobne wejście. WeakMap-cache automatycznie
            // posprząta, gdy element wyleci z DOM-u.
            if (!g.convolver) {
                g.convolver = ctx.createConvolver();
                g.convolver.buffer = getSharedHallIR(ctx);
                g.convolver.connect(ctx.destination);
            }
            g.src.connect(g.wetGain);
            g.wetGain.connect(g.convolver);
            g.wetConnected = true;
        } catch (e) {
            console.warn('medytao audio: late wet connect failed', e);
        }
    }
}

// ── medytaoAudio ──────────────────────────────────────────────────────────────
window.medytaoAudio = {
    // play(audioEl, volume, rate?, reverbMix?) — rate i reverbMix opcjonalne.
    // Reverb wymaga AudioContextu, więc gdy mix > 0 budujemy graf preview-a
    // (raz na element, cache w _previewGraphs). audio.volume nadal działa
    // pre-graph, więc istniejąca logika volumu zostaje bez zmian.
    play(audioEl, volume, rate, reverbMix) {
        if (!audioEl) return;
        if (typeof volume === 'number') {
            audioEl.volume = Math.max(0, Math.min(1, volume));
        }
        applyRateToEl(audioEl, rate);
        applyPreviewReverb(audioEl, reverbMix);
        audioEl.currentTime = 0;
        audioEl.play().catch(err => console.warn('Audio play failed:', err));
    },

    pause(audioEl) {
        if (!audioEl) return;
        try { audioEl.pause(); } catch { }
    },

    stop(audioEl) {
        if (!audioEl) return;
        try {
            audioEl.pause();
            audioEl.currentTime = 0;
        } catch { }
    },

    setVolume(audioEl, volume) {
        if (!audioEl) return;
        audioEl.volume = Math.max(0, Math.min(1, volume));
    },

    // Live-update tempa preview-a. Wołane z AudioPreviewButton podczas drag-u
    // suwaka Speed w TrackCard. preservesPitch=true zachowuje wysokość tonu
    // — zmiana tempa nie zmienia "głębokości" lektora.
    setRate(audioEl, rate) {
        if (!audioEl) return;
        applyRateToEl(audioEl, rate);
    },

    // Live-update wet/dry mixu reverbu preview-a. Wołane z AudioPreviewButton
    // podczas drag-u suwaka Reverb w TrackCard — analogicznie do setRate.
    // 0 = bypass (gdy graf jeszcze nie istnieje, w ogóle go nie tworzymy).
    setReverbMix(audioEl, mix) {
        if (!audioEl) return;
        applyPreviewReverb(audioEl, mix);
    },

    pauseAll() {
        document.querySelectorAll('audio').forEach(a => {
            try { a.pause(); } catch { }
        });
    },

    getProgress(audioEl) {
        if (!audioEl) return null;
        const d = audioEl.duration;
        return {
            currentTime: audioEl.currentTime || 0,
            duration: (isFinite(d) && d > 0) ? d : 0
        };
    }
};

// Alias dla kompatybilności wstecz.
window.meditationPlayer = window.medytaoAudio;

// ── medytaoPlayer ─────────────────────────────────────────────────────────────
// Sesja playbacku. Każda warstwa gra sekwencyjnie, warstwy grają równolegle.
//
// startSession(layers) → sessionId
//   layers: [{ id, volume, muted,
//              tracks: [{ trackId, url, volume, loopCount, playbackRate, reverbMix }] }]
//   loopCount: 0 = loop forever (blocks next tracks), N = play N times.
//   playbackRate: 1.0 = normalna prędkość. preservesPitch=true zachowuje wysokość
//     tonu — slowdown brzmi naturalnie, bez "grunting" efektu.
//   reverbMix: 0..1 per-Track wet/dry. 0 = bypass (nawet graf nie wpina convolvera
//     dla danego sample-a). >0 = audio leci w wet path przez współdzielony
//     ConvolverNode warstwy (Hall IR).
//
// stopSession(sessionId) — zatrzymuje i zwalnia wszystkie <audio>.
// setLayerVolume(sessionId, layerId, volume) — zmienia głośność warstwy w czasie rzeczywistym.
// setLayerMuted(sessionId, layerId, muted) — wycisza / przywraca warstwę.
// setTrackVolume(sessionId, layerId, trackId, volume) — zmienia głośność tracka (stosuje się
//   od razu, jeśli ten track jest akurat odtwarzany w swojej warstwie).
// setTrackPlaybackRate(sessionId, layerId, trackId, rate) — zmienia tempo tracka. Jeśli track
//   leci, .playbackRate aplikuje się natychmiast; jeśli nie, wartość zostaje zapamiętana.
// setTrackReverbMix(sessionId, layerId, trackId, mix) — zmienia wet/dry mix reverbu tracka.
//   Pierwsza wartość >0 w warstwie buduje współdzielony ConvolverNode warstwy (lazy).
//
// Time-anchored triggery (track ze StartAtMs):
//   Text   → interrupt: nowy fragment hard-cut wycina poprzedni audio w warstwie.
//   Fx     → overlay: nowy akcent gra równolegle do tego, co już leci.
//   Music  → crossfade: stary fade-out, nowy fade-in (czas z track.crossfadeMs;
//            0 = hard cut, default 3000 podsuwany w UI przy aktywacji time-anchored).
//   Nature → jak Music.

(function () {
    const sessions = new Map(); // sessionId → { layers: [LayerState] }
    // LayerState: { layerVolume, muted, tracks, index, playsLeft, audio }

    function createAudio(url) {
        // KRYTYCZNA kolejność: crossOrigin MUSI być ustawione PRZED src,
        // inaczej resource jest tainted i createMediaElementSource zwraca
        // cichy output (cały graf milczy, włącznie z reverbem). Stąd
        // `new Audio()` bez URL, potem crossOrigin, potem dopiero src.
        // Wcześniejsza wersja `new Audio(url)` natychmiast inicjowała
        // request bez CORS headers — ustawienie crossOrigin po tym fakcie
        // już niczego nie zmieniało.
        const a = new Audio();
        a.preload = 'auto';
        a.crossOrigin = 'anonymous';
        a.src = url;
        return a;
    }

    // ── AudioContext + reverb ─────────────────────────────────────────────────
    // AudioContext i Hall IR pochodzą z modułu (zob. getSharedAudioCtx /
    // getSharedHallIR na początku pliku). Aliasy lokalne dla czytelności.
    const getAudioCtx = getSharedAudioCtx;
    const getHallIR = getSharedHallIR;

    // Lazy convolver per-warstwa. Tworzymy gdy pierwszy track w warstwie ma
    // mix > 0. Wszystkie wet path-e tracków w warstwie spotykają się tutaj
    // (sumacja w grafie audio = ten sam reverb dla wszystkich) — zamiast
    // N convolverów na N tracków, mamy jeden per warstwa, czyli max ~4
    // w sesji, niezależnie od liczby tracków.
    function ensureLayerConvolver(L) {
        const ctx = getAudioCtx();
        if (!ctx) return null;
        if (L.convolver) return L.convolver;

        if (ctx.state === 'suspended') {
            ctx.resume().catch(() => { /* idempotent */ });
        }

        const conv = ctx.createConvolver();
        conv.buffer = getHallIR(ctx);
        conv.connect(ctx.destination);
        L.convolver = conv;
        return conv;
    }

    // Per-track audio graph: source(audio) → trackDryGain → destination
    //                                       ↘ trackWetGain → layer.convolver
    // Build wykonujemy w playCurrent przy każdym nowym <audio> elemencie.
    // Wartość mix-u zapamiętana w state.tracks[index].reverbMix.
    function buildTrackGraph(L, audio, mix) {
        const ctx = getAudioCtx();
        if (!ctx) return null;
        try {
            const src = ctx.createMediaElementSource(audio);
            const dryGain = ctx.createGain();
            const wetGain = ctx.createGain();
            const m = clamp01(mix);
            dryGain.gain.value = 1 - m;
            wetGain.gain.value = m;

            src.connect(dryGain);
            dryGain.connect(ctx.destination);

            // Wet path tworzymy tylko gdy faktycznie potrzebny — bez tego
            // omijamy ensureLayerConvolver (a zatem cały koszt FFT) dla
            // tracków, które są dry. Gdy user później zwiększy mix, dorobimy
            // połączenie w setTrackReverbMix.
            if (m > 0) {
                src.connect(wetGain);
                const conv = ensureLayerConvolver(L);
                if (conv) wetGain.connect(conv);
            }

            return { src, dryGain, wetGain, wetConnected: m > 0 };
        } catch (e) {
            // createMediaElementSource rzuca, jeśli element już ma source.
            // W normalnym flow to nie powinno się stać (świeży <audio> na każdy track).
            console.warn('[medytaoPlayer] buildTrackGraph failed', e);
            return null;
        }
    }

    function applyTrackMix(L, audioGraph, mix) {
        if (!audioGraph) return;
        const m = clamp01(mix);
        audioGraph.dryGain.gain.value = 1 - m;
        audioGraph.wetGain.gain.value = m;

        // Gdy wet pierwszy raz zaczyna być potrzebny, dorabiamy połączenie
        // wetGain → convolver (lazy, dla tracków które startowały dry).
        if (m > 0 && !audioGraph.wetConnected) {
            try {
                audioGraph.src.connect(audioGraph.wetGain);
                const conv = ensureLayerConvolver(L);
                if (conv) audioGraph.wetGain.connect(conv);
                audioGraph.wetConnected = true;
            } catch (e) {
                console.warn('[medytaoPlayer] late wet connect failed', e);
            }
        }
    }

    function effectiveVolume(layerVolume, trackVolume, muted) {
        if (muted) return 0;
        const v = (layerVolume ?? 1) * (trackVolume ?? 1);
        return Math.max(0, Math.min(1, v));
    }

    // Ustawia stan startowy warstwy dla pozycji master-clocka (seekMs ms
    // od początku medytacji). Iteruje po sekwencji tracków i odejmuje ich
    // łączny czas, aż trafi na ten, w którym znajduje się seek.
    //
    // Zasady semantyczne (zgodne z onTrackEnded):
    //   - loopCount == 0  → loop forever, blokuje kolejne tracki w warstwie.
    //                        Track gra "wiecznie", offset = seekMs % duration.
    //   - loopCount == N  → track gra N razy, łącznie N*duration ms.
    //                        Po wyczerpaniu lecimy do następnego.
    //   - track bez znanego durationMs (asset bez metadanych) → nie znamy
    //     skali czasu, więc nie próbujemy przewinąć. Patrz: special case-y poniżej.
    function applyFastForward(state, seekMs) {
        // Warstwa bez sekwencyjnych tracków (wszystkie są scheduled) — applyFastForward
        // nie ma na czym pracować. Ustawiamy "empty sequence" i wracamy. Bez tego
        // odwołanie do state.tracks[0] poniżej rzuca TypeError, startSession
        // łapie wyjątek przez try/catch w PlaybackSessionService i Play cicho
        // nic nie robi — typowy objaw przy warstwie Text z samymi time-anchored fragmentami.
        if (state.tracks.length === 0) {
            state.index = 0;
            state.startOffsetMs = 0;
            state.playsLeft = 1;
            return;
        }

        const firstTrack = state.tracks[0];
        const firstPlaysLeft = firstTrack.loopCount === 0 ? 1 : Math.max(1, firstTrack.loopCount || 1);

        // seekMs === 0: normalny Play od początku — bez liczenia. Pierwszy
        // track, pełna liczba pętli, brak offsetu. To pokrywa sytuację
        // gdy assety nie mają metadanych durationMs (typowy stan w bazie,
        // bo upload UI dziś ich nie wysyła) — bez tego shortcutu wszystkie
        // tracki dostają dMs=0 i fast-forward by skipował całą warstwę.
        if (seekMs <= 0) {
            state.index = 0;
            state.startOffsetMs = 0;
            state.playsLeft = firstPlaysLeft;
            return;
        }

        // Znamy hipotetyczne pozycje w czasie tylko gdy chociaż jeden track
        // ma durationMs. Bez metadanych seek nie ma jak policzyć offsetu —
        // fallback: graj od początku. Suwak skoczy do 0:00 wizualnie po
        // refrshu — drobny artefakt, ale lepszy niż cisza.
        const anyDuration = state.tracks.some(t =>
            typeof t.durationMs === 'number' && t.durationMs > 0);
        if (!anyDuration) {
            console.warn('[medytaoPlayer] seek requested but no durationMs metadata; starting from 0');
            state.index = 0;
            state.startOffsetMs = 0;
            state.playsLeft = firstPlaysLeft;
            return;
        }

        let remaining = seekMs;
        for (let i = 0; i < state.tracks.length; i++) {
            const t = state.tracks[i];
            const dMs = (typeof t.durationMs === 'number' && t.durationMs > 0) ? t.durationMs : 0;
            // Effective wall-clock duration tracka = naturalna / rate.
            // Slider Speed (warstwa Text) wydłuża/skraca fizyczny czas
            // odtwarzania — fast-forward musi liczyć po wall-clocku, bo seek
            // jest zaprojektowany w master-clocku (też wall-clock).
            const rate = (typeof t.playbackRate === 'number' && t.playbackRate > 0) ? t.playbackRate : 1.0;
            const effectiveMs = dMs / rate;

            if (t.loopCount === 0) {
                // Wieczna pętla — zatrzymujemy się na tym tracku.
                // startOffsetMs trzymamy w wall-clocku; playCurrent przeliczy
                // na audio-time (offsetMs * rate) przy ustawianiu currentTime.
                state.index = i;
                state.startOffsetMs = effectiveMs > 0 ? (remaining % effectiveMs) : 0;
                state.playsLeft = 1;
                return;
            }

            const loops = Math.max(1, t.loopCount || 1);

            if (dMs === 0) {
                // Track bez metadanych w środku sekwencji — nie wiemy ile zajmuje,
                // więc nie da się stwierdzić, czy seek mieści się w nim. Pomijamy
                // (zachowanie heurystyczne: zakładamy "0-długości") — w praktyce
                // to edge case, bo większość warstw ma jednolitą metadanową historię.
                continue;
            }

            const totalMs = effectiveMs * loops;
            if (remaining < totalMs) {
                const playedLoops = Math.floor(remaining / effectiveMs);
                state.index = i;
                state.startOffsetMs = remaining - playedLoops * effectiveMs;
                state.playsLeft = loops - playedLoops;
                return;
            }
            remaining -= totalMs;
        }

        // Seek poza końcem sekwencji warstwy — layer milknie.
        state.index = state.tracks.length;
        state.startOffsetMs = 0;
        state.playsLeft = 1;
    }

    function playCurrent(state) {
        const track = state.tracks[state.index];
        if (!track) return; // end of layer

        // Detach previous listener if reusing element (we don't — fresh each time).
        const audio = createAudio(track.url);
        const targetVol = effectiveVolume(state.layerVolume, track.volume, state.muted);
        applyRate(audio, track.playbackRate);

        // Jeśli weszliśmy w track po seeku, ustawiamy currentTime na offset.
        // Ustawiamy PRZED play(), żeby pierwszy sample już leciał z właściwej
        // pozycji. startOffsetMs jest w wall-clocku; gdy track gra z
        // playbackRate != 1, audio-time = wall-clock * rate (track 10s przy
        // rate=0.5 trwa 20s wall-clock, wall-clock-offset 5s = audio 2.5s).
        // Po pierwszym użyciu zerujemy startOffsetMs.
        const startOffsetWallMs = state.startOffsetMs || 0;
        state.startOffsetMs = 0;
        const trackRate = (typeof track.playbackRate === 'number' && track.playbackRate > 0) ? track.playbackRate : 1.0;
        const startOffsetSec = (startOffsetWallMs / 1000) * trackRate;

        audio.addEventListener('ended', () => onTrackEnded(state));
        state.audio = audio;

        // Track graph — tworzymy zawsze, bo .audio.volume działa pre-graph,
        // więc cała istniejąca logika volume/mute zostaje bez zmian. Wet
        // path wpinamy lazy w buildTrackGraph wyłącznie gdy mix > 0.
        const ctx = getAudioCtx();
        if (ctx) {
            state.audioGraph = buildTrackGraph(state, audio, track.reverbMix || 0);
        } else {
            state.audioGraph = null;
        }

        // currentTime można ustawić bezpiecznie przed play() — przeglądarki
        // akceptują nawet wartości spoza loadedmetadata (do odtworzenia
        // dochodzą metadane, klamują do duration). Odporność: try/catch.
        if (startOffsetSec > 0) {
            try { audio.currentTime = startOffsetSec; } catch { /* noop */ }
        }

        // FadeIn: jeśli track.fadeInMs > 0, applyFadeIn ustawi volume=0
        // i ramp do targetVol. FadeOut: setTimeout na koniec ostatniego loopa.
        applyFadeIn(audio, targetVol, track.fadeInMs);
        scheduleFadeOut(audio, track.fadeOutMs, track.durationMs, trackRate, track.loopCount);

        audio.play().catch(err => console.warn('Medytao player: play failed', err));
    }

    // preservesPitch: spec-y wszystkich obecnych przeglądarek (Chrome/Edge/Firefox/Safari)
    // już dawno wspierają standardową nazwę. Stary `mozPreservesPitch`/`webkitPreservesPitch`
    // ustawiamy z czysto defensywnych powodów — koszt zerowy, a chroni przed dziwnymi WebView.
    function applyRate(audio, rate) {
        const r = clampRate(rate);
        try { audio.preservesPitch = true; } catch { }
        try { audio.mozPreservesPitch = true; } catch { }
        try { audio.webkitPreservesPitch = true; } catch { }
        try { audio.playbackRate = r; } catch (e) {
            console.warn('Medytao player: playbackRate set failed', e);
        }
    }

    function clampRate(v) {
        if (typeof v !== 'number' || !isFinite(v) || v <= 0) return 1.0;
        // Lustro walidacji w handlerze backend-u — spec dopuszcza więcej, ale
        // ekstrema poza tym oknem brzmią źle nawet z preservesPitch.
        return Math.max(0.5, Math.min(2.0, v));
    }

    function onTrackEnded(state) {
        const track = state.tracks[state.index];
        if (!track) return;

        // LoopCount = 0 → loop forever, replay current indefinitely.
        if (track.loopCount === 0) {
            try {
                state.audio.currentTime = 0;
                state.audio.play().catch(err => console.warn('Medytao player: loop replay failed', err));
            } catch (e) { /* noop */ }
            return;
        }

        // Still replays to do for this track?
        state.playsLeft -= 1;
        if (state.playsLeft > 0) {
            try {
                state.audio.currentTime = 0;
                state.audio.play().catch(err => console.warn('Medytao player: repeat failed', err));
            } catch (e) { /* noop */ }
            return;
        }

        // Advance to next track.
        disposeAudio(state.audio);
        state.audio = null;
        state.index += 1;

        if (state.index >= state.tracks.length) {
            return; // layer finished
        }

        const nextTrack = state.tracks[state.index];
        state.playsLeft = Math.max(1, nextTrack.loopCount || 1);
        // loopCount = 0 is handled in ended handler (we set playsLeft = 1 so we enter ended once).
        if (nextTrack.loopCount === 0) state.playsLeft = 1;

        playCurrent(state);
    }

    function disposeAudio(audio) {
        if (!audio) return;
        // Anuluj zaplanowany fadeOut żeby nie odpalił się na disposowanym
        // elemencie (i nie utrzymywał referencji w setTimeout do audio
        // przez czas fadeStart).
        clearFadeOut(audio);
        try {
            audio.pause();
            audio.src = '';
            audio.load();
        } catch (e) { /* noop */ }
    }

    // ── Time-anchored triggers ────────────────────────────────────────────────
    // Dla każdego scheduled tracka decyzja zależy od relacji jego okna
    // [startAt, startAt+duration] i punktu seek:
    //
    //   1) startAt > seek          → future. setTimeout(startAt - seek).
    //   2) startAt <= seek < end   → past-but-active. Seek wpadł w środku
    //      odtwarzania tego tracka. Odpalamy NATYCHMIAST z offsetem
    //      (audio.currentTime = seek - startAt), żeby brzmiał tak, jakby
    //      sesja leciała od początku i właśnie do tego momentu doszła.
    //   3) seek >= end             → past-and-finished. Pomijamy — track
    //      się zakończył przed pozycją seek, nie ma czego dogonić.
    //
    // Bez przypadku (2) klik suwaka w środek długiego scheduled tracka
    // dawał ciszę: future-only-handling drop'owało wszystko, sesja po
    // chwili sama auto-stop'owała się (Finished=true), suwak skakał do 0.
    function scheduleAnchoredTracks(state, seekFromMs) {
        const seq = state.scheduledTracks || [];
        if (seq.length === 0) return;
        const isOverlayLayer = isOverlayLayerType(state.layerType);

        // Music/Nature: wszystkie ignored aż do Etapu 3.
        if (!isOverlayLayer) {
            for (const t of seq) {
                console.debug('[medytaoPlayer] scheduled track in non-overlay layer ignored',
                    { layerId: state.layerId, layerType: state.layerType, trackId: t.trackId, startAtMs: t.startAtMs });
            }
            return;
        }

        // Past-but-active: dla warstwy interrupt (Text) liczy się tylko
        // NAJPÓŹNIEJSZY z wciąż-grających, bo każdy następny i tak by
        // wyciszył poprzedni. Dla overlay (Fx) odpalamy wszystkie active —
        // jako akcenty tła nakładające się na siebie też mają sens
        // (choć w praktyce krótkie sample-e Fx rzadko nakładają się
        // na siebie; ten branch dotyczy edge case-ów).
        //
        // Effective duration (= natural / rate) decyduje czy track jeszcze
        // gra w czasie ściennym. Slider Speed wolniejszy = track żyje dłużej
        // w wall-clocku, więc past-but-active obejmuje go w szerszym oknie
        // niż goła naturalna duration.
        const interruptMode = isInterruptLayerType(state.layerType);
        const pastActive = []; // { track, offsetMs, startAt }
        for (const t of seq) {
            const startAt = t.startAtMs ?? 0;
            const dur = t.durationMs || 0;
            if (startAt > seekFromMs) continue; // future, obsługujemy poniżej
            if (dur <= 0) continue;             // bez metadanych nie wiemy
            const rate = (typeof t.playbackRate === 'number' && t.playbackRate > 0) ? t.playbackRate : 1.0;
            const effectiveDur = dur / rate;
            const end = startAt + effectiveDur;
            if (seekFromMs >= end) continue;    // już się skończył
            pastActive.push({ track: t, offsetMs: seekFromMs - startAt, startAt });
        }
        if (interruptMode && pastActive.length > 1) {
            // Zostaw tylko najpóźniej startujący — interrupt by i tak wyciął
            // poprzednie, więc nie ma sensu odpalać kilku jeden po drugim.
            pastActive.sort((a, b) => b.startAt - a.startAt);
            pastActive.length = 1;
        }

        // Music/Nature → triggerCrossfade (fade-out poprzedniego, fade-in nowego).
        // Text/Fx → triggerOverlay (Text z hard-cut, Fx jako pure overlay).
        const isCrossfadeLayer = isCrossfadeLayerType(state.layerType);
        const triggerFn = isCrossfadeLayer ? triggerCrossfade : triggerOverlay;

        for (const pa of pastActive) {
            triggerFn(state, pa.track, pa.offsetMs);
        }

        // Future triggers — schedule normalnie.
        for (const t of seq) {
            const startAt = t.startAtMs ?? 0;
            const delay = startAt - seekFromMs;
            if (delay <= 0) continue; // past — już obsłużone

            state.scheduledPending += 1;
            const timerId = setTimeout(
                () => {
                    state.scheduledPending = Math.max(0, state.scheduledPending - 1);
                    triggerFn(state, t, 0);
                },
                delay
            );
            state.scheduledTimers.push(timerId);
        }
    }

    function isOverlayLayerType(layerType) {
        // Case-insensitive — backend zwraca canonical case z enuma
        // (Text/Music/Nature/Fx). Overlay = "graj scheduled niezależnie od
        // istniejącego audio w warstwie". Crossfade-mode warstwy też tu
        // wpadają jako true bo i one wymagają trigger-u — różnica jest
        // dalej w kodzie wyboru funkcji trigger (overlay vs crossfade).
        if (!layerType) return false;
        const t = String(layerType).toLowerCase();
        return t === 'text' || t === 'fx' || t === 'music' || t === 'nature';
    }

    // Music i Nature: scheduled track wycina aktualne tło z fade-em.
    // Text: też wycina, ale bez fade-u (interrupt). Fx: overlay (żaden cut).
    function isCrossfadeLayerType(layerType) {
        if (!layerType) return false;
        const t = String(layerType).toLowerCase();
        return t === 'music' || t === 'nature';
    }

    // Trigger time-anchored tracka w warstwie typu overlay (Text/Fx).
    // Tworzymy nowy <audio> równolegle do tego, co aktualnie gra w warstwie.
    //
    // Tryb zależy od layerType (egzekwowany przez isInterruptLayerType):
    //   - Text → interrupt: nowy fragment wycisza wszystko inne grające
    //     w warstwie (poprzednie overlay-e + aktualna sekwencja). Dwa głosy
    //     lektora jeden na drugim brzmią jak chaos.
    //   - Fx   → overlay: nowy gong/dzwon nakłada się na to, co już leci.
    //     Świadomy efekt akustycznego akcentu nad podkładem.
    //
    // Volume warstwy/track aplikujemy bezpośrednio na audio.volume (jak w
    // sekwencyjnym playCurrent). PlaybackRate przez applyRate. Reverb dla
    // overlay-a obecnie pomijamy — wymaga buildTrackGraph, a w Stage 2 nie
    // chcemy komplikować.
    // offsetMs > 0 oznacza, że odpalamy track "w trakcie" — np. po seeku
    // do środka scheduled fragmentu. offsetMs to wall-clock offset (ms od
    // startAtMs do seekMs). audio.currentTime to natomiast pozycja audio-time
    // wewnątrz pliku — gdy gramy z playbackRate != 1, te dwie wartości się
    // rozjeżdżają: track o duration=10s przy rate=0.5 trwa 20s wall-clock,
    // i wall-clock-offset=5s odpowiada audio-time=2.5s. Stąd mnożenie
    // (offsetMs / 1000) * rate.
    function triggerOverlay(state, track, offsetMs) {
        if (isInterruptLayerType(state.layerType)) {
            // Wycisz aktualny sekwencyjny audio (jeśli leci).
            if (state.audio) {
                disposeAudio(state.audio);
                state.audio = null;
            }
            // Wycisz wszystkie wcześniejsze overlay-e tej warstwy.
            if (state.scheduledOverlays && state.scheduledOverlays.length > 0) {
                for (const o of state.scheduledOverlays) disposeAudio(o.audio);
                state.scheduledOverlays = [];
            }
        }

        const audio = createAudio(track.url);
        const targetVol = effectiveVolume(state.layerVolume, track.volume, state.muted);
        const rate = (typeof track.playbackRate === 'number' && track.playbackRate > 0) ? track.playbackRate : 1.0;
        applyRate(audio, rate);

        if (typeof offsetMs === 'number' && offsetMs > 0) {
            const audioTimeSec = (offsetMs / 1000) * rate;
            try { audio.currentTime = audioTimeSec; } catch { /* noop */ }
        }

        // Reverb path identyczny jak dla sequenced (playCurrent) — buildTrackGraph
        // wpina audio w MediaElementAudioSource, dryGain idzie do destination,
        // a wetGain (gdy mix > 0) do współdzielonego layer.convolver. Bez
        // tego scheduled tracki w Text/Fx nie miałyby reverbu nawet przy
        // mix > 0, bo audio leciało wprost przez domyślne wyjście elementu.
        let overlayGraph = null;
        const ctx = getAudioCtx();
        if (ctx) {
            overlayGraph = buildTrackGraph(state, audio, track.reverbMix || 0);
        }

        const onEnded = () => {
            const idx = state.scheduledOverlays.findIndex(o => o.audio === audio);
            if (idx >= 0) state.scheduledOverlays.splice(idx, 1);
            disposeAudio(audio);
        };
        audio.addEventListener('ended', onEnded);

        state.scheduledOverlays.push({ trackId: track.trackId, audio, graph: overlayGraph });
        // FadeIn (jeśli ustawione) + scheduleFadeOut. Po seeku z offsetMs > 0
        // fadeIn ma sens jako "in-medias-res rozjazd" — pozostaje nawet gdy
        // weszliśmy w środku tracka, krótki ramp łagodzi twardy start.
        // FadeOut planowany od bieżącego currentTime — totalEffectiveMs to
        // czas od początku audio, więc dla offset>0 pozostaje za dużo, ale
        // to akceptowalna nieprecyzja dla overlay (one-shot).
        applyFadeIn(audio, targetVol, track.fadeInMs);
        scheduleFadeOut(audio, track.fadeOutMs, track.durationMs, rate, track.loopCount);
        audio.play().catch(err => console.warn('Medytao player: overlay play failed', err));

        console.debug('[medytaoPlayer] overlay triggered',
            { layerId: state.layerId, layerType: state.layerType,
              trackId: track.trackId, startAtMs: track.startAtMs,
              reverbMix: track.reverbMix || 0,
              offsetMs: offsetMs || 0,
              hasGraph: !!overlayGraph,
              interrupt: isInterruptLayerType(state.layerType) });
    }

    // Trigger time-anchored tracka w warstwie crossfade (Music/Nature).
    // Aktualnie grający track w warstwie (sequenced state.audio lub ostatni
    // overlay z poprzedniego crossfade) — fade-out. Nowy track — fade-in.
    // Czas fade-u: track.crossfadeMs (0 = hard cut, bez fade'u).
    //
    // Sequenced sequence po fade-out NIE wraca — state.audio = null oznacza
    // koniec sekwencji w tej warstwie. Po końcu nowego time-anchored
    // (jego ended event) → cisza w warstwie aż do następnego crossfade.
    //
    // Bez AudioContextu (sytuacja awaryjna): fallback do triggerOverlay,
    // hard-cut bez fade-u — i tak jest to lepsze niż brak triggera.
    function triggerCrossfade(state, track, offsetMs) {
        const ctx = getAudioCtx();
        if (!ctx) {
            triggerOverlay(state, track, offsetMs);
            return;
        }

        const audio = createAudio(track.url);
        const targetVol = effectiveVolume(state.layerVolume, track.volume, state.muted);
        const rate = (typeof track.playbackRate === 'number' && track.playbackRate > 0) ? track.playbackRate : 1.0;
        applyRate(audio, rate);

        if (typeof offsetMs === 'number' && offsetMs > 0) {
            const audioTimeSec = (offsetMs / 1000) * rate;
            try { audio.currentTime = audioTimeSec; } catch { /* noop */ }
        }

        const graph = buildTrackGraph(state, audio, track.reverbMix || 0);

        // Crossfade duration prosto z "Crossfade to next" (track.crossfadeMs).
        // Brak ukrytego defaultu — gdy user nie ustawi nic albo postawi 0,
        // szanujemy to (hard cut: stary disposed natychmiast, nowy startuje
        // od pełnego volume). User świadomie wpisuje liczbę gdy chce fade.
        const fadeMs = (typeof track.crossfadeMs === 'number' && track.crossfadeMs > 0)
            ? track.crossfadeMs
            : 0;

        // Stary aktywny: najpierw sequenced, w razie braku — ostatni overlay
        // (poprzedni crossfade). Capture refs lokalnie, żeby setTimeout
        // closure nie czytał state.audio po zmianie.
        const oldSequenced = state.audio;
        const oldOverlay = (!oldSequenced && state.scheduledOverlays.length > 0)
            ? state.scheduledOverlays[state.scheduledOverlays.length - 1]
            : null;
        const oldAudio = oldSequenced || (oldOverlay && oldOverlay.audio);

        if (oldAudio) {
            // Anuluj zaplanowany fadeOut na starym audio — inaczej dwie pętle
            // RAF (ten fadeAudio crossfade-out + zaplanowany scheduleFadeOut)
            // konkurowałyby o audio.volume z różnymi targetami.
            clearFadeOut(oldAudio);
            const startVol = oldAudio.volume;
            fadeAudio(oldAudio, startVol, 0, fadeMs, () => {
                disposeAudio(oldAudio);
                if (oldSequenced && state.audio === oldSequenced) {
                    state.audio = null;
                }
                if (oldOverlay) {
                    const idx = state.scheduledOverlays.indexOf(oldOverlay);
                    if (idx >= 0) state.scheduledOverlays.splice(idx, 1);
                }
            });
        }

        // Nowy fade-in: zaczyna od 0, ramp do targetVol. To SAM crossfade,
        // dlatego nie wołamy applyFadeIn — track.fadeInMs jest tu nieistotne
        // (i tak fade-in trwa fadeMs == crossfade duration).
        audio.volume = 0;

        const onEnded = () => {
            const idx = state.scheduledOverlays.findIndex(o => o.audio === audio);
            if (idx >= 0) state.scheduledOverlays.splice(idx, 1);
            disposeAudio(audio);
        };
        audio.addEventListener('ended', onEnded);

        state.scheduledOverlays.push({ trackId: track.trackId, audio, graph });
        audio.play().catch(err => console.warn('Medytao player: crossfade play failed', err));
        fadeAudio(audio, 0, targetVol, fadeMs);
        // FadeOut planujemy na końcu nowego tracka. Jeśli następny scheduled
        // przyjdzie przed jego końcem, kolejny triggerCrossfade clearFadeOut
        // ten timer i zrobi crossfade-out wcześniej.
        scheduleFadeOut(audio, track.fadeOutMs, track.durationMs, rate, track.loopCount);

        console.debug('[medytaoPlayer] crossfade triggered',
            { layerId: state.layerId, layerType: state.layerType,
              trackId: track.trackId, startAtMs: track.startAtMs,
              fadeMs, offsetMs: offsetMs || 0,
              hasGraph: !!graph,
              fadingOut: !!oldAudio });
    }

    // Interrupt = "nowy track wycisza poprzedni w warstwie". Text wycina
    // twardo (bez fade-u), Music/Nature przez crossfade. Wszystkie trzy
    // share-ują logikę "tylko najpóźniejszy past-but-active jest aktywny
    // po seeku" — bo poprzednie i tak by zostały ucięte przez kolejne triggery.
    function isInterruptLayerType(layerType) {
        if (!layerType) return false;
        const t = String(layerType).toLowerCase();
        return t === 'text' || t === 'music' || t === 'nature';
    }

    // Płynna zmiana audio.volume przez requestAnimationFrame. RAF
    // synchronizuje z VSync browsera, więc fade jest gładki bez
    // zacinania się (vs setInterval z fixed step). Po zakończeniu
    // wywoływany onEnd. Bezpieczne dla audio = null / disposed.
    function fadeAudio(audio, fromVol, toVol, durationMs, onEnd) {
        if (!audio || durationMs <= 0) {
            if (audio) {
                try { audio.volume = toVol; } catch { }
            }
            if (onEnd) onEnd();
            return;
        }
        const start = performance.now();
        const tick = () => {
            // Audio mogło zostać disposed w trakcie fade — zatrzymujemy się
            // żeby nie ustawić volume na zwolnionym elemencie.
            if (!audio || !audio.src) {
                if (onEnd) onEnd();
                return;
            }
            const t = Math.min(1, (performance.now() - start) / durationMs);
            try { audio.volume = fromVol + (toVol - fromVol) * t; } catch { }
            if (t < 1) {
                requestAnimationFrame(tick);
            } else if (onEnd) {
                onEnd();
            }
        };
        requestAnimationFrame(tick);
    }

    // Per-Track fade-in/out timery — WeakMap żeby anulowanie było tożsame
    // z disposem audio (GC sprząta). Klucz = HTMLAudioElement, wartość =
    // setTimeout id zaplanowanego fade-outu. fade-in nie potrzebuje tracking-u
    // bo odpala się natychmiast przez requestAnimationFrame i nie ma czego anulować.
    const _fadeOutTimers = new WeakMap();

    // applyFadeIn: jeśli fadeInMs > 0 startujemy od volume=0 i ramp-ujemy
    // do targetVol; inaczej ustawiamy targetVol bezpośrednio. Wywoływane
    // po audio.play() (volume na <audio> przed play to default 1.0,
    // applyFadeIn natychmiast korygowuje).
    function applyFadeIn(audio, targetVol, fadeInMs) {
        if (!audio) return;
        if (typeof fadeInMs !== 'number' || fadeInMs <= 0) {
            try { audio.volume = targetVol; } catch { }
            return;
        }
        try { audio.volume = 0; } catch { }
        fadeAudio(audio, 0, targetVol, fadeInMs);
    }

    // scheduleFadeOut: setTimeout planowany na początku grania tracka,
    // odpala się gdy audio jest blisko końca ostatniego loop-a. Używa
    // setTimeout (wall-clock) — przy zmianie playbackRate w trakcie
    // fade timer się rozjedzie, ale to edge case (Speed slider rzadko
    // ruszany w trakcie). loopCount=0 (forever) → brak fadeOut, bo
    // track nie ma końca.
    function scheduleFadeOut(audio, fadeOutMs, naturalDurationMs, rate, loopCount) {
        if (!audio) return;
        if (typeof fadeOutMs !== 'number' || fadeOutMs <= 0) return;
        if (typeof naturalDurationMs !== 'number' || naturalDurationMs <= 0) return;
        if (loopCount === 0) return; // wieczna pętla — brak końca, brak fadeOut

        const r = (typeof rate === 'number' && rate > 0) ? rate : 1.0;
        const loops = Math.max(1, loopCount || 1);
        const totalEffectiveMs = (naturalDurationMs / r) * loops;
        const fadeStartMs = Math.max(0, totalEffectiveMs - fadeOutMs);

        const id = setTimeout(() => {
            _fadeOutTimers.delete(audio);
            // audio mogło zostać disposed (track wycięty crossfade-em
            // albo session stopped) — fadeAudio sam wykryje brak src
            // i zakończy bez efektu.
            fadeAudio(audio, audio.volume || 0, 0, fadeOutMs);
        }, fadeStartMs);
        _fadeOutTimers.set(audio, id);
    }

    function clearFadeOut(audio) {
        if (!audio) return;
        const id = _fadeOutTimers.get(audio);
        if (id !== undefined) {
            clearTimeout(id);
            _fadeOutTimers.delete(audio);
        }
    }

    function newSessionId() {
        return 'sess_' + Math.random().toString(36).slice(2, 10) + Date.now().toString(36);
    }

    // Lazy-fetch metadata dla tracków bez znanego durationMs. Backend dziś
    // przy uploadzie nie zapisuje duration (osobny ticket), więc istniejące
    // assety mają DurationMs = NULL — bez tego silnik nie umie liczyć "co gra
    // w sekundzie X" przy seeku, suwak fallback'uje do 0:00, a licznik totalu
    // pokazuje placeholder 1:00.
    //
    // Robimy to RAZ przy każdym startSession (synchronicznie do startu
    // playback-u, ale asynchronicznie wewnątrz). Pierwszy Play opóźnia się
    // o ~ms na network round-trip do nagłówków audio (preload=metadata, nie
    // ładujemy całego pliku), kolejne tracki podczas tej samej sesji nie
    // płacą drugi raz, bo mutujemy track.durationMs in place. W kolejnych
    // sesjach JS i tak dostaje świeże track-objekty z DTO, więc fetch się
    // powtarza — to jest świadomy kompromis na rzecz prostoty (osobny ticket
    // zoptymalizuje przez zapis do bazy).
    function fetchDurationFor(track) {
        return new Promise(resolve => {
            const audio = new Audio();
            audio.preload = 'metadata';
            audio.crossOrigin = 'anonymous';
            audio.src = track.url;

            const cleanup = () => {
                try { audio.src = ''; } catch { }
            };
            const timer = setTimeout(() => {
                cleanup();
                resolve(null);
            }, 8000); // 8s — sensible upper bound dla wolnego storage

            audio.addEventListener('loadedmetadata', () => {
                clearTimeout(timer);
                const ms = isFinite(audio.duration) && audio.duration > 0
                    ? Math.round(audio.duration * 1000)
                    : null;
                cleanup();
                resolve(ms);
            });
            audio.addEventListener('error', () => {
                clearTimeout(timer);
                cleanup();
                resolve(null);
            });
        });
    }

    // dotNetRef — DotNetObjectReference do PlaybackSessionService. Po pobraniu
    // metadanych raportujemy z powrotem do C# (ReportAssetDuration po assetId,
    // bo duration to property pliku, nie tracka). Cache w C# trzymamy po
    // assetId, plus PATCH /assets/{id}/duration persistuje do bazy.
    //
    // Deduplikacja po assetId: jeden plik może być w wielu trackach (ten sam
    // asset linkowany do kilku tracków warstwy). Po fetch propagujemy duration
    // na wszystkie tracki tego assetu, żeby applyFastForward i scheduleAnchored
    // miały spójne dane bez wielokrotnego fetchu.
    async function ensureDurations(layers, dotNetRef) {
        // Zbieramy mapowanie assetId → [track] żeby propagować ms po fetchu.
        const tracksByAsset = new Map();
        for (const l of (layers || [])) {
            for (const t of (l.tracks || [])) {
                if (!t.assetId) continue;
                let arr = tracksByAsset.get(t.assetId);
                if (!arr) { arr = []; tracksByAsset.set(t.assetId, arr); }
                arr.push(t);
            }
        }

        const promises = [];
        for (const [assetId, tracks] of tracksByAsset) {
            // Pomiń jeśli któryś z tracków już ma duration (cache hit z C# albo
            // DTO miało wartość) — wystarczy jeden source-of-truth dla całego asset-u.
            if (tracks.some(t => typeof t.durationMs === 'number' && t.durationMs > 0)) continue;

            promises.push(
                fetchDurationFor(tracks[0]).then(ms => {
                    if (!ms) return;
                    for (const t of tracks) t.durationMs = ms;
                    if (dotNetRef) {
                        try {
                            dotNetRef.invokeMethodAsync('ReportAssetDuration', assetId, ms);
                        } catch (e) {
                            console.warn('[medytaoPlayer] ReportAssetDuration failed', e);
                        }
                    }
                })
            );
        }
        if (promises.length > 0) {
            console.debug('[medytaoPlayer] fetching durations for', promises.length, 'assets');
            await Promise.all(promises);
        }
    }

    window.medytaoPlayer = {
        // startSession(layers, startFromMs?) — gdy startFromMs > 0, każda
        // warstwa "fast-forwards" do tego punktu na master clocku medytacji:
        // znajdujemy track, który grałby w tym momencie, ustawiamy jego
        // currentTime na offset wewnątrz, doliczamy ile pętli już się odbyło.
        // Pozwala seekować bez konieczności słuchania wszystkiego od zera.
        async startSession(layers, startFromMs, dotNetRef) {
            // Pobierz brakujące metadane PRZED zbudowaniem grafu warstw —
            // applyFastForward i scheduleAnchoredTracks polegają na
            // track.durationMs przy seeku, więc bez tego seek by zawsze
            // wracał do fallback "od zera". dotNetRef pozwala raportować
            // zwrotnie do C# żeby UI też wiedział o świeżych durations.
            await ensureDurations(layers, dotNetRef);

            const sessionId = newSessionId();
            const seekMs = (typeof startFromMs === 'number' && startFromMs > 0) ? startFromMs : 0;
            const layerStates = (layers || [])
                .filter(l => l && l.tracks && l.tracks.length > 0)
                .map(l => {
                    // Tracki w warstwie dzielimy na dwie pule:
                    //   - sequenced: tracki z startAtMs == null, grają w sekwencji
                    //     wg Order (czyli kolejności w tablicy) jak dotychczas.
                    //   - scheduled: tracki z startAtMs != null, time-anchored —
                    //     setTimeout w master clocku sesji wystrzeli triggerScheduled.
                    // Sequenced lecą przez normalny mechanizm (playCurrent +
                    // onTrackEnded). Scheduled mają osobny life-cycle.
                    const sequenced = [];
                    const scheduled = [];
                    for (const t of l.tracks) {
                        if (typeof t.startAtMs === 'number' && t.startAtMs >= 0) {
                            scheduled.push(t);
                        } else {
                            sequenced.push(t);
                        }
                    }
                    return {
                        layerId: l.id,
                        layerType: l.type || '',
                        layerVolume: l.volume,
                        muted: !!l.muted,
                        convolver: null,
                        // Pole `tracks` zostaje na sekwencyjnych — cała stara
                        // logika (index/playsLeft/onTrackEnded) operuje na nim
                        // bez zmian.
                        tracks: sequenced,
                        scheduledTracks: scheduled,
                        scheduledTimers: [],   // setTimeout id-y, do clearTimeout w stop
                        scheduledOverlays: [], // aktywne overlay <audio> elementy
                        scheduledPending: 0,   // ile triggerów jeszcze nie odpaliło — żeby
                                               // getProgress nie zgłaszał Finished przedwcześnie
                                               // dla warstw mających tylko scheduled tracki
                        index: 0,
                        playsLeft: 1,
                        startOffsetMs: 0,
                        audio: null,
                        audioGraph: null
                    };
                });

            for (const state of layerStates) {
                applyFastForward(state, seekMs);
            }

            sessions.set(sessionId, { layers: layerStates });
            console.debug('[medytaoPlayer] startSession', {
                sessionId,
                seekMs,
                layers: layerStates.map(L => ({
                    layerId: L.layerId,
                    layerType: L.layerType,
                    index: L.index,
                    playsLeft: L.playsLeft,
                    startOffsetMs: L.startOffsetMs,
                    layerVolume: L.layerVolume,
                    muted: L.muted,
                    sequencedCount: L.tracks.length,
                    scheduledCount: L.scheduledTracks.length
                }))
            });

            for (const state of layerStates) {
                // Sekwencja warstwy startuje natychmiast (lub w ogóle nie startuje,
                // gdy warstwa ma tylko scheduled tracki).
                if (state.tracks.length > 0) playCurrent(state);
                // Time-anchored: setTimeout dla każdego scheduled tracka.
                scheduleAnchoredTracks(state, seekMs);
            }
            return sessionId;
        },

        stopSession(sessionId) {
            const s = sessions.get(sessionId);
            if (!s) return;
            for (const state of s.layers) {
                disposeAudio(state.audio);
                state.audio = null;
                // Anuluj jeszcze nieuruchomione triggery time-anchored —
                // setTimeout-y, które miały odpalić overlay w przyszłości.
                if (state.scheduledTimers) {
                    for (const id of state.scheduledTimers) clearTimeout(id);
                    state.scheduledTimers = [];
                }
                // Dispose aktywnych overlay-i (te już grające). Każdy overlay
                // to teraz {trackId, audio, graph} — disposujemy element,
                // graf zniknie z GC gdy źródło przestaje być referencjowane.
                if (state.scheduledOverlays) {
                    for (const o of state.scheduledOverlays) disposeAudio(o.audio);
                    state.scheduledOverlays = [];
                }
            }
            sessions.delete(sessionId);
        },

        getProgress(sessionId) {
            const s = sessions.get(sessionId);
            if (!s) return [];
            return s.layers.map((state, i) => {
                const a = state.audio;
                const d = a ? a.duration : 0;
                // Finished = sekwencja warstwy się skończyła AND nie ma żadnego
                // aktywnego overlay-a AND żaden scheduled trigger nie czeka
                // jeszcze na odpalenie. Bez warunków na overlay/pending,
                // warstwy mające tylko scheduled tracki (np. warstwa Text
                // z fragmentami narracji) raportowałyby Finished od razu po
                // start, bo state.tracks.length=0 → auto-stop ucinałby sesję
                // zanim jakikolwiek trigger by wystrzelił.
                const overlaysActive = state.scheduledOverlays && state.scheduledOverlays.length > 0;
                const pending = state.scheduledPending > 0;
                const sequenceDone = state.index >= state.tracks.length;
                return {
                    layerIndex: i,
                    layerId: state.layerId,
                    trackIndex: state.index,
                    trackCount: state.tracks.length,
                    currentTime: a ? (a.currentTime || 0) : 0,
                    duration: (a && isFinite(d) && d > 0) ? d : 0,
                    finished: sequenceDone && !overlaysActive && !pending
                };
            });
        },

        // Real-time głośność warstwy. Aktualizuje WSZYSTKIE aktywne audio
        // w warstwie — sequenced (state.audio) i overlay (scheduled tracki
        // grające po time-anchored triggerze). Bez applyAllAudiosVolume
        // setter działał tylko na sequenced i nie ogarniał warstw z samymi
        // scheduled trackami.
        setLayerVolume(sessionId, layerId, volume) {
            const L = findLayer(sessionId, layerId);
            if (!L) {
                console.warn('[medytaoPlayer] setLayerVolume: layer not found', { sessionId, layerId });
                return;
            }
            L.layerVolume = clamp01(volume);
            applyAllAudiosVolume(L);
            console.debug('[medytaoPlayer] setLayerVolume', {
                layerId, volume: L.layerVolume,
                seqVolume: L.audio ? L.audio.volume : null,
                overlayCount: L.scheduledOverlays ? L.scheduledOverlays.length : 0
            });
        },

        // Wycisz / odcisz warstwę. Nie zatrzymujemy <audio> — tylko ściągamy
        // głośność do 0, żeby synchronizacja z innymi warstwami się nie rozjechała.
        setLayerMuted(sessionId, layerId, muted) {
            const L = findLayer(sessionId, layerId);
            if (!L) {
                console.warn('[medytaoPlayer] setLayerMuted: layer not found', { sessionId, layerId });
                return;
            }
            L.muted = !!muted;
            applyAllAudiosVolume(L);
            console.debug('[medytaoPlayer] setLayerMuted', { layerId, muted: L.muted });
        },

        // Real-time głośność pojedynczego tracka. Mutujemy wpis w state.tracks
        // (sequenced) ALBO state.scheduledTracks (time-anchored) — w zależności
        // gdzie ten track jest. Następnie aktualizujemy volume na każdym
        // aktywnym audio — sequenced state.audio jeśli to current sequenced,
        // każdy overlay jeśli to scheduled track aktualnie grający.
        setTrackVolume(sessionId, layerId, trackId, volume) {
            const L = findLayer(sessionId, layerId);
            if (!L) {
                console.warn('[medytaoPlayer] setTrackVolume: layer not found', { sessionId, layerId });
                return;
            }
            const v = clamp01(volume);

            // Mutuj wartość w odpowiednim bucket-cie. Settery są niezależne
            // od bieżącego stanu odtwarzania — następne wejście playCurrent /
            // triggerOverlay/Crossfade zobaczy nową wartość.
            let inSeq = L.tracks.find(x => x && eqId(x.trackId, trackId));
            let inSched = inSeq ? null : (L.scheduledTracks ? L.scheduledTracks.find(x => x && eqId(x.trackId, trackId)) : null);
            if (!inSeq && !inSched) {
                console.warn('[medytaoPlayer] setTrackVolume: track not found', { layerId, trackId });
                return;
            }
            if (inSeq) inSeq.volume = v;
            if (inSched) inSched.volume = v;

            // Live-update: applyAllAudiosVolume liczy effective volume dla
            // każdego aktywnego audio na podstawie zmutowanego stanu.
            applyAllAudiosVolume(L);

            console.debug('[medytaoPlayer] setTrackVolume', {
                layerId, trackId, volume: v, sequenced: !!inSeq, scheduled: !!inSched
            });
        },

        // Real-time tempo pojedynczego tracka. Mutuje state.tracks (sequenced)
        // ALBO state.scheduledTracks (time-anchored). Jeśli track akurat leci
        // jako sekwencja (state.audio i to current sequenced) — applyRate na
        // żywo. Jeśli leci jako scheduled overlay — applyRate na overlay.audio.
        // Inaczej tylko mutacja, następne playCurrent/triggerOverlay/Crossfade
        // wezmą nową wartość.
        setTrackPlaybackRate(sessionId, layerId, trackId, rate) {
            const L = findLayer(sessionId, layerId);
            if (!L) {
                console.warn('[medytaoPlayer] setTrackPlaybackRate: layer not found', { sessionId, layerId });
                return;
            }
            const r = clampRate(rate);

            const inSeq = L.tracks.find(x => x && eqId(x.trackId, trackId));
            const inSched = inSeq ? null : (L.scheduledTracks ? L.scheduledTracks.find(x => x && eqId(x.trackId, trackId)) : null);
            if (!inSeq && !inSched) {
                console.warn('[medytaoPlayer] setTrackPlaybackRate: track not found', { layerId, trackId });
                return;
            }
            if (inSeq) inSeq.playbackRate = r;
            if (inSched) inSched.playbackRate = r;

            // Live update na grającym audio: sequenced lub overlay z tym trackId.
            const current = L.tracks[L.index];
            const isCurrentSeq = current && eqId(current.trackId, trackId);
            if (isCurrentSeq && L.audio) applyRate(L.audio, r);
            if (L.scheduledOverlays) {
                for (const o of L.scheduledOverlays) {
                    if (eqId(o.trackId, trackId) && o.audio) applyRate(o.audio, r);
                }
            }
            console.debug('[medytaoPlayer] setTrackPlaybackRate', { layerId, trackId, rate: r });
        },

        // Real-time wet/dry mix reverbu pojedynczego tracka. Mutuje state
        // (sequenced lub scheduled bucket). Jeśli track akurat gra w którymś
        // z grafów (sequenced state.audioGraph lub overlay.graph), applyTrackMix
        // zmienia gain-y na żywo bez glitchu. Lazy connect wet path: gdy track
        // startował z mix=0 i user pierwszy raz przesuwa suwak >0, dopiero
        // wtedy podpinamy wetGain do convolvera.
        setTrackReverbMix(sessionId, layerId, trackId, mix) {
            const L = findLayer(sessionId, layerId);
            if (!L) {
                console.warn('[medytaoPlayer] setTrackReverbMix: layer not found', { sessionId, layerId });
                return;
            }
            const m = clamp01(mix);

            const inSeq = L.tracks.find(x => x && eqId(x.trackId, trackId));
            const inSched = inSeq ? null : (L.scheduledTracks ? L.scheduledTracks.find(x => x && eqId(x.trackId, trackId)) : null);
            if (!inSeq && !inSched) {
                console.warn('[medytaoPlayer] setTrackReverbMix: track not found', { layerId, trackId });
                return;
            }
            if (inSeq) inSeq.reverbMix = m;
            if (inSched) inSched.reverbMix = m;

            const current = L.tracks[L.index];
            const isCurrentSeq = current && eqId(current.trackId, trackId);
            if (isCurrentSeq && L.audioGraph) {
                applyTrackMix(L, L.audioGraph, m);
            }
            if (L.scheduledOverlays) {
                for (const o of L.scheduledOverlays) {
                    if (eqId(o.trackId, trackId) && o.graph) applyTrackMix(L, o.graph, m);
                }
            }
            console.debug('[medytaoPlayer] setTrackReverbMix', { layerId, trackId, mix: m });
        },

        // Pobiera durationMs dla podanej listy assetów bez startowania
        // sesji. Wywoływane z editor-a zaraz po wczytaniu medytacji, żeby
        // skala timeline-a była poprawna jeszcze przed kliknięciem Play.
        // assets: [{ assetId, url }] (zdedupowane po assetId po stronie C#).
        // Każda odkryta wartość leci do ReportAssetDuration → cache C# +
        // PATCH /assets/{id}/duration → backend zapisuje, więc kolejne
        // sesje (po refresh strony) nie wymagają już fetch-u.
        async preloadDurations(assets, dotNetRef) {
            if (!assets || assets.length === 0) return;
            const promises = assets
                .filter(a => a && a.url && a.assetId)
                .map(a => fetchDurationFor(a).then(ms => {
                    if (!ms) return;
                    if (dotNetRef) {
                        try {
                            dotNetRef.invokeMethodAsync('ReportAssetDuration', a.assetId, ms);
                        } catch (e) {
                            console.warn('[medytaoPlayer] preloadDurations report failed', e);
                        }
                    }
                }));
            if (promises.length > 0) {
                console.debug('[medytaoPlayer] preloading durations for', promises.length, 'assets');
                await Promise.all(promises);
            }
        },

        // Diagnostyka — pozwala sprawdzić z konsoli, co jest w sesji.
        dump(sessionId) {
            const s = sessions.get(sessionId);
            if (!s) return null;
            return s.layers.map(L => ({
                layerId: L.layerId,
                layerVolume: L.layerVolume,
                muted: L.muted,
                index: L.index,
                audioVolume: L.audio ? L.audio.volume : null,
                hasConvolver: !!L.convolver,
                hasGraph: !!L.audioGraph,
                tracks: L.tracks.map(t => ({
                    trackId: t.trackId, volume: t.volume,
                    loopCount: t.loopCount, reverbMix: t.reverbMix
                }))
            }));
        },

        // Debug helper — not used by Blazor.
        _sessions: sessions
    };

    // Guid-y z Blazora czasem przychodzą w różnych notacjach (małe/wielkie
    // litery, z/bez myślników). Porównujemy po znormalizowanym stringu.
    function normalizeId(v) {
        if (v === null || v === undefined) return '';
        return String(v).toLowerCase().replace(/[^0-9a-f]/g, '');
    }

    function eqId(a, b) {
        return normalizeId(a) === normalizeId(b);
    }

    function findLayer(sessionId, layerId) {
        const s = sessions.get(sessionId);
        if (!s) return null;
        return s.layers.find(L => eqId(L.layerId, layerId)) || null;
    }

    // Aktualizuje volume na sequenced state.audio (jeśli leci) — wartość
    // pochodzi z aktualnego tracka w sekwencji.
    function applyCurrentVolume(L) {
        if (!L.audio) return;
        const track = L.tracks[L.index];
        L.audio.volume = effectiveVolume(L.layerVolume, track ? track.volume : 1, L.muted);
    }

    // Aktualizuje volume na WSZYSTKICH aktywnych audio w warstwie:
    // sequenced (state.audio) + overlay (state.scheduledOverlays). Bez
    // tego setLayerVolume / setLayerMuted działały tylko na sequenced —
    // gdy warstwa miała same scheduled tracki (np. Text z time-anchored
    // fragmentami), suwaki nic nie zmieniały bo state.audio = null.
    function applyAllAudiosVolume(L) {
        if (L.audio) {
            const seqTrack = L.tracks[L.index];
            L.audio.volume = effectiveVolume(L.layerVolume, seqTrack ? seqTrack.volume : 1, L.muted);
        }
        if (L.scheduledOverlays) {
            for (const o of L.scheduledOverlays) {
                // Volume scheduled tracka znajdujemy po trackId w
                // L.scheduledTracks (źródło prawdy mutowane przez settery).
                let trackVol = 1;
                if (L.scheduledTracks) {
                    const t = L.scheduledTracks.find(x => x && eqId(x.trackId, o.trackId));
                    if (t) trackVol = (typeof t.volume === 'number') ? t.volume : 1;
                }
                o.audio.volume = effectiveVolume(L.layerVolume, trackVol, L.muted);
            }
        }
    }

    function clamp01(v) {
        if (typeof v !== 'number' || !isFinite(v)) return 0;
        return Math.max(0, Math.min(1, v));
    }
})();