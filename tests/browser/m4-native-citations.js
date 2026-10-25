// Controlled native provider and synthetic source text only.
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
    for (const width of [1280, 390]) {
        await page.setViewportSize({width, height: 900});
        await page.goto(origin + '/chat', {waitUntil: 'networkidle'});
        if (width < 600) await page.getByRole('button', {name: 'Conversations', exact: true}).click();
        await page.getByRole('button', {name: 'Orchard code name', exact: true}).click();
        const source = page.locator('[data-citation-source]').first();
        await source.locator('summary').click();
        await source.getByText('Exact quoted text', {exact: true}).waitFor();
        const quotes = await source.locator('blockquote').allTextContents();
        if (quotes.length !== 1 || quotes[0] !== 'Blue Otter') throw new Error('The native exact quote was not displayed');
        if (!await page.locator('[data-citation-link]').first().isVisible()) throw new Error('The native evidence marker was not linked');
        if (!await page.evaluate(() => document.documentElement.scrollWidth <= innerWidth)) throw new Error('Page overflow');
        await page.screenshot({path: '/home/justinleahy/Developer/SecondBrain/docs/build/evidence/m4-native-citations-20261024/chat-' + width + '.png', fullPage: true});
        results.push({width, exactQuoteVisible: true, wholePassageNotSubstituted: true, citationLinkVisible: true, noHorizontalOverflow: true});
    }
    return results;
}
