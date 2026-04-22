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
//   layers: [{ id, volume, muted, tracks: [{ url, volume, loopCount }] }]
//   loopCount: 0 = loop forever (blocks next tracks), N = play N times.
//
// stopSession(sessionId) — zatrzymuje i zwalnia wszystkie <audio>.

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

        audio.addEventListener('ended', () => onTrackEnded(state));
        state.audio = audio;

        audio.play().catch(err => console.warn('Medytao player: play failed', err));
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

        // Debug helper — not used by Blazor.
        _sessions: sessions
    };
})();