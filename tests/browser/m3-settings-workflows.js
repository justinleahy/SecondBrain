// Run against the isolated KnowledgeBrowserQualification fixture.
async (page) => {
    const origin=new URL(page.url()).origin;
    await page.evaluate(()=>sessionStorage.setItem('m3-settings-check',JSON.stringify({status:'running'})));
    try {
    const require=(value,message)=>{if(!value)throw new Error(message);};
    const login=await page.evaluate(async()=>{
        const token=await(await fetch('/auth/antiforgery')).json();
        return(await fetch('/auth/login',{method:'POST',headers:{'Content-Type':'application/json',[token.headerName]:token.token},body:JSON.stringify({password:'test-password'})})).status;
    });
    require(login===200,'Fixture login failed');
    await page.goto(origin+'/settings');
    await page.getByRole('heading',{name:'Providers and role bindings',exact:true}).waitFor();
    require(await page.getByRole('button',{name:'Edit privacy',exact:true}).isDisabled(),'Configuration editing did not require step-up');
    await page.getByLabel('Password',{exact:true}).fill('test-password');
    await Promise.all([page.waitForEvent('load'),page.getByRole('button',{name:'Confirm password',exact:true}).click()]);
    const evidence=[];
    for(const width of [1280,390]) {
        await page.setViewportSize({width,height:900});
        await page.getByRole('button',{name:'Edit privacy',exact:true}).click();
        await page.waitForFunction(()=>document.getElementById('settings-editor')?.contains(document.activeElement));
        await page.getByLabel('Restrict model calls to local providers',{exact:true}).uncheck();
        await page.getByRole('button',{name:'Save settings',exact:true}).click();
        await page.getByRole('region',{name:'Edit settings',exact:true}).waitFor({state:'detached'});
        require(await page.getByRole('region',{name:'Privacy',exact:true}).innerText().then(text=>text.includes('Local-only mode: Off')),'Privacy change did not reload');
        await page.getByRole('button',{name:'Edit privacy',exact:true}).click();
        await page.getByLabel('Restrict model calls to local providers',{exact:true}).check();
        await page.getByRole('button',{name:'Save settings',exact:true}).click();
        await page.getByRole('region',{name:'Edit settings',exact:true}).waitFor({state:'detached'});
        await page.getByRole('button',{name:'Edit daily budget',exact:true}).click();
        const draft=page.getByLabel('Configuration values (JSON object)',{exact:true});
        await draft.fill(JSON.stringify({dailyTokens:123456+width}));
        await page.getByRole('button',{name:'Save settings',exact:true}).click();
        await page.getByRole('region',{name:'Edit settings',exact:true}).waitFor({state:'detached'});
        const saved=await page.evaluate(async()=>await(await fetch('/configuration')).json());
        require(saved.budgets.dailyTokens===123456+width,'Budget was not persisted');
        await page.getByRole('button',{name:'Edit daily budget',exact:true}).click();
        await draft.fill(JSON.stringify({dailyTokens:999999}));
        const concurrent=await page.evaluate(async version=>window.secondBrainApi.send('/configuration','PUT',{version,section:'budgets',value:{dailyTokens:234567}}),saved.version);
        require(concurrent.status===200,'Concurrent settings edit failed');
        await page.getByRole('button',{name:'Save settings',exact:true}).click();
        await page.getByText('Settings changed while you were editing. Your draft has been retained.',{exact:true}).waitFor();
        require((await draft.inputValue()).includes('999999'),'Settings conflict lost the draft');
        require(await page.getByRole('button',{name:'Save settings',exact:true}).isDisabled(),'Conflicting save remained enabled');
        await page.getByRole('button',{name:'Discard draft and reload settings',exact:true}).click();
        await page.getByRole('region',{name:'Edit settings',exact:true}).waitFor({state:'detached'});
        await page.getByRole('button',{name:'Edit role bindings',exact:true}).click();
        const models=JSON.parse(await draft.inputValue());
        models.chat.limits.maxOutputTokens=width===1280?512:640;
        await draft.fill(JSON.stringify(models));
        await page.getByRole('button',{name:'Save settings',exact:true}).click();
        await page.getByRole('region',{name:'Edit settings',exact:true}).waitFor({state:'detached'});
        const roles=await page.evaluate(async()=>(await(await fetch('/configuration')).json()).roles);
        require(roles.find(role=>role.role==='chat').limits.maxOutputTokens===(width===1280?512:640),'Resolved role limits did not change');
        const providerName='fixture'+width;
        const endpoint=await page.evaluate(async()=>(await(await fetch('/configuration')).json()).providers.find(provider=>provider.name==='mock').endpoint);
        await page.getByRole('button',{name:'Add provider',exact:true}).click();
        await page.getByLabel('Provider name',{exact:true}).fill(providerName);
        await page.getByLabel('Adapter kind',{exact:true}).fill('openai_compatible');
        await page.getByLabel('Endpoint',{exact:true}).fill(endpoint);
        await page.getByLabel('Trust this provider',{exact:true}).check();
        await page.getByRole('button',{name:'Save settings',exact:true}).click();
        await page.getByRole('button',{name:'Edit '+providerName,exact:true}).click();
        await page.getByLabel('Trust this provider',{exact:true}).uncheck();
        await page.getByRole('button',{name:'Save settings',exact:true}).click();
        await page.getByRole('region',{name:'Edit settings',exact:true}).waitFor({state:'detached'});
        const confirmation=page.waitForEvent('dialog').then(async dialog=>{require(dialog.type()==='confirm','Expected provider removal confirmation');await dialog.accept();});
        await page.getByRole('button',{name:'Remove '+providerName,exact:true}).click();
        await confirmation;
        await page.getByRole('button',{name:'Edit '+providerName,exact:true}).waitFor({state:'detached'});
        await page.getByLabel('Key name',{exact:true}).fill('Browser bridge '+width);
        await page.getByRole('button',{name:'Create API key',exact:true}).click();
        await page.getByRole('textbox',{name:'New API key',exact:true}).waitFor();
        const key=await page.getByRole('textbox',{name:'New API key',exact:true}).inputValue();
        const access=await page.evaluate(async key=>({read:(await fetch('/types',{headers:{Authorization:'Bearer '+key}})).status,admin:(await fetch('/configuration',{headers:{Authorization:'Bearer '+key}})).status}),key);
        require(access.read===200&&access.admin===403,'Default key scopes were not read-only');
        await page.getByRole('button',{name:'Dismiss key',exact:true}).click();
        await page.getByRole('textbox',{name:'New API key',exact:true}).waitFor({state:'detached'});
        require(!(await page.locator('main').innerText()).includes(key),'Dismissed key remained visible');
        require(await page.evaluate(()=>document.documentElement.scrollWidth<=innerWidth),'Settings layout overflow');
        evidence.push({width,stepUp:true,privacyReload:true,budgetPersistence:true,conflictDraftPreserved:true,roleCapabilities:true,roleLimitReload:true,providerCreateEditRemove:true,oneTimeReadOnlyKey:true,noOverflow:true});
    }
    await page.goto(origin+'/settings');
    await page.getByRole('heading',{name:'Providers and role bindings',exact:true}).waitFor();
    require((await page.getByRole('region',{name:'Limits and preferences',exact:true}).innerText()).includes('234567'),'Saved settings did not survive a full page load');
    await page.evaluate(evidence=>sessionStorage.setItem('m3-settings-check',JSON.stringify({status:'passed',checks:evidence})),evidence);
    return evidence;
    } catch(error) {
        await page.evaluate(message=>sessionStorage.setItem('m3-settings-check',JSON.stringify({status:'failed',error:message})),String(error));
        throw error;
    }
}
