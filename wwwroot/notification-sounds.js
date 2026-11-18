// Short, quiet synthesized tones keep previews and notifications self-contained.
window.repoLauncherSounds = (() => {
    let context;
    const sounds = {
        bell:    { notes: [784, 1047], duration: 0.16, gap: 0.07, wave: 'sine' },
        chime:   { notes: [659, 880, 1047], duration: 0.14, gap: 0.075, wave: 'sine' },
        drop:    { notes: [988, 740], duration: 0.13, gap: 0.08, wave: 'sine' },
        sparkle: { notes: [1047, 1319, 1568], duration: 0.11, gap: 0.055, wave: 'sine' },
        soft:    { notes: [523, 659], duration: 0.2, gap: 0.1, wave: 'sine' },
        glass:   { notes: [1175, 880], duration: 0.15, gap: 0.09, wave: 'triangle' },
        marimba: { notes: [523, 784], duration: 0.1, gap: 0.09, wave: 'triangle' },
        rise:    { notes: [440, 554, 659], duration: 0.13, gap: 0.08, wave: 'sine' },
        warm:    { notes: [392, 523], duration: 0.22, gap: 0.1, wave: 'sine' },
        ping:    { notes: [880], duration: 0.18, gap: 0, wave: 'sine' }
    };

    // Unlock audio on the first interaction for WebViews enforcing autoplay rules.
    const unlock = () => {
        try {
            context ??= new AudioContext();
            if (context.state === 'suspended') void context.resume().catch(() => {});
        } catch { /* Audio is optional. */ }
    };
    document.addEventListener('pointerdown', unlock, { once: true });
    document.addEventListener('keydown', unlock, { once: true });

    async function play(id) {
        try {
            context ??= new AudioContext();
            if (context.state === 'suspended') await context.resume();
            const sound = sounds[id] ?? sounds.bell;
            const start = context.currentTime + 0.01;
            sound.notes.forEach((frequency, index) => {
                const at = start + index * sound.gap;
                const oscillator = context.createOscillator();
                const gain = context.createGain();
                oscillator.type = sound.wave;
                oscillator.frequency.value = frequency;
                gain.gain.setValueAtTime(0.0001, at);
                gain.gain.exponentialRampToValueAtTime(0.045, at + 0.015);
                gain.gain.exponentialRampToValueAtTime(0.0001, at + sound.duration);
                oscillator.connect(gain);
                gain.connect(context.destination);
                oscillator.start(at);
                oscillator.stop(at + sound.duration);
                oscillator.onended = () => { oscillator.disconnect(); gain.disconnect(); };
            });
        } catch (error) {
            console.warn('Could not play completion sound', error);
        }
    }

    return { play };
})();
