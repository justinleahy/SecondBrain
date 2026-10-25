// Run on a KnowledgeBrowserQualification fixture. Revokes only a synthetic session.
async (page) => {
    const origin = new URL(page.url()).origin;
    const require = (value, message) => { if (!value) throw new Error(message); };
    const login = await page.evaluate(async () => {
        const token = await (await fetch('/auth/antiforgery')).json();
        return (await fetch('/auth/login', { method:'POST', credentials:'same-origin',
            headers:{'Content-Type':'application/json',[token.headerName]:token.token},body:JSON.stringify({password:'test-password'}) })).status;
    });
    require(login === 200, 'Fixture login failed');
    for (let index = 0; index < 8; index++) {
        await page.goto(origin + (index % 2 === 0 ? '/activity' : '/library'));
        await page.getByRole('link', {name:'Garden article',exact:true}).waitFor();
        require(await page.locator('main [role="alert"]').count() === 0, 'Direct navigation failed');
    }
    let activeSocket, closedAt;
    let resolveClosed;
    const closed = new Promise(resolve => { resolveClosed = resolve; });
    const watchSocket = socket => {
        if (!socket.url().includes('/_blazor')) return;
        activeSocket = socket;
        socket.on('close', () => { if (socket === activeSocket) { closedAt = Date.now(); resolveClosed(); } });
    };
    page.on('websocket', watchSocket);
    const revoker = await page.context().browser().newContext();
    try {
        await page.goto(origin + '/library');
        await page.getByRole('link', {name:'Garden article',exact:true}).waitFor();
        require(activeSocket, 'Expected a live WebSocket circuit');
        const target = await page.evaluate(async () => (await (await fetch('/auth/me')).json()).id);
        const api = revoker.request;
        const signIn = await api.post(origin + '/auth/login', { headers:{Origin:origin}, data:{password:'test-password'} });
        require(signIn.status() === 200, 'Independent fixture login failed');
        let csrf = await (await api.get(origin + '/auth/antiforgery')).json();
        const stepUp = await api.post(origin + '/auth/step-up', {
            headers:{Origin:origin,[csrf.headerName]:csrf.token},data:{password:'test-password'}
        });
        require(stepUp.status() === 200, 'Independent fixture step-up failed');
        csrf = await (await api.get(origin + '/auth/antiforgery')).json();
        const start = Date.now();
        const removed = await api.delete(origin + '/auth/sessions/' + encodeURIComponent(target), {
            headers:{Origin:origin,[csrf.headerName]:csrf.token}
        });
        require(removed.status() === 204, 'Revocation failed');
        let timeout;
        try { await Promise.race([closed, new Promise((_, reject) => { timeout = setTimeout(() => reject(new Error('Revoked idle circuit did not close within five seconds')), 5000); })]); }
        finally { clearTimeout(timeout); }
        const elapsed = closedAt - start;
        require(elapsed >= 0 && elapsed <= 5000, 'Unexpected circuit shutdown timing');
        require((await api.get(origin + '/auth/me')).status() === 200, 'Independent session was revoked');
        require(await page.evaluate(async () => (await fetch('/auth/me')).status) === 401, 'Revoked credential remained usable');
        return {directLoads:9,revokedIdleCircuitClosedMilliseconds:elapsed,independentSessionPreserved:true,revokedCredentialRejected:true};
    } finally {
        page.off('websocket', watchSocket);
        await revoker.close();
    }
}
