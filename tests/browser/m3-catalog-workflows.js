// Run on the origin of KnowledgeBrowserQualification with a fresh fixture.
async (page) => {
    const origin = new URL(page.url()).origin;
    const require = (value, message) => { if (!value) throw new Error(message); };
    const login = await page.evaluate(async () => {
        const token = await (await fetch('/auth/antiforgery')).json();
        return (await fetch('/auth/login', { method:'POST', credentials:'same-origin',
            headers:{'Content-Type':'application/json',[token.headerName]:token.token}, body:JSON.stringify({password:'test-password'}) })).status;
    });
    require(login === 200, 'Fixture login failed');
    const evidence = [];
    for (const width of [1280, 390]) {
        await page.setViewportSize({width,height:900});
        await page.goto(origin+'/library');
        await page.getByRole('link',{name:'Retrieval evaluation',exact:true}).waitFor();
        await page.getByLabel('Search your notes and documents').fill('retrieval');
        await page.getByLabel('Search your notes and documents').press('Tab');
        await page.getByRole('combobox',{name:'Mode',exact:true}).selectOption('keyword');
        await page.getByRole('button',{name:/^(Browse|Search)$/}).click();
        await page.locator('.search-snippet mark').first().waitFor();
        await page.getByRole('button',{name:'note (1)',exact:true}).waitFor();
        const filteredUrl = page.url();
        require(new URL(filteredUrl).searchParams.get('q') === 'retrieval','Query missing from URL');
        await page.getByRole('link',{name:'Retrieval evaluation',exact:true}).click();
        await page.getByRole('heading',{name:'Retrieval evaluation',exact:true}).waitFor();
        await page.goBack();
        await page.locator('.search-snippet mark').first().waitFor();
        require(await page.getByLabel('Search your notes and documents').inputValue() === 'retrieval','Query lost on return');
        await page.getByRole('link',{name:'Timeline',exact:true}).click();
        await page.getByRole('link',{name:'Garden article',exact:true}).waitFor();
        await page.locator('.timeline-day[aria-label="2026-10-23"]').waitFor();
        await page.getByLabel('People',{exact:true}).fill('Nobody in the fixture');
        await page.getByLabel('People',{exact:true}).press('Tab');
        await page.getByRole('button',{name:'Apply filters',exact:true}).click();
        await page.getByText('No dated documents match this period.',{exact:true}).waitFor();
        const title='Lifecycle note '+width;
        await page.getByRole('link',{name:'Capture',exact:true}).click();
        await page.getByRole('button',{name:'Use plain text editor',exact:true}).click();
        await page.getByLabel('Title',{exact:true}).waitFor();
        await page.getByLabel('Title',{exact:true}).fill(title);
        await page.locator('textarea.note-editor').fill('Retained lifecycle text '+width);
        await page.locator('textarea.note-editor').press('Tab');
        await page.getByRole('button',{name:'Save note',exact:true}).click();
        await page.getByRole('heading',{name:title,exact:true}).waitFor();
        const id=new URL(page.url()).pathname.split('/').at(-1);
        await page.getByRole('button',{name:'Move to trash',exact:true}).click();
        await page.getByRole('button',{name:'Confirm move to trash',exact:true}).click();
        await page.waitForURL(origin+'/manage/trash');
        await page.getByRole('button',{name:'Restore '+title,exact:true}).waitFor();
        require(!await page.locator('main').innerText().then(text=>text.includes('Retained lifecycle text')),'Trash leaked text');
        const hidden = await page.evaluate(async id=>(await fetch('/documents/'+id+'?include=text')).status,id);
        require(hidden === 404,'Deleted text remained readable');
        await page.getByRole('link',{name:'Manage suppressions',exact:true}).click();
        const suppression=page.locator('article').filter({has:page.getByRole('heading',{name:id,exact:true})});
        await suppression.getByRole('button',{name:'Lift suppression',exact:true}).click();
        await suppression.getByRole('button',{name:'Confirm lift suppression',exact:true}).click();
        await page.getByText('Suppression lifted.',{exact:true}).waitFor();
        require(await page.getByRole('heading',{name:id,exact:true}).count()===0,'Suppression remained');
        await page.getByRole('link',{name:'Open trash',exact:true}).click();
        await page.getByRole('button',{name:'Restore '+title,exact:true}).click();
        await page.getByText('Restored '+title+'.',{exact:true}).waitFor();
        await page.goto(origin+'/library/'+id);
        await page.getByText('Retained lifecycle text '+width,{exact:true}).waitFor();
        require(await page.evaluate(()=>document.documentElement.scrollWidth<=innerWidth),'Horizontal overflow');
        evidence.push({width,facets:true,filterHistory:true,timeline:true,peopleFilter:true,trashMetadataOnly:true,liftSuppression:true,restore:true,noOverflow:true});
    }
    return evidence;
}
