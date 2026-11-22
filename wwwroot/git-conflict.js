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
    scrollHandlers.set(left, { right, onLeftScroll, onRightScroll, editor, onResultScroll });

    requestAnimationFrame(() => {
        const firstDiff = left.querySelector(".git-conflict-diff-line[data-conflict-row='true']");
        if (firstDiff) {
            const containerTop = left.getBoundingClientRect().top;
            const diffTop = firstDiff.getBoundingClientRect().top;
            left.scrollTop = Math.max(0, left.scrollTop + diffTop - containerTop);
            right.scrollTop = left.scrollTop;
        }
    });
}

export function renderConflictResult(textarea, rows, pendingValue) {
    const editor = textarea.previousElementSibling;
    const fragment = document.createDocumentFragment();
    for (const row of rows) {
        const line = document.createElement("div");
        line.dataset.resultLine = String(row.lineNumber);
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

export function scrollConflictResultToLine(textarea, lineNumber) {
    const editor = textarea.previousElementSibling;
    const line = editor.querySelector(`[data-result-line="${lineNumber}"]`);
    if (!line) return;
    const editorBounds = editor.getBoundingClientRect();
    const lineBounds = line.getBoundingClientRect();
    const targetScrollTop = editor.scrollTop + lineBounds.top - editorBounds.top - (editor.clientHeight - lineBounds.height) / 2;
    editor.scrollTop = Math.max(0, Math.min(editor.scrollHeight - editor.clientHeight, targetScrollTop));
    textarea.scrollTop = editor.scrollTop;
}

export function scrollConflictSidesToLine(left, right, lineNumber) {
    const leftLine = left.querySelector(`[data-conflict-line="${lineNumber}"]`);
    const leftBounds = left.getBoundingClientRect();
    if (leftLine) {
        const lineBounds = leftLine.getBoundingClientRect();
        const targetScrollTop = left.scrollTop + lineBounds.top - leftBounds.top - (left.clientHeight - lineBounds.height) / 2;
        left.scrollTop = Math.max(0, Math.min(left.scrollHeight - left.clientHeight, targetScrollTop));
        right.scrollTop = left.scrollTop;
        return;
    }

    const firstDiff = left.querySelector(".git-conflict-diff-line[data-conflict-row='true']");
    if (!firstDiff) return;
    const lineBounds = firstDiff.getBoundingClientRect();
    left.scrollTop = Math.max(0, Math.min(left.scrollHeight - left.clientHeight, left.scrollTop + lineBounds.top - leftBounds.top));
    right.scrollTop = left.scrollTop;
}

export function disposeConflictScroll(left, right, editor) {
    const handlers = scrollHandlers.get(left);
    if (handlers && handlers.right === right) {
        left.removeEventListener("scroll", handlers.onLeftScroll);
        right.removeEventListener("scroll", handlers.onRightScroll);
        handlers.editor?.removeEventListener("scroll", handlers.onResultScroll);
        scrollHandlers.delete(left);
    }
}
