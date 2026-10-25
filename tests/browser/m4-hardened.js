// Run only against the isolated Compose fixture after m4-meetings.py.
async (page) => {
    const origin = new URL(page.url()).origin;
    const data = await page.evaluate(async () => {
        for (const path of ['/auth/login', '/auth/step-up']) {
            const csrf = await (await fetch('/auth/antiforgery')).json();
            const result = await fetch(path, {method: 'POST', headers: {'Content-Type': 'application/json', [csrf.headerName]: csrf.token}, body: JSON.stringify({password: 'compose-smoke-password'})});
            if (!result.ok) throw new Error('Fixture login failed');
        }
        const csrf = await (await fetch('/auth/antiforgery')).json();
        const query = async type => (await (await fetch('/v1/query', {method: 'POST', headers: {'Content-Type': 'application/json', [csrf.headerName]: csrf.token}, body: JSON.stringify({type})})).json()).records;
        const transcript = (await query('transcript')).find(item => item.title.startsWith('m4-meetings-') && item.title.endsWith(' transcript'));
        const prefix = transcript.title.slice(0, -' transcript'.length);
        const event = (await query('calendar_event')).find(item => item.title === prefix);
        return {transcript: transcript.id, event: event.id};
    });
    const checks = [];
    for (const width of [1280, 390]) {
        await page.setViewportSize({width, height: 900});
        await page.goto(origin + '/library/' + data.transcript, {waitUntil: 'networkidle'});
        await page.locator('#segment-0').waitFor();
        if (!(await page.locator('#segment-0').innerText()).includes('Ada')) throw new Error('Missing speaker');
        await page.getByRole('checkbox', {name: 'Show lossless text'}).check();
        if (!(await page.locator('pre.document-text').innerText()).includes('forty credits')) throw new Error('Missing preserved text');
        await page.getByRole('checkbox', {name: 'Show lossless text'}).uncheck();
        await page.locator('#segment-2 a').click();
        if (!page.url().endsWith('#segment-2')) throw new Error('Missing timestamp navigation');
        if (!await page.evaluate(() => document.documentElement.scrollWidth <= innerWidth)) throw new Error('Transcript overflow');
        await page.screenshot({path: '/home/justinleahy/Developer/SecondBrain/docs/build/evidence/m4-hardened-20261024/transcript-' + width + '.png', fullPage: true});
        await page.goto(origin + '/library/' + data.event, {waitUntil: 'networkidle'});
        const calendar = page.getByRole('region', {name: 'Calendar event'});
        await calendar.waitFor();
        const details = await calendar.innerText();
        if (!details.includes('Repeating event') || !details.includes('1 other imported calendar')) throw new Error('Missing recurrence or duplicate identity');
        if (!await page.evaluate(() => document.documentElement.scrollWidth <= innerWidth)) throw new Error('Calendar overflow');
        await page.screenshot({path: '/home/justinleahy/Developer/SecondBrain/docs/build/evidence/m4-hardened-20261024/calendar-' + width + '.png', fullPage: true});
        checks.push({width, transcript: true, speaker: true, losslessText: true, timestampNavigation: true, recurrence: true, duplicateNotice: true, noOverflow: true});
    }
    return {status: 'passed', syntheticData: true, hardenedCompose: true, checks};
}
