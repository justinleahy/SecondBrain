// The M4RetentionQualification host seeds three synthetic notes through the real capture/trash API.
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
    for (const [width, days] of [[1280, 14], [390, 7]]) {
        await page.setViewportSize({width, height: 900});
        await page.goto(origin + '/settings');
        const retention = page.getByRole('region', {name: 'Retention', exact: true});
        await page.getByRole('button', {name: 'Edit retention', exact: true}).click();
        await page.getByLabel('Trash retention (days)', {exact: true}).fill(String(days));
        await page.getByRole('button', {name: 'Save settings', exact: true}).click();
        await page.getByText('Settings saved and reloaded.', {exact: true}).waitFor();
        await retention.getByText('Trash: ' + days + ' days', {exact: false}).waitFor();
        await retention.getByRole('button', {name: 'Run cleanup now…', exact: true}).click();
        await retention.getByRole('button', {name: 'Cancel cleanup', exact: true}).click();
        const before = await page.evaluate(async () => (await (await fetch('/trash')).json()).items.map(item => item.title));
        require(before.includes('Expired ' + width), 'Cancel removed an eligible note');
        await retention.getByRole('button', {name: 'Run cleanup now…', exact: true}).click();
        await retention.getByRole('button', {name: 'Confirm cleanup', exact: true}).click();
        await page.getByText('Cleanup finished.', {exact: true}).waitFor();
        await retention.screenshot({path: '/home/justinleahy/Developer/SecondBrain/docs/build/evidence/m4-document-retention-20261024/settings-' + width + '.png'});
        await page.goto(origin + '/manage/trash');
        await page.getByRole('button', {name: 'Restore Recent note', exact: true}).waitFor();
        require(await page.getByRole('button', {name: 'Restore Expired ' + width, exact: true}).count() === 0, 'Expired note remained in trash');
        require(await page.evaluate(() => document.documentElement.scrollWidth <= innerWidth), 'Page overflow');
        await page.screenshot({path: '/home/justinleahy/Developer/SecondBrain/docs/build/evidence/m4-document-retention-20261024/trash-' + width + '.png', fullPage: true});
        evidence.push({width, retentionDays: days, policyEdited: true, cancellationPreservesTrash: true, expiredTrashPurged: true, recentTrashPreserved: true, noOverflow: true});
    }
    return {status: 'passed', evidence};
}
