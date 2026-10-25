window.secondBrainApi = {
    focusSection(id) {
        const section = document.getElementById(id);
        if (!section) return;
        section.scrollIntoView({ block: "start" });
        section.querySelector("input,textarea,select,button")?.focus({ preventScroll: true });
    },
    async send(path, method, body, revision, requestId) {
        const url = new URL(path, location.origin);
        if (url.origin !== location.origin || !path.startsWith("/") || path.startsWith("//")) {
            throw new Error("Only same-origin API paths are allowed.");
        }
        const headers = { Accept: "application/json" };
        if (method !== "GET" && method !== "HEAD") {
            const tokenResponse = await fetch("/auth/antiforgery", { credentials: "same-origin", cache: "no-store", redirect: "error" });
            if (!tokenResponse.ok) return { status: tokenResponse.status, body: null };
            const token = await tokenResponse.json();
            headers[token.headerName] = token.token;
        }
        if (body !== null && body !== undefined) headers["Content-Type"] = "application/json";
        if (revision !== null && revision !== undefined) headers["If-Match"] = `"${revision}"`;
        if (requestId) headers["Idempotency-Key"] = requestId;
        const response = await fetch(url, {
            method, headers, credentials: "same-origin", cache: "no-store", redirect: "error",
            body: body === null || body === undefined ? undefined : JSON.stringify(body)
        });
        const text = await response.text();
        let value = null;
        if (text) {
            try { value = JSON.parse(text); }
            catch { value = { title: "The server returned an unexpected response. Reload and try again." }; }
        }
        const next = /<([^>]+)>;\s*rel="next"/.exec(response.headers.get("Link") || "");
        return { status: response.status, body: value, next: next ? next[1] : null };
    },
    async sendAndNavigate(path, method, body, destination) {
        const target = new URL(destination, location.origin);
        if (target.origin !== location.origin || !destination.startsWith("/") || destination.startsWith("//")) {
            throw new Error("Only same-origin navigation is allowed.");
        }
        const response = await window.secondBrainApi.send(path, method, body, null, null);
        // Session rotation/revocation can invalidate the circuit before its JS-result callback.
        // Start the full navigation here, while the successful HTTP response is still in the browser.
        if (response.status >= 200 && response.status < 300) location.assign(destination);
        return response;
    }
};
