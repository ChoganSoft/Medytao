// Silnik audio dla Medytao.
// Dwa namespace'y:
//   window.medytaoAudio  — proste helpery nad pojedynczym <audio> (preview button).
//   window.medytaoPlayer — odtwarzacz sesji: wiele warstw grających równolegle,
//                          każda warstwa to sekwencja tracków (LoopCount).

// ── medytaoAudio ──────────────────────────────────────────────────────────────
window.medytaoAudio = {
    // play(audioEl, volume, rate?) — rate opcjonalny (kompatybilność wstecz).
    // Jeśli podany, ustawiamy preservesPitch + playbackRate przed play(),
    // żeby pierwszy sample już leciał w docelowym tempie.
    play(audioEl, volume, rate) {
        if (!audioEl) return;
        if (typeof volume === 'number') {
            audioEl.volume = Math.max(0, Math.min(1, volume));
        }
        applyRateToEl(audioEl, rate);
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

// Helper współdzielony przez medytaoAudio i medytaoPlayer (ten drugi ma
// własną wewnętrzną kopię applyRate w IIFE poniżej — duplikujemy świadomie,
// żeby IIFE nie musiał sięgać do globala).
function applyRateToEl(audioEl, rate) {
    const r = (typeof rate === 'number' && isFinite(rate) && rate > 0)
        ? Math.max(0.5, Math.min(2.0, rate))
        : 1.0;
    try { audioEl.preservesPitch = true; } catch { }
    try { audioEl.mozPreservesPitch = true; } catch { }
    try { audioEl.webkitPreservesPitch = true; } catch { }
    try { audioEl.playbackRate = r; } catch (e) {
        console.warn('medytaoAudio: playbackRate set failed', e);
    }
}

// Alias dla kompatybilności wstecz.
window.meditationPlayer = window.medytaoAudio;

// ── medytaoPlayer ─────────────────────────────────────────────────────────────
// Sesja playbacku. Każda warstwa gra sekwencyjnie, warstwy grają równolegle.
//
// startSession(layers) → sessionId
//   layers: [{ id, volume, muted, reverbPreset, reverbMix,
//              tracks: [{ trackId, url, volume, loopCount, playbackRate }] }]
//   loopCount: 0 = loop forever (blocks next tracks), N = play N times.
//   playbackRate: 1.0 = normalna prędkość. preservesPitch=true zachowuje wysokość
//     tonu — slowdown brzmi naturalnie, bez "grunting" efektu.
//   reverbPreset: "Off" / "Room" / "Hall" — gdy != "Off" oraz reverbMix > 0,
//     warstwa rusza w grafie AudioContext z ConvolverNode-em (syntetyczne IR).
//   reverbMix: 0..1 wet/dry. 0 = bypass nawet gdy preset != "Off".
//
// stopSession(sessionId) — zatrzymuje i zwalnia wszystkie <audio>.
// setLayerVolume(sessionId, layerId, volume) — zmienia głośność warstwy w czasie rzeczywistym.
// setLayerMuted(sessionId, layerId, muted) — wycisza / przywraca warstwę.
// setLayerReverb(sessionId, layerId, preset, mix) — zmienia preset/mix reverbu.
//   Pierwsze włączenie buduje graf AudioContext dla warstwy; kolejne tylko
//   przełączają wet/dry gain i ewentualnie podmieniają IR.
// setTrackVolume(sessionId, layerId, trackId, volume) — zmienia głośność tracka (stosuje się
//   od razu, jeśli ten track jest akurat odtwarzany w swojej warstwie).
// setTrackPlaybackRate(sessionId, layerId, trackId, rate) — zmienia tempo tracka. Jeśli track
//   leci, .playbackRate aplikuje się natychmiast; jeśli nie, wartość zostaje zapamiętana.

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
    // Globalny singleton — Chrome wymaga user gesture na pierwszy resume(),
    // więc tworzymy lazy w startSession (po kliknięciu Play). Sentinel `false`
    // znaczy "próbowaliśmy i się nie udało" — nie spamujemy logiem.
    let _audioCtx = null;

    function getAudioCtx() {
        if (_audioCtx === null) {
            try {
                const Ctor = window.AudioContext || window.webkitAudioContext;
                _audioCtx = new Ctor();
            } catch (e) {
                console.warn('[medytaoPlayer] AudioContext unavailable', e);
                _audioCtx = false;
            }
        }
        return _audioCtx || null;
    }

    // IR-y per preset cache'owane między sesjami. AudioContext.sampleRate
    // bywa różne (44.1k/48k), ale w obrębie jednej sesji jest stabilny —
    // jeśli kiedyś będziemy mieli wiele context-ów, klucz trzeba rozszerzyć.
    const _irCache = new Map();

    function getOrCreateIR(presetName, ctx) {
        if (_irCache.has(presetName)) return _irCache.get(presetName);
        const buf = synthIR(presetName, ctx);
        _irCache.set(presetName, buf);
        return buf;
    }

    // Algorytmiczny impulse response — biały szum * exp envelope, lekki LP
    // smoothing dla stłumienia syku. Stereo decorrelated (osobny noise per
    // kanał) = naturalna szerokość. To nie jest fizyczna symulacja sali;
    // wystarcza dla medytacji, gdzie chodzi o przestrzenność, nie realizm.
    function synthIR(preset, ctx) {
        let decaySec, smoothFactor, gain;
        switch (preset) {
            // smoothFactor: 0 = bardzo "drewniane" (mocne LP), 1 = brak filtra (ostre).
            // Krótszy decay → mocniejszy attack (Room brzmi szybciej, Hall delikatniej).
            case 'Room':
                decaySec = 0.6; smoothFactor = 0.55; gain = 0.85; break;
            case 'Hall':
                decaySec = 2.2; smoothFactor = 0.35; gain = 0.7; break;
            default:
                decaySec = 0.6; smoothFactor = 0.55; gain = 0.85;
        }
        const sr = ctx.sampleRate;
        const length = Math.max(1, Math.floor(decaySec * sr));
        const ir = ctx.createBuffer(2, length, sr);
        for (let ch = 0; ch < 2; ch++) {
            const data = ir.getChannelData(ch);
            let prev = 0;
            for (let i = 0; i < length; i++) {
                const t = i / length;
                // power(2.5) zamiast eksp — fonetycznie naturalniejsze
                // gasniecie, brak długich szumiących "ogonów".
                const env = Math.pow(1 - t, 2.5);
                const noise = (Math.random() * 2 - 1) * env * gain;
                // 1-bieg LP: prev * (1-s) + sample * s. Wyższy s → bliżej oryginału.
                const out = prev * (1 - smoothFactor) + noise * smoothFactor;
                data[i] = out;
                prev = out;
            }
        }
        return ir;
    }

    // Tworzy graf reverbu warstwy: source(audio) → [dry, convolver→wet] → destination.
    // MediaElementAudioSourceNode można utworzyć tylko RAZ na element, więc
    // robimy to przy każdym nowym <audio> (bo każdy track = nowy element).
    // Sam graf (dry/wet/convolver) tworzymy raz per warstwa i reusujemy.
    function ensureLayerGraph(L) {
        const ctx = getAudioCtx();
        if (!ctx) return false;
        if (L.graphReady) return true;

        if (ctx.state === 'suspended') {
            ctx.resume().catch(() => { /* idempotent */ });
        }

        L.dryGain = ctx.createGain();
        L.wetGain = ctx.createGain();
        L.convolver = ctx.createConvolver();
        L.dryGain.connect(ctx.destination);
        L.wetGain.connect(ctx.destination);
        L.convolver.connect(L.wetGain);
        L.audioSources = []; // referencje do MediaElementAudioSourceNode-ów

        applyConvolverIR(L, ctx);
        applyReverbGains(L);
        L.graphReady = true;
        return true;
    }

    function attachAudioToLayerGraph(L, audio) {
        if (!L.graphReady) return;
        const ctx = getAudioCtx();
        if (!ctx) return;
        try {
            const src = ctx.createMediaElementSource(audio);
            src.connect(L.dryGain);
            src.connect(L.convolver);
            L.audioSources.push(src);
        } catch (e) {
            // createMediaElementSource rzuca, jeśli element już został wpięty
            // (nie powinno się zdarzyć, bo robimy świeży <audio> na każdy track).
            console.warn('[medytaoPlayer] attach to graph failed', e);
        }
    }

    function applyReverbGains(L) {
        if (!L.dryGain || !L.wetGain) return;
        const mix = clamp01(L.reverbMix);
        const presetActive = L.reverbPreset && L.reverbPreset !== 'Off';
        // mix=0 lub Off → 100% dry, 0% wet (efektywny bypass bez burzenia grafu).
        const wet = presetActive ? mix : 0;
        const dry = presetActive ? (1 - mix) : 1;
        L.dryGain.gain.value = dry;
        L.wetGain.gain.value = wet;
    }

    function applyConvolverIR(L, ctx) {
        if (!L.convolver) return;
        const preset = (L.reverbPreset && L.reverbPreset !== 'Off') ? L.reverbPreset : null;
        L.convolver.buffer = preset ? getOrCreateIR(preset, ctx) : null;
    }

    function effectiveVolume(layerVolume, trackVolume, muted) {
        if (muted) return 0;
        const v = (layerVolume ?? 1) * (trackVolume ?? 1);
        return Math.max(0, Math.min(1, v));
    }

    function playCurrent(state) {
        const track = state.tracks[state.index];
        if (!track) return; // end of layer

        // Detach previous listener if reusing element (we don't — fresh each time).
        const audio = createAudio(track.url);
        audio.volume = effectiveVolume(state.layerVolume, track.volume, state.muted);
        applyRate(audio, track.playbackRate);

        audio.addEventListener('ended', () => onTrackEnded(state));
        state.audio = audio;

        // Jeśli warstwa już ma utworzony graf reverbu, wpinamy nowo
        // utworzony element. (audio.volume nadal działa pre-graph, więc
        // logika volumu zostaje bez zmian.)
        if (state.graphReady) {
            attachAudioToLayerGraph(state, audio);
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
        startSession(layers) {
            const sessionId = newSessionId();
            const layerStates = (layers || [])
                .filter(l => l && l.tracks && l.tracks.length > 0)
                .map(l => {
                    const firstTrack = l.tracks[0];
                    const playsLeft = firstTrack.loopCount === 0
                        ? 1
                        : Math.max(1, firstTrack.loopCount || 1);
                    return {
                        layerId: l.id,
                        layerVolume: l.volume,
                        muted: !!l.muted,
                        // Reverb state — graf utworzymy lazy, gdy faktycznie potrzebny.
                        reverbPreset: l.reverbPreset || 'Off',
                        reverbMix: clamp01(l.reverbMix),
                        graphReady: false,
                        dryGain: null, wetGain: null, convolver: null,
                        audioSources: null,
                        tracks: l.tracks,
                        index: 0,
                        playsLeft,
                        audio: null
                    };
                });

            sessions.set(sessionId, { layers: layerStates });
            console.debug('[medytaoPlayer] startSession', {
                sessionId,
                layers: layerStates.map(L => ({
                    layerId: L.layerId,
                    layerVolume: L.layerVolume,
                    muted: L.muted,
                    reverbPreset: L.reverbPreset,
                    reverbMix: L.reverbMix,
                    tracks: L.tracks.map(t => ({ trackId: t.trackId, volume: t.volume, url: t.url }))
                }))
            });

            // Warstwy z aktywnym reverbem — tworzymy graf zanim odpalimy
            // playCurrent (który wpina audio w graf). Bez tego pierwszy
            // track byłby dry, a wet pojawiłby się dopiero przy następnym.
            for (const state of layerStates) {
                if (state.reverbPreset !== 'Off' && state.reverbMix > 0) {
                    ensureLayerGraph(state);
                }
            }

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

        // Real-time reverb warstwy. Pierwsze włączenie (preset != Off, mix > 0)
        // buduje graf AudioContext i wpina aktualny <audio> element. Kolejne
        // wywołania tylko aktualizują wet/dry gain albo podmieniają IR.
        // Nie odpinamy źródeł nawet po wyłączeniu (mix=0/preset=Off) —
        // MediaElementAudioSource raz wpięty zostaje na zawsze; ustawiamy
        // wet=0, dry=1 = efektywny bypass bez zauważalnej różnicy.
        setLayerReverb(sessionId, layerId, preset, mix) {
            const L = findLayer(sessionId, layerId);
            if (!L) {
                console.warn('[medytaoPlayer] setLayerReverb: layer not found', { sessionId, layerId });
                return;
            }
            const newPreset = (preset || 'Off');
            const newMix = clamp01(mix);
            const wantsActive = newPreset !== 'Off' && newMix > 0;

            L.reverbPreset = newPreset;
            L.reverbMix = newMix;

            if (wantsActive && !L.graphReady) {
                const ok = ensureLayerGraph(L);
                if (!ok) return;
                // Wpinamy obecnie grający audio; kolejne tracki same się
                // wpiną w playCurrent dzięki state.graphReady = true.
                if (L.audio) attachAudioToLayerGraph(L, L.audio);
            }

            if (L.graphReady) {
                applyConvolverIR(L, getAudioCtx());
                applyReverbGains(L);
            }

            console.debug('[medytaoPlayer] setLayerReverb', {
                layerId, preset: L.reverbPreset, mix: L.reverbMix, graphReady: L.graphReady
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
                tracks: L.tracks.map(t => ({ trackId: t.trackId, volume: t.volume, loopCount: t.loopCount }))
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