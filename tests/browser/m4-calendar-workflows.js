// Run against the isolated M4CalendarQualification fixture.
async (page) => {
    const origin = new URL(page.url()).origin;
    const require = (condition, message) => { if (!condition) throw new Error(message); };
    const login = await page.evaluate(async () => {
        const token = await (await fetch('/auth/antiforgery')).json();
        return (await fetch('/auth/login', {method: 'POST', headers: {'Content-Type': 'application/json', [token.headerName]: token.token},
            body: JSON.stringify({password: 'test-password'})})).status;
    });
    require(login === 200, 'Fixture login failed');
    const evidence = [];
    for (const width of [1280, 390]) {
        await page.setViewportSize({width, height: 900});
        await page.goto(origin + '/library?type=calendar_event&occurredFrom=2026-10-01&occurredTo=2026-12-01&people=Ada%20Lovelace');
        await page.getByRole('link', {name: 'Calendar browser fixture', exact: true}).first().waitFor();
        require(await page.locator('article.knowledge-card').count() === 6, 'Repeating instances or display-name filtering missing');
        await page.getByRole('link', {name: 'Calendar browser fixture', exact: true}).first().click();
        const event = page.getByRole('region', {name: 'Calendar event'});
        await event.waitFor();
        require((await event.innerText()).includes('Planning room'), 'Location missing');
        require((await event.innerText()).includes('ada@example.com'), 'Attendee missing');
        require((await event.innerText()).includes('Repeating event'), 'Recurrence details missing');
        require((await event.innerText()).includes('1 other imported calendar'), 'Duplicate-calendar notice missing');
        require((await event.innerText()).includes('<script>'), 'Calendar text changed');
        require(await page.evaluate(() => !window.calendarExecuted && document.querySelectorAll('main script').length === 0), 'Calendar executed markup');
        require(await page.evaluate(() => document.documentElement.scrollWidth <= innerWidth), 'Calendar overflow');
        await page.getByRole('link', {name: 'Edit', exact: true}).click();
        await page.locator('form select').waitFor();
        require(await page.locator('form select').inputValue() === 'calendar_event', 'Calendar editor type missing');
        require(await page.getByText("The original file's text cannot be edited here.", {exact: false}).isVisible(), 'Calendar body should be read-only');
        await page.goto(origin + '/library?type=calendar_event&occurredFrom=1900-01-01&occurredTo=2026-12-01');
        await page.getByText('Some requested dates fall outside the imported calendar range.', {exact: false}).waitFor();
        evidence.push({width, recurringInstances: true, displayNameFilter: true, eventDetails: true, duplicateNotice: true, inertMarkup: true, editorType: true, partialCoverage: true});
    }
    return {status: 'passed', evidence};
}
