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

export function disposeConflictScroll(left, right, editor) {
    const handlers = scrollHandlers.get(left);
    if (handlers && handlers.right === right) {
        left.removeEventListener("scroll", handlers.onLeftScroll);
        right.removeEventListener("scroll", handlers.onRightScroll);
        handlers.editor?.removeEventListener("scroll", handlers.onResultScroll);
        scrollHandlers.delete(left);
    }
}
