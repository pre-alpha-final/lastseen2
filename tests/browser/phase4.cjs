// Run against a disposable host: PHASE4_TEST_URL and PHASE4_TEST_KEY are required.
// Board responses are intercepted; this suite never writes to a database.
const { chromium } = require('playwright');
const assert = require('node:assert/strict');
const fs = require('node:fs');
const base = process.env.PHASE4_TEST_URL;
const key = process.env.PHASE4_TEST_KEY;
if (!base || !key) throw new Error('Set PHASE4_TEST_URL and PHASE4_TEST_KEY for a disposable test host.');
const empty = { revision: 0, videos: [], channels: { frequent: [], infrequent: [], archived: [] } };
const now = '2026-10-09T12:00:00Z';
const video = (i, title, thumbnail = null) => ({ id: `00000000-0000-0000-0000-${String(i).padStart(12, '0')}`, title,
  url: `https://example.com/watch/${i}`, thumbnailMode: 'Automatic', automaticThumbnailUrl: thumbnail,
  manualThumbnailUrl: null, createdAt: now, updatedAt: now });
const channel = (i, name, group) => ({ id: `10000000-0000-0000-0000-${String(i).padStart(12, '0')}`, name,
  url: `https://example.com/channel/${i}`, group, createdAt: now, updatedAt: now });
const populated = { revision: 9, videos: [
  video(1, 'Exploring the Backrooms (Full Documentary)', 'https://preview.example.test/preview.svg'),
  video(2, 'My New Linux Setup for 2024'), video(3, 'Elden Ring — All Remembrance Bosses (No Damage)'),
  video(4, 'Building a Custom Mechanical Keyboard', 'https://preview.example.test/broken.jpg'),
  video(5, 'shroud — ranked with friends'), video(6, 'Factorio 2.0 First Look'),
  video(7, 'Japan’s Forgotten Rural Towns'), video(8, 'Notion for Everything — Complete Guide'),
  video(9, 'lofi hip hop radio — beats to relax/study to')], channels: {
    frequent: ['LTT', 'Fireship', 'zackrawrr', 'MKBHD', 'tomscott', 'pirate software'].map((n,i) => channel(i+1,n,'Frequent')),
    infrequent: ['Computerphile', '3Blue1Brown', 'Vox', 'Linus Tech Tips', 'Quin69'].map((n,i) => channel(i+20,n,'Infrequent')),
    archived: ['oldergamer', 'asmongold', 'handtoolrescue', 'lemmino'].map((n,i) => channel(i+40,n,'Archived')) } };
