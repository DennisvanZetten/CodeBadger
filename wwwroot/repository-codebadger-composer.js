const observers = new WeakMap();
const activeComposerRoots = new Set();

function getEditor(target) {
    return target?.closest?.(".repository-codebadger-input");
}

function getModel(editor) {
    return editor?.closest(".repository-codebadger-editor")
        ?.querySelector(`.repository-codebadger-input-model[data-session-id="${CSS.escape(editor.dataset.sessionId ?? "")}"]`);
}

function isChip(node) {
    return node?.nodeType === Node.ELEMENT_NODE && node.matches(".repository-codebadger-paste-chip");
}

function getNodeText(node) {
    if (node.nodeType === Node.TEXT_NODE) return node.textContent ?? "";
    if (node.nodeType !== Node.ELEMENT_NODE) return "";
    if (isChip(node)) return node.dataset.pasteText ?? "";
    if (node.tagName === "BR") return "\n";
    if (node.tagName === "DIV" || node.tagName === "P") {
        const children = Array.from(node.childNodes).map(getNodeText).join("");
        return node.nextSibling ? `${children}\n` : children;
    }
    return Array.from(node.childNodes).map(getNodeText).join("");
}

function getEditorText(editor, trimTrailingNewline = false) {
    const value = Array.from(editor.childNodes).map(getNodeText).join("");
    return trimTrailingNewline && value.endsWith("\n") ? value.slice(0, -1) : value;
}

function syncModel(editor, editorChanged = true) {
    const model = getModel(editor);
    if (!model) return;
    const editorValue = getEditorText(editor, true);
    const lastValue = editor.dataset.syncedValue;

    // Blazor may replace the bound model after processing an input event.
    // Treat that as a model-originated update and rebuild plain text, rather than
    // retaining DOM-only chips whose source content no longer exists.
    if (!editorChanged && lastValue !== undefined && model.value !== lastValue) {
        editor.replaceChildren(...(model.value ? [document.createTextNode(model.value)] : []));
        editor.dataset.syncedValue = model.value;
        return;
    }

    const value = editorChanged || lastValue === undefined ? editorValue : model.value;
    editor.dataset.syncedValue = value;
    if (model.value !== value) {
        model.value = value;
        model.dispatchEvent(new Event("input", { bubbles: true }));
    }
}

function makeChip(editor, text) {
    const chip = document.createElement("span");
    chip.className = "repository-codebadger-paste-chip";
    chip.contentEditable = "false";
    chip.dataset.pasteText = text;

    const label = document.createElement("span");
    label.className = "repository-codebadger-paste-chip-label";
    const count = text.split("\n").length;
    const format = count === 1
        ? editor.dataset.pastedSummarySingular ?? "{0} line"
        : editor.dataset.pastedSummaryPlural ?? "{0} lines";
    label.textContent = `[${format.replace("{0}", String(count))}]`;

    const previewButton = document.createElement("button");
    previewButton.type = "button";
    previewButton.className = "repository-codebadger-paste-preview-toggle";
    previewButton.setAttribute("aria-label", editor.dataset.previewLabel ?? "Preview pasted text");
    previewButton.setAttribute("aria-expanded", "false");
    previewButton.innerHTML = "<svg viewBox=\"0 0 24 24\" aria-hidden=\"true\"><path fill=\"currentColor\" d=\"M12 5c-5 0-9.27 3.11-11 7 1.73 3.89 6 7 11 7s9.27-3.11 11-7c-1.73-3.89-6-7-11-7Zm0 12a5 5 0 1 1 0-10 5 5 0 0 1 0 10Zm0-2a3 3 0 1 0 0-6 3 3 0 0 0 0 6Z\"/></svg>";

    const editButton = document.createElement("button");
    editButton.type = "button";
    editButton.className = "repository-codebadger-paste-edit";
    editButton.setAttribute("aria-label", editor.dataset.editLabel ?? "Edit pasted text");
    editButton.innerHTML = "<svg viewBox=\"0 0 24 24\" aria-hidden=\"true\"><path fill=\"currentColor\" d=\"M3 17.25V21h3.75L17.81 9.94l-3.75-3.75L3 17.25Zm17.71-10.04a1 1 0 0 0 0-1.41l-2.51-2.51a1 1 0 0 0-1.41 0l-1.83 1.83 3.75 3.75 2-1.66Z\"/></svg>";

    chip.append(label, previewButton, editButton);
    return chip;
}

