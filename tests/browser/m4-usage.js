// M4UsageQualification provides a disposable account and synthetic usage totals.
async (page) => {
    const origin = new URL(page.url()).origin;
    const require = (value, message) => { if (!value) throw new Error(message); };
    require(await page.evaluate(async () => {
        for (const path of ['/auth/login', '/auth/step-up']) {
            const csrf = await (await fetch('/auth/antiforgery')).json();
            const response = await fetch(path, {method: 'POST', headers: {'Content-Type': 'application/json', [csrf.headerName]: csrf.token}, body: JSON.stringify({password: 'test-password'})});
            if (!response.ok) return false;
        }
        return true;
    }), 'Fixture sign-in failed');
    const errors = [];
    page.on('pageerror', error => errors.push(error.message));
    const results = [];
    for (const [width, limit] of [[1280, 3000], [390, 2500]]) {
        await page.setViewportSize({width, height: 900});
        await page.goto(origin + '/settings');
        const usage = page.getByRole('region', {name: 'Model usage', exact: true});
        await usage.getByText('100 tokens across 1 call.', {exact: false}).waitFor();
        await usage.getByText('60 tokens across 1 call.', {exact: false}).waitFor();
        await page.getByRole('button', {name: 'Edit daily budget', exact: true}).click();
        await page.getByLabel('Daily token limit', {exact: true}).fill(String(limit));
        await page.getByRole('button', {name: 'Save settings', exact: true}).click();
        await page.getByText('Settings saved and reloaded.', {exact: true}).waitFor();
        await usage.getByRole('button', {name: 'Refresh usage', exact: true}).click();
        await usage.getByText('160 of ' + limit.toLocaleString('en-US') + ' tokens used today (UTC).', {exact: true}).waitFor();
        require(await page.evaluate(() => document.documentElement.scrollWidth <= innerWidth), 'Page overflow');
        await usage.screenshot({path: '/home/justinleahy/Developer/SecondBrain/docs/build/evidence/m4-usage-20261024/usage-' + width + '.png'});
        results.push({width, dailyTokens: 160, dailyLimit: limit, numericBudgetEdited: true, usageRefreshed: true, noHorizontalOverflow: true});
    }
    require(errors.length === 0, 'Browser page errors: ' + errors.join('; '));
    return {results, pageErrors: errors};
}
