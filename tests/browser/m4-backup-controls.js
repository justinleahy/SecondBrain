// Uses the isolated M4BackupQualification host and synthetic content only.
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
        await page.goto(origin + '/settings');
        const backups = page.getByRole('region', {name: 'Backups', exact: true});
        await backups.getByText('The backup key is ready.', {exact: true}).waitFor();
        await backups.getByRole('button', {name: 'Create encrypted backup', exact: true}).click();
        await page.getByText('Encrypted backup created.', {exact: true}).waitFor();
        const latest = await page.evaluate(async () => (await (await fetch('/v1/maintenance/backups')).json())[0]);
        const created = new Date(latest.createdAt);
        const utcTime = String(created.getUTCHours()).padStart(2, '0') + ':' + String(created.getUTCMinutes()).padStart(2, '0');
        require((await backups.innerText()).includes(utcTime), 'Backup time does not use the configured UTC zone: ' + JSON.stringify({latest, expected: utcTime, rendered: await backups.innerText()}));
        const download = page.waitForEvent('download');
        await backups.getByRole('link', {name: 'Download encrypted backup', exact: true}).first().click();
        await (await download).saveAs('/tmp/m4-backup-browser-zone/downloads/backup-' + width + '.sbk');
        require(await page.evaluate(() => document.documentElement.scrollWidth <= innerWidth), 'Page overflow');
        await backups.screenshot({path: '/home/justinleahy/Developer/SecondBrain/docs/build/evidence/m4-backup-controls-20261024/settings-' + width + '.png'});
        evidence.push({width, encryptedBackupCreated: true, authenticatedDownload: true, configuredTimeZone: true, noOverflow: true});
    }
    return {status: 'passed', evidence};
}
