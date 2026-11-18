const attachedSplitters = new WeakMap();

export function attachHistorySplitter() {
    const splitter = document.querySelector(".git-history-workspace:not(.git-history-workspace-collapsed) .git-history-splitter");
    const workspace = splitter?.closest(".git-history-workspace");
    if (!splitter || !workspace || attachedSplitters.has(splitter))
        return;

    let dragging = false;
    const isNarrow = () => window.matchMedia("(max-width: 760px)").matches;

    const onPointerMove = event => {
        if (!dragging)
            return;

        const bounds = workspace.getBoundingClientRect();
        if (isNarrow()) {
            const height = Math.max(120, Math.min(bounds.height - 160, event.clientY - bounds.top));
            workspace.style.setProperty("--branch-panel-height", `${height}px`);
        } else {
            const width = Math.max(150, Math.min(bounds.width - 220, event.clientX - bounds.left));
            workspace.style.setProperty("--branch-panel-width", `${width}px`);
        }
    };

    const stopDragging = () => {
        if (!dragging)
            return;
        dragging = false;
        document.body.classList.remove("git-history-resizing");
        window.removeEventListener("pointermove", onPointerMove);
        window.removeEventListener("pointerup", stopDragging);
        window.removeEventListener("pointercancel", stopDragging);
    };

    const startDragging = event => {
        if (event.button !== 0)
            return;
        event.preventDefault();
        dragging = true;
        document.body.classList.add("git-history-resizing");
        window.addEventListener("pointermove", onPointerMove);
        window.addEventListener("pointerup", stopDragging);
        window.addEventListener("pointercancel", stopDragging);
    };

    const onKeyDown = event => {
        const horizontal = isNarrow();
        const decrease = horizontal ? event.key === "ArrowUp" : event.key === "ArrowLeft";
        const increase = horizontal ? event.key === "ArrowDown" : event.key === "ArrowRight";
        if (!decrease && !increase)
            return;
        event.preventDefault();

        const bounds = workspace.getBoundingClientRect();
        const property = horizontal ? "--branch-panel-height" : "--branch-panel-width";
        const configured = getComputedStyle(workspace).getPropertyValue(property).trim();
        const configuredValue = parseFloat(configured);
        const current = horizontal
            ? (configured.endsWith("%") ? bounds.height * configuredValue / 100 : configuredValue || bounds.height * 0.4)
            : configuredValue || 260;
        const next = Math.max(horizontal ? 120 : 150, current + (increase ? 20 : -20));
        workspace.style.setProperty(property, `${next}px`);
    };

    splitter.addEventListener("pointerdown", startDragging);
    splitter.addEventListener("keydown", onKeyDown);
    attachedSplitters.set(splitter, () => {
        stopDragging();
        splitter.removeEventListener("pointerdown", startDragging);
        splitter.removeEventListener("keydown", onKeyDown);
    });
}

export function detachHistorySplitter() {
    for (const splitter of document.querySelectorAll(".git-history-splitter")) {
        const detach = attachedSplitters.get(splitter);
        if (!detach)
            continue;
        detach();
        attachedSplitters.delete(splitter);
    }
}
