// Run against the isolated M4SubscriptionQualification fixture with synthetic data.
async (page) => {
    const origin = new URL(page.url()).origin;
    const require = (condition, message) => { if (!condition) throw new Error(message); };
    const login = await page.evaluate(async () => {
        for (const path of ['/auth/login', '/auth/step-up']) {
            const csrf = await (await fetch('/auth/antiforgery')).json();
            const response = await fetch(path, {method: 'POST', headers: {'Content-Type': 'application/json', [csrf.headerName]: csrf.token}, body: JSON.stringify({password: 'test-password'})});
            if (!response.ok) return response.status;
        }
        return 200;
    });
    require(login === 200, 'Fixture login failed');
    const evidence = [];
    for (const width of [1280, 390]) {
        await page.setViewportSize({width, height: 900});
        const source = await page.evaluate(async name => {
            const csrf = await (await fetch('/auth/antiforgery')).json();
            const response = await fetch('/v1/sources', {method: 'POST', headers: {'Content-Type': 'application/json', [csrf.headerName]: csrf.token},
                body: JSON.stringify({kind: 'calendar_subscription', name, url: 'https://feed.test/synthetic-calendar'})});
            if (!response.ok) throw new Error('Fixture source registration failed');
            return await response.json();
        }, 'Lifecycle ' + width);
        let before = [];
        for (let attempt = 0; attempt < 200 && before.length === 0; attempt++) {
            before = await page.evaluate(async id => {
                const csrf = await (await fetch('/auth/antiforgery')).json();
                return (await (await fetch('/v1/query', {method: 'POST', headers: {'Content-Type': 'application/json', [csrf.headerName]: csrf.token}, body: JSON.stringify({type: 'calendar_event', source: id})})).json()).records.map(item => item.id);
            }, source.id);
            if (before.length === 0) await page.waitForTimeout(50);
        }
        require(before.length === 1, 'Fixture event missing');
        await page.goto(origin + '/library/' + source.container_id);
        await page.getByRole('button', {name: 'Move to trash', exact: true}).click();
        await page.getByText('Its calendar events will also be hidden.', {exact: false}).waitFor();
        await page.getByRole('button', {name: 'Confirm move to trash', exact: true}).click();
        await page.waitForURL('**/manage/trash');
        await page.getByRole('button', {name: 'Restore Lifecycle ' + width, exact: true}).waitFor();
        require(await page.locator('article.knowledge-card').count() === 1, 'Trash should show the restorable container, not its unavailable children');
        await page.screenshot({path: '/home/justinleahy/Developer/SecondBrain/docs/build/evidence/m4-calendar-lifecycle-20261024/trash-' + width + '.png', fullPage: true});
        await page.goto(origin + '/manage/sources');
        const card = page.getByRole('article', {name: 'Calendar Lifecycle ' + width, exact: true});
        await card.getByText('https://feed.test · Paused', {exact: true}).waitFor();
        await card.getByRole('button', {name: 'Resume', exact: true}).click();
        await page.getByText('Restore the calendar container before resuming this subscription.', {exact: false}).waitFor();
        await page.goto(origin + '/manage/trash');
        await page.getByRole('button', {name: 'Restore Lifecycle ' + width, exact: true}).click();
        await page.getByText('Restored Lifecycle ' + width + '.', {exact: true}).waitFor();
        const after = await page.evaluate(async id => {
            const csrf = await (await fetch('/auth/antiforgery')).json();
            return (await (await fetch('/v1/query', {method: 'POST', headers: {'Content-Type': 'application/json', [csrf.headerName]: csrf.token}, body: JSON.stringify({type: 'calendar_event', source: id})})).json()).records.map(item => item.id);
        }, source.id);
        require(JSON.stringify(before) === JSON.stringify(after), 'Restore changed the event identity');
        await page.goto(origin + '/manage/sources');
        await card.getByText('https://feed.test · Paused', {exact: true}).waitFor();
        await page.screenshot({path: '/home/justinleahy/Developer/SecondBrain/docs/build/evidence/m4-calendar-lifecycle-20261024/restored-' + width + '.png', fullPage: true});
        require(await page.evaluate(() => document.documentElement.scrollWidth <= innerWidth), 'Source page overflow');
        await card.getByRole('button', {name: 'Resume', exact: true}).click();
        await card.getByText('The calendar could not be accessed.', {exact: false}).waitFor();
        await card.getByRole('button', {name: 'Force refresh', exact: true}).click();
        await card.getByText('https://feed.test · Up to date', {exact: true}).waitFor();
        await card.getByRole('button', {name: 'Remove calendar…', exact: true}).click();
        await card.getByRole('button', {name: 'Confirm remove calendar', exact: true}).click();
        await card.waitFor({state: 'detached'});
        evidence.push({width, containerTrash: true, childrenHidden: true, sourcePaused: true, prematureResumeRejected: true, restoredIdentity: true, explicitResume: true, noOverflow: true});
    }
    return {status: 'passed', evidence};
}
