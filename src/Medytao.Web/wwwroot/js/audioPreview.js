// Silnik audio dla Medytao — proste helpery nad natywnymi <audio>.

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
        try { audioEl.pause(); } catch {}
    },

    stop(audioEl) {
        if (!audioEl) return;
        try {
            audioEl.pause();
            audioEl.currentTime = 0;
        } catch {}
    },

    setVolume(audioEl, volume) {
        if (!audioEl) return;
        audioEl.volume = Math.max(0, Math.min(1, volume));
    },

    pauseAll() {
        document.querySelectorAll('audio').forEach(a => {
            try { a.pause(); } catch {}
        });
    }
};

// Alias dla MeditationPlayer (używa tych samych helperów)
window.meditationPlayer = window.medytaoAudio;
