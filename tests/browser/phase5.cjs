// Requires an empty, isolated disposable Mongo board; never use an application database.
const { chromium } = require('playwright');
const assert = require('node:assert/strict');
const screenshotDir = process.env.BROWSER_SCREENSHOT_DIR || 'docs/screenshots';
require('node:fs').mkdirSync(screenshotDir, {recursive:true});
const base = process.env.PHASE5_TEST_URL, key = process.env.PHASE5_TEST_KEY;
if (!base || !key || process.env.PHASE5_ISOLATED !== 'yes') throw Error('Set PHASE5_TEST_URL, PHASE5_TEST_KEY and PHASE5_ISOLATED=yes for an empty disposable test database.');
(async () => {
 const browser = await chromium.launch({headless:true, channel:'msedge'});
 try {
 const page = await browser.newPage({viewport:{width:1536,height:1024}});
 const errors=[],requests=[];
 page.on('pageerror',e=>errors.push(e.message)); page.on('request',r=>requests.push(r));
 const api = async (path='/board',method='GET',data) => {
  const response=await page.request.fetch(base+'/api'+path,{method,headers:{'X-Api-Key':key},data});
  assert.equal(response.status(),200); return response.json();
 };
 assert.deepEqual((await api()).videos,[]); assert.deepEqual((await api()).channels,{frequent:[],infrequent:[],archived:[]});
 const button = name=>page.getByRole('button',{name,exact:true});
 const dialog=page.getByRole('dialog');
 const field=name=>dialog.getByLabel(name,{exact:true});
 const closed=()=>page.locator('dialog').waitFor({state:'detached'});
 const save=async()=>{await button('Save').click();await closed();};
 const cancel=async()=>{await button('Cancel').click();await closed();};
 await page.route('https://preview.example.test/**',r=>r.fulfill({status:404}));
 await page.goto(base);
 await page.getByLabel('Access key',{exact:true}).fill(key);await button('Unlock board').click();
 await button('+ Add Video').waitFor();
 // Native modal focus trap, Escape cancellation and restoration.
 await button('+ Add Video').click();await dialog.waitFor();
 await field('URL').fill('https://example.com/cancel');await field('Title').fill('Canceled');
 for(let i=0;i<12;i++){await page.keyboard.press('Tab');assert(await page.evaluate(()=>document.querySelector('dialog').contains(document.activeElement)));}
 await page.keyboard.press('Escape');await closed();
 assert.equal(await button('+ Add Video').evaluate(e=>e===document.activeElement),true);
 assert.equal((await api()).revision,0);
 const addVideo=async(title)=>{await button('+ Add Video').click();await field('URL').fill('https://example.com/video');await field('Title').fill(title);await save();};
 await addVideo('First video');await addVideo('Second video');
 assert.deepEqual((await api()).videos.map(v=>v.title),['Second video','First video']);
 await button('Edit video: First video').click();
 await field('Thumbnail mode').selectOption('Manual');await field('Manual thumbnail URL').fill('https://preview.example.test/broken.jpg');
 await field('Title').fill('Edited video');await save();
 assert.equal((await api()).videos[0].title,'Edited video');
 await page.waitForFunction(()=>document.querySelector('.video-row img').getAttribute('src')==='/images/video-placeholder.svg');
 await button('Edit video: Edited video').click();await field('Thumbnail mode').selectOption('Automatic');await save();
 assert.equal((await api()).videos[0].manualThumbnailUrl,'https://preview.example.test/broken.jpg');
 await button('Edit video: Edited video').click();await button('Clear manual thumbnail').click();await save();
 assert.equal((await api()).videos[0].manualThumbnailUrl,null);
 const addChannel=async(name)=>{await button('+ Add Channel').click();assert.equal(await field('Group').inputValue(),'Frequent');await field('Name').fill(name);await field('URL').fill('https://example.com/channel');await save();};
 await addChannel('First channel');await addChannel('Second channel');
 await button('Edit channel: First channel').click();await field('Name').fill('Edited channel');await save();
 assert.deepEqual((await api()).channels.frequent.map(c=>c.name),['Second channel','Edited channel']);
 await button('Edit channel: Edited channel').click();await field('Group').selectOption('Archived');await save();
 assert.equal((await api()).channels.archived[0].name,'Edited channel');
 // Keyboard submits channel form.
 await button('+ Add Channel').click();await field('Name').fill('Keyboard channel');await field('URL').fill('https://example.com/keyboard');
 await page.keyboard.press('Enter');await closed();assert.equal((await api()).channels.frequent[0].name,'Keyboard channel');
 const before=await api();
 await button('Delete video: Edited video').click();assert(await button('Cancel').evaluate(e=>e===document.activeElement));await cancel();
 await button('Delete channel: Edited channel').click();await dialog.waitFor();await page.keyboard.press('Escape');await closed();assert.deepEqual(await api(),before);
 await button('Delete video: Edited video').click();await button('Delete bookmark').click();await closed();
 await button('Delete channel: Edited channel').click();await button('Delete bookmark').focus();await page.keyboard.press('Enter');await closed();
 await page.reload();await button('Edit video: Second video').waitFor();assert.equal(await button('Edit channel: Edited channel').count(),0);
 // Real stale revision from another writer, draft retained, explicit review and resubmit.
 await button('Edit video: Second video').click();await field('Title').fill('My draft');
 let board=await api();await api('/channels','POST',{expectedRevision:board.revision,name:'Concurrent channel',url:'https://example.com/concurrent',group:'Frequent'});
 await button('Save').click();await button('Reload and review').waitFor();assert.equal(await field('Title').inputValue(),'My draft');assert(await button('Save').isDisabled());
 await button('Reload and review').click();await page.getByText('Latest saved values:',{exact:false}).waitFor();await save();assert.equal((await api()).videos[0].title,'My draft');
 // Recoverable transport/server errors keep input and force review for uncertain writes.
 await button('Edit video: My draft').click();await field('Title').fill('Recovered draft');
 await page.route('**/api/videos/*',r=>r.fulfill({status:503}));await button('Save').click();await button('Reload and review').waitFor();
 assert.equal(await field('Title').inputValue(),'Recovered draft');await page.unroute('**/api/videos/*');await button('Reload and review').click();await page.getByText('Latest saved values:',{exact:false}).waitFor();await save();
 // Slow metadata cannot replace touched text or apply after URL changes.
 let metadata={status:'Available',title:'Suggested title',thumbnailUrl:null,message:null}, delay=0;
 await page.route('**/api/metadata',async r=>{const result={...metadata};await new Promise(resolve=>setTimeout(resolve,delay));await r.fulfill({json:result});});
 await button('+ Add Video').click();await field('URL').fill('https://youtu.be/dQw4w9WgXcQ');await button('Look up metadata').click();
 await page.waitForFunction(()=>document.querySelector('#bookmark-name').value==='Suggested title');
 delay=300;await button('Look up metadata').click();await field('Title').fill('My own title');await page.waitForTimeout(400);assert.equal(await field('Title').inputValue(),'My own title');
 await button('Look up metadata').click();await field('URL').fill('https://example.com/new');await page.waitForTimeout(400);assert.equal(await field('Title').inputValue(),'My own title');
 metadata={status:'Unavailable',title:null,thumbnailUrl:null};delay=0;await button('Look up metadata').click();await page.getByText('No metadata available.',{exact:false}).waitFor();
 await page.unroute('**/api/metadata');await page.route('**/api/metadata',r=>r.abort());await button('Look up metadata').click();await page.getByText('Metadata lookup failed.',{exact:false}).waitFor();
 await field('URL').fill('https://user:password@example.com/video');await button('Save').click();await page.getByRole('alert').filter({hasText:'without credentials'}).waitFor();await field('URL').fill('https://example.com/new');
 await field('Thumbnail mode').selectOption('Manual');await field('Manual thumbnail URL').fill('https://preview.example.test/broken.jpg');
 await page.waitForFunction(()=>document.querySelector('.preview-line img').getAttribute('src')==='/images/video-placeholder.svg');
 await page.screenshot({path:`${screenshotDir}/phase5-video-dialog.png`,fullPage:true});
 await page.setViewportSize({width:320,height:844});assert(await page.evaluate(()=>document.documentElement.scrollWidth<=innerWidth));await page.screenshot({path:`${screenshotDir}/phase5-mobile-dialog.png`,fullPage:true});
 // Duplicate submits while a save is in flight produce one request.
 let writes=0;await page.route('**/api/videos',async r=>{writes++;await new Promise(resolve=>setTimeout(resolve,300));await r.continue();});
 await button('Save').click();await page.keyboard.press('Enter');await closed();assert.equal(writes,1);await page.unroute('**/api/videos');
 await page.setViewportSize({width:1536,height:1024});await page.screenshot({path:`${screenshotDir}/phase5-board.png`,fullPage:true});
 for(const width of [390,320]){await page.setViewportSize({width,height:844});assert(await page.evaluate(()=>document.documentElement.scrollWidth<=innerWidth));}
 // Deleted elsewhere: stale deletion cannot be retried against a missing item.
 await button('Delete video: My own title').click();board=await api();const removed=board.videos.find(v=>v.title==='My own title');await api(`/videos/${removed.id}?expectedRevision=${board.revision}`,'DELETE');
 await button('Delete bookmark').click();await button('Reload and review').click();await page.getByText('This bookmark has been deleted.',{exact:false}).waitFor();assert(await button('Delete bookmark').isDisabled());await cancel();
 // URL-free items render as text, persist, and can gain/lose a link through editing.
 await page.setViewportSize({width:1536,height:1024});
 for(const [kind,title] of [['Video','Video without URL'],['Channel','Channel without URL']]) {
  await button('+ Add '+kind).click();await field(kind==='Video'?'Title':'Name').fill(title);
  if(kind==='Video') assert(await button('Look up metadata').isDisabled());
  await save();
  const item=()=>page.locator(kind==='Video'?'.video-row':'.channel-list li').filter({hasText:title});
  assert.equal(await item().locator('a').count(),0);
  await button('Edit '+kind.toLowerCase()+': '+title).click();await field('URL').fill('https://example.com/item');await save();
  assert.equal(await item().locator('a').getAttribute('href'),'https://example.com/item');
  await button('Edit '+kind.toLowerCase()+': '+title).click();await field('URL').fill('');await save();
  await page.reload();await button('+ Add Video').waitFor();assert.equal(await item().locator('a').count(),0);
  const persisted=await api();assert.equal((kind==='Video'?persisted.videos:persisted.channels.frequent).find(v=>(v.title||v.name)===title).url,'');
  await button('Delete '+kind.toLowerCase()+': '+title).click();await button('Delete bookmark').click();await closed();
 }
 // Unauthorized mutation clears the modal, board and stored session.
 await button('Edit video: Recovered draft').click();await page.route('**/api/videos/*',r=>r.fulfill({status:401}));await button('Save').click();
 await page.getByRole('heading',{name:'Unlock board'}).waitFor();await closed();assert.equal(await page.evaluate(()=>sessionStorage.getItem('lastseen2.accessKey')),null);
 for(const r of requests){assert(!r.url().includes(key));if(r.headers()['x-api-key']){assert.equal(new URL(r.url()).origin,new URL(base).origin);assert(new URL(r.url()).pathname.startsWith('/api/'));}}
 assert.deepEqual(errors,[]);
 console.log('Phase 5 passed: real Mongo CRUD/order/reload, cancel, keyboard/modal focus, manual preservation/clearing, conflict/review, save failure, metadata races/fallback, duplicate submission, responsive screenshots, missing item, unauthorized and key isolation.');
 } finally {await browser.close();}
})().catch(e=>{console.error(e);process.exit(1);});
