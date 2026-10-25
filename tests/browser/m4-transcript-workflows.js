// Run against the isolated M4TranscriptQualification fixture.
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
        await page.goto(origin + '/library?q=pricing&mode=keyword');
        await page.getByRole('link', {name: 'Transcript browser fixture', exact: true}).first().waitFor();
        await page.getByRole('link', {name: 'Transcript browser fixture', exact: true}).first().click();
        await page.locator('#segment-0').waitFor();
        const documentPath = new URL(page.url()).pathname;
        require((await page.locator('#segment-0').innerText()).includes('Ada'), 'Speaker missing');
        require((await page.locator('#segment-0').innerText()).includes('00:00:01.000'), 'Timestamp missing');
        require((await page.locator('#segment-1').innerText()).includes('Bob'), 'Overlapping speaker missing');
        require((await page.locator('#segment-2').innerText()).includes('<script>'), 'Literal transcript text changed');
        require(await page.evaluate(() => !window.transcriptExecuted && document.querySelectorAll('main script').length === 0), 'Transcript executed markup');
        await page.getByRole('checkbox', {name: 'Show lossless text'}).check();
        require((await page.locator('pre.document-text').innerText()).includes('Pricing should be reviewed.'), 'Lossless text missing');
        await page.getByRole('checkbox', {name: 'Show lossless text'}).uncheck();
        await page.locator('#segment-1 a').click();
        require(page.url().endsWith('#segment-1') && new URL(page.url()).pathname === documentPath, 'Timestamp left the document');
        await page.locator('#segment-1').waitFor();
        require(await page.evaluate(() => document.documentElement.scrollWidth <= innerWidth), 'Transcript overflow');
        evidence.push({width, speakers: true, overlappingTimes: true, timestampNavigation: true, losslessText: true, inertMarkup: true});
    }
    return {status: 'passed', evidence};
}
