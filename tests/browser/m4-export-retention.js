// Uses the isolated M4ExportRetentionQualification host with synthetic content and keys.
async (page) => {
    const origin = new URL(page.url()).origin;
    const require = (value, message) => { if (!value) throw new Error(message); };
    require(await page.evaluate(async () => {
        for (const path of ['/auth/login', '/auth/step-up']) {
            const csrf = await (await fetch('/auth/antiforgery')).json();
            const result = await fetch(path, {method: 'POST', headers: {'Content-Type': 'application/json', [csrf.headerName]: csrf.token}, body: JSON.stringify({password: 'test-password'})});
            if (!result.ok) return false;
        }
        return true;
    }), 'Fixture sign-in failed');
    const evidence = [];
    for (const width of [1280, 390]) {
        await page.setViewportSize({width, height: 900});
        await page.goto(origin + '/settings');
        const backups = page.getByRole('region', {name: 'Backups', exact: true});
        await backups.getByRole('button', {name: 'Edit backup retention', exact: true}).click();
        await page.getByLabel('Backups to keep', {exact: true}).fill('1');
        await page.getByRole('button', {name: 'Save settings', exact: true}).click();
        await page.getByText('Settings saved and reloaded.', {exact: true}).waitFor();
        await backups.getByText('Keep the latest 1 archives.', {exact: false}).waitFor();
        await backups.getByRole('button', {name: 'Create encrypted backup', exact: true}).click();
        await page.getByText('Encrypted backup created.', {exact: true}).waitFor();
        const archive = page.waitForEvent('download');
        await backups.getByRole('link', {name: 'Download encrypted backup', exact: true}).click();
        await (await archive).saveAs('/tmp/m4-export-retention-final/downloads/backup-' + width + '.sbk');
        const count = await page.evaluate(async () => (await (await fetch('/maintenance/backups')).json()).length);
        require(count === 1, 'Rotation did not keep exactly one complete archive');

        const exports = page.getByRole('region', {name: 'Export', exact: true});
        let finish;
        const download = new Promise(resolve => { finish = resolve; });
        const listen = popup => popup.once('download', finish);
        page.once('download', finish);
        page.context().on('page', listen);
        try {
            await exports.getByRole('button', {name: 'Download portable export', exact: true}).click();
            const timer = new Promise((_, reject) => setTimeout(() => reject(new Error('Export download did not start')), 20000));
            await (await Promise.race([download, timer])).saveAs('/tmp/m4-export-retention-final/downloads/export-' + width + '.tar');
        } finally {
            page.off('download', finish); page.context().off('page', listen);
            for (const popup of page.context().pages()) if (popup !== page) await popup.close();
        }
        require(await page.evaluate(() => document.documentElement.scrollWidth <= innerWidth), 'Page overflow');
        await backups.screenshot({path: '/home/justinleahy/Developer/SecondBrain/docs/build/evidence/m4-export-retention-20261024/backups-' + width + '.png'});
        await exports.screenshot({path: '/home/justinleahy/Developer/SecondBrain/docs/build/evidence/m4-export-retention-20261024/export-' + width + '.png'});
        evidence.push({width, retentionEdited: true, oneArchiveRetained: true, backupDownloaded: true, nativeExportDownloaded: true, noOverflow: true});
    }
    return {status: 'passed', evidence};
}
