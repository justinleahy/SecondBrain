// Run against the isolated KnowledgeBrowserQualification fixture.
async page => {
    const origin=new URL(page.url()).origin;
    const require=(ok,message)=>{if(!ok)throw new Error(message);};
    const login=await page.evaluate(async()=>{
        const token=await(await fetch('/auth/antiforgery')).json();
        return(await fetch('/auth/login',{method:'POST',headers:{'Content-Type':'application/json',[token.headerName]:token.token},body:JSON.stringify({password:'test-password'})})).status;
    });
    require(login===200,'Fixture login failed');
    await page.goto(origin+'/settings');
    await page.getByLabel('Password',{exact:true}).fill('test-password');
    await Promise.all([page.waitForEvent('load'),page.getByRole('button',{name:'Confirm password',exact:true}).click()]);
    const evidence=[];
    for(const width of [1280,390]) {
        await page.setViewportSize({width,height:900});
        const text=('Violet telescope astronomy observation. ').repeat(150);
        const captured=await page.evaluate(async({text,width})=>window.secondBrainApi.send('/documents','POST',{content:text,title:'Chunk settings '+width,tags:['chunkcheck'+width]}),{text,width});
        require(captured.status===201,'Capture before settings failed');
        const before=captured.body.document;
        const desired=width===1280?{targetTokens:64,maxTokens:128,overlapTokens:8}:{targetTokens:128,maxTokens:256,overlapTokens:16};
        await page.getByRole('button',{name:'Edit chunking',exact:true}).click();
        await page.getByLabel('Target tokens',{exact:true}).fill(String(desired.targetTokens));
        await page.getByLabel('Maximum tokens',{exact:true}).fill(String(desired.maxTokens));
        await page.getByLabel('Overlap tokens',{exact:true}).fill(String(desired.overlapTokens));
        await page.getByRole('button',{name:'Save settings',exact:true}).click();
        await page.getByRole('region',{name:'Edit settings',exact:true}).waitFor({state:'detached'});
        let current;
        for(let attempt=0;attempt<120;attempt++) {
            current=await page.evaluate(async id=>(await fetch('/documents/'+id+'?include=text,chunks')).json(),before.id);
            if(current.chunks.length!==before.chunks.length&&current.chunks.every(chunk=>chunk.token_count<=desired.maxTokens))break;
            await page.waitForTimeout(250);
        }
        require(current.chunks.length!==before.chunks.length,'Existing document was not rechunked');
        require(current.chunks.every(chunk=>chunk.token_count<=desired.maxTokens),'Chunk bounds were exceeded');
        require(current.text===text&&current.revision===before.revision&&current.content_hash===before.content_hash,'Rechunking changed authored content or revision');
        let search;
        for(let attempt=0;attempt<60;attempt++) {
            search=await page.evaluate(async width=>window.secondBrainApi.send('/search','POST',{query:'violet telescope',mode:'semantic',filters:{tags:['chunkcheck'+width]},limit:8}),width);
            if(search.status===200&&search.body.degraded.length===0&&search.body.results.some(hit=>hit.document_id===before.id))break;
            await page.waitForTimeout(250);
        }
        require(search.status===200&&search.body.degraded.length===0&&search.body.results.some(hit=>hit.document_id===before.id),'Semantic search did not recover after rechunking');
        const settings=await page.evaluate(async()=>(await fetch('/configuration')).json());
        require(settings.chunking.targetTokens===desired.targetTokens,'Chunking settings did not persist');
        await page.getByRole('button',{name:'Edit chunking',exact:true}).click();
        await page.getByLabel('Overlap tokens',{exact:true}).fill(String(desired.targetTokens));
        await page.getByRole('button',{name:'Save settings',exact:true}).click();
        await page.getByRole('alert').filter({hasText:'chunking requires'}).waitFor();
        await page.getByRole('button',{name:'Cancel settings edit',exact:true}).click();
        require(await page.evaluate(()=>document.documentElement.scrollWidth<=innerWidth),'Chunking settings overflow');
        evidence.push({width,settingsPersisted:true,oldChunks:before.chunks.length,newChunks:current.chunks.length,authoredContentRetained:true,revisionRetained:true,semanticSearchRecovered:true,invalidBoundsRejected:true});
    }
    await page.getByRole('button',{name:'Edit chunking',exact:true}).click();
    await page.getByLabel('Target tokens',{exact:true}).fill('512');
    await page.getByLabel('Maximum tokens',{exact:true}).fill('1024');
    await page.getByLabel('Overlap tokens',{exact:true}).fill('64');
    await page.getByRole('button',{name:'Save settings',exact:true}).click();
    await page.getByRole('region',{name:'Edit settings',exact:true}).waitFor({state:'detached'});
    return {status:'passed',chunking:evidence,restoredDefaultProfile:true};
}
