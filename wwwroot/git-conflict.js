const scrollHandlers = new WeakMap();

export function initializeConflictScroll(left, right, editor) {
    disposeConflictScroll(left, right, editor);

    let syncing = false;
    const sync = (source, target) => {
        if (syncing) return;
        syncing = true;
        const sourceRange = source.scrollHeight - source.clientHeight;
        const targetRange = Math.max(0, target.scrollHeight - target.clientHeight);
        const ratio = sourceRange > 0 ? source.scrollTop / sourceRange : 0;
        target.scrollTop = ratio * targetRange;
        target.scrollLeft = source.scrollLeft;
        syncing = false;
    };
    const onLeftScroll = () => sync(left, right);
    const onRightScroll = () => sync(right, left);
    left.addEventListener("scroll", onLeftScroll, { passive: true });
    right.addEventListener("scroll", onRightScroll, { passive: true });
    const preview = editor?.previousElementSibling;
    const onResultScroll = () => {
        if (!preview) return;
        preview.scrollTop = editor.scrollTop;
        preview.scrollLeft = editor.scrollLeft;
    };
    editor?.addEventListener("scroll", onResultScroll, { passive: true });
    let alignFrame = 0;
    const onSourceScroll = () => {
        if (alignFrame) cancelAnimationFrame(alignFrame);
        alignFrame = requestAnimationFrame(() => {
            alignFrame = 0;
            alignConflictResultToVisibleLine(left, editor);
        });
    };
    left.addEventListener("scroll", onSourceScroll, { passive: true });
    right.addEventListener("scroll", onSourceScroll, { passive: true });
    scrollHandlers.set(left, { right, onLeftScroll, onRightScroll, editor, onResultScroll, onSourceScroll, get alignFrame() { return alignFrame; } });

    requestAnimationFrame(() => {
        const firstDiff = left.querySelector(".git-conflict-diff-line[data-conflict-row='true']");
        if (firstDiff) {
            const containerTop = left.getBoundingClientRect().top;
            const diffTop = firstDiff.getBoundingClientRect().top;
            left.scrollTop = Math.max(0, left.scrollTop + diffTop - containerTop);
            right.scrollTop = left.scrollTop;
        }
        onSourceScroll();
    });
}

export function renderConflictResult(textarea, rows, pendingValue) {
    const editor = textarea.previousElementSibling;
    const fragment = document.createDocumentFragment();
    for (const row of rows) {
        const line = document.createElement("div");
        line.dataset.resultLine = String(row.lineNumber);
        if (row.sourceRowIndex !== null && row.sourceRowIndex !== undefined)
            line.dataset.sourceRowIndex = String(row.sourceRowIndex);
        line.className = `git-conflict-result-line git-conflict-result-${row.kind}`;
        const number = document.createElement("span");
        number.className = "git-conflict-result-number";
        number.textContent = String(row.lineNumber);
        line.appendChild(number);
        const text = document.createElement("code");
        text.textContent = row.text;
        line.appendChild(text);
        fragment.appendChild(line);
    }
    const previewScrollTop = editor.scrollTop;
    const previewScrollLeft = editor.scrollLeft;
    editor.replaceChildren(fragment);
    editor.scrollTop = previewScrollTop;
    editor.scrollLeft = previewScrollLeft;
    if (pendingValue !== null) {
        const valueChanged = textarea.value !== pendingValue;
        if (valueChanged)
            textarea.value = pendingValue;
        if (valueChanged) {
            textarea.scrollTop = previewScrollTop;
            textarea.scrollLeft = previewScrollLeft;
        }
    }
}

export function alignConflictResultToVisibleLine(source, textarea) {
    const editor = textarea.previousElementSibling;
    const visibleSourceRowIndex = getVisibleConflictSourceRowIndex(source);
    const matchingLines = Array.from(editor.querySelectorAll("[data-source-row-index]"));
    const line = matchingLines.find(item => Number(item.dataset.sourceRowIndex) >= visibleSourceRowIndex) ?? matchingLines.at(-1);
    if (!line) return;
    const editorBounds = editor.getBoundingClientRect();
    const lineBounds = line.getBoundingClientRect();
    editor.scrollTop = Math.max(0, Math.min(editor.scrollHeight - editor.clientHeight, editor.scrollTop + lineBounds.top - editorBounds.top));
    textarea.scrollTop = editor.scrollTop;
}

export function getVisibleConflictSourceRowIndex(source) {
    const sourceBounds = source.getBoundingClientRect();
    const visibleLine = Array.from(source.children).find(child => {
        const bounds = child.getBoundingClientRect();
        return bounds.bottom > sourceBounds.top && bounds.top < sourceBounds.bottom;
    });
    return visibleLine ? Number(visibleLine.dataset.sourceRowIndex ?? 0) : 0;
}

export function disposeConflictScroll(left, right, editor) {
    const handlers = scrollHandlers.get(left);
    if (handlers && handlers.right === right) {
        left.removeEventListener("scroll", handlers.onLeftScroll);
        left.removeEventListener("scroll", handlers.onSourceScroll);
        right.removeEventListener("scroll", handlers.onRightScroll);
        right.removeEventListener("scroll", handlers.onSourceScroll);
        handlers.editor?.removeEventListener("scroll", handlers.onResultScroll);
        if (handlers.alignFrame)
            cancelAnimationFrame(handlers.alignFrame);
        scrollHandlers.delete(left);
    }
}
