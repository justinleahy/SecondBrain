// Uses only the synthetic M4FolderPurgeQualification host.
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
        await page.goto(origin + '/manage/sources');
        const card = page.getByRole('article', {name: 'Source Folder purge ' + width, exact: true});
        await card.getByRole('button', {name: 'Purge source…', exact: true}).click();
        await card.getByText('Permanently remove this source and all its retained documents, including trash? Files in the original folder will remain on disk.', {exact: true}).waitFor();
        await card.getByRole('button', {name: 'Cancel purge', exact: true}).click();
        require(await card.getByRole('button', {name: 'Scan', exact: true}).isEnabled(), 'Cancellation changed source state');
        await card.getByRole('button', {name: 'Purge source…', exact: true}).click();
        await card.screenshot({path: '/home/justinleahy/Developer/SecondBrain/docs/build/evidence/m4-folder-purge-20261024/confirm-' + width + '.png'});
        await card.getByRole('button', {name: 'Confirm purge source', exact: true}).click();
        await page.getByText('Source and its retained data purged. External originals remain on disk.', {exact: true}).waitFor();
        await card.getByText('This source was removed. Its documents are no longer available in search.', {exact: true}).waitFor();
        require(await card.getByText('1 documents · 0 available · 0 pending · 0 failed or skipped · 0 missing or removed', {exact: true}).count() === 1, 'Purged documents remained in the source counts');
        require(await page.evaluate(() => document.documentElement.scrollWidth <= innerWidth), 'Page overflow');
        await card.screenshot({path: '/home/justinleahy/Developer/SecondBrain/docs/build/evidence/m4-folder-purge-20261024/purged-' + width + '.png'});
        evidence.push({width, cancellationPreservesSource:true, confirmationExplainsExternalFiles:true, sourceRemoved:true, retainedDocumentsPurged:true, noOverflow:true});
    }
    return {status:'passed', evidence};
}
