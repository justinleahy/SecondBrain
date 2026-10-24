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

// Follow the latest turn after sending or opening a conversation. While its
// answer grows, stay at the end only until the reader scrolls up deliberately.
// Observe rendered DOM locally so streamed text never waits on another round trip.
(() => {
    const panes = new Map();
    const selector = '[data-chat-scroll="conversation"]';
    let frame;

    const schedule = () => {
        if (frame) return;
        frame = requestAnimationFrame(sync);
    };

    function sync() {
        frame = undefined;
        for (const [pane, state] of panes) {
            if (pane.isConnected && pane.matches(selector)) continue;
            pane.removeEventListener("scroll", state.onScroll);
            state.resize.disconnect();
            panes.delete(pane);
        }

        for (const pane of document.querySelectorAll(selector)) {
            let state = panes.get(pane);
            if (!state) {
                state = { followLatest: true, conversation: undefined, turn: undefined, lastTurn: null };
                state.onScroll = () => {
                    state.followLatest = pane.scrollHeight - pane.scrollTop - pane.clientHeight <= 48;
                };
                pane.addEventListener("scroll", state.onScroll, { passive: true });
                state.resize = new ResizeObserver(schedule);
                state.resize.observe(pane);
                panes.set(pane, state);
            }

            const turns = pane.querySelectorAll("[data-chat-turn]");
            const lastTurn = turns[turns.length - 1] ?? null;
            const conversation = pane.dataset.conversationId;
            const turn = lastTurn?.dataset.chatTurn;
            if (conversation !== state.conversation || turn !== state.turn) {
                state.followLatest = true;
                state.conversation = conversation;
                state.turn = turn;
            }
            if (lastTurn !== state.lastTurn) {
                if (state.lastTurn) state.resize.unobserve(state.lastTurn);
                if (lastTurn) state.resize.observe(lastTurn);
                state.lastTurn = lastTurn;
            }
            if (lastTurn && state.followLatest) pane.scrollTop = pane.scrollHeight;
        }
    }

    new MutationObserver(schedule).observe(document.documentElement, {
        childList: true,
        characterData: true,
        subtree: true,
        attributes: true,
        attributeFilter: ["data-chat-scroll", "data-conversation-id", "data-chat-turn"]
    });
    schedule();
})();

// Mobile browsers can leave layout-viewport units unchanged while their
// keyboard is open. Keep the chat within the visible area, without changing
// the layout during pinch zoom or forcing the user's scroll position.
if (window.visualViewport) {
    let viewportFrame;
    const fitChatToViewport = () => {
        cancelAnimationFrame(viewportFrame);
        viewportFrame = requestAnimationFrame(() => {
            const viewport = window.visualViewport;
            if (viewport.scale === 1) {
                document.documentElement.style.setProperty("--chat-viewport-height", `${viewport.height}px`);
            } else {
                document.documentElement.style.removeProperty("--chat-viewport-height");
            }
        });
    };
    window.visualViewport.addEventListener("resize", fitChatToViewport);
    fitChatToViewport();
}
