// Isolated M4SubscriptionQualification only; no live links or event bodies are used.
async (page) => {
    const origin = new URL(page.url()).origin;
    const require = (condition, message) => { if (!condition) throw new Error(message); };
    const login = await page.evaluate(async () => {
        let csrf = await (await fetch('/auth/antiforgery')).json();
        let response = await fetch('/auth/login', {method: 'POST', headers: {'Content-Type': 'application/json', [csrf.headerName]: csrf.token}, body: JSON.stringify({password: 'test-password'})});
        if (!response.ok) return response.status;
        csrf = await (await fetch('/auth/antiforgery')).json();
        response = await fetch('/auth/step-up', {method: 'POST', headers: {'Content-Type': 'application/json', [csrf.headerName]: csrf.token}, body: JSON.stringify({password: 'test-password'})});
        return response.status;
    });
    require(login === 200, 'Fixture login or password confirmation failed');
    const evidence = [];
    for (const width of [1280, 390]) {
        await page.setViewportSize({width, height: 900});
        await page.goto(origin + '/manage/sources');
        await page.getByRole('button', {name: 'Add calendar subscription', exact: true}).click();
        const form = page.locator('form').filter({hasText: 'New shared calendar'});
        await form.getByLabel('Calendar name', {exact: true}).fill('Shared ' + width);
        await form.getByLabel('Shared calendar link', {exact: true}).fill('webcal://feed.test/published/2/synthetic-private-link');
        require(await form.getByLabel('Shared calendar link', {exact: true}).getAttribute('type') === 'password', 'Link entry should be hidden');
        await form.getByRole('button', {name: 'Add calendar', exact: true}).click();
        const card = page.getByRole('article', {name: 'Calendar Shared ' + width, exact: true});
        await card.getByText('https://feed.test · Up to date', {exact: true}).waitFor();
        require(!await page.locator('body').innerText().then(text => text.includes('synthetic-private-link')), 'Saved link leaked into UI');
        await card.getByRole('button', {name: 'Refresh now', exact: true}).click();
        await card.getByText('The calendar could not be accessed.', {exact: false}).waitFor();
        await page.goto(origin + '/library?type=calendar_event&people=nobody');
        await page.getByText('Some calendar subscriptions are paused, overdue,', {exact: false}).waitFor();
        await page.goto(origin + '/manage/sources');
        await card.getByRole('button', {name: 'Force refresh', exact: true}).click();
        await card.getByText('https://feed.test · Up to date', {exact: true}).waitFor();
        await card.getByRole('button', {name: 'Pause', exact: true}).click();
        await card.getByText('https://feed.test · Paused', {exact: true}).waitFor();
        require(await card.getByRole('button', {name: 'Refresh now', exact: true}).isDisabled(), 'Paused calendar allowed manual refresh');
        await card.getByRole('button', {name: 'Edit calendar', exact: true}).click();
        await card.getByLabel('Calendar name', {exact: true}).fill('Renamed ' + width);
        await card.getByLabel('Refresh interval (minutes)', {exact: true}).fill('10');
        await card.getByRole('button', {name: 'Save calendar', exact: true}).click();
        const renamed = page.getByRole('article', {name: 'Calendar Renamed ' + width, exact: true});
        await renamed.waitFor();
        await renamed.getByRole('button', {name: 'Resume', exact: true}).click();
        await renamed.getByText('https://feed.test · Up to date', {exact: true}).waitFor();
        require(await page.evaluate(() => document.documentElement.scrollWidth <= innerWidth), 'Source page overflow');
        await page.screenshot({path: '/home/justinleahy/Developer/SecondBrain/docs/build/evidence/m4-subscription-controls-20261024/sources-' + width + '.png', fullPage: true});
        await renamed.getByRole('button', {name: 'Remove calendar…', exact: true}).click();
        await renamed.getByRole('button', {name: 'Confirm remove calendar', exact: true}).click();
        await renamed.waitFor({state: 'detached'});
        evidence.push({width, hiddenRegistration: true, initialImport: true, sanitizedFailure: true, emptyQueryFreshness: true, retry: true, pause: true, edit: true, resume: true, removal: true, noOverflow: true});
    }
    return {status: 'passed', evidence};
}
