const controllers = new WeakMap();

function isAtBottom(conversation) {
    return conversation.scrollHeight - conversation.scrollTop - conversation.clientHeight <= 48;
}

function syncButton(conversation, state) {
    const button = conversation.parentElement?.querySelector(".repository-codebadger-scroll-latest");
    if (button) button.hidden = state.following;
}

function scrollToLatest(conversation, state) {
    conversation.scrollTop = conversation.scrollHeight;
    state.following = true;
    syncButton(conversation, state);
}

function watchConversation(conversation, states) {
    if (states.has(conversation)) return;

    const state = { following: true };
    states.set(conversation, state);
    conversation.addEventListener("scroll", () => {
        state.following = isAtBottom(conversation);
        syncButton(conversation, state);
    }, { passive: true });

    const button = conversation.parentElement?.querySelector(".repository-codebadger-scroll-latest");
    button?.addEventListener("click", () => scrollToLatest(conversation, state));

    scrollToLatest(conversation, state);
}

export function attach(root) {
    if (!root || controllers.has(root)) return;

    const states = new WeakMap();
    const scan = () => root.querySelectorAll(".repository-codebadger-conversation").forEach(conversation => watchConversation(conversation, states));
    scan();

    const observer = new MutationObserver(records => {
        scan();
        const conversations = new Set();
        for (const record of records) {
            if (record.type === "attributes" && record.attributeName === "style" &&
                record.target.classList?.contains("repository-codebadger-terminal-view")) {
                const conversation = record.target.querySelector(".repository-codebadger-conversation");
                const state = conversation && states.get(conversation);
                if (conversation?.style.display !== "none" && state?.following)
                    scrollToLatest(conversation, state);
            }
            const target = record.target.nodeType === Node.ELEMENT_NODE ? record.target : record.target.parentElement;
            const conversation = target?.closest?.(".repository-codebadger-conversation");
            if (conversation) conversations.add(conversation);
            for (const added of record.addedNodes) {
                if (added.nodeType !== Node.ELEMENT_NODE) continue;
                if (added.matches?.(".repository-codebadger-conversation")) conversations.add(added);
                added.querySelectorAll?.(".repository-codebadger-conversation").forEach(item => conversations.add(item));
            }
        }
        for (const conversation of conversations) {
            const state = states.get(conversation);
            if (state?.following) scrollToLatest(conversation, state);
            else if (state) syncButton(conversation, state);
        }
    });

    observer.observe(root, { childList: true, characterData: true, attributes: true, attributeFilter: ["style"], subtree: true });
    controllers.set(root, { observer });
}

export function detach(root) {
    const controller = root && controllers.get(root);
    if (!controller) return;
    controller.observer.disconnect();
    controllers.delete(root);
}
