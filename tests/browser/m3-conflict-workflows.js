// Before running, overwrite the fixture's conflictNotes paths with external text.
async (page) => {
    const origin = new URL(page.url()).origin;
    const require = (ok, message) => { if (!ok) throw new Error(message); };
    const login = await page.evaluate(async () => {
        const csrf = await (await fetch('/auth/antiforgery')).json();
        return (await fetch('/auth/login', {method:'POST',headers:{'Content-Type':'application/json',[csrf.headerName]:csrf.token},body:JSON.stringify({password:'test-password'})})).status;
    });
    require(login===200,'Fixture login failed');
    const evidence=[];
    for(const width of [1280,390]) {
        await page.setViewportSize({width,height:900});
        await page.goto(origin+'/library?q='+encodeURIComponent('File conflict '+width)+'&mode=keyword');
        await page.getByRole('link',{name:'File conflict '+width,exact:true}).click();
        const id = new URL(page.url()).pathname.split('/').pop();
        await page.getByRole('link',{name:'Edit',exact:true}).click();
        await page.getByRole('button',{name:'Use plain text editor',exact:true}).click();
        const editor=page.locator('textarea.note-editor'); await editor.waitFor();
        require(await editor.inputValue()==='Original conflict text '+width,'Original projection not loaded');
        const draft='Browser preserved draft '+width;
        await editor.fill(draft);
        await page.getByRole('button',{name:'Save new revision',exact:true}).click();
        const conflict=page.getByRole('region',{name:'File conflict'}); await conflict.waitFor();
        require(await editor.inputValue()===draft,'Conflict lost the editor draft');
        const download=await conflict.getByRole('link',{name:'Download preserved draft'}).getAttribute('href');
        const saved=await page.evaluate(async path=>({status:(await fetch(path)).status,text:await(await fetch(path)).text()}),download);
        require(saved.status===200&&saved.text===draft,'Preserved draft download did not match');
        let current;
        for(let attempt=0;attempt<80;attempt++) {
            current=await page.evaluate(async id=>(await fetch('/documents/'+id+'?include=text')).json(),id);
            if(current.text==='External file revision '+width) break;
            await page.waitForTimeout(250);
        }
        require(current.revision===2&&current.text==='External file revision '+width,'External revision was not indexed');
        if(width===1280) {
            await conflict.getByRole('button',{name:'Replace draft with external revision'}).click();
            await page.waitForFunction(text=>document.querySelector('textarea.note-editor')?.value===text,'External file revision '+width);
            await editor.fill('Reconciled external revision '+width);
            await page.getByRole('button',{name:'Save new revision',exact:true}).click();
            await page.getByText('Reconciled external revision '+width,{exact:true}).waitFor();
        } else {
            await conflict.getByRole('button',{name:'Save draft as a new note'}).click();
            await page.getByRole('heading',{name:'File conflict '+width+' (conflict copy)',exact:true}).waitFor();
            await page.getByText(draft,{exact:true}).waitFor();
            require(!page.url().endsWith('/'+id),'Conflict copy reused original document');
        }
        require(await page.evaluate(()=>document.documentElement.scrollWidth<=innerWidth),'Conflict layout overflow');
        evidence.push({width,draftPreserved:true,downloadVerified:true,externalRevisionIndexed:true,outcome:width===1280?'reconciled':'new note'});
    }
    return {status:'passed',fileConflicts:evidence};
}
