const terminals = new Map();

export function create(id, element, dotnet) {
    const terminal = new Terminal({
        cursorBlink: true,
        fontFamily: 'Consolas, "Cascadia Mono", monospace',
        fontSize: 14,
        convertEol: false,
        theme: { background: '#0b1220', foreground: '#e5e7eb', cursor: '#60a5fa', selectionBackground: '#334155' }
    });
    const fitAddon = new FitAddon.FitAddon();
    terminal.loadAddon(fitAddon);
    terminal.open(element);
    fitAddon.fit();
    terminal.onData(data => dotnet.invokeMethodAsync('SendInput', data));
    terminal.onResize(size => dotnet.invokeMethodAsync('Resize', size.cols, size.rows));
    const observer = new ResizeObserver(() => {
        fitAddon.fit();
    });
    observer.observe(element);
    terminals.set(id, { terminal, fitAddon, observer, dotnet });
}

export function write(id, text) {
    terminals.get(id)?.terminal.write(text);
}

export function setVisible(id, visible) {
    const entry = terminals.get(id);
    if (visible && entry) {
        requestAnimationFrame(() => entry.fitAddon.fit());
    }
}

export function dispose(id) {
    const entry = terminals.get(id);
    if (!entry) return;
    entry.observer.disconnect();
    entry.terminal.dispose();
    terminals.delete(id);
}
