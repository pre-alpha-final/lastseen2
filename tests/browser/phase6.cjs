// Only run against an empty, explicitly isolated disposable MongoDB board.
const { chromium } = require('playwright');
const assert = require('node:assert/strict');
const base = process.env.PHASE6_TEST_URL, key = process.env.PHASE6_TEST_KEY;
if (!base || !key || process.env.PHASE6_ISOLATED !== 'yes') throw Error('Set PHASE6_TEST_URL, PHASE6_TEST_KEY and PHASE6_ISOLATED=yes.');
(async () => {
 const browser = await chromium.launch({headless:true,channel:'msedge'});
 try {
 const context = await browser.newContext({viewport:{width:1536,height:1024},hasTouch:true});
 const page = await context.newPage(), errors=[];
 page.on('pageerror',e=>errors.push(e.message));
 page.on('request',r=>{assert(!r.url().includes(key));if(r.headers()['x-api-key']) assert.equal(new URL(r.url()).origin,new URL(base).origin);});
 const api=async(path='/board',method='GET',data)=>{const r=await page.request.fetch(base+'/api'+path,{method,headers:{'X-Api-Key':key},data});assert.equal(r.status(),200);return r.json();};
 const initial=await api();assert.equal(initial.revision,0);assert.deepEqual(initial.videos,[]);assert.deepEqual(initial.channels,{frequent:[],infrequent:[],archived:[]});
 let b=initial;
 for(const title of ['Video A','Video B','Video C']) b=await api('/videos','POST',{expectedRevision:b.revision,title,url:'https://example.com/video',thumbnailMode:'Automatic',manualThumbnailUrl:null});
 for(const name of ['Channel A','Channel B','Channel C']) b=await api('/channels','POST',{expectedRevision:b.revision,name,url:'https://example.com/channel',group:'Frequent'});
 const button=name=>page.getByRole('button',{name,exact:true});
 const ready=()=>page.locator('.board-grid[data-order-disabled="false"]').waitFor();
 const unlock=async()=>{await page.getByLabel('Access key',{exact:true}).fill(key);await button('Unlock board').click();await ready();};
 const videoNames=()=>page.locator('.video-row .bookmark-title').evaluateAll(es=>es.map(e=>e.firstChild.textContent));
 const saved=async(revision)=>{await page.waitForFunction(r=>document.querySelector('.board-grid')?.dataset.revision===String(r+1),revision);await page.waitForFunction(()=>document.querySelector('.order-status')?.textContent==='Order saved.');await ready();assert.equal((await api()).revision,revision+1);};
 await page.goto(base);await unlock();
 // Keyboard move uses the same complete reorder endpoint.
 await button('Move Video C down').focus();await page.keyboard.press('Enter');await saved(b.revision);b=await api();
 assert.deepEqual(await videoNames(),['Video B','Video C','Video A']);
 await page.reload();await ready();assert.deepEqual(await videoNames(),b.videos.map(v=>v.title));
 const drag=async(source,target,edge=.85)=>{
  await source.scrollIntoViewIfNeeded();const a=await source.boundingBox(),z=await target.boundingBox();
  await page.mouse.move(a.x+a.width/2,a.y+a.height/2);await page.mouse.down();
  await page.mouse.move(a.x+a.width/2+8,a.y+a.height/2+8,{steps:3});await page.waitForTimeout(180);
  await page.mouse.move(z.x+z.width/2,z.y+z.height*edge,{steps:18});await page.waitForTimeout(250);await page.mouse.up();
 };
 await drag(page.locator('.video-row').first().locator('.drag-handle'),page.locator('.video-row').last());await saved(b.revision);b=await api();
 assert.deepEqual(b.videos.map(v=>v.title),['Video C','Video A','Video B']);assert.deepEqual(await videoNames(),b.videos.map(v=>v.title));
 // Same-group drag, then empty destination and back out of that group.
 const group=g=>page.locator(`[data-group="${g}"]`);
 await drag(group('Frequent').locator('.drag-handle').first(),group('Frequent').locator('li').last());await saved(b.revision);b=await api();
 assert.deepEqual(b.channels.frequent.map(c=>c.name),['Channel B','Channel A','Channel C']);
 await drag(group('Frequent').locator('.drag-handle').first(),group('Archived'));await saved(b.revision);b=await api();assert.equal(b.channels.archived[0].name,'Channel B');
 assert.equal(await page.locator('.archived .count').innerText(),'1');
 await drag(group('Archived').locator('.drag-handle'),group('Infrequent'));await saved(b.revision);b=await api();assert.equal(b.channels.archived.length,0);assert.equal(b.channels.infrequent[0].name,'Channel B');
 // Keyboard group select and boundary controls.
 await page.getByLabel('Move Channel B to group').selectOption('Frequent');await saved(b.revision);b=await api();assert.equal(b.channels.frequent[0].name,'Channel B');
 assert(await button('Move Channel B up').isDisabled());await button('Move Channel B down').focus();await page.keyboard.press('Enter');await saved(b.revision);b=await api();
 // Touch drag uses browser touch events, not an interop shortcut.
 const cdp=await context.newCDPSession(page);
 const touchDrag=async(source,target)=>{
  await source.scrollIntoViewIfNeeded();const a=await source.boundingBox(),z=await target.boundingBox();const x=a.x+a.width/2,y=a.y+a.height/2;
  await cdp.send('Input.dispatchTouchEvent',{type:'touchStart',touchPoints:[{x,y}]});await page.waitForTimeout(180);
  for(let i=1;i<=16;i++){await cdp.send('Input.dispatchTouchEvent',{type:'touchMove',touchPoints:[{x:x+(z.x+z.width/2-x)*i/16,y:y+(z.y+z.height*.8-y)*i/16}]});await page.waitForTimeout(20);}
  await page.waitForTimeout(250);await cdp.send('Input.dispatchTouchEvent',{type:'touchEnd',touchPoints:[]});
 };
 await touchDrag(page.locator('.video-row').first().locator('.drag-handle'),page.locator('.video-row').last());await saved(b.revision);b=await api();
 await touchDrag(group('Frequent').locator('.drag-handle').first(),group('Archived'));await saved(b.revision);b=await api();assert.equal(b.channels.archived.length,1);
 // Edit rules remain distinct after manual ordering.
 const editedVideo=b.videos.at(-1);await button('Edit video: '+editedVideo.title).click();await button('Save').click();await page.locator('dialog').waitFor({state:'detached'});await ready();b=await api();assert.equal(b.videos[0].id,editedVideo.id);
 const editedChannel=b.channels.frequent.at(-1),oldIds=b.channels.frequent.map(c=>c.id);
 await button('Edit channel: '+editedChannel.name).click();await button('Save').click();await page.locator('dialog').waitFor({state:'detached'});await ready();b=await api();assert.deepEqual(b.channels.frequent.map(c=>c.id),oldIds);
 // Links and edit actions do not initiate ordering.
 const popup=page.waitForEvent('popup');await page.locator('.video-row .bookmark-title').first().click();await (await popup).close();assert.equal((await api()).revision,b.revision);
 // Duplicate moves disabled during an in-flight save.
 let writes=0;await page.route('**/api/videos/reorder',async r=>{writes++;await new Promise(resolve=>setTimeout(resolve,400));await r.continue();});
 await button('Move '+b.videos[0].title+' down').click();await page.waitForFunction(()=>document.querySelector('.order-status').textContent==='Saving order…');
 assert(await button('+ Add Video').isDisabled());assert(await button('Refresh').isDisabled());await page.keyboard.press('Enter');await saved(b.revision);b=await api();assert.equal(writes,1);await page.unroute('**/api/videos/reorder');
 // Real concurrent writer triggers conflict; no attempted local order remains.
 b=await api('/channels','POST',{expectedRevision:b.revision,name:'Other session',url:'https://example.com/other',group:'Infrequent'});
 await button('Move '+b.videos[0].title+' down').click();await button('Reload and review').waitFor();assert.equal(await page.locator('.board-grid').count(),0);
 await button('Reload and review').click();await ready();assert.deepEqual(await videoNames(),b.videos.map(v=>v.title));
 // Both rejected and committed-but-lost responses require explicit reload.
 for(const committed of [false,true]){
  await page.route('**/api/videos/reorder',async r=>{if(committed) await r.fetch();await r.fulfill({status:503});});
  await drag(page.locator('.video-row').first().locator('.drag-handle'),page.locator('.video-row').last());await button('Reload and review').waitFor();assert.equal(await page.locator('.board-grid').count(),0);
  await page.unroute('**/api/videos/reorder');await button('Reload and review').click();await ready();b=await api();assert.deepEqual(await videoNames(),b.videos.map(v=>v.title));
 }
 await page.reload();await ready();assert.deepEqual(await videoNames(),b.videos.map(v=>v.title));
 await page.screenshot({path:'docs/screenshots/phase6-desktop.png',fullPage:true});
 for(const width of [390,320]){await page.setViewportSize({width,height:844});assert(await page.evaluate(()=>document.documentElement.scrollWidth<=innerWidth));}
 await page.screenshot({path:'docs/screenshots/phase6-mobile.png',fullPage:true});
 // Unauthorized channel move clears the board and session.
 await page.route('**/api/channels/*/move',r=>r.fulfill({status:401}));
 await page.getByLabel('Move '+b.channels.frequent[0].name+' to group').selectOption('Archived');await page.getByRole('heading',{name:'Unlock board'}).waitFor();assert.equal(await page.evaluate(()=>sessionStorage.getItem('lastseen2.accessKey')),null);
 await page.unroute('**/api/channels/*/move');await unlock();await page.setViewportSize({width:1536,height:1024});
 // A late successful response after Lock must not reveal data again.
 await page.route('**/api/videos/reorder',async r=>{const response=await r.fetch();await new Promise(resolve=>setTimeout(resolve,500));await r.fulfill({response});});
 await button('Move '+b.videos[0].title+' down').click();await button('Lock').click();await page.waitForTimeout(700);assert.equal(await page.locator('.board-grid').count(),0);assert.equal(await page.evaluate(()=>sessionStorage.getItem('lastseen2.accessKey')),null);
 assert.deepEqual(errors,[]);console.log('Phase 6 passed: keyboard/pointer/touch, empty groups/counts, reload persistence, edit rules, duplicate suppression, conflicts, rejected/uncertain saves, 401, Lock race and mobile layout.');
 } finally {await browser.close();}
})().catch(e=>{console.error(e);process.exit(1);});
