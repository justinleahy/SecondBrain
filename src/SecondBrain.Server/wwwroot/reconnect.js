// Uses the .NET 10 Blazor template's reconnect state events and client APIs.
// A rejected circuit offers an explicit reload so unsaved browser text can be copied first.
(() => {
    const modal = document.getElementById('components-reconnect-modal');
    if (!modal) return;
    let reconnecting = false;
    const open = () => { if (!modal.open) modal.showModal(); };
    const rejected = () => {
        modal.className = 'connection-dialog components-reconnect-rejected';
        open();
    };
    async function retry() {
        if (reconnecting) return;
        reconnecting = true;
        document.removeEventListener('visibilitychange', visible);
        try {
            if (await Blazor.reconnect()) modal.close();
            else if (await Blazor.resumeCircuit()) modal.close();
            else rejected();
        } catch {
            modal.className = 'connection-dialog components-reconnect-failed';
            open();
            document.addEventListener('visibilitychange', visible);
        } finally { reconnecting = false; }
    }
    function visible() { if (document.visibilityState === 'visible') void retry(); }
    modal.addEventListener('components-reconnect-state-changed', event => {
        if (event.detail.state === 'hide') { modal.close(); document.removeEventListener('visibilitychange', visible); }
        else if (event.detail.state === 'rejected') rejected();
        else {
            open();
            if (event.detail.state === 'failed') document.addEventListener('visibilitychange', visible);
        }
    });
    document.getElementById('connection-retry').addEventListener('click', retry);
    document.getElementById('connection-resume').addEventListener('click', async () => {
        try { if (!await Blazor.resumeCircuit()) rejected(); else modal.close(); }
        catch { modal.className = 'connection-dialog components-reconnect-failed'; open(); }
    });
    document.getElementById('connection-reload').addEventListener('click', () => location.reload());
    document.getElementById('connection-dismiss').addEventListener('click', () => {
        document.removeEventListener('visibilitychange', visible); modal.close();
    });
})();
