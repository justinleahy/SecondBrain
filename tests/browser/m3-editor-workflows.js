// Run on a fresh KnowledgeBrowserQualification origin.
async (page) => {
    const origin = new URL(page.url()).origin;
    const require = (value, message) => { if (!value) throw new Error(message); };
    // The browser tool can yield when the native confirmation opens. Keep the
    // final outcome readable after that yield without rerunning the workflow.
    await page.evaluate(() => sessionStorage.setItem('m3-editor-check',JSON.stringify({status:'running'})));
    try {
    const status = await page.evaluate(async () => {
        const token = await (await fetch('/auth/antiforgery')).json();
        return (await fetch('/auth/login', {method:'POST',credentials:'same-origin',headers:{'Content-Type':'application/json',[token.headerName]:token.token},body:JSON.stringify({password:'test-password'})})).status;
    });
    require(status === 200, 'Fixture login failed');
    const evidence = [];
    for (const width of [1280, 390]) {
        await page.setViewportSize({width,height:900});
        await page.goto(origin + '/capture');
        const switchMode = page.getByRole('button',{name:'Use plain text editor',exact:true});
        await switchMode.waitFor();
        await page.waitForFunction(() => document.querySelector('.monaco-editor') && !document.querySelector('.note-editor-toolbar button').disabled);
        const title = 'Rich editor note ' + width;
        await page.getByLabel('Title',{exact:true}).fill(title);
        await page.getByLabel('Aliases',{exact:true}).fill('Rich alias '+width);
        await page.getByLabel('Source URL',{exact:true}).fill('https://example.test/rich');
        await page.getByLabel('Tags',{exact:true}).fill('personal, eva');
        await page.waitForFunction(() => [...document.querySelectorAll('datalist option')].some(option => option.value==='personal, evaluation'));
        await page.getByLabel('Tags',{exact:true}).fill('personal, evaluation');
        await page.locator('.monaco-editor .view-lines').click({position:{x:90,y:10}});
        await page.keyboard.insertText('Authored rich text. ');
        await page.keyboard.type('[[Retr',{delay:70});
        await page.keyboard.press('Control+Space');
        await page.getByRole('option',{name:'Retrieval evaluation, Reference',exact:true}).waitFor();
        await page.getByRole('option',{name:'Retrieval evaluation, Reference',exact:true}).click();
        await switchMode.click();
        await page.locator('textarea.note-editor').waitFor();
        const value = await page.locator('textarea.note-editor').inputValue();
        require(value.startsWith('Authored rich text. [[') && value.endsWith(']]') && !value.endsWith(']]]]'), 'Completion duplicated brackets or lost text');
        await page.getByRole('button',{name:'Use rich editor',exact:true}).click();
        await page.waitForFunction(() => document.querySelector('.monaco-editor') && !document.querySelector('.note-editor-toolbar button').disabled);
        const dialog = page.waitForEvent('dialog').then(async dialog => { require(dialog.type()==='confirm','Expected draft confirmation'); await dialog.dismiss(); });
        await page.getByRole('link',{name:'Library',exact:true}).click();
        await dialog;
        require(page.url().endsWith('/capture'), 'Unsaved navigation was not prevented');
        await page.getByRole('button',{name:'Save note',exact:true}).click();
        await page.getByRole('heading',{name:title,exact:true}).waitFor();
        await page.getByText('Rich alias '+width,{exact:true}).waitFor();
        await page.getByRole('region',{name:'Document links'}).getByRole('link',{name:'Retrieval evaluation',exact:true}).waitFor();
        require((await page.getByRole('region',{name:'Revision history'}).innerText()).includes('Revision 1'), 'Missing revision history');
        await page.getByRole('link',{name:'Edit',exact:true}).click();
        await page.waitForFunction(() => document.querySelector('.monaco-editor') && !document.querySelector('.note-editor-toolbar button').disabled);
        await page.locator('.monaco-editor .view-lines').click({position:{x:90,y:10}});
        await page.keyboard.press('Control+Home'); await page.keyboard.press('Control+a');
        await page.keyboard.insertText('Revised rich text '+width);
        await page.getByRole('button',{name:'Save new revision',exact:true}).click();
        await page.getByText('Revised rich text '+width,{exact:true}).waitFor();
        require((await page.getByRole('region',{name:'Revision history'}).innerText()).includes('Revision 2'), 'Save did not add a revision');
        require(await page.evaluate(() => document.documentElement.scrollWidth<=innerWidth), 'Mobile layout overflow');
        await page.goto(origin+'/library');
        await page.getByLabel('People',{exact:true}).fill('May');
        await page.waitForFunction(() => [...document.querySelectorAll('datalist option')].some(option => option.textContent==='Maya Chen'));
        evidence.push({width,monaco:true,linkCompletion:true,tagCompletion:true,peopleCompletion:true,plainTextRoundTrip:true,draftGuard:true,aliases:true,history:true,revisionSave:true});
    }
    await page.goto(origin+'/library?q=sapphire&mode=keyword');
    await page.getByRole('link',{name:'Two-page PDF',exact:true}).click();
    await page.locator('#pdf-page-2.selected-passage').waitFor();
    require((await page.locator('#pdf-page-2').innerText()).includes('sapphire'), 'Selected PDF passage lost text');
    require((await page.locator('#pdf-page-1').innerText()).includes('amber') && !(await page.locator('#pdf-page-1').innerText()).includes('sapphire'), 'PDF pages were merged or mislabelled');
    require(await page.evaluate(()=>window.pdfXss!==true && document.querySelectorAll('main script').length===0), 'PDF text executed markup');
    await page.evaluate(() => {
        window.cspViolations=[];
        document.addEventListener('securitypolicyviolation', event => window.cspViolations.push(event.effectiveDirective));
        window.inlineCspExecuted=false;
        const renderedDocument = document.querySelector('#pdf-page-2');
        const script=document.createElement('script'); script.textContent='window.inlineCspExecuted=true'; renderedDocument.appendChild(script);
        const image=document.createElement('img'); image.src='https://example.invalid/csp-probe.png'; renderedDocument.appendChild(image);
    });
    await page.waitForFunction(()=>window.cspViolations.includes('script-src-elem') && window.cspViolations.includes('img-src'));
    require(await page.evaluate(()=>window.inlineCspExecuted===false),'Inline script was allowed');
    const result = {status:'passed',editor:evidence,pdfPageSelection:true,pdfMarkupEscaped:true,inlineScriptBlocked:true,remoteImageBlocked:true};
    await page.evaluate(result => sessionStorage.setItem('m3-editor-check',JSON.stringify(result)),result);
    return result;
    } catch(error) {
        await page.evaluate(message => sessionStorage.setItem('m3-editor-check',JSON.stringify({status:'failed',error:message})),String(error));
        throw error;
    }
}
