const terminals = new Map();

function terminalTheme(theme) {
    if (!theme) return { background: '#0b1220', foreground: '#e5e7eb', cursor: '#60a5fa', selectionBackground: '#334155' };
    const { isDark, ...colors } = theme;
    return isDark ? colors : {
        ...colors,
        black: '#1f2937', red: '#b42318', green: '#176b35', yellow: '#805600',
        blue: '#1c56a5', magenta: '#913b91', cyan: '#006978', white: '#4b5563',
        brightBlack: '#596273', brightRed: '#b42318', brightGreen: '#176b35', brightYellow: '#805600',
        brightBlue: '#1c56a5', brightMagenta: '#913b91', brightCyan: '#006978', brightWhite: '#1f2937'
    };
}

window.addEventListener('repo-launcher-theme-changed', event => {
    for (const { terminal } of terminals.values()) terminal.options.theme = terminalTheme(event.detail);
});

export function create(id, element, dotnet) {
    const terminal = new Terminal({
        cursorBlink: true,
        fontFamily: 'Consolas, "Cascadia Mono", monospace',
        fontSize: 14,
        convertEol: false,
        theme: terminalTheme(window.codeBadgerTerminalTheme)
    });
    const fitAddon = new FitAddon.FitAddon();
    terminal.loadAddon(fitAddon);
    terminal.open(element);
    const focusOnPointerDown = () => terminal.focus();
    const focusOnClick = () => terminal.focus();
    element.addEventListener('pointerdown', focusOnPointerDown, true);
    element.addEventListener('click', focusOnClick);
    let resizeFrame = 0;
    const fitWhenVisible = () => {
        if (resizeFrame) return;
        resizeFrame = requestAnimationFrame(() => {
            resizeFrame = 0;
            const bounds = element.getBoundingClientRect();
            if (bounds.width <= 0 || bounds.height <= 0 || !element.isConnected) return;
            fitAddon.fit();
        });
    };
    terminal.onData(data => dotnet.invokeMethodAsync('SendInput', data));
    terminal.onResize(size => dotnet.invokeMethodAsync('Resize', size.cols, size.rows));
    const observer = new ResizeObserver(() => {
        fitWhenVisible();
    });
    observer.observe(element);
    terminals.set(id, { terminal, fitAddon, observer, dotnet, element, fitWhenVisible, focusOnPointerDown, focusOnClick, get resizeFrame() { return resizeFrame; } });
    fitWhenVisible();
}

export function write(id, text) {
    terminals.get(id)?.terminal.write(text);
}

export function setVisible(id, visible) {
    const entry = terminals.get(id);
    if (visible && entry) entry.fitWhenVisible();
}

export function dispose(id) {
    const entry = terminals.get(id);
    if (!entry) return;
    entry.observer.disconnect();
    entry.element.removeEventListener('pointerdown', entry.focusOnPointerDown, true);
    entry.element.removeEventListener('click', entry.focusOnClick);
    if (entry.resizeFrame) cancelAnimationFrame(entry.resizeFrame);
    entry.terminal.dispose();
    terminals.delete(id);
}