function placeCaretAfter(node) {
    const selection = window.getSelection();
    if (!selection) return;
    let followingText = node.nextSibling;
    if (followingText?.nodeType !== Node.TEXT_NODE) {
        followingText = document.createTextNode("");
        node.after(followingText);
    }
    const range = document.createRange();
    range.setStart(followingText, 0);
    range.collapse(true);
    selection.removeAllRanges();
    selection.addRange(range);
}

function replaceSelectionWithChip(editor, text) {
    const selection = window.getSelection();
    const chip = makeChip(editor, text);
    if (selection?.rangeCount && editor.contains(selection.anchorNode)) {
        const range = selection.getRangeAt(0);
        range.deleteContents();
        range.insertNode(chip);
    } else {
        editor.append(chip);
    }

    // Keep following text on its own editable side of the non-editable inline chip.
    placeCaretAfter(chip);
    if (document.activeElement !== editor) editor.focus({ preventScroll: true });
    syncModel(editor, true);
}

function insertPlainText(editor, text) {
    const selection = window.getSelection();
    if (!selection?.rangeCount || !editor.contains(selection.anchorNode)) {
        editor.append(document.createTextNode(text));
        return;
    }
    const range = selection.getRangeAt(0);
    range.deleteContents();
    const textNode = document.createTextNode(text);
    range.insertNode(textNode);
    const caret = document.createRange();
    caret.setStart(textNode, textNode.length);
    caret.collapse(true);
    selection.removeAllRanges();
    selection.addRange(caret);
}

function closePastePreview(root) {
    const state = observers.get(root);
    if (!state?.previewPanel) return;
    const wasOpen = !state.previewPanel.hidden;
    state.previewButton?.setAttribute("aria-expanded", "false");
    state.previewPanel.hidden = true;
    state.previewButton = null;
    return wasOpen;
}

function positionPastePreview(button, panel) {
    const rect = button.getBoundingClientRect();
    const margin = 12;
    const width = Math.min(480, window.innerWidth - margin * 2);
    panel.style.width = `${width}px`;
    panel.style.left = `${Math.max(margin, Math.min(rect.left, window.innerWidth - width - margin))}px`;
    const panelHeight = panel.getBoundingClientRect().height;
    const top = rect.top - panelHeight - 8 >= margin
        ? rect.top - panelHeight - 8
        : Math.min(window.innerHeight - panelHeight - margin, rect.bottom + 8);
    panel.style.top = `${Math.max(margin, top)}px`;
}

function setPastePreview(root, button, open) {
    const state = observers.get(root);
    const chip = button.closest(".repository-codebadger-paste-chip");
    if (!state?.previewPanel || !chip) return;
    if (!open) {
        closePastePreview(root);
        return;
    }
    closePastePreview(root);
    state.previewPanel.querySelector(".repository-codebadger-paste-preview-content").textContent =
        chip.dataset.pasteText ?? "";
    state.previewButton = button;
    button.setAttribute("aria-expanded", "true");
    state.previewPanel.hidden = false;
    positionPastePreview(button, state.previewPanel);
    state.previewPanel.querySelector(".repository-codebadger-paste-preview-content")?.focus({ preventScroll: true });
}

function expandPasteChip(button) {
    const chip = button.closest(".repository-codebadger-paste-chip");
    const editor = chip?.closest(".repository-codebadger-input");
    if (!chip || !editor) return;
    const textNode = document.createTextNode(chip.dataset.pasteText ?? "");
    chip.replaceWith(textNode);
    const selection = window.getSelection();
    const range = document.createRange();
    range.setStart(textNode, textNode.length);
    range.collapse(true);
    selection?.removeAllRanges();
    selection?.addRange(range);
    editor.focus({ preventScroll: true });
    syncModel(editor, true);
}

