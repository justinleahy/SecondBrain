// The classification fixture uses synthetic content and the real extraction protocol.
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
        const result = await (await fetch('/query', {method: 'POST', headers: {'Content-Type': 'application/json', [csrf.headerName]: csrf.token}, body: JSON.stringify({type: 'article', limit: 20})})).json();
        return result.records.find(item => item.title === 'Classification browser fixture')?.id;
    });
    require(id, 'The classified fixture must be present');
    const results = [];
    for (const [width, enabled, confidence] of [[1280, false, 83], [390, true, 91]]) {
        await page.setViewportSize({width, height: 900});
        await page.goto(origin + '/settings');
        const classification = page.getByRole('region', {name: 'Document classification', exact: true});
        await classification.getByRole('button', {name: 'Edit classification', exact: true}).click();
        await page.getByLabel('Classify ambiguous documents with the model', {exact: true}).setChecked(enabled);
        await page.getByLabel('Minimum confidence (%)', {exact: true}).fill(String(confidence));
        await page.getByRole('button', {name: 'Save settings', exact: true}).click();
        await page.getByText('Settings saved and reloaded.', {exact: true}).waitFor();
        await classification.getByText('Automatic classification: ' + (enabled ? 'on' : 'off') + '. Minimum confidence: ' + confidence + '%.', {exact: true}).waitFor();
        await classification.screenshot({path: '/home/justinleahy/Developer/SecondBrain/docs/build/evidence/m4-classification-20261024/settings-' + width + '.png'});
        await page.goto(origin + '/library/' + id);
        await page.getByRole('heading', {name: 'Classification browser fixture', exact: true}).waitFor();
        await page.getByText('95%', {exact: true}).waitFor();
        require(await page.evaluate(() => document.documentElement.scrollWidth <= innerWidth), 'Page overflow');
        await page.screenshot({path: '/home/justinleahy/Developer/SecondBrain/docs/build/evidence/m4-classification-20261024/document-' + width + '.png', fullPage: true});
        results.push({width, enabled, minimumConfidencePercent: confidence, classifiedDocumentVisible: true, storedConfidenceVisible: true, noHorizontalOverflow: true});
    }
    return results;
}
