// Real transport interruption/rejoin; rejected-state controls are checked separately.
async (page) => {
    const origin=new URL(page.url()).origin;
    const require=(ok,message)=>{if(!ok)throw new Error(message);};
    const evidence=[];
    for(const width of [1280,390]) {
        const context=await page.context().browser().newContext({viewport:{width,height:900}});
        try {
            const login=await context.request.post(origin+'/auth/login',{headers:{Origin:origin},data:{password:'test-password'}});
            require(login.status()===200,'Fixture login failed');
            const test=await context.newPage(); let socket,server;
            await test.routeWebSocket(/\/_blazor\?/, route=> { socket=route;server=route.connectToServer(); });
            await test.goto(origin+'/capture');
            await test.getByRole('button',{name:'Use plain text editor',exact:true}).click();
            await test.locator('textarea.note-editor').waitFor();
            const draft='Retained during real connection drop '+width;
            await test.getByLabel('Title',{exact:true}).fill('Reconnect note '+width);
            await test.locator('textarea.note-editor').fill(draft);
            require(socket&&server,'No proxied Blazor socket');
            await context.setOffline(true);
            await socket.close({code:1011,reason:'Qualification connection interruption'});
            await server.close({code:1011,reason:'Qualification connection interruption'});
            const modal=test.locator('#components-reconnect-modal');
            await test.waitForFunction(()=>document.getElementById('components-reconnect-modal').open);
            require((await modal.innerText()).includes('Reconnecting'),'Reconnect status missing');
            await context.setOffline(false);
            await test.waitForFunction(()=>!document.getElementById('components-reconnect-modal').open,{},{timeout:30000});
            require(await test.locator('textarea.note-editor').inputValue()===draft,'Reconnect lost draft');
            await test.getByRole('button',{name:'Save note',exact:true}).click();
            await test.getByRole('heading',{name:'Reconnect note '+width,exact:true}).waitFor();
            await test.getByText(draft,{exact:true}).waitFor();
            // Exercise the explicit recovery UI without claiming a real daemon restart.
            await test.evaluate(()=> {
                const element=document.getElementById('components-reconnect-modal');
                element.className='connection-dialog components-reconnect-rejected';
                element.dispatchEvent(new CustomEvent('components-reconnect-state-changed',{detail:{state:'rejected'}}));
            });
            await modal.getByRole('button',{name:'Reload page',exact:true}).waitFor();
            require((await modal.innerText()).includes('Copy any unsaved draft'),'Recovery warning missing');
            require(await test.evaluate(()=>document.documentElement.scrollWidth<=innerWidth),'Reconnect dialog overflow');
            await modal.getByRole('button',{name:'Keep page open',exact:true}).click();
            require(!await modal.evaluate(element=>element.open),'Keep page open did not dismiss');
            evidence.push({width,transportInterrupted:true,rejoined:true,draftRetainedAndSaved:true,rejectedStateControls:true,actualDaemonRestartTested:false});
        } finally {await context.close();}
    }
    return {status:'passed',reconnect:evidence};
}
