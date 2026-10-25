// Run with the browser tool's run-code capability after starting KnowledgeBrowserQualification.
// The current page must be on the fixture origin; this fixture uses only synthetic data.
async (page) => {
    const origin = new URL(page.url()).origin;
    const require = (condition, message) => { if (!condition) throw new Error(message); };
    await page.setViewportSize({ width: 1280, height: 900 });
    // Seed authentication through the same public API; interactive login is qualified separately.
    // This also avoids filling an SSR form while its initial interactive render replaces it.
    const login = await page.evaluate(async () => {
        const token = await (await fetch('/auth/antiforgery', { credentials: 'same-origin' })).json();
        const response = await fetch('/auth/login', { method: 'POST', credentials: 'same-origin',
            headers: { 'Content-Type': 'application/json', [token.headerName]: token.token },
            body: JSON.stringify({ password: 'test-password' }) });
        return response.status;
    });
    require(login === 200, 'Fixture login failed');
    await page.goto(origin + '/library');
    await page.getByRole('link', { name: 'Retrieval evaluation', exact: true }).waitFor();
    await page.getByLabel('Search your notes and documents').fill('retrieval');
    await page.getByLabel('Search your notes and documents').press('Tab');
    await page.getByRole('combobox', { name: 'Mode', exact: true }).selectOption('keyword');
    await page.getByRole('button', { name: /^(Browse|Search)$/ }).click();
    await page.locator('.search-snippet mark').first().waitFor();
    const highlighted = await page.locator('.search-snippet mark').allTextContents();
    const link = page.getByRole('link', { name: 'Retrieval evaluation', exact: true });
    const href = await link.getAttribute('href');
    const id = new URL(origin + href).pathname.split('/').at(-1);
    const initialRevision = await page.evaluate(async id => (await (await fetch('/documents/' + id)).json()).revision, id);
    await link.click();
    await page.locator('.selected-passage').waitFor();
    require(!await page.evaluate(() => window.untrustedDocumentExecuted === true), 'Document script executed');
    require(await page.locator('main img').count() === 0, 'Remote image rendered');
    await page.getByRole('link', { name: 'Edit', exact: true }).click();
    await page.getByRole('button', { name: 'Use plain text editor', exact: true }).click();
    const content = page.locator('textarea.note-editor');
    await content.waitFor();
    const draft = (await content.inputValue()) + '\n\nDraft retained through conflict.';
    await content.fill(draft);
    await content.press('Tab');
    const changed = await page.evaluate(async ({id,revision}) => window.secondBrainApi.send('/documents/' + id, 'PATCH', { content: 'Concurrent revision from another editor.' }, revision), {id,revision:initialRevision});
    require(changed.status === 200, 'Concurrent update failed');
    await page.getByRole('button', { name: 'Save new revision' }).click();
    await page.getByRole('heading', { name: 'Latest saved revision: ' + (initialRevision + 1) }).waitFor();
    require(await content.inputValue() === draft, 'Draft was lost');
    require(await page.getByRole('button', { name: 'Save new revision' }).isDisabled(), 'Stale save remained enabled');
    await page.getByRole('button', { name: 'Replace draft with latest revision' }).click();
    await page.waitForFunction(() => document.querySelector('.note-editor')?.value === 'Concurrent revision from another editor.');
    await content.fill('Merged revision with Blue Otter retrieval evaluation.');
    await content.press('Tab');
    await page.getByRole('button', { name: 'Save new revision' }).click();
    await page.waitForURL(origin + '/library/' + id);
    await page.getByText('Merged revision with Blue Otter retrieval evaluation.', { exact: true }).waitFor();
    await page.setViewportSize({ width: 390, height: 844 });
    await page.goto(origin + '/library');
    await page.getByRole('link', { name: 'Retrieval evaluation', exact: true }).waitFor();
    require(await page.evaluate(() => document.documentElement.scrollWidth <= innerWidth), 'Mobile library overflows');
    await page.getByRole('link', { name: 'Capture', exact: true }).click();
    await page.getByRole('button', { name: 'Use plain text editor', exact: true }).click();
    await page.getByLabel('Title', { exact: true }).waitFor();
    await page.getByLabel('Title', { exact: true }).fill('Mobile capture');
    await page.locator('textarea.note-editor').fill('A note captured directly on mobile without the model.');
    await page.locator('textarea.note-editor').press('Tab');
    await page.getByRole('button', { name: 'Save note', exact: true }).click();
    await page.getByRole('heading', { name: 'Mobile capture', exact: true }).waitFor();
    await page.getByText('A note captured directly on mobile without the model.', { exact: true }).waitFor();
    await page.getByRole('link', { name: 'Sources and jobs', exact: true }).click();
    await page.getByRole('heading', { name: 'Processing jobs' }).waitFor();
    await page.waitForFunction(() => !document.querySelector('main [role="alert"]'));
    await page.getByRole('link', { name: 'Settings', exact: true }).click();
    await page.getByRole('heading', { name: 'Session security' }).waitFor();
    require(await page.getByRole('button', { name: 'Revoke session', exact: true }).count() > 0, 'Session listing missing');
    await page.getByLabel('Password', { exact: true }).fill('test-password');
    await page.getByLabel('Password', { exact: true }).press('Tab');
    await Promise.all([
        page.waitForEvent('load'),
        page.getByRole('button', { name: 'Confirm password', exact: true }).click()
    ]);
    await page.getByRole('heading', { name: 'Session security', exact: true }).waitFor();
    await page.getByRole('button', { name: 'Revoke session', exact: true }).first().waitFor();
    require(await page.getByLabel('Password', { exact: true }).inputValue() === '', 'Password remained after rotation');
    await page.getByRole('button', { name: 'Sign out', exact: true }).click();
    await page.waitForURL(origin + '/login');
    return { highlighted, passage: true, scriptBlocked: true, conflictPreserved: true, mergedSave: true, mobileCapture: true, mobileNoOverflow: true, settingsAndJobs: true, stepUpReload: true, signOut: true };
}
