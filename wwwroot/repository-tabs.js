let dotnet = null;
let listeners = [];
let dragState = null;
let detachedIndicatorsObserver = null;

function detachListeners() {
    for (const [element, eventName, handler] of listeners)
        element.removeEventListener(eventName, handler, true);
    listeners = [];
}

function repositoryIdFromTab(tab) {
    return tab.querySelector('.repository-tab-label[data-repository-id]')?.dataset.repositoryId ?? null;
}

function syncTabIds() {
    const tabs = document.querySelectorAll('.repository-tabs .mud-tab');
    const panels = document.querySelectorAll('.repository-tabs .mud-tabs-panels .mud-tab-panel');
    tabs.forEach((tab, index) => {
        const id = panels[index]?.querySelector('[data-repository-id]')?.dataset.repositoryId;
        if (id) tab.dataset.repositoryId = id;
    });
}

function startDrag(event) {
    if (event.button !== 0) return;
    if (event.target.closest('.repository-undock-button, .repository-dock-button')) return;
    const detachedIndicator = event.target.closest('.repository-detached-tab-indicator');
    if (detachedIndicator) {
        if (event.target.closest('.repository-dock-button')) return;
        dragState = {
            repositoryId: detachedIndicator.dataset.repositoryId,
            tab: detachedIndicator,
            detached: true,
            startX: event.clientX,
            startY: event.clientY
        };
        return;
    }
    const tab = event.target.closest('.repository-tabs .mud-tab');
    if (!tab) return;
    const repositoryId = tab.dataset.repositoryId ?? repositoryIdFromTab(tab);
    if (!repositoryId) return;
    const bounds = tab.getBoundingClientRect();
    dragState = {
        repositoryId,
        tab,
        detached: false,
        startX: event.clientX,
        startY: event.clientY,
        bounds: { left: bounds.left, right: bounds.right, top: bounds.top, bottom: bounds.bottom },
        undocked: false
    };
}

function moveDrag(event) {
    if (!dragState) return;
    const dx = event.clientX - dragState.startX;
    const dy = event.clientY - dragState.startY;
    if (dragState.detached) return;
    const outsideSourceTab = event.clientX < dragState.bounds.left - 6 || event.clientX > dragState.bounds.right + 6 ||
        event.clientY < dragState.bounds.top - 6 || event.clientY > dragState.bounds.bottom + 6;
    if (!dragState.undocked && Math.hypot(dx, dy) > 18 && outsideSourceTab) {
        dragState.undocked = true;
        const id = dragState.repositoryId;
        dragState.detached = true;
        dragState.tab = null;
        event.preventDefault();
        dotnet?.invokeMethodAsync('UndockRepository', id);
    }
}

function endDrag(event) {
    if (!dragState) return;
    const state = dragState;
    dragState = null;
    if (state.undocked) return;
    const moved = Math.hypot(event.clientX - state.startX, event.clientY - state.startY) > 30;
    const target = document.elementFromPoint(event.clientX, event.clientY);
    const dockedTab = target?.closest('.repository-tabs .mud-tab');
    const tabPanels = target?.closest('.repository-tabs .mud-tabs-panels');
    const tabbar = dockedTab || tabPanels;
    if (state.detached && moved && tabbar) {
        dotnet?.invokeMethodAsync('DockRepository', state.repositoryId);
        return;
    }
}

export function attach(reference) {
    detachListeners();
    dotnet = reference;
    const down = event => startDrag(event);
    const move = event => moveDrag(event);
    const up = event => endDrag(event);
    const cancel = () => { dragState = null; };
    document.addEventListener('pointerdown', down, true);
    document.addEventListener('pointermove', move, true);
    document.addEventListener('pointerup', up, true);
    document.addEventListener('pointercancel', cancel, true);
    listeners = [[document, 'pointerdown', down], [document, 'pointermove', move], [document, 'pointerup', up], [document, 'pointercancel', cancel]];
    syncTabIds();
    detachedIndicatorsObserver = new MutationObserver(syncTabIds);
    const root = document.querySelector('.repository-tabs');
    if (root) detachedIndicatorsObserver.observe(root, { childList: true, subtree: true });
}

export function detach() {
    detachListeners();
    detachedIndicatorsObserver?.disconnect();
    detachedIndicatorsObserver = null;
    dotnet = null;
    dragState = null;
}
