// The local fixture uses synthetic data and the real extractor protocol.
async (page) => {
    const origin = new URL(page.url()).origin;
    if (!await page.evaluate(async () => {
        for (const path of ['/auth/login', '/auth/step-up']) {
            const csrf = await (await fetch('/auth/antiforgery')).json();
            const response = await fetch(path, {method: 'POST', headers: {'Content-Type': 'application/json', [csrf.headerName]: csrf.token}, body: JSON.stringify({password: 'test-password'})});
            if (!response.ok) return false;
        }
        return true;
    })) throw new Error('Fixture sign-in failed');
    const results = [];
    for (const [width, enabled] of [[1280, false], [390, true]]) {
        await page.setViewportSize({width, height: 900});
        await page.goto(origin + '/settings');
        const region = page.getByRole('region', {name: 'Document enrichment', exact: true});
        await region.getByRole('button', {name: 'Edit enrichment', exact: true}).click();
        await page.getByLabel('Use batch processing for supported backfill models', {exact: true}).setChecked(enabled);
        await page.getByRole('button', {name: 'Save settings', exact: true}).click();
        await page.getByText('Settings saved and reloaded.', {exact: true}).waitFor();
        await region.getByText('Batch processing for backfills: ' + (enabled ? 'on when supported by the model.' : 'off.'), {exact: true}).waitFor();
        await page.reload();
        await region.getByText('Batch processing for backfills: ' + (enabled ? 'on when supported by the model.' : 'off.'), {exact: true}).waitFor();
        if (!await page.evaluate(() => document.documentElement.scrollWidth <= innerWidth)) throw new Error('Page overflow');
        await region.screenshot({path: '/home/justinleahy/Developer/SecondBrain/docs/build/evidence/m4-openai-batch-20261024/settings-' + width + '.png'});
        results.push({width, enabled, survivesReload: true, noHorizontalOverflow: true});
    }
    return results;
}