(async () => {
 const browser = await chromium.launch({ headless: true, channel: 'msedge' });
 const context = await browser.newContext({ viewport: { width: 1536, height: 1024 } });
 const page = await context.newPage();
 const errors = [], requests = [];
 page.on('pageerror', e => errors.push(e.message));
 page.on('request', r => requests.push(r));
 let board = empty, failure = null, authFailure = null, delay = 0;
 await page.route('**/api/board', async route => {
   if (delay) await new Promise(r => setTimeout(r, delay));
   if (failure === 'network') return route.abort();
   return route.fulfill({ status: failure || 200, contentType: 'application/json', body: JSON.stringify(failure ? {} : board) });
 });
 await page.route('**/api/auth/check', route => authFailure === 'network' ? route.abort() : authFailure ? route.fulfill({status: authFailure, headers: authFailure === 302 ? {location: 'https://preview.example.test/redirect-target'} : {}}) : route.continue());
 await page.route('https://preview.example.test/**', route => route.request().url().endsWith('broken.jpg') ? route.fulfill({status:404}) : route.fulfill({contentType:'image/svg+xml', body:'<svg xmlns="http://www.w3.org/2000/svg" width="144" height="81"><rect width="144" height="81" fill="#264b62"/><path d="M0 81L50 12L100 81M75 81L115 30L144 81" fill="#83ac96"/></svg>'}));
 const heading = name => page.getByRole('heading', { name, exact: true });
 const unlock = async (value=key) => {
   await page.getByLabel('Access key', {exact:true}).fill(value);
   await page.getByRole('button',{name:'Unlock board',exact:true}).click();
 };
 const unlocked = () => heading('Saved Videos').waitFor();
 fs.mkdirSync('docs/screenshots', {recursive:true});
 await page.goto(base);
 await heading('Unlock board').waitFor();
 await page.screenshot({path:'docs/screenshots/phase4-unlock.png',fullPage:true});
 await unlock('wrong-test-key');
 await page.getByRole('alert').filter({hasText:'not accepted'}).waitFor();
 assert.equal(await page.evaluate(() => sessionStorage.getItem('lastseen2.accessKey')),null);
 authFailure='network'; await unlock();
 await page.getByRole('alert').filter({hasText:'Could not reach'}).waitFor();
 authFailure=302; await unlock();
 await page.getByRole('alert').filter({hasText:'Could not reach'}).waitFor();
 assert(!requests.some(r => r.url().endsWith('/redirect-target')));
 authFailure=429; await unlock();
 await page.getByRole('alert').filter({hasText:'Wait a minute'}).waitFor();
 authFailure=null; await unlock(); await unlocked();
 assert.equal(await page.evaluate(() => sessionStorage.getItem('lastseen2.accessKey')),key);
 await page.screenshot({path:'docs/screenshots/phase4-empty-desktop.png',fullPage:true});
 await page.reload(); await unlocked();
 await page.getByRole('button',{name:'Lock',exact:true}).click();
 await heading('Unlock board').waitFor();
 assert.equal(await page.evaluate(() => sessionStorage.getItem('lastseen2.accessKey')),null);
 await page.reload(); await heading('Unlock board').waitFor();
 board=populated; await unlock(); await unlocked();
 await page.waitForFunction(() => [...document.querySelectorAll('.video-row img')].every(i => i.complete));
 assert.equal(await page.locator('.video-row img').nth(3).getAttribute('src'),'/images/video-placeholder.svg');
 await page.screenshot({path:'docs/screenshots/phase4-populated-desktop.png',fullPage:true});
 for (const width of [390,320]) {
   await page.setViewportSize({width,height:844});
   assert(await page.evaluate(() => document.documentElement.scrollWidth <= innerWidth), `overflow at ${width}`);
   const videos=await page.locator('.videos-panel').boundingBox(), channels=await page.locator('.channels-panel').boundingBox();
   assert(channels.y >= videos.y+videos.height);
   if(width===390) await page.screenshot({path:'docs/screenshots/phase4-populated-mobile.png',fullPage:true});
 }
 // Untrusted provider/bookmark text stays text, even with extreme lengths.
 board={...populated,videos:[video(10,'<img src=x onerror=alert(1)> '+ 'LongTitle'.repeat(50))]};
 await page.getByRole('button',{name:'Refresh',exact:true}).click();
 await page.getByText('<img src=x onerror=alert(1)>',{exact:false}).waitFor();
 assert.equal(await page.locator('.video-copy img').count(),0);
 assert(await page.evaluate(() => document.documentElement.scrollWidth <= innerWidth));
 failure=503; await page.getByRole('button',{name:'Refresh',exact:true}).click();
 await page.getByRole('alert').filter({hasText:'storage is unavailable'}).waitFor();
 assert.equal(await page.evaluate(() => sessionStorage.getItem('lastseen2.accessKey')),key);
 failure='network'; await page.getByRole('button',{name:'Retry loading board'}).click();
 await page.getByRole('alert').filter({hasText:'Could not reach'}).waitFor();
 failure=null; board=empty; await page.getByRole('button',{name:'Retry loading board'}).click(); await unlocked();
 await page.screenshot({path:'docs/screenshots/phase4-empty-mobile.png',fullPage:true});
 failure=401; await page.getByRole('button',{name:'Refresh',exact:true}).click();
 await heading('Unlock board').waitFor();
 assert.equal(await page.evaluate(() => sessionStorage.getItem('lastseen2.accessKey')),null);
 failure=null; await unlock(); await unlocked();
 // A response arriving after Lock must never resurrect protected content.
 delay=800; await page.getByRole('button',{name:'Refresh',exact:true}).click();
 await page.getByRole('button',{name:'Lock',exact:true}).click();
 await page.waitForTimeout(1100);
 assert.equal(await heading('Saved Videos').count(),0); delay=0;
 await unlock(); await unlocked();
 authFailure='network'; await page.reload();
 await page.getByRole('alert').filter({hasText:'Could not reach'}).waitFor();
 assert.equal(await page.evaluate(() => sessionStorage.getItem('lastseen2.accessKey')),key);
 authFailure=null; await page.getByRole('button',{name:'Retry saved session'}).click(); await unlocked();
 authFailure=401; await page.reload();
 await page.getByRole('alert').filter({hasText:'not accepted'}).waitFor();
 assert.equal(await page.evaluate(() => sessionStorage.getItem('lastseen2.accessKey')),null);
 // Native keyboard form submission and a visible focus indicator.
 authFailure=null;
 await page.getByLabel('Access key',{exact:true}).fill(key);
 await page.keyboard.press('Tab');
 assert.equal(await page.evaluate(() => document.activeElement.textContent),'Unlock board');
 assert.equal(await page.evaluate(() => getComputedStyle(document.activeElement).outlineStyle),'solid');
 await page.keyboard.press('Enter'); await unlocked();
 for(const request of requests) {
   assert(!request.url().includes(key),'key in URL');
   if(request.headers()['x-api-key']) {
     assert.equal(new URL(request.url()).origin,new URL(base).origin);
     assert(new URL(request.url()).pathname.startsWith('/api/'));
   }
 }
 assert(requests.some(r => r.url().includes('preview.example.test')));
 assert(!requests.some(r => /fonts.googleapis|fonts.gstatic|cdn.jsdelivr/.test(r.url())));
 assert.deepEqual(errors,[]);
 await browser.close();
 console.log('Phase 4 browser checks passed: authentication, restoration, Lock race, errors/retries, keyboard, responsive layout, safe text, image fallback, key isolation, and screenshots.');
})().catch(e => { console.error(e); process.exit(1); });
