const observers = new WeakMap();

function resize(textarea) {
    textarea.style.height = "auto";
    textarea.style.height = `${Math.min(textarea.scrollHeight, 144)}px`;
}

function describeElement(element) {
    if (!element) return "none";
    const tag = element.tagName?.toLowerCase() ?? "element";
    const id = element.id ? `#${element.id}` : "";
    const className = typeof element.className === "string"
        ? element.className.trim().split(/\s+/).filter(Boolean).slice(0, 2).map(name => `.${name}`).join("")
        : "";
    return `${tag}${id}${className}`;
}

function attachDebug(root) {
    let lastPointerMove = performance.now();
    let lastFrame = performance.now();
    let maxFrameDelay = 0;
    let lastReport = expectedFrame;
    let frame = 0;
    let reportTimer = 0;

    const onPointerMove = event => {
        lastPointerMove = performance.now();
        const target = document.elementFromPoint(event.clientX, event.clientY);
        root.querySelectorAll("[data-debug-hover]").forEach(label => {
            const textarea = label.closest(".repository-codebadger-input-debug")
                ?.parentElement?.querySelector(".repository-codebadger-input");
            const composer = textarea?.closest(".repository-codebadger-composer");
            const underComposer = composer && target?.closest?.(".repository-codebadger-composer") === composer;
            const cursor = target ? getComputedStyle(target).cursor : "unknown";
            label.textContent = `Hover: ${describeElement(target)} · cursor ${cursor}${underComposer ? " (composer)" : ""}`;
        });
    };
    const onFocusChange = () => {
        root.querySelectorAll("[data-debug-focus]").forEach(label => {
            label.textContent = `Focus: ${describeElement(document.activeElement)}`;
        });
    };
    const onVisibilityChange = () => {
        lastFrame = performance.now();
    };
    const measureFrame = now => {
        if (!document.hidden) {
            const frameGap = now - lastFrame;
            if (frameGap >= 100)
                maxFrameDelay = Math.max(maxFrameDelay, frameGap);
        }
        lastFrame = now;
        frame = requestAnimationFrame(measureFrame);
    };
    const report = () => {
        const now = performance.now();
        const elapsed = now - lastReport;
        const stalled = maxFrameDelay >= 250;
        const pointer = now - lastPointerMove < 1200 ? "moving" : "idle";
        const delay = stalled ? `${Math.round(maxFrameDelay)}ms` : `${Math.round(maxFrameDelay)}ms max`;
        root.querySelectorAll("[data-debug-loop]").forEach(label => {
            label.textContent = `Loop delay: ${delay} · pointer ${pointer}`;
            label.classList.toggle("stalled", stalled);
        });
        maxFrameDelay = 0;
        lastReport = now;
        reportTimer = window.setTimeout(report, Math.max(250, 1000 - elapsed % 1000));
    };

    root.addEventListener("pointermove", onPointerMove, true);
    document.addEventListener("focusin", onFocusChange, true);
    document.addEventListener("focusout", onFocusChange, true);
    document.addEventListener("visibilitychange", onVisibilityChange);
    lastFrame = performance.now();
    frame = requestAnimationFrame(measureFrame);
    reportTimer = window.setTimeout(report, 1000);
    onFocusChange();

    return () => {
        root.removeEventListener("pointermove", onPointerMove, true);
        document.removeEventListener("focusin", onFocusChange, true);
        document.removeEventListener("focusout", onFocusChange, true);
        document.removeEventListener("visibilitychange", onVisibilityChange);
        cancelAnimationFrame(frame);
        clearTimeout(reportTimer);
    };
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
    const detachDebug = attachDebug(root);
    observers.set(root, { observer, onInput, onPointerDown, onClick, onKeyDown, onPaste, detachDebug });
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
    state.detachDebug();
    observers.delete(root);
}
