// Drives the reconnect card. Blazor reports connection changes through "components-reconnect-state-changed";
// this decides when the card actually appears, so short drops reconnect without anyone seeing it.

// Most drops (a server restart, a phone waking up, a flaky Wi-Fi hop) reconnect within a second or two.
const showDelayMs = 2500;

const reconnectModal = document.getElementById("components-reconnect-modal");
reconnectModal.addEventListener("components-reconnect-state-changed", handleReconnectStateChanged);

document.getElementById("components-reconnect-button").addEventListener("click", retry);
document.getElementById("components-resume-button").addEventListener("click", resume);
for (const button of document.querySelectorAll(".components-reload-button")) {
    button.addEventListener("click", () => location.reload());
}

let showTimer;

function open() {
    clearTimeout(showTimer);
    if (!reconnectModal.open) {
        reconnectModal.showModal();
    }
}

function close() {
    clearTimeout(showTimer);
    if (reconnectModal.open) {
        reconnectModal.close();
    }
}

function handleReconnectStateChanged(event) {
    switch (event.detail.state) {
        case "show":
            clearTimeout(showTimer);
            showTimer = setTimeout(open, showDelayMs);
            break;
        case "hide":
            close();
            break;
        case "failed":
            // Blazor has run out of automatic attempts. Try again by ourselves when the tab or network comes back.
            open();
            document.addEventListener("visibilitychange", retryWhenDocumentBecomesVisible);
            window.addEventListener("online", retry, { once: true });
            break;
        case "paused":
            // The server paused the session. Resume straight away instead of asking for a click.
            resume();
            break;
        case "resume-failed":
            open();
            break;
        case "rejected":
            // The server no longer has this session, so a reload is the only way back.
            location.reload();
            break;
    }
}

async function retry() {
    document.removeEventListener("visibilitychange", retryWhenDocumentBecomesVisible);

    try {
        // Reconnect will asynchronously return:
        // - true to mean success
        // - false to mean we reached the server, but it rejected the connection (e.g., unknown circuit ID)
        // - exception to mean we didn't reach the server (this can be sync or async)
        const successful = await Blazor.reconnect();
        if (!successful) {
            // We have been able to reach the server, but the circuit is no longer available.
            // We'll reload the page so the user can continue using the app as quickly as possible.
            const resumeSuccessful = await Blazor.resumeCircuit();
            if (!resumeSuccessful) {
                location.reload();
            } else {
                close();
            }
        }
    } catch (err) {
        // We got an exception, server is currently unavailable
        document.addEventListener("visibilitychange", retryWhenDocumentBecomesVisible);
    }
}

async function resume() {
    try {
        const successful = await Blazor.resumeCircuit();
        if (!successful) {
            location.reload();
        } else {
            close();
        }
    } catch {
        reconnectModal.classList.replace("components-reconnect-paused", "components-reconnect-resume-failed");
        open();
    }
}

async function retryWhenDocumentBecomesVisible() {
    if (document.visibilityState === "visible") {
        await retry();
    }
}
