// Keep keyboard defaults local to the browser; a server round trip is too late
// to stop Enter inserting a newline. Submission uses the existing Blazor form.
document.addEventListener("keydown", (event) => {
    const input = event.target;
    if (!(input instanceof HTMLTextAreaElement) || !input.hasAttribute("data-chat-input") ||
        event.key !== "Enter" || event.shiftKey || event.isComposing || event.keyCode === 229) {
        return;
    }

    event.preventDefault();
    if (!event.repeat && !input.disabled && input.value.trim() && input.form) {
        // Input and submit events travel through the same ordered circuit. Do
        // not depend on the button having rerendered after the last keystroke.
        input.form.requestSubmit();
    }
});

document.addEventListener("click", (event) => {
    if (!(event.target instanceof Element) || event.button !== 0 || event.ctrlKey || event.metaKey || event.shiftKey || event.altKey) return;
    const link = event.target.closest("a[data-citation-link]");
    if (!link || !link.hash) return;
    const source = document.getElementById(link.hash.slice(1));
    if (!(source instanceof HTMLDetailsElement) || !source.hasAttribute("data-citation-source") ||
        source.closest(".turn") !== link.closest(".turn")) return;

    event.preventDefault();
    source.open = true;
    const summary = source.querySelector("summary");
    summary?.focus({ preventScroll: true });
    (summary ?? source).scrollIntoView({
        behavior: window.matchMedia("(prefers-reduced-motion: reduce)").matches ? "auto" : "smooth",
        block: "center"
    });
});