function getAdjacentChip(selection, editor, direction) {
    if (!selection?.isCollapsed || !selection.anchorNode || !editor.contains(selection.anchorNode)) return null;
    const node = selection.anchorNode;
    const offset = selection.anchorOffset;
    if (node.nodeType === Node.TEXT_NODE) {
        if (direction < 0 && offset === 0 && isChip(node.previousSibling)) return node.previousSibling;
        if (direction > 0 && offset === node.length && isChip(node.nextSibling)) return node.nextSibling;
        return null;
    }
    if (node.nodeType === Node.ELEMENT_NODE) {
        if (node === editor && direction < 0 && offset > 0 && isChip(node.childNodes[offset - 1]))
            return node.childNodes[offset - 1];
        if (node === editor && direction > 0 && offset < node.childNodes.length && isChip(node.childNodes[offset]))
            return node.childNodes[offset];
        const adjacent = node.childNodes[direction < 0 ? offset - 1 : offset];
        return isChip(adjacent) ? adjacent : null;
    }
    return null;
}

function removeChipAtCaret(editor, chip, direction) {
    closePastePreview(editor.closest(".repository-terminal-stack"));
    const previous = chip.previousSibling;
    const next = chip.nextSibling;
    chip.remove();

    let caretNode;
    let caretOffset;
    if (direction < 0 && previous?.nodeType === Node.TEXT_NODE) {
        caretNode = previous;
        caretOffset = previous.length;
    } else if (direction > 0 && next?.nodeType === Node.TEXT_NODE) {
        caretNode = next;
        caretOffset = 0;
    } else if (previous?.nodeType === Node.TEXT_NODE) {
        caretNode = previous;
        caretOffset = previous.length;
    } else if (next?.nodeType === Node.TEXT_NODE) {
        caretNode = next;
        caretOffset = 0;
    } else {
        caretNode = document.createTextNode("");
        editor.append(caretNode);
        caretOffset = 0;
    }

    if (previous?.nodeType === Node.TEXT_NODE && next?.nodeType === Node.TEXT_NODE && previous.parentNode === editor) {
        const previousLength = previous.length;
        previous.appendData(next.data);
        if (caretNode === next) {
            caretNode = previous;
            caretOffset = previousLength;
        }
        next.remove();
    }

    // The editor remains focused when Backspace/Delete is used; avoid resetting
    // the just-restored selection in WebViews that place the caret at the start.
    if (document.activeElement !== editor)
        editor.focus({ preventScroll: true });
    const selection = window.getSelection();
    const range = document.createRange();
    range.setStart(caretNode, Math.min(caretOffset, caretNode.length ?? caretNode.childNodes.length));
    range.collapse(true);
    selection?.removeAllRanges();
    selection?.addRange(range);
    syncModel(editor, true);
}

function commandOptions(editor) {
    return Array.from(editor.closest(".repository-codebadger-composer")
        ?.querySelectorAll(".repository-command-autocomplete:not([hidden]) .repository-command-autocomplete-option") ?? []);
}

function updateCommandSelection(editor, index) {
    const options = commandOptions(editor);
    if (!options.length) return false;
    const boundedIndex = (index + options.length) % options.length;
    options.forEach((option, optionIndex) => {
        const selected = optionIndex === boundedIndex;
        option.setAttribute("aria-selected", String(selected));
        option.classList.toggle("selected", selected);
    });
    options[boundedIndex].scrollIntoView({ block: "nearest" });
    editor.dataset.commandSelection = String(boundedIndex);
    return true;
}

