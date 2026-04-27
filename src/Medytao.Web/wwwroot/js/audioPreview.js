// Silnik audio dla Medytao.
// Dwa namespace'y:
//   window.medytaoAudio  — proste helpery nad pojedynczym <audio> (preview button).
//   window.medytaoPlayer — odtwarzacz sesji: wiele warstw grających równolegle,
//                          każda warstwa to sekwencja tracków (LoopCount).

// ── medytaoAudio ──────────────────────────────────────────────────────────────
window.medytaoAudio = {
    play(audioEl, volume) {
        if (!audioEl) return;
        if (typeof volume === 'number') {
            audioEl.volume = Math.max(0, Math.min(1, volume));
        }
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
//   layers: [{ id, volume, muted, tracks: [{ trackId, url, volume, loopCount, playbackRate }] }]
//   loopCount: 0 = loop forever (blocks next tracks), N = play N times.
//   playbackRate: 1.0 = normalna prędkość. preservesPitch=true zachowuje wysokość
//     tonu — slowdown brzmi naturalnie, bez "grunting" efektu.
//
// stopSession(sessionId) — zatrzymuje i zwalnia wszystkie <audio>.
// setLayerVolume(sessionId, layerId, volume) — zmienia głośność warstwy w czasie rzeczywistym.
// setLayerMuted(sessionId, layerId, muted) — wycisza / przywraca warstwę.
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
        return a;
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
                    tracks: L.tracks.map(t => ({ trackId: t.trackId, volume: t.volume, url: t.url }))
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