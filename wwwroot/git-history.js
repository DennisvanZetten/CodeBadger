const attachedSplitters = new WeakMap();

export function positionCommitContextMenu(clientX, clientY) {
    const menu = document.querySelector(".git-commit-context-menu");
    const backdrop = menu?.closest(".git-context-menu-backdrop");
    if (!menu || !backdrop)
        return;

    const bounds = backdrop.getBoundingClientRect();
    menu.style.left = `${clientX - bounds.left}px`;
    menu.style.top = `${clientY - bounds.top}px`;
}

export function scrollToCurrentBranch() {
    const branch = document.querySelector(".git-branch-list [data-branch-name]");
    if (!branch)
        return;

    for (let folder = branch.closest("details"); folder; folder = folder.parentElement?.closest("details"))
        folder.open = true;

    const list = branch.closest(".git-branch-list");
    if (!list)
        return;

    const listBounds = list.getBoundingClientRect();
    const branchBounds = branch.getBoundingClientRect();
    list.scrollTo({
        top: list.scrollTop + branchBounds.top - listBounds.top - (list.clientHeight - branchBounds.height) / 2,
        behavior: "smooth"
    });
}

export function attachHistorySplitter() {
    for (const splitter of document.querySelectorAll(".git-history-workspace .git-history-splitter")) {
        const workspace = splitter.closest(".git-history-workspace");
        if (!workspace || attachedSplitters.has(splitter))
            continue;

        const isBranchSplitter = splitter.classList.contains("git-history-branch-splitter");
        let dragging = false;
        const isNarrow = () => window.matchMedia("(max-width: 760px)").matches;

        const onPointerMove = event => {
            if (!dragging)
                return;

            const bounds = workspace.getBoundingClientRect();
            if (isNarrow()) {
                const height = Math.max(120, Math.min(bounds.height - 160, event.clientY - bounds.top));
                workspace.style.setProperty(isBranchSplitter ? "--branch-panel-height" : "--commit-files-panel-height", `${height}px`);
            } else if (isBranchSplitter) {
                const width = Math.max(150, Math.min(bounds.width - 220, event.clientX - bounds.left));
                workspace.style.setProperty("--branch-panel-width", `${width}px`);
            } else {
                const branchWidth = parseFloat(getComputedStyle(workspace).getPropertyValue("--branch-panel-width")) || 260;
                const width = Math.max(150, Math.min(bounds.width - branchWidth - 220 - 12, bounds.right - event.clientX));
                workspace.style.setProperty("--commit-files-panel-width", `${width}px`);
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
            const property = horizontal
                ? (isBranchSplitter ? "--branch-panel-height" : "--commit-files-panel-height")
                : (isBranchSplitter ? "--branch-panel-width" : "--commit-files-panel-width");
            const configured = getComputedStyle(workspace).getPropertyValue(property).trim();
            const configuredValue = parseFloat(configured);
            const current = horizontal
                ? (configured.endsWith("%") ? bounds.height * configuredValue / 100 : configuredValue || bounds.height * 0.4)
                : configuredValue || (isBranchSplitter ? 260 : 280);
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
