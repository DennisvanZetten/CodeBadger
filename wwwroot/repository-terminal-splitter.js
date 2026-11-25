const attached = new WeakMap();

export function attach(stack) {
    detach(stack);

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
        if (tabFrame) cancelAnimationFrame(tabFrame);
        tabObserver.disconnect();
        sizeObserver.disconnect();
    });
}

export function detach(stack) {
    attached.get(stack)?.();
    attached.delete(stack);
}
