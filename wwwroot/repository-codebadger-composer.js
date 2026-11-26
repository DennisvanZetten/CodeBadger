const observers = new WeakMap();

function resize(textarea) {
    textarea.style.height = "auto";
    textarea.style.height = `${Math.min(textarea.scrollHeight, 144)}px`;
}

function imageToJpeg(file) {
    return new Promise((resolve, reject) => {
        const reader = new FileReader();
        reader.onerror = () => reject(reader.error ?? new Error("Could not read clipboard image."));
        reader.onload = () => {
            const image = new Image();
            image.onerror = () => reject(new Error("Could not decode clipboard image."));
            image.onload = () => {
                const scale = Math.min(1, 480 / image.width, 480 / image.height);
                const canvas = document.createElement("canvas");
                canvas.width = Math.max(1, Math.round(image.width * scale));
                canvas.height = Math.max(1, Math.round(image.height * scale));
                const context = canvas.getContext("2d", { alpha: false });
                if (!context) return reject(new Error("Image compression is unavailable."));
                context.fillStyle = "#ffffff";
                context.fillRect(0, 0, canvas.width, canvas.height);
                context.drawImage(image, 0, 0, canvas.width, canvas.height);
                let quality = 0.66;
                let dataUrl = canvas.toDataURL("image/jpeg", quality);
                while (dataUrl.length > 120_000 && quality > 0.35) {
                    quality -= 0.1;
                    dataUrl = canvas.toDataURL("image/jpeg", quality);
                }
                while (dataUrl.length > 120_000 && canvas.width > 160 && canvas.height > 160) {
                    canvas.width = Math.max(1, Math.round(canvas.width * 0.8));
                    canvas.height = Math.max(1, Math.round(canvas.height * 0.8));
                    context.fillStyle = "#ffffff";
                    context.fillRect(0, 0, canvas.width, canvas.height);
                    context.drawImage(image, 0, 0, canvas.width, canvas.height);
                    dataUrl = canvas.toDataURL("image/jpeg", 0.45);
                }
                if (dataUrl.length > 160_000) return reject(new Error("Clipboard image remains too large after compression."));
                resolve(dataUrl);
            };
            image.src = reader.result;
        };
        reader.readAsDataURL(file);
    });
}

export function attach(root, dotNetReference) {
    if (!root || observers.has(root)) return;

    const onInput = event => {
        const textarea = event.target.closest?.(".repository-codebadger-input");
        if (textarea) resize(textarea);
    };
    const onPointerDown = event => {
        const textarea = event.target.closest?.(".repository-codebadger-input");
        if (textarea && !textarea.disabled && document.activeElement !== textarea)
            textarea.focus({ preventScroll: true });
    };
    const onClick = event => {
        const target = event.target;
        const codeCopyButton = target.closest?.(".markdown-code-copy");
        if (codeCopyButton) {
            event.preventDefault();
            event.stopPropagation();
            const code = codeCopyButton.closest(".markdown-code-fold")?.querySelector("pre code")?.textContent;
            if (code === undefined) return;
            navigator.clipboard.writeText(code)
                .then(() => dotNetReference.invokeMethodAsync("NotifyCodeBlockCopiedAsync", true))
                .catch(() => dotNetReference.invokeMethodAsync("NotifyCodeBlockCopiedAsync", false));
            return;
        }
        if (target.closest?.("button, a, input, select, [role='button']")) return;
        const composer = target.closest?.(".repository-codebadger-composer");
        const textarea = target.closest?.(".repository-codebadger-input")
            ?? composer?.querySelector(".repository-codebadger-input");
        if (textarea && !textarea.disabled && document.activeElement !== textarea)
            textarea.focus({ preventScroll: true });
    };
    const onKeyDown = event => {
        const textarea = event.target.closest?.(".repository-codebadger-input");
        if (!textarea || event.isComposing) return;
        if (event.key === "Tab" && event.shiftKey && !event.ctrlKey && !event.altKey && !event.metaKey &&
            textarea.dataset.modeToggleEnabled === "true") {
            event.preventDefault();
            dotNetReference.invokeMethodAsync("ToggleTerminalModeAsync", textarea.dataset.sessionId);
            return;
        }
        if (event.key !== "Enter" || event.shiftKey) return;
        event.preventDefault();
        dotNetReference.invokeMethodAsync("SubmitTerminalInputAsync", textarea.dataset.sessionId)
            .then(() => resize(textarea));
    };
    const onPaste = async event => {
        const textarea = event.target.closest?.(".repository-codebadger-input");
        if (!textarea) return;
        const images = Array.from(event.clipboardData?.items ?? [])
            .filter(item => item.kind === "file" && item.type.startsWith("image/"));
        if (!images.length) return;
        event.preventDefault();
        for (const item of images.slice(0, 3)) {
            try {
                const dataUrl = await imageToJpeg(item.getAsFile());
                await dotNetReference.invokeMethodAsync("AddClipboardImageAsync", textarea.dataset.sessionId, dataUrl);
            } catch (error) {
                console.warn("Could not process a pasted CodeBadger image.", error);
            }
        }
    };
    const scan = () => root.querySelectorAll(".repository-codebadger-input").forEach(resize);
    root.addEventListener("input", onInput);
    root.addEventListener("pointerdown", onPointerDown, true);
    root.addEventListener("click", onClick);
    root.addEventListener("keydown", onKeyDown);
    root.addEventListener("paste", onPaste);
    const observer = new MutationObserver(records => {
        for (const record of records) {
            for (const node of record.addedNodes) {
                if (node.nodeType !== Node.ELEMENT_NODE) continue;
                if (node.matches?.(".repository-codebadger-input")) resize(node);
                node.querySelectorAll?.(".repository-codebadger-input").forEach(resize);
            }
        }
    });
    observer.observe(root, { childList: true, subtree: true });
    scan();
    observers.set(root, { observer, onInput, onPointerDown, onClick, onKeyDown, onPaste });
}

export function clear(root, sessionId) {
    const textarea = root?.querySelector(`.repository-codebadger-input[data-session-id="${sessionId}"]`);
    if (!textarea) return;
    textarea.value = "";
    resize(textarea);
}

export function detach(root) {
    const state = root && observers.get(root);
    if (!state) return;
    state.observer.disconnect();
    root.removeEventListener("input", state.onInput);
    root.removeEventListener("pointerdown", state.onPointerDown, true);
    root.removeEventListener("click", state.onClick);
    root.removeEventListener("keydown", state.onKeyDown);
    root.removeEventListener("paste", state.onPaste);
    observers.delete(root);
}
