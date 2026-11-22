const attachedSplitters = new WeakMap();
const activeSplitters = new Set();

export function positionCommitContextMenu(clientX, clientY) {
    positionContextMenu(".git-commit-context-menu", clientX, clientY);
}

export function positionStashContextMenu(clientX, clientY) {
    positionContextMenu(".git-stash-context-menu", clientX, clientY);
}

function positionContextMenu(selector, clientX, clientY) {
    const menu = document.querySelector(selector);
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
    for (const splitter of activeSplitters) {
        if (splitter.isConnected)
            continue;
        attachedSplitters.get(splitter)?.();
        attachedSplitters.delete(splitter);
        activeSplitters.delete(splitter);
    }

    for (const splitter of document.querySelectorAll(".git-history-workspace .git-history-splitter, .git-history-workspace .git-commit-file-detail-splitter, .git-commit-workspace .git-commit-splitter")) {
        const branchGroupSplitter = splitter.classList.contains("git-branch-group-splitter");
        if (branchGroupSplitter) {
            const groups = splitter.closest(".git-branch-groups");
            if (!groups)
                continue;

            const sectionCount = groups.querySelectorAll(":scope > .git-branch-group").length;
            if (groups.dataset.splitterSectionCount !== String(sectionCount)) {
                for (const property of ["--git-branch-local-height", "--git-branch-remote-height", "--git-branch-stash-height"])
                    groups.style.removeProperty(property);
                groups.dataset.splitterSectionCount = String(sectionCount);
            }
            if (attachedSplitters.has(splitter))
                continue;

            const boundary = Number(splitter.dataset.branchGroupSplitter);
            let dragging = false;
            let pointerOffset = 0;
            const sections = () => [...groups.querySelectorAll(":scope > .git-branch-group")];
            const heightProperties = ["--git-branch-local-height", "--git-branch-remote-height", "--git-branch-stash-height"];
            const freezeSectionHeights = () => {
                for (const [index, section] of sections().entries())
                    groups.style.setProperty(heightProperties[index], `${section.getBoundingClientRect().height}px`);
            };
            const setBoundary = boundaryCenter => {
                const sectionItems = sections();
                if (sectionItems.length < boundary + 2)
                    return;

                const firstBounds = sectionItems[boundary].getBoundingClientRect();
                const secondBounds = sectionItems[boundary + 1].getBoundingClientRect();
                const splitterBounds = splitter.getBoundingClientRect();
                const pairStart = firstBounds.top;
                const pairEnd = secondBounds.bottom;
                const minimum = 80;
                const splitterSize = splitterBounds.height;
                const gridGap = parseFloat(getComputedStyle(groups).rowGap) || 0;
                const pairSize = pairEnd - pairStart - splitterSize - 2 * gridGap;
                const firstSize = Math.max(minimum, Math.min(pairSize - minimum, boundaryCenter - pairStart - gridGap - splitterSize / 2));
                const secondSize = pairSize - firstSize;
                groups.style.setProperty(heightProperties[boundary], `${firstSize}px`);
                groups.style.setProperty(heightProperties[boundary + 1], `${secondSize}px`);
            };
            const onPointerMove = event => {
                if (dragging)
                    setBoundary(event.clientY + pointerOffset);
            };
            const stopDragging = () => {
                if (!dragging)
                    return;
                dragging = false;
                document.body.classList.remove("git-branch-groups-resizing");
                window.removeEventListener("pointermove", onPointerMove);
                window.removeEventListener("pointerup", stopDragging);
                window.removeEventListener("pointercancel", stopDragging);
            };
            const startDragging = event => {
                if (event.button !== 0)
                    return;
                event.preventDefault();
                freezeSectionHeights();
                const splitterBounds = splitter.getBoundingClientRect();
                pointerOffset = (splitterBounds.top + splitterBounds.bottom) / 2 - event.clientY;
                dragging = true;
                document.body.classList.add("git-branch-groups-resizing");
                window.addEventListener("pointermove", onPointerMove);
                window.addEventListener("pointerup", stopDragging);
                window.addEventListener("pointercancel", stopDragging);
            };
            const onKeyDown = event => {
                const decrease = event.key === "ArrowUp" || event.key === "ArrowLeft";
                const increase = event.key === "ArrowDown" || event.key === "ArrowRight";
                if (!decrease && !increase)
                    return;
                event.preventDefault();
                freezeSectionHeights();
                const splitterBounds = splitter.getBoundingClientRect();
                const currentCenter = (splitterBounds.top + splitterBounds.bottom) / 2;
                setBoundary(currentCenter + (increase ? 20 : -20));
            };

            splitter.addEventListener("pointerdown", startDragging);
            splitter.addEventListener("keydown", onKeyDown);
            activeSplitters.add(splitter);
            attachedSplitters.set(splitter, () => {
                stopDragging();
                splitter.removeEventListener("pointerdown", startDragging);
                splitter.removeEventListener("keydown", onKeyDown);
            });
            continue;
        }

        if (splitter.classList.contains("git-commit-file-detail-splitter")) {
            const panel = splitter.closest(".git-commit-files-panel");
            const fileList = panel?.querySelector(":scope > .git-commit-files-list");
            if (!panel || !fileList || attachedSplitters.has(splitter))
                continue;

            let dragging = false;
            const setBoundary = coordinate => {
                const panelBounds = panel.getBoundingClientRect();
                const listBounds = fileList.getBoundingClientRect();
                const diffTitle = panel.querySelector(":scope > .git-commit-file-diff-title");
                const styles = getComputedStyle(panel);
                const gap = parseFloat(styles.rowGap) || 0;
                const bottomPadding = parseFloat(styles.paddingBottom) || 0;
                const minimum = 80;
                const detailMinimum = 180;
                const titleHeight = diffTitle?.getBoundingClientRect().height ?? 0;
                const maximum = panelBounds.bottom - bottomPadding - detailMinimum - titleHeight - splitter.getBoundingClientRect().height - 3 * gap - listBounds.top;
                const height = Math.max(minimum, Math.min(maximum, coordinate - listBounds.top));
                panel.style.setProperty("--git-commit-file-list-height", `${height}px`);
            };
            const onPointerMove = event => {
                if (dragging)
                    setBoundary(event.clientY);
            };
            const stopDragging = () => {
                if (!dragging)
                    return;
                dragging = false;
                document.body.classList.remove("git-commit-file-resizing");
                window.removeEventListener("pointermove", onPointerMove);
                window.removeEventListener("pointerup", stopDragging);
                window.removeEventListener("pointercancel", stopDragging);
            };
            const startDragging = event => {
                if (event.button !== 0)
                    return;
                event.preventDefault();
                dragging = true;
                document.body.classList.add("git-commit-file-resizing");
                window.addEventListener("pointermove", onPointerMove);
                window.addEventListener("pointerup", stopDragging);
                window.addEventListener("pointercancel", stopDragging);
            };
            const onKeyDown = event => {
                const decrease = event.key === "ArrowUp" || event.key === "ArrowLeft";
                const increase = event.key === "ArrowDown" || event.key === "ArrowRight";
                if (!decrease && !increase)
                    return;
                event.preventDefault();
                const listBounds = fileList.getBoundingClientRect();
                setBoundary(listBounds.bottom + (increase ? 20 : -20));
            };

            splitter.addEventListener("pointerdown", startDragging);
            splitter.addEventListener("keydown", onKeyDown);
            activeSplitters.add(splitter);
            attachedSplitters.set(splitter, () => {
                stopDragging();
                splitter.removeEventListener("pointerdown", startDragging);
                splitter.removeEventListener("keydown", onKeyDown);
            });
            continue;
        }

        const isCommitSplitter = splitter.classList.contains("git-commit-splitter");
        const workspace = splitter.closest(isCommitSplitter ? ".git-commit-workspace" : ".git-history-workspace");
        if (!workspace || attachedSplitters.has(splitter))
            continue;

        const isBranchSplitter = splitter.classList.contains("git-history-branch-splitter");
        let dragging = false;
        const isNarrow = () => window.matchMedia(isCommitSplitter ? "(max-width: 960px)" : "(max-width: 760px)").matches;
        const resizingClass = isCommitSplitter ? "git-commit-resizing" : "git-history-resizing";
        const sizeProperty = horizontal => {
            if (isCommitSplitter)
                return horizontal ? "--git-diff-panel-height" : "--git-diff-panel-width";
            return horizontal
                ? (isBranchSplitter ? "--branch-panel-height" : "--commit-files-panel-height")
                : (isBranchSplitter ? "--branch-panel-width" : "--commit-files-panel-width");
        };

        const syncOrientation = () => splitter.setAttribute("aria-orientation", isNarrow() ? "horizontal" : "vertical");
        syncOrientation();
        window.addEventListener("resize", syncOrientation);

        const setCommitSize = (horizontal, coordinate, bounds) => {
            const total = horizontal ? bounds.height : bounds.width;
            const start = horizontal ? bounds.top : bounds.left;
            const minimum = horizontal ? 120 : 200;
            const trailingMinimum = horizontal ? 300 : 320;
            const size = Math.max(minimum, Math.min(total - trailingMinimum, coordinate - start));
            workspace.style.setProperty(sizeProperty(horizontal), `${size}px`);
        };

        const onPointerMove = event => {
            if (!dragging)
                return;

            const bounds = workspace.getBoundingClientRect();
            if (isNarrow()) {
                if (isCommitSplitter) {
                    setCommitSize(true, event.clientY, bounds);
                    return;
                }
                const height = Math.max(120, Math.min(bounds.height - 160, event.clientY - bounds.top));
                workspace.style.setProperty(sizeProperty(true), `${height}px`);
            } else if (isBranchSplitter) {
                const width = Math.max(150, Math.min(bounds.width - 220, event.clientX - bounds.left));
                workspace.style.setProperty(sizeProperty(false), `${width}px`);
            } else if (isCommitSplitter) {
                setCommitSize(false, event.clientX, bounds);
            } else {
                const branchWidth = parseFloat(getComputedStyle(workspace).getPropertyValue("--branch-panel-width")) || 260;
                const width = Math.max(150, Math.min(bounds.width - branchWidth - 220 - 12, bounds.right - event.clientX));
                workspace.style.setProperty(sizeProperty(false), `${width}px`);
            }
        };

        const stopDragging = () => {
            if (!dragging)
                return;
            dragging = false;
            document.body.classList.remove(resizingClass);
            window.removeEventListener("pointermove", onPointerMove);
            window.removeEventListener("pointerup", stopDragging);
            window.removeEventListener("pointercancel", stopDragging);
        };

        const startDragging = event => {
            if (event.button !== 0)
                return;
            event.preventDefault();
            dragging = true;
            document.body.classList.add(resizingClass);
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
            const property = sizeProperty(horizontal);
            const configured = getComputedStyle(workspace).getPropertyValue(property).trim();
            const configuredValue = parseFloat(configured);
            const current = horizontal
                ? (configured.endsWith("%") ? bounds.height * configuredValue / 100 : configuredValue || bounds.height * 0.4)
                : (configured.endsWith("%") ? bounds.width * configuredValue / 100 : configuredValue || (isCommitSplitter ? bounds.width * 0.68 : (isBranchSplitter ? 260 : 280)));
            const minimum = horizontal ? 120 : (isCommitSplitter ? 200 : 150);
            const maximum = horizontal ? bounds.height - (isCommitSplitter ? 300 : 160) : bounds.width - (isCommitSplitter ? 320 : 220);
            const next = Math.max(minimum, Math.min(maximum, current + (increase ? 20 : -20)));
            workspace.style.setProperty(property, `${next}px`);
        };

        splitter.addEventListener("pointerdown", startDragging);
        splitter.addEventListener("keydown", onKeyDown);
        activeSplitters.add(splitter);
        attachedSplitters.set(splitter, () => {
            stopDragging();
            splitter.removeEventListener("pointerdown", startDragging);
            splitter.removeEventListener("keydown", onKeyDown);
            window.removeEventListener("resize", syncOrientation);
        });
    }
}

export function detachHistorySplitter() {
    for (const splitter of activeSplitters) {
        const detach = attachedSplitters.get(splitter);
        detach?.();
        attachedSplitters.delete(splitter);
    }
    activeSplitters.clear();
}
