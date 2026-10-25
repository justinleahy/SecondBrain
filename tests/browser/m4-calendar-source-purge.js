// Run against M4SubscriptionQualification with synthetic data and the real extractor.
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
    }), 'Fixture login failed');
    const evidence = [];
    for (const width of [1280, 390]) {
        await page.setViewportSize({width, height: 900});
        const source = await page.evaluate(async name => {
            const csrf = await (await fetch('/auth/antiforgery')).json();
            const response = await fetch('/v1/sources', {method: 'POST', headers: {'Content-Type': 'application/json', [csrf.headerName]: csrf.token},
                body: JSON.stringify({kind: 'calendar_subscription', name, url: 'https://feed.test/synthetic-calendar'})});
            if (!response.ok) throw new Error('Fixture source registration failed');
            return response.json();
        }, 'Purge ' + width);
        const query = async () => page.evaluate(async id => {
            const csrf = await (await fetch('/auth/antiforgery')).json();
            const response = await fetch('/v1/query', {method: 'POST', headers: {'Content-Type': 'application/json', [csrf.headerName]: csrf.token}, body: JSON.stringify({type: 'calendar_event', source: id})});
            return (await response.json()).records;
        }, source.id);
        let records = [];
        for (let attempt = 0; attempt < 200 && records.length === 0; attempt++) {
            records = await query();
            if (records.length === 0) await page.waitForTimeout(50);
        }
        require(records.length === 1, 'Fixture event not published');
        await page.goto(origin + '/manage/sources');
        const card = page.getByRole('article', {name: 'Calendar Purge ' + width, exact: true});
        await card.getByRole('button', {name: 'Purge calendar…', exact: true}).click();
        await card.getByText('Permanently remove this subscription, its imported events and its saved calendar files?', {exact: false}).waitFor();
        await card.getByRole('button', {name: 'Cancel purge', exact: true}).click();
        require((await query()).length === 1, 'Cancelling purge changed events');
        await card.getByRole('button', {name: 'Purge calendar…', exact: true}).click();
        require(await page.evaluate(() => document.documentElement.scrollWidth <= innerWidth), 'Confirmation overflow');
        await page.screenshot({path: '/home/justinleahy/Developer/SecondBrain/docs/build/evidence/m4-source-purge-20261024/confirm-' + width + '.png', fullPage: true});
        await card.getByRole('button', {name: 'Confirm purge calendar', exact: true}).click();
        await page.getByText('Calendar source and its owned data purged.', {exact: true}).waitFor();
        await card.waitFor({state: 'detached'});
        require((await query()).length === 0, 'Purged source still has searchable events');
        require(await page.evaluate(async id => (await fetch('/v1/sources/' + id)).status, source.id) === 404, 'Purged source still listed');
        require(await page.evaluate(() => document.documentElement.scrollWidth <= innerWidth), 'Completion overflow');
        await page.screenshot({path: '/home/justinleahy/Developer/SecondBrain/docs/build/evidence/m4-source-purge-20261024/completed-' + width + '.png', fullPage: true});
        evidence.push({width, cancelPreservesEvents: true, explicitConfirmation: true, sourceRemoved: true, eventsRemoved: true, noOverflow: true});
    }
    return {status: 'passed', evidence};
}