function selectCommand(editor, option, dotNetReference, submitWithoutArguments = false) {
    const modelReference = option?.dataset.modelReference;
    if (modelReference) {
        delete editor.dataset.commandSelection;
        dotNetReference.invokeMethodAsync("SelectTerminalModelSuggestionAsync", editor.dataset.sessionId, modelReference);
        return true;
    }
    const command = option?.dataset.command;
    if (!command) return false;
    const submitNow = submitWithoutArguments && option.dataset.commandSubmitOnSelect === "true";
    editor.replaceChildren(document.createTextNode(submitNow ? command : `${command} `));
    delete editor.dataset.commandSelection;
    delete editor.dataset.commandAutocompleteDismissed;
    syncModel(editor, true);
    if (submitNow) {
        dotNetReference.invokeMethodAsync("SubmitTerminalInputAsync", editor.dataset.sessionId, command);
        return true;
    }
    editor.focus({ preventScroll: true });
    const selection = window.getSelection();
    const range = document.createRange();
    range.selectNodeContents(editor);
    range.collapse(false);
    selection?.removeAllRanges();
    selection?.addRange(range);
    return true;
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
        const editor = getEditor(event.target);
        if (!editor) return;
        delete editor.dataset.commandAutocompleteDismissed;
        delete editor.dataset.commandSelection;
        const list = editor.closest(".repository-codebadger-composer")
            ?.querySelector(".repository-command-autocomplete");
        if (list) list.hidden = false;
        syncModel(editor, true);
        if (!/^\/model(?:\s|$)/i.test(getEditorText(editor, true).trimStart())) {
            const staleModelList = editor.closest(".repository-codebadger-composer")
                ?.querySelector(".repository-model-autocomplete");
            if (staleModelList) staleModelList.hidden = true;
        }
        const input = getEditorText(editor, true).trimStart();
        if (/^\/model(?:\s|$)/i.test(input))
            dotNetReference.invokeMethodAsync("LoadTerminalModelSuggestionsAsync", editor.dataset.sessionId);
    };
    const onPointerDown = event => {
        const editor = getEditor(event.target);
        if (editor?.getAttribute("contenteditable") === "true" && document.activeElement !== editor)
            editor.focus({ preventScroll: true });
    };
    const onClick = event => {
        const target = event.target;
        const previewButton = target.closest?.(".repository-codebadger-paste-preview-toggle");
        if (previewButton) {
            event.preventDefault();
            event.stopPropagation();
            setPastePreview(root, previewButton, previewButton.getAttribute("aria-expanded") !== "true");
            return;
        }
        const editButton = target.closest?.(".repository-codebadger-paste-edit");
        if (editButton) {
            event.preventDefault();
            event.stopPropagation();
            expandPasteChip(editButton);
            return;
        }
        const option = target.closest?.(".repository-command-autocomplete-option");
        if (option) {
            event.preventDefault();
            const editor = option.closest(".repository-codebadger-composer")?.querySelector(".repository-codebadger-input");
            if (editor) selectCommand(editor, option, dotNetReference);
            return;
        }
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
        const editor = getEditor(target) ?? composer?.querySelector(".repository-codebadger-input");
        if (editor && editor.getAttribute("contenteditable") === "true" && document.activeElement !== editor)
            editor.focus({ preventScroll: true });
    };
    const onKeyDown = event => {
        const editor = getEditor(event.target);
        if (!editor || event.isComposing) return;
        if (event.key === "Escape" && !observers.get(root)?.previewPanel?.hidden) {
            closePastePreview(root);
            return;
        }
        if (event.key === "Backspace" || event.key === "Delete") {
            const chip = getAdjacentChip(window.getSelection(), editor, event.key === "Backspace" ? -1 : 1);
            if (chip) {
                event.preventDefault();
                removeChipAtCaret(editor, chip, event.key === "Backspace" ? -1 : 1);
                return;
            }
        }
        const options = commandOptions(editor);
        if (options.length && !editor.dataset.commandAutocompleteDismissed) {
            const selectedIndex = Number.parseInt(editor.dataset.commandSelection ?? "-1", 10);
            if (event.key === "ArrowDown" || event.key === "ArrowUp") {
                event.preventDefault();
                const nextIndex = selectedIndex < 0
                    ? (event.key === "ArrowDown" ? 0 : options.length - 1)
                    : selectedIndex + (event.key === "ArrowDown" ? 1 : -1);
                updateCommandSelection(editor, nextIndex);
                return;
            }
            if (event.key === "Tab" && !event.shiftKey && !event.ctrlKey && !event.altKey && !event.metaKey) {
                event.preventDefault();
                selectCommand(editor, options[selectedIndex >= 0 ? selectedIndex : 0]);
                return;
            }
            if (event.key === "Escape") {
                event.preventDefault();
                editor.dataset.commandAutocompleteDismissed = "true";
                delete editor.dataset.commandSelection;
                editor.closest(".repository-codebadger-composer")
                    ?.querySelector(".repository-command-autocomplete")?.setAttribute("hidden", "");
                return;
            }
            if (event.key === "Enter" && !event.shiftKey && options[0]?.dataset.modelReference) {
                event.preventDefault();
                selectCommand(editor, options[selectedIndex >= 0 ? selectedIndex : 0], dotNetReference, true);
                return;
            }
            if (event.key === "Enter" && !event.shiftKey) {
                event.preventDefault();
                selectCommand(editor, options[selectedIndex >= 0 ? selectedIndex : 0], dotNetReference, true);
                return;
            }
        }
        if (event.key === "Enter" && !event.shiftKey && /^\/model(?:\s|$)/i.test(getEditorText(editor, true).trimStart())) {
            event.preventDefault();
            dotNetReference.invokeMethodAsync("LoadTerminalModelSuggestionsAsync", editor.dataset.sessionId);
            return;
        }
        if (event.key === "Tab" && event.shiftKey && !event.ctrlKey && !event.altKey && !event.metaKey &&
            editor.dataset.modeToggleEnabled === "true") {
            event.preventDefault();
            dotNetReference.invokeMethodAsync("ToggleTerminalModeAsync", editor.dataset.sessionId);
            return;
        }
        if (event.key !== "Enter" || event.shiftKey) return;
        event.preventDefault();
        syncModel(editor);
        dotNetReference.invokeMethodAsync("SubmitTerminalInputAsync", editor.dataset.sessionId,
            getEditorText(editor, true));
    };
    const onPaste = async event => {
        const editor = getEditor(event.target);
        if (!editor) return;
        const clipboardText = event.clipboardData?.getData("text/plain") ?? "";
        const images = Array.from(event.clipboardData?.items ?? [])
            .filter(item => item.kind === "file" && item.type.startsWith("image/"));
        if (images.length) {
            event.preventDefault();
            for (const item of images.slice(0, 3)) {
                try {
                    const dataUrl = await imageToJpeg(item.getAsFile());
                    await dotNetReference.invokeMethodAsync("AddClipboardImageAsync", editor.dataset.sessionId, dataUrl);
                } catch (error) {
                    console.warn("Could not process a pasted CodeBadger image.", error);
                }
            }
            return;
        }

        if (!clipboardText) return;
        event.preventDefault();
        const threshold = Number.parseInt(editor.dataset.pasteCollapseThreshold ?? "0", 10);
        if (threshold > 0 && clipboardText.length >= threshold) {
            replaceSelectionWithChip(editor, clipboardText);
        } else {
            insertPlainText(editor, clipboardText);
            editor.focus({ preventScroll: true });
            syncModel(editor, true);
        }
    };
    const scan = (editorChanged = false) => root.querySelectorAll(".repository-codebadger-input").forEach(editor => {
        const model = getModel(editor);
        if (editor.dataset.syncedValue === undefined) {
            if (model?.value) editor.append(document.createTextNode(model.value));
            editor.dataset.syncedValue = model?.value ?? "";
        } else if (!editorChanged && model) {
            const currentText = getEditorText(editor, true);
            if (model.value !== editor.dataset.syncedValue || currentText !== model.value) {
                editor.replaceChildren(...(model.value ? [document.createTextNode(model.value)] : []));
                editor.dataset.syncedValue = model.value;
            }
        }
    });
    const previewPanel = document.createElement("div");
    previewPanel.className = "repository-codebadger-paste-preview-panel";
    previewPanel.hidden = true;
    previewPanel.innerHTML = `<div class="repository-codebadger-paste-preview-header"><span></span><button type="button" aria-label="Close preview">×</button></div><pre class="repository-codebadger-paste-preview-content"></pre>`;
    previewPanel.querySelector(".repository-codebadger-paste-preview-header span").textContent =
        root.querySelector(".repository-codebadger-input")?.dataset.previewLabel ?? "Pasted text preview";
    previewPanel.querySelector(".repository-codebadger-paste-preview-content").tabIndex = 0;
    const closePreviewButton = previewPanel.querySelector("button");
    closePreviewButton.setAttribute("aria-label", root.querySelector(".repository-codebadger-input")?.dataset.closePreviewLabel ?? "Close preview");
    closePreviewButton.addEventListener("click", () => closePastePreview(root));
    const onDocumentPointerDown = event => {
        const state = observers.get(root);
        if (!state || state.previewPanel.hidden) return;
        if (!state.previewPanel.contains(event.target) && !event.target.closest?.(".repository-codebadger-paste-preview-toggle"))
            closePastePreview(root);
    };
    const onDocumentKeyDown = event => {
        if (event.key === "Escape" && !previewPanel.hidden) {
            event.preventDefault();
            const previewButton = observers.get(root)?.previewButton;
            if (closePastePreview(root)) previewButton?.focus({ preventScroll: true });
        }
    };
    const onPreviewResize = () => {
        const state = observers.get(root);
        if (state?.previewButton && !state.previewPanel.hidden)
            positionPastePreview(state.previewButton, state.previewPanel);
    };
    const onPreviewScroll = () => onPreviewResize();
    document.body.append(previewPanel);
    observers.set(root, { previewPanel, previewButton: null, onDocumentPointerDown, onPreviewResize });
    document.addEventListener("pointerdown", onDocumentPointerDown, true);
    document.addEventListener("keydown", onDocumentKeyDown, true);
    window.addEventListener("resize", onPreviewResize);
    window.addEventListener("scroll", onPreviewScroll, true);
    const onAnyPreviewClick = event => {
        const button = event.target.closest?.(".repository-codebadger-paste-preview-toggle");
        if (!button) return;
        for (const otherRoot of activeComposerRoots)
            if (otherRoot !== root) closePastePreview(otherRoot);
    };
    activeComposerRoots.add(root);
    document.addEventListener("click", onAnyPreviewClick, true);
    root.addEventListener("input", onInput);
    root.addEventListener("pointerdown", onPointerDown, true);
    root.addEventListener("click", onClick);
    root.addEventListener("keydown", onKeyDown);
    root.addEventListener("paste", onPaste);
    const observer = new MutationObserver(records => {
        for (const record of records) {
            for (const node of record.addedNodes) {
                if (node.nodeType !== Node.ELEMENT_NODE) continue;
                if (node.matches?.(".repository-codebadger-input")) scan(false);
                if (node.querySelector?.(".repository-codebadger-input")) scan(false);
            }
        }
    });
    observer.observe(root, { childList: true, subtree: true });
    scan();
    Object.assign(observers.get(root), { observer, onInput, onPointerDown, onClick, onKeyDown, onPaste, onPreviewScroll, onAnyPreviewClick, onDocumentKeyDown });
}

