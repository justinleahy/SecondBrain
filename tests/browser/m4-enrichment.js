// Synthetic document only. The harness uses the real extraction protocol.
async (page) => {
    const origin = new URL(page.url()).origin;
    const require = (value, message) => { if (!value) throw new Error(message); };
    require(await page.evaluate(async () => {
        for (const path of ['/auth/login', '/auth/step-up']) {
            const csrf = await (await fetch('/auth/antiforgery')).json();
            const result = await fetch(path, {method: 'POST', headers: {'Content-Type': 'application/json', [csrf.headerName]: csrf.token}, body: JSON.stringify({password: 'test-password'})});
            if (!result.ok) return false;
        }
        return true;
    }), 'Fixture sign-in failed');
    const id = await page.evaluate(async () => {
        const csrf = await (await fetch('/auth/antiforgery')).json();
        const result = await (await fetch('/query', {method: 'POST', headers: {'Content-Type': 'application/json', [csrf.headerName]: csrf.token}, body: JSON.stringify({limit: 20})})).json();
        return result.records.find(item => item.title === 'Enrichment browser fixture')?.id;
    });
    require(id, 'The enrichment fixture must be present');
    const results = [];
    for (const [width, enabled] of [[1280, false], [390, true]]) {
        await page.setViewportSize({width, height: 900});
        await page.goto(origin + '/settings');
        const enrichment = page.getByRole('region', {name: 'Document enrichment', exact: true});
        await enrichment.getByRole('button', {name: 'Edit enrichment', exact: true}).click();
        await page.getByLabel('Enable document enrichment', {exact: true}).setChecked(enabled);
        await page.getByRole('button', {name: 'Save settings', exact: true}).click();
        await page.getByText('Settings saved and reloaded.', {exact: true}).waitFor();
        await enrichment.getByText('Automatic enrichment: ' + (enabled ? 'on' : 'off') + '.', {exact: false}).waitFor();
        await enrichment.screenshot({path: '/home/justinleahy/Developer/SecondBrain/docs/build/evidence/m4-enrichment-20261024/settings-' + width + '.png'});
        await page.goto(origin + '/library/' + id);
        await page.getByRole('heading', {name: 'Enrichment browser fixture', exact: true}).waitFor();
        await page.getByRole('region', {name: 'Model suggestions', exact: true}).waitFor();
        await page.getByText('Ada describes sustainable transport.', {exact: true}).waitFor();
        await page.getByRole('link', {name: 'City planning', exact: true}).waitFor();
        require(await page.getByRole('button', {name: 'Enrich document', exact: true}).isEnabled() === enabled, 'Enrichment follows the global setting');
        if (enabled) {
            await page.getByRole('button', {name: 'Enrich document', exact: true}).click();
            await page.getByText('Enrichment is up to date.', {exact: false}).waitFor();
        }
        require(await page.evaluate(() => document.documentElement.scrollWidth <= innerWidth), 'Page overflow');
        await page.screenshot({path: '/home/justinleahy/Developer/SecondBrain/docs/build/evidence/m4-enrichment-20261024/document-' + width + '.png', fullPage: true});
        results.push({width, enabled, summaryVisible: true, mentionsVisible: true, suggestedLinksVisible: true, triggerFollowsSetting: true, noHorizontalOverflow: true});
    }
    return results;
}
