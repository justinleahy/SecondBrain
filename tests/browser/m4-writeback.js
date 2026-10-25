async (page) => {
    const origin = new URL(page.url()).origin;
    const require = (value, message) => { if (!value) throw new Error(message); };
    await page.evaluate(async () => {
        for (const path of ['/auth/login', '/auth/step-up']) {
            const csrf = await (await fetch('/auth/antiforgery')).json();
            const response = await fetch(path, {method:'POST',headers:{'Content-Type':'application/json',[csrf.headerName]:csrf.token},body:JSON.stringify({password:'test-password'})});
            if (!response.ok) throw new Error('Fixture sign-in failed');
        }
    });
    const id = await page.evaluate(async () => {
        const csrf = await (await fetch('/auth/antiforgery')).json();
        const result = await (await fetch('/query',{method:'POST',headers:{'Content-Type':'application/json',[csrf.headerName]:csrf.token},body:JSON.stringify({limit:20})})).json();
        return result.records.find(item => item.title === 'Write-back browser fixture')?.id;
    });
    require(id, 'Fixture missing');
    const warnings = [];
    const listener = message => { if (['warning','error'].includes(message.type())) warnings.push(message.text()); };
    page.on('console',listener);
    const rows = [];
    for (const width of [1280,390]) {
        await page.setViewportSize({width,height:900});
        await page.goto(origin+'/settings');
        const section = page.getByRole('region',{name:'Document enrichment',exact:true});
        await section.getByRole('button',{name:'Edit enrichment',exact:true}).click();
        await page.getByLabel('Save summaries and suggested tags in managed Markdown files',{exact:true}).setChecked(true);
        await page.getByRole('button',{name:'Save settings',exact:true}).click();
        await page.getByText('Settings saved and reloaded.',{exact:true}).waitFor();
        await section.getByText('Save suggestions in managed Markdown files: on.',{exact:true}).waitFor();
        await section.screenshot({path:'/home/justinleahy/Developer/SecondBrain/docs/build/evidence/m4-writeback-20261024/settings-'+width+'.png'});
        let saved = false;
        for (let attempt=0; attempt<60; attempt++) {
            saved = await page.evaluate(async (id) => {
                const doc = await (await fetch('/documents/'+id+'?include=text')).json();
                return doc.revision===2 && doc.enrichment?.writtenBack;
            },id);
            if (saved) break;
            await page.waitForTimeout(500);
        }
        require(saved,'Write-back did not finish');
        await page.goto(origin+'/library/'+id);
        await page.getByText('Summary and suggested tags saved in this file.',{exact:true}).waitFor();
        await page.getByRole('button',{name:'Enrich document',exact:true}).click();
        await page.getByText('Enrichment is up to date.',{exact:false}).waitFor();
        const result = await page.evaluate(async (id) => {
            const doc = await (await fetch('/documents/'+id+'?include=text')).json();
            const file = await (await fetch('/files/'+id)).text();
            return {revision:doc.revision,fields:file.includes('author: Ada'),body:file.endsWith('An article about sustainable transport.'),block:file.includes('secondbrain_enrichment:')};
        },id);
        require(result.revision===2 && result.fields && result.body && result.block,'Write-back content changed unexpectedly');
        require(!(await page.locator('main').innerText()).includes('secondbrain_enrichment:'),'Generated storage fields should be hidden in reading view');
        await page.getByLabel('Show lossless text',{exact:true}).check();
        await page.locator('pre.document-text').waitFor({state:'visible'});
        require((await page.locator('main').innerText()).includes('secondbrain_enrichment:'),'Lossless view should preserve the actual file');
        await page.getByLabel('Show lossless text',{exact:true}).uncheck();
        await page.locator('pre.document-text').waitFor({state:'hidden'});
        require(await page.evaluate(()=>document.documentElement.scrollWidth<=innerWidth),'Horizontal overflow');
        await page.screenshot({path:'/home/justinleahy/Developer/SecondBrain/docs/build/evidence/m4-writeback-20261024/document-'+width+'.png',fullPage:true});
        rows.push({width,settingsSaved:true,...result,noHorizontalOverflow:true});
    }
    page.off('console',listener);
    require(warnings.length===0,'Unexpected workflow console warnings or errors');
    return {checks:rows,warnings:0,errors:0};
}
