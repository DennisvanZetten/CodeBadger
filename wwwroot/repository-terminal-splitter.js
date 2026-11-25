const attached = new WeakMap();

export function attach(stack, dotnet) {
    detach(stack);

    const controls = stack.querySelector('.repository-terminal-splitter');
    const grip = stack.querySelector('.repository-terminal-splitter-grip');
    const snap = percent => percent <= 5 ? 0 : percent >= 95 ? 100 : percent;
    const availableHeight = () => Math.max(1, stack.clientHeight - (controls?.offsetHeight || 0));
    let dragging = false;
    let pointerOffset = 0;
    let pendingPercent = null;
    let savedStyle = null;
    let savedChatStyle = null;
    const preview = percent => {
        pendingPercent = snap(Math.max(0, Math.min(100, percent)));
        stack.classList.toggle('ai-pane-collapsed', pendingPercent === 0);
        stack.classList.toggle('ai-pane-expanded', pendingPercent === 100);
        stack.style.setProperty('--ai-terminal-ratio', `${pendingPercent / 100}fr`);
        stack.style.setProperty('--chat-terminal-ratio', `${1 - pendingPercent / 100}fr`);
    };
    const move = event => {
        if (!dragging) return;
        preview((event.clientY - stack.getBoundingClientRect().top - pointerOffset) * 100 / availableHeight());
    };
    const finish = event => {
        if (!dragging) return;
        if (event?.type === 'pointerup') move(event);
        dragging = false;
        document.body.classList.remove('repository-terminal-resizing');
        window.removeEventListener('pointermove', move);
        window.removeEventListener('pointerup', finish);
        window.removeEventListener('pointercancel', finish);
        // Restore Blazor's last value so the next render applies the committed size.
        if (savedStyle !== null) stack.style.setProperty('--ai-terminal-ratio', savedStyle);
        if (savedChatStyle !== null) stack.style.setProperty('--chat-terminal-ratio', savedChatStyle);
        if (pendingPercent !== null) dotnet.invokeMethodAsync('SetAiPanelSize', pendingPercent);
    };
    const start = event => {
        if (event.button !== 0) return;
        event.preventDefault();
        pointerOffset = event.clientY - controls.getBoundingClientRect().top;
        savedStyle = stack.style.getPropertyValue('--ai-terminal-ratio');
        savedChatStyle = stack.style.getPropertyValue('--chat-terminal-ratio');
        pendingPercent = null;
        dragging = true;
        document.body.classList.add('repository-terminal-resizing');
        window.addEventListener('pointermove', move);
        window.addEventListener('pointerup', finish);
        window.addEventListener('pointercancel', finish);
    };
    const keydown = event => {
        if (!['ArrowUp', 'ArrowDown', 'Home', 'End'].includes(event.key)) return;
        event.preventDefault();
        const current = stack.classList.contains('ai-pane-collapsed') ? 0
            : stack.classList.contains('ai-pane-expanded') ? 100
            : parseFloat(getComputedStyle(stack).getPropertyValue('--ai-terminal-ratio')) * 100;
        const next = event.key === 'Home' ? 0 : event.key === 'End' ? 100
            : snap(Math.max(0, Math.min(100, current + (event.key === 'ArrowDown' ? 6 : -6))));
        dotnet.invokeMethodAsync('SetAiPanelSize', next);
    };
    grip?.addEventListener('pointerdown', start);
    grip?.addEventListener('keydown', keydown);

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

    attached.set(stack, () => {
        dragging = false;
        document.body.classList.remove('repository-terminal-resizing');
        window.removeEventListener('pointermove', move);
        window.removeEventListener('pointerup', finish);
        window.removeEventListener('pointercancel', finish);
        grip?.removeEventListener('pointerdown', start);
        grip?.removeEventListener('keydown', keydown);
        if (tabFrame) cancelAnimationFrame(tabFrame);
        tabObserver.disconnect();
        sizeObserver.disconnect();
    });
}

export function detach(stack) {
    attached.get(stack)?.();
    attached.delete(stack);
}
