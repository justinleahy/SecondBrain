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
