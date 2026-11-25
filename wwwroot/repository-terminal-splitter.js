const attached = new WeakMap();

export function attach(stack, dotnet) {
    detach(stack);

    const splitter = stack.querySelector('.repository-terminal-splitter-grip');
    if (!splitter) return;

    let dragging = false;
    let animationFrame = 0;
    let pendingPercent = null;
    const viewport = stack.querySelector('.repository-codebadger-session-tabs-viewport');
    let tabFrame = 0;
    let visibleSessionId = null;
    let wasVisible = false;

    const revealSelectedTab = () => {
        tabFrame = 0;
        if (!viewport) return;
        const visible = viewport.clientWidth > 0 && viewport.getClientRects().length > 0;
        if (!visible) {
            wasVisible = false;
            return;
        }
        const selected = viewport.querySelector('.repository-codebadger-session-tab.selected');
        if (!selected) return;
        const sessionId = selected.dataset.sessionId;
        if (wasVisible && visibleSessionId === sessionId) return;
        wasVisible = true;
        visibleSessionId = sessionId;

        const bounds = viewport.getBoundingClientRect();
        const tabBounds = selected.getBoundingClientRect();
        if (tabBounds.left < bounds.left)
            viewport.scrollLeft += tabBounds.left - bounds.left;
        else if (tabBounds.right > bounds.right)
            viewport.scrollLeft += tabBounds.right - bounds.right;
    };
    const scheduleTabReveal = () => {
        if (!tabFrame) tabFrame = requestAnimationFrame(revealSelectedTab);
    };
    const tabObserver = new MutationObserver(scheduleTabReveal);
    const sizeObserver = new ResizeObserver(scheduleTabReveal);
    if (viewport) {
        tabObserver.observe(viewport, { childList: true, subtree: true, attributes: true, attributeFilter: ['class'] });
        sizeObserver.observe(viewport);
        scheduleTabReveal();
    }

    const update = event => {
        if (!dragging) return;
        const bounds = stack.getBoundingClientRect();
        if (bounds.height <= 0) return;
        const percent = Math.max(15, Math.min(75, (event.clientY - bounds.top) * 100 / bounds.height));
        pendingPercent = percent;
        if (animationFrame) return;
        animationFrame = requestAnimationFrame(() => {
            animationFrame = 0;
            stack.style.setProperty('--ai-terminal-height', `${pendingPercent}%`);
        });
    };

    const stop = () => {
        if (!dragging) return;
        dragging = false;
        document.body.classList.remove('repository-terminal-resizing');
        window.removeEventListener('pointermove', update);
        window.removeEventListener('pointerup', stop);
        window.removeEventListener('pointercancel', stop);
        if (pendingPercent !== null)
            dotnet.invokeMethodAsync('SetAiPanelSize', pendingPercent);
    };

    const start = event => {
        if (event.button !== 0) return;
        event.preventDefault();
        dragging = true;
        document.body.classList.add('repository-terminal-resizing');
        window.addEventListener('pointermove', update);
        window.addEventListener('pointerup', stop);
        window.addEventListener('pointercancel', stop);
    };

    const keydown = event => {
        if (event.key !== 'ArrowUp' && event.key !== 'ArrowDown') return;
        event.preventDefault();
        const bounds = stack.getBoundingClientRect();
        const current = parseFloat(getComputedStyle(stack).getPropertyValue('--ai-terminal-height')) || bounds.height * 0.38;
        const delta = bounds.height > 0 && current > 100 ? 20 * 100 / bounds.height : 3;
        const next = Math.max(15, Math.min(75, current + (event.key === 'ArrowDown' ? delta : -delta)));
        stack.style.setProperty('--ai-terminal-height', `${next}%`);
        dotnet.invokeMethodAsync('SetAiPanelSize', next);
    };

    splitter.addEventListener('pointerdown', start);
    splitter.addEventListener('keydown', keydown);
    attached.set(stack, () => {
        stop();
        if (animationFrame) cancelAnimationFrame(animationFrame);
        if (tabFrame) cancelAnimationFrame(tabFrame);
        tabObserver.disconnect();
        sizeObserver.disconnect();
        splitter.removeEventListener('pointerdown', start);
        splitter.removeEventListener('keydown', keydown);
    });
}

export function detach(stack) {
    attached.get(stack)?.();
    attached.delete(stack);
}
