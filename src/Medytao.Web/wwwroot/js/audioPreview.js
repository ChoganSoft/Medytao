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

(function () {
    const sessions = new Map(); // sessionId → { layers: [LayerState] }
    // LayerState: { layerVolume, muted, tracks, index, playsLeft, audio }

    function createAudio(url) {
        const a = new Audio(url);
        a.preload = 'auto';
        // crossOrigin trzeba ustawić ZANIM Web Audio dotknie elementu —
        // inaczej tainted-resource odcina output grafu (cisza) na zasobach
        // serwowanych spoza origin (np. blob storage z innym hostem). 'anonymous'
        // = bez credentiali, działa wszędzie gdzie odpowiada CORS-em.
        a.crossOrigin = 'anonymous';
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

            if (t.loopCount === 0) {
                // Wieczna pętla — zatrzymujemy się na tym tracku.
                state.index = i;
                state.startOffsetMs = dMs > 0 ? (remaining % dMs) : 0;
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

            const totalMs = dMs * loops;
            if (remaining < totalMs) {
                const playedLoops = Math.floor(remaining / dMs);
                state.index = i;
                state.startOffsetMs = remaining - playedLoops * dMs;
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
        audio.volume = effectiveVolume(state.layerVolume, track.volume, state.muted);
        applyRate(audio, track.playbackRate);

        // Jeśli weszliśmy w track po seeku, ustawiamy currentTime na offset.
        // Ustawiamy PRZED play(), żeby pierwszy sample już leciał z właściwej
        // pozycji. Po pierwszym użyciu zerujemy startOffsetMs — następne
        // wywołania playCurrent w tej warstwie (kolejne tracki w sekwencji)
        // mają zaczynać od początku.
        const startOffsetSec = (state.startOffsetMs || 0) / 1000;
        state.startOffsetMs = 0;

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
        try {
            audio.pause();
            audio.src = '';
            audio.load();
        } catch (e) { /* noop */ }
    }

    function newSessionId() {
        return 'sess_' + Math.random().toString(36).slice(2, 10) + Date.now().toString(36);
    }

    window.medytaoPlayer = {
        // startSession(layers, startFromMs?) — gdy startFromMs > 0, każda
        // warstwa "fast-forwards" do tego punktu na master clocku medytacji:
        // znajdujemy track, który grałby w tym momencie, ustawiamy jego
        // currentTime na offset wewnątrz, doliczamy ile pętli już się odbyło.
        // Pozwala seekować bez konieczności słuchania wszystkiego od zera.
        startSession(layers, startFromMs) {
            const sessionId = newSessionId();
            const seekMs = (typeof startFromMs === 'number' && startFromMs > 0) ? startFromMs : 0;
            const layerStates = (layers || [])
                .filter(l => l && l.tracks && l.tracks.length > 0)
                .map(l => ({
                    layerId: l.id,
                    layerVolume: l.volume,
                    muted: !!l.muted,
                    convolver: null,
                    tracks: l.tracks,
                    index: 0,
                    playsLeft: 1,
                    startOffsetMs: 0,  // offset wewnątrz aktualnego tracka (tylko przy starcie po seek)
                    audio: null,
                    audioGraph: null
                }));

            // Fast-forward: dla każdej warstwy ustal index/playsLeft/startOffsetMs
            // wg seekMs. Warstwa, w której seek wypada poza końcem sekwencji,
            // dostaje index = tracks.length (czyli "skończona" — playCurrent
            // od razu wróci, layer milczy).
            for (const state of layerStates) {
                applyFastForward(state, seekMs);
            }

            sessions.set(sessionId, { layers: layerStates });
            console.debug('[medytaoPlayer] startSession', {
                sessionId,
                seekMs,
                layers: layerStates.map(L => ({
                    layerId: L.layerId,
                    index: L.index,
                    playsLeft: L.playsLeft,
                    startOffsetMs: L.startOffsetMs,
                    layerVolume: L.layerVolume,
                    muted: L.muted,
                    tracks: L.tracks.map(t => ({
                        trackId: t.trackId, volume: t.volume,
                        durationMs: t.durationMs, reverbMix: t.reverbMix
                    }))
                }))
            });

            for (const state of layerStates) {
                playCurrent(state);
            }
            return sessionId;
        },

        stopSession(sessionId) {
            const s = sessions.get(sessionId);
            if (!s) return;
            for (const state of s.layers) {
                disposeAudio(state.audio);
                state.audio = null;
            }
            sessions.delete(sessionId);
        },

        getProgress(sessionId) {
            const s = sessions.get(sessionId);
            if (!s) return [];
            return s.layers.map((state, i) => {
                const a = state.audio;
                const d = a ? a.duration : 0;
                return {
                    layerIndex: i,
                    layerId: state.layerId,
                    trackIndex: state.index,
                    trackCount: state.tracks.length,
                    currentTime: a ? (a.currentTime || 0) : 0,
                    duration: (a && isFinite(d) && d > 0) ? d : 0,
                    finished: state.index >= state.tracks.length
                };
            });
        },

        // Real-time głośność warstwy. Jeżeli coś w warstwie gra, zmieniamy
        // od razu głośność aktywnego <audio>.
        setLayerVolume(sessionId, layerId, volume) {
            const L = findLayer(sessionId, layerId);
            if (!L) {
                console.warn('[medytaoPlayer] setLayerVolume: layer not found', { sessionId, layerId });
                return;
            }
            L.layerVolume = clamp01(volume);
            applyCurrentVolume(L);
            console.debug('[medytaoPlayer] setLayerVolume', { layerId, volume: L.layerVolume, effective: L.audio ? L.audio.volume : null });
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
            applyCurrentVolume(L);
            console.debug('[medytaoPlayer] setLayerMuted', { layerId, muted: L.muted });
        },

        // Real-time głośność pojedynczego tracka. Mutujemy wpis w state.tracks —
        // nawet jeżeli track jeszcze nie leci, następne wywołanie playCurrent
        // zobaczy nową wartość.
        setTrackVolume(sessionId, layerId, trackId, volume) {
            const L = findLayer(sessionId, layerId);
            if (!L) {
                console.warn('[medytaoPlayer] setTrackVolume: layer not found', { sessionId, layerId, knownLayers: sessions.get(sessionId)?.layers.map(l => l.layerId) });
                return;
            }
            const t = L.tracks.find(x => x && eqId(x.trackId, trackId));
            if (!t) {
                console.warn('[medytaoPlayer] setTrackVolume: track not found', { layerId, trackId, knownTracks: L.tracks.map(x => x && x.trackId) });
                return;
            }
            t.volume = clamp01(volume);
            const current = L.tracks[L.index];
            const isCurrent = current && eqId(current.trackId, trackId);
            if (isCurrent) applyCurrentVolume(L);
            console.debug('[medytaoPlayer] setTrackVolume', {
                layerId, trackId, volume: t.volume, isCurrent,
                audioVolume: L.audio ? L.audio.volume : null,
                layerVolume: L.layerVolume, muted: L.muted
            });
        },

        // Real-time tempo pojedynczego tracka. Mutujemy state.tracks żeby
        // następne wejście playCurrent wzięło nową wartość; jeśli track akurat
        // leci, ustawiamy playbackRate na żywo. preservesPitch zostaje on
        // (ustawione na początku przez applyRate).
        setTrackPlaybackRate(sessionId, layerId, trackId, rate) {
            const L = findLayer(sessionId, layerId);
            if (!L) {
                console.warn('[medytaoPlayer] setTrackPlaybackRate: layer not found', { sessionId, layerId });
                return;
            }
            const t = L.tracks.find(x => x && eqId(x.trackId, trackId));
            if (!t) {
                console.warn('[medytaoPlayer] setTrackPlaybackRate: track not found', { layerId, trackId });
                return;
            }
            t.playbackRate = clampRate(rate);
            const current = L.tracks[L.index];
            const isCurrent = current && eqId(current.trackId, trackId);
            if (isCurrent && L.audio) applyRate(L.audio, t.playbackRate);
            console.debug('[medytaoPlayer] setTrackPlaybackRate', {
                layerId, trackId, rate: t.playbackRate, isCurrent
            });
        },

        // Real-time wet/dry mix reverbu pojedynczego tracka. Mutujemy stan
        // w state.tracks — następne wejście w playCurrent zobaczy nowy mix
        // przy tworzeniu grafu. Jeśli track akurat gra, applyTrackMix
        // zmienia gain-y w grafie audio natychmiast (bez glitchu w sygnale).
        // Lazy connect wet path: gdy track startował z mix=0 i user pierwszy
        // raz przesuwa suwak >0, dopiero wtedy podpinamy wetGain do convolvera.
        setTrackReverbMix(sessionId, layerId, trackId, mix) {
            const L = findLayer(sessionId, layerId);
            if (!L) {
                console.warn('[medytaoPlayer] setTrackReverbMix: layer not found', { sessionId, layerId });
                return;
            }
            const t = L.tracks.find(x => x && eqId(x.trackId, trackId));
            if (!t) {
                console.warn('[medytaoPlayer] setTrackReverbMix: track not found', { layerId, trackId });
                return;
            }
            t.reverbMix = clamp01(mix);
            const current = L.tracks[L.index];
            const isCurrent = current && eqId(current.trackId, trackId);
            if (isCurrent && L.audioGraph) {
                applyTrackMix(L, L.audioGraph, t.reverbMix);
            }
            console.debug('[medytaoPlayer] setTrackReverbMix', {
                layerId, trackId, mix: t.reverbMix, isCurrent
            });
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

    function applyCurrentVolume(L) {
        if (!L.audio) return;
        const track = L.tracks[L.index];
        L.audio.volume = effectiveVolume(L.layerVolume, track ? track.volume : 1, L.muted);
    }

    function clamp01(v) {
        if (typeof v !== 'number' || !isFinite(v)) return 0;
        return Math.max(0, Math.min(1, v));
    }
})();