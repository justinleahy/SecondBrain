// Set sessionStorage['m3-source-paths'] from the isolated fixture's ready.json.sourcePaths first.
async (page) => {
    const origin = new URL(page.url()).origin;
    const require = (value, message) => { if (!value) throw new Error(message); };
    const paths = await page.evaluate(() => JSON.parse(sessionStorage.getItem('m3-source-paths') || 'null'));
    require(paths, 'Set fixture source paths before running');
    const login = await page.evaluate(async () => {
        const token = await (await fetch('/auth/antiforgery')).json();
        return (await fetch('/auth/login',{method:'POST',credentials:'same-origin',headers:{'Content-Type':'application/json',[token.headerName]:token.token},body:JSON.stringify({password:'test-password'})})).status;
    });
    require(login === 200, 'Fixture login failed');
    await page.goto(origin+'/settings');
    await page.getByLabel('Password',{exact:true}).fill('test-password');
    await Promise.all([page.waitForEvent('load'),page.getByRole('button',{name:'Confirm password',exact:true}).click()]);
    const evidence=[];
    for(const width of [1280,390]) {
        await page.setViewportSize({width,height:900});
        await page.goto(origin+'/manage/sources');
        await page.getByRole('button',{name:'Add folder source',exact:true}).click();
        const name='Browser source '+width;
        await page.getByLabel('Name',{exact:true}).fill(name);
        await page.getByLabel('Folder path',{exact:true}).fill(paths[width]);
        await page.getByLabel('Scan interval (seconds)',{exact:true}).fill('86400');
        await page.getByRole('button',{name:'Create source',exact:true}).click();
        const card=page.getByRole('article',{name:'Source '+name,exact:true});
        await card.waitFor();
        const id=await page.evaluate(async name => (await(await fetch('/sources')).json()).find(source=>source.name===name).id,name);
        await card.getByRole('button',{name:'Deep scan',exact:true}).click();
        await page.getByText('Scan completed. Processing jobs may continue in the background.',{exact:true}).waitFor();
        let failed=[];
        for(let attempt=0;attempt<80;attempt++) {
            failed=await page.evaluate(async id => (await(await fetch('/browse/jobs?status=failed&source='+id)).json()).items,id);
            if(failed.some(job=>job.fileName==='retry-me.md'&&job.attempts===3)&&failed.some(job=>job.fileName==='broken.pdf')) break;
            await page.waitForTimeout(250);
        }
        require(failed.some(job=>job.fileName==='retry-me.md'&&job.attempts===3),'Expected three real failed extraction attempts');
        await card.getByRole('button',{name:'Show jobs',exact:true}).click();
        await page.getByRole('combobox',{name:'Status',exact:true}).selectOption('failed');
        await page.getByRole('button',{name:'Filter',exact:true}).click();
        const transient=failed.find(job=>job.fileName==='retry-me.md');
        const deterministic=failed.find(job=>job.fileName==='broken.pdf');
        require(transient.canRetry && transient.error==='extraction-unavailable','Transient failure was not retryable');
        require(!deterministic.canRetry && deterministic.retryRejection==='deterministic-failure','Invalid PDF should not retry');
        const retryCard=page.getByRole('article',{name:'Job '+transient.id,exact:true});
        await retryCard.getByText('File: retry-me.md',{exact:true}).waitFor();
        await page.getByRole('article',{name:'Job '+deterministic.id,exact:true}).getByText(/This failure cannot be retried/).waitFor();
        await retryCard.getByRole('button',{name:'Retry job',exact:true}).click();
        await page.getByText('Retried job completed.',{exact:true}).waitFor({timeout:20000});
        const retried=await page.evaluate(async id => (await(await fetch('/jobs/'+id)).json()),transient.id);
        require(retried.status==='completed','Retry did not finish through real extraction');
        const document=await page.evaluate(async id => (await(await fetch('/documents/'+id+'?include=text')).json()),transient.documentId);
        require(document.text.includes('recoverable extraction fixture'),'Retry failed to publish extracted text');
        require((await card.innerText()).includes('2 documents'),'Source counts were not shown');
        await card.getByRole('button',{name:'Pause',exact:true}).click();
        await card.getByRole('button',{name:'Resume',exact:true}).click();
        await card.getByRole('button',{name:'Pause',exact:true}).waitFor();
        require(await page.evaluate(()=>document.documentElement.scrollWidth<=innerWidth),'Source layout overflow');
        await card.getByRole('button',{name:'Remove source…',exact:true}).click();
        await card.getByRole('button',{name:'Confirm remove source',exact:true}).click();
        await card.getByText('This source was removed. Its documents are no longer available in search.',{exact:true}).waitFor();
        const hidden=await page.evaluate(async id=>(await fetch('/documents/'+id+'?include=text')).status,transient.documentId);
        require(hidden===404,'Removed source remained readable');
        evidence.push({width,sourceId:id,created:true,scan:true,fileFailureReason:true,transientRetryCompleted:true,deterministicRetryRefused:true,counts:true,pauseResume:true,removed:true,noOverflow:true});
    }
    return evidence;
}
