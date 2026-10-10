// Run from the repository root. Owns only fresh localhost test processes/data.
const assert = require('node:assert/strict');
const fs = require('node:fs');
const path = require('node:path');
const net = require('node:net');
const {spawn} = require('node:child_process');
const {randomUUID} = require('node:crypto');
const {chromium} = require('playwright');
const root = process.cwd();
const mongod = process.env.LASTSEEN2_TEST_MONGOD;
if (!mongod || !fs.existsSync(mongod)) throw Error('Set LASTSEEN2_TEST_MONGOD to a mongod executable.');
const output = path.join(root, 'artifacts', 'release-' + randomUUID());
fs.mkdirSync(output, {recursive:true});
const screenshots = path.join(output, 'screenshots');
const publish = path.join(output, 'publish');
const dbpath = path.join(output, 'mongo');
fs.mkdirSync(dbpath);
const env = {...process.env, ASPNETCORE_ENVIRONMENT:'Production', DOTNET_ENVIRONMENT:'Production',
  Authentication__AccessKey:randomUUID(), MongoDb__Collection:'lastseen2', BROWSER_SCREENSHOT_DIR:screenshots};
let mongo, host, browser, base, mongoPort, hostPort, testCount;
const delay = ms => new Promise(resolve => setTimeout(resolve, ms));
async function port() {
  const server = net.createServer();
  await new Promise((resolve,reject) => {server.once('error',reject);server.listen(0,'127.0.0.1',resolve);});
  const value = server.address().port;
  await new Promise(resolve => server.close(resolve));
  return value;
}
function start(command, args, cwd, label) {
  const log = fs.openSync(path.join(output,label+'.log'),'a');
  try {
    const child = spawn(command,args,{cwd,env,windowsHide:true,stdio:['ignore',log,log]});
    child.on('error',error => {child.startError=error;});
    return child;
  } finally {fs.closeSync(log);}
}
async function stop(child) {
  if (!child || !child.pid || child.exitCode !== null || child.signalCode !== null) return;
  const done = new Promise(resolve => child.once('exit',resolve));
  child.kill();
  await done;
}
async function run(command,args,label) {
  console.log(label);
  const child=start(command,args,root,label);
  const code=await new Promise((resolve,reject)=>{child.once('error',reject);child.once('exit',resolve);});
  assert.equal(code,0,label+' failed; inspect '+path.join(output,label+'.log'));
}
async function waitFor(child,probe,label) {
  for(let i=0;i<120;i++) {
    if(child.startError) throw child.startError;
    assert.equal(child.exitCode,null,label+' exited; inspect logs');
    try {if(await probe()) return;} catch {}
    await delay(250);
  }
  throw Error(label+' did not become ready');
}
async function startMongo() {
  mongo=start(mongod,['--dbpath',dbpath,'--bind_ip','127.0.0.1','--port',String(mongoPort)],root,'mongo');
  await waitFor(mongo,()=>new Promise(resolve=>{
    const socket=net.connect(mongoPort,'127.0.0.1');
    socket.once('connect',()=>{socket.destroy();resolve(true);});
    socket.once('error',()=>{socket.destroy();resolve(false);});
  }),'MongoDB');
}
async function startHost(fresh=false) {
  if(fresh) env.ConnectionStrings__LastSeen2=`mongodb://127.0.0.1:${mongoPort}/lastseen2_test_${randomUUID().replaceAll('-','')}`;
  host=start('dotnet',['LastSeen2.Server.dll','--urls',base],publish,'host');
  await waitFor(host,async()=> (await fetch(base)).ok,'Published host');
}
async function api(route='/board',method='GET',data) {
  const response=await fetch(base+'/api'+route,{method,headers:{'X-Api-Key':env.Authentication__AccessKey,'Content-Type':'application/json'},body:data===undefined?undefined:JSON.stringify(data)});
  assert.equal(response.status,200,route);
  return response.json();
}
async function smoke() {
  const assets={
    '/':'text/html','/client-route':'text/html','/_framework/blazor.webassembly.js':'javascript',
    '/_framework/dotnet.js':'javascript','/_framework/dotnet.native.wasm':'application/wasm',
    '/css/app.css':'text/css',
    '/vendor/fonts/ComingSoon-Regular.ttf':'application/x-font-ttf',
    '/images/video-placeholder.svg':'image/svg+xml','/dialog.js':'javascript','/ordering.js':'javascript',
    '/vendor/sortablejs/sortable.complete.esm.js':'javascript'
  };
  for(const [route,type] of Object.entries(assets)) {
    const response=await fetch(base+route);assert.equal(response.status,200,route);
    assert(response.headers.get('content-type').includes(type),route);
    const body=await response.text();assert(body.length>0);if(type==='text/html') assert(!body.includes('#['));
  }
  for(const route of ['/api/missing','/api/missing.json','/appsettings.json','/appsettings.Production.json']) {
    const response=await fetch(base+route);assert.equal(response.status,404,route);
    if(route.startsWith('/api')) assert.equal((await response.json()).code,'not_found');
  }
  const denied=await fetch(base+'/api/board');assert.equal(denied.status,401);
  assert.equal(denied.headers.get('cache-control'),'no-store');
  console.log('Published asset MIME types, API boundaries and unauthorized access passed.');
}
function scanAssets() {
  const secrets=[env.Authentication__AccessKey,process.env.Authentication__AccessKey,process.env.ConnectionStrings__LastSeen2].filter(v=>v && v.length>=8);
  function walk(dir) {for(const entry of fs.readdirSync(dir,{withFileTypes:true})) {
    const file=path.join(dir,entry.name);
    if(entry.isDirectory()) walk(file);
    else {
      const bytes=fs.readFileSync(file);
      for(const secret of secrets) assert(!bytes.includes(Buffer.from(secret)),'Configured secret found in client asset: '+file);
      assert(!/appsettings.*\.json$/i.test(entry.name),'Unexpected client settings file: '+file);
    }
  }}
  walk(path.join(publish,'wwwroot'));
  console.log('Client asset scan passed for configured secret values and settings files.');
}
(async()=>{
  try {
    console.log('Release evidence: '+output);
    mongoPort=await port();hostPort=await port();base=`http://127.0.0.1:${hostPort}`;
    await startMongo();
    env.LASTSEEN2_TEST_MONGODB=`mongodb://127.0.0.1:${mongoPort}/test_source`;
    env.ConnectionStrings__LastSeen2=`mongodb://127.0.0.1:${mongoPort}/lastseen2_test_${randomUUID().replaceAll('-','')}`;
    await run('dotnet',['restore','src/LastSeen2.slnx','--nologo'],'restore');
    await run('dotnet',['build','src/LastSeen2.slnx','-c','Release','--no-restore','--nologo'],'build');
    await run('dotnet',['test','src/LastSeen2.slnx','-c','Release','--no-build','--nologo','--logger','trx','--results-directory',path.join(output,'results')],'backend-tests');
    const results=fs.readdirSync(path.join(output,'results')).filter(name=>name.endsWith('.trx'));
    assert.equal(results.length,2,'Expected both backend test projects');
    testCount=0;
    for(const name of results) {
      const xml=fs.readFileSync(path.join(output,'results',name),'utf8');
      const counters=xml.match(/<Counters ([^>]+)\/>/)[1];
      const counts=Object.fromEntries([...counters.matchAll(/(\w+)="(\d+)"/g)].map(m=>[m[1],Number(m[2])]));
      assert(counts.total>0);assert.equal(counts.passed,counts.total,'All backend tests must pass without skips');
      testCount+=counts.passed;
    }
    console.log(`${testCount} backend tests passed, zero skips.`);
    await run('dotnet',['publish','src/LastSeen2.Server','-c','Release','--no-restore','--nologo','-o',publish],'publish');
    scanAssets();
    for(const phase of [4,5,6]) {
      await startHost(true);
      env[`PHASE${phase}_TEST_URL`]=base;env[`PHASE${phase}_TEST_KEY`]=env.Authentication__AccessKey;env[`PHASE${phase}_ISOLATED`]='yes';
      if(phase===4) await smoke();
      await run(process.execPath,[`tests/browser/phase${phase}.cjs`],`browser-phase${phase}`);
      await stop(host);
    }
    // The Phase 6 database contains real persisted edits and ordering.
    await startHost();
    const before=await api();assert(before.videos.length>=2);
    browser=await chromium.launch({headless:true,channel:'msedge'});
    const page=await browser.newPage({viewport:{width:1536,height:1024}});
    page.setDefaultTimeout(30000);
    const errors=[];page.on('pageerror',e=>errors.push(e.message));
    const button=name=>page.getByRole('button',{name,exact:true});
    const ready=()=>page.locator('.board-grid[data-order-disabled="false"]').waitFor();
    const unlock=async()=>{await page.getByLabel('Access key',{exact:true}).fill(env.Authentication__AccessKey);await button('Unlock board').click();await ready();};
    const check=async()=>{
      assert.deepEqual(await api(),before);
      assert.deepEqual(await page.locator('.video-row .bookmark-title').evaluateAll(es=>es.map(e=>e.firstChild.textContent)),before.videos.map(v=>v.title));
      for(const group of ['Frequent','Infrequent','Archived']) assert.deepEqual(
        await page.locator(`[data-group="${group}"] li > a`).evaluateAll(es=>es.map(e=>e.firstChild.textContent)),before.channels[group.toLowerCase()].map(c=>c.name));
    };
    await page.goto(base);await unlock();await check();
    await stop(host);await startHost();await page.reload();await ready();await check();
    // Kill the real database, attempt a mutation, then recover the same files.
    await stop(mongo);
    const failed=page.waitForResponse(r=>r.url().endsWith('/api/videos/reorder'));
    await button('Move '+before.videos[0].title).click();
    await button('Move '+before.videos[0].title+' down').click();
    assert.equal((await failed).status(),503);
    await button('Reload and review').waitFor();assert.equal(await page.locator('.board-grid').count(),0);
    assert((await page.getByRole('alert').innerText()).includes('storage is unavailable'));
    await page.screenshot({path:path.join(screenshots,'phase7-outage.png'),fullPage:true});
    await startMongo();await button('Reload and review').click();await ready();await check();
    // Rotate the server key: the old browser session must be rejected and cleared.
    const oldKey=env.Authentication__AccessKey;
    await stop(host);env.Authentication__AccessKey=randomUUID();await startHost();
    assert.equal((await fetch(base+'/api/board',{headers:{'X-Api-Key':oldKey}})).status,401);
    await page.reload();await page.getByRole('heading',{name:'Unlock board'}).waitFor();
    assert.equal(await page.evaluate(()=>sessionStorage.getItem('lastseen2.accessKey')),null);
    assert.equal(await page.locator('.board-grid').count(),0);
    await unlock();await check();
    await page.screenshot({path:path.join(screenshots,'phase7-desktop.png'),fullPage:true});
    for(const width of [390,320]) {await page.setViewportSize({width,height:844});assert(await page.evaluate(()=>document.documentElement.scrollWidth<=innerWidth));}
    await page.screenshot({path:path.join(screenshots,'phase7-mobile.png'),fullPage:true});
    assert.deepEqual(errors,[]);
    await run('git',['diff','--check'],'diff-check');
    fs.writeFileSync(path.join(output,'passed.txt'),`Build, ${testCount} backend tests, publish/assets, Phase 4/5/6 browser suites, process restart, real Mongo outage/recovery, key rotation and responsive checks passed.\r\n`);
    console.log('Release passed: process restart, real database outage/recovery, key rotation and unchanged full snapshot.');
  } finally {
    try {if(browser) await browser.close();}
    finally {await Promise.all([stop(host),stop(mongo)]);}
  }
})().catch(error=>{console.error(error);process.exitCode=1;});
