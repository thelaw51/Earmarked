// Keyboard shortcuts for the first title on the review page. Keys pressed while typing in a field, with Ctrl, Alt or
// Cmd held (so Cmd+F still finds in the page), or held down to repeat are left to the browser.

const keys = new Set(["c", "f", "s", "n"]);

export function listen(page) {
    const onKeyDown = event => {
        if (event.ctrlKey || event.metaKey || event.altKey || event.repeat || isTyping(event.target)) {
            return;
        }

        const key = event.key.toLowerCase();
        if (!keys.has(key)) {
            return;
        }

        event.preventDefault();
        // A rejected call means the page is gone (its .NET reference was disposed, perhaps before it ever received
        // this listener's handle), so the listener removes itself instead of swallowing keys for the whole circuit.
        page.invokeMethodAsync("OnShortcutAsync", key).catch(() => document.removeEventListener("keydown", onKeyDown));
    };

    document.addEventListener("keydown", onKeyDown);
    return { dispose: () => document.removeEventListener("keydown", onKeyDown) };
}

function isTyping(target) {
    return target instanceof Element
        && target.closest("input, textarea, select, [contenteditable]:not([contenteditable='false'])") !== null;
}
