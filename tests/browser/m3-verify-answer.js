// Verify the newest completed answer in an isolated, authenticated acceptance fixture.
async page => {
    const id=await page.locator('[data-chat-turn]').last().getAttribute('data-chat-turn');
    const turn=await page.evaluate(async id=>(await fetch('/turns/'+id)).json(),id);
    if(turn.status==='running')return {status:'running',id};
    if(turn.status!=='complete'||!turn.answer?.citations?.length)throw new Error('Expected a completed cited answer: '+turn.status+' / '+turn.failureCode);
    const checks=[];
    let displayNumber=0;
    for(const citation of turn.answer.citations) {
        displayNumber++;
        const source=citation.source;
        const card=page.locator('[data-chat-turn="'+id+'"] [data-citation-source]').filter({has:page.locator('.source-number',{hasText:'['+displayNumber+']'})});
        await page.locator('[data-chat-turn="'+id+'"] a[data-citation-link][aria-label="Go to source '+displayNumber+'"]').first().click();
        if(!await card.evaluate(element=>element.open))throw new Error('Footnote did not open its source');
        if(await card.locator('blockquote').textContent()!==source.excerpt)throw new Error('Displayed excerpt changed');
        const valid=await page.evaluate(async source=>{
            const document=await(await fetch('/documents/'+source.documentId+'?include=text')).json();
            const bytes=await crypto.subtle.digest('SHA-256',new TextEncoder().encode(source.excerpt));
            const hash=Array.from(new Uint8Array(bytes),value=>value.toString(16).padStart(2,'0')).join('');
            return document.text.includes(source.excerpt)&&hash===source.sha256.toLowerCase();
        },source);
        if(!valid)throw new Error('Citation does not match its exact source passage');
        checks.push({number:source.number,displayNumber,documentId:source.documentId,exactPassage:true,hashVerified:true});
    }
    const noOverflow=await page.evaluate(()=>document.documentElement.scrollWidth<=innerWidth);
    if(!noOverflow)throw new Error('Answer layout exceeds the viewport');
    return {status:'passed',width:page.viewportSize().width,turn,checks,noOverflow};
}
