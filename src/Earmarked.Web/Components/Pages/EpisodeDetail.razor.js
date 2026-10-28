// Scrolls the episode page to a transcript row or the list of books, then highlights it for a moment. The page calls
// this once its data has rendered: the browser's own jump to a #fragment happens before the rows exist.

export function reveal(id) {
    const target = document.getElementById(id);
    if (!target) {
        return;
    }

    const behavior = window.matchMedia("(prefers-reduced-motion: reduce)").matches ? "auto" : "smooth";
    const box = target.closest(".transcript");
    if (box) {
        // The transcript scrolls inside its own box. Chrome drops a smooth scroll of the box when the page starts one,
        // so the box jumps to centre the row ("instant": its CSS would make "auto" smooth) and only the page glides.
        const offset = target.getBoundingClientRect().top - box.getBoundingClientRect().top;
        box.scrollTo({ top: box.scrollTop + offset - (box.clientHeight - target.offsetHeight) / 2, behavior: "instant" });
        box.scrollIntoView({ behavior, block: "nearest" });
    } else {
        target.scrollIntoView({ behavior, block: "start" });
    }

    // Removing the class and forcing a layout restarts the animation when the same row is revealed twice.
    target.classList.remove("revealed");
    void target.offsetWidth;
    target.classList.add("revealed");
}
