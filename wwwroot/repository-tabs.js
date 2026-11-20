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
    const detachedIndicator = event.target.closest('.repository-detached-tab-indicator');
    if (detachedIndicator) {
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
    if (dragState.detached) {
        if (dragState.undocked)
            dotnet?.invokeMethodAsync('MoveDetachedWindow', dragState.repositoryId,
                Math.round(event.screenX * devicePixelRatio), Math.round(event.screenY * devicePixelRatio));
        return;
    }
    const outsideSourceTab = event.clientX < dragState.bounds.left - 6 || event.clientX > dragState.bounds.right + 6 ||
        event.clientY < dragState.bounds.top - 6 || event.clientY > dragState.bounds.bottom + 6;
    if (!dragState.undocked && Math.hypot(dx, dy) > 18 && outsideSourceTab) {
        dragState.undocked = true;
        const id = dragState.repositoryId;
        dragState.detached = true;
        dragState.tab = null;
        event.preventDefault();
        dotnet?.invokeMethodAsync('UndockRepository', id,
            Math.round(event.screenX * devicePixelRatio), Math.round(event.screenY * devicePixelRatio));
    }
}

function endDrag(event) {
    if (!dragState) return;
    const state = dragState;
    dragState = null;
    if (state.undocked) {
        dotnet?.invokeMethodAsync('DockAtCursor', state.repositoryId);
        return;
    }
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

export function attachDetached(header, reference) {
    let pointerId = null;
    let startX = 0;
    let startY = 0;
    let moved = false;
    const down = event => {
        if (event.button !== 0) return;
        pointerId = event.pointerId;
        startX = event.screenX;
        startY = event.screenY;
        moved = false;
        try { header.setPointerCapture(pointerId); } catch { }
    };
    const move = event => {
        if (event.pointerId !== pointerId || moved) return;
        if (Math.hypot(event.screenX - startX, event.screenY - startY) < 10) return;
        moved = true;
        reference.invokeMethodAsync('DockDragStarted');
        reference.invokeMethodAsync('BeginDetachedWindowDrag', startX * devicePixelRatio, startY * devicePixelRatio);
    };
    const dragMove = event => {
        if (event.pointerId === pointerId && moved)
            reference.invokeMethodAsync('MoveDetachedWindow', event.screenX * devicePixelRatio, event.screenY * devicePixelRatio);
    };
    const up = async event => {
        if (event.pointerId !== pointerId) return;
        pointerId = null;
        if (moved)
            await reference.invokeMethodAsync('DockAtCursor');
        else
            await reference.invokeMethodAsync('DockDragEnded');
        moved = false;
    };
    const cancel = () => {
        pointerId = null;
        if (moved) reference.invokeMethodAsync('DockDragEnded');
        moved = false;
    };
    header.addEventListener('pointerdown', down);
    header.addEventListener('pointermove', move);
    header.addEventListener('pointermove', dragMove);
    header.addEventListener('pointerup', up);
    header.addEventListener('pointercancel', cancel);
    const stopWindowDrag = () => reference.invokeMethodAsync('DockDragEnded');
    window.addEventListener('blur', stopWindowDrag);
    return {
        dispose() {
            header.removeEventListener('pointerdown', down);
            header.removeEventListener('pointermove', move);
            header.removeEventListener('pointermove', dragMove);
            header.removeEventListener('pointerup', up);
            header.removeEventListener('pointercancel', cancel);
            window.removeEventListener('blur', stopWindowDrag);
        }
    };
}