export function clear(root, sessionId) {
    const editor = root?.querySelector(`.repository-codebadger-input[data-session-id="${CSS.escape(sessionId)}"]`);
    if (!editor) return;
    closePastePreview(root);
    editor.replaceChildren();
    const model = getModel(editor);
    if (model) model.value = "";
    editor.dataset.syncedValue = "";
}

export function sync(root) {
    if (!root) return;
    for (const editor of root.querySelectorAll(".repository-codebadger-input")) {
        const model = getModel(editor);
        if (!model) continue;
        if (editor.dataset.syncedValue === undefined || editor.dataset.syncedValue !== model.value ||
            getEditorText(editor, true) !== model.value) {
            editor.replaceChildren(...(model.value ? [document.createTextNode(model.value)] : []));
            editor.dataset.syncedValue = model.value;
        }
    }
}

export function detach(root) {
    const state = root && observers.get(root);
    if (!state) return;
    state.observer.disconnect();
    document.removeEventListener("pointerdown", state.onDocumentPointerDown, true);
    document.removeEventListener("keydown", state.onDocumentKeyDown, true);
    window.removeEventListener("resize", state.onPreviewResize);
    window.removeEventListener("scroll", state.onPreviewScroll, true);
    document.removeEventListener("click", state.onAnyPreviewClick, true);
    activeComposerRoots.delete(root);
    state.previewPanel?.remove();
    root.removeEventListener("input", state.onInput);
    root.removeEventListener("pointerdown", state.onPointerDown, true);
    root.removeEventListener("click", state.onClick);
    root.removeEventListener("keydown", state.onKeyDown);
    root.removeEventListener("paste", state.onPaste);
    observers.delete(root);
}
