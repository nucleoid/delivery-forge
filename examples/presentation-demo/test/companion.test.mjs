import test from 'node:test';
import assert from 'node:assert/strict';
import path from 'node:path';
import { pathToFileURL, fileURLToPath } from 'node:url';
import { chromium } from 'playwright';
const root=path.resolve(path.dirname(fileURLToPath(import.meta.url)),'..');
test('file-compatible companion supports keyboard replay, evidence detail and intent preview',async()=>{
  const browser=await chromium.launch({headless:true});
  try{
    const page=await browser.newPage({viewportSize:{width:1440,height:900}});const errors=[];page.on('pageerror',e=>errors.push(String(e)));
    await page.goto(pathToFileURL(path.join(root,'companion/index.html')).href);
    assert.match(await page.locator('#identity').textContent(),/DEMO • SYNTHETIC/);
    await page.keyboard.press('ArrowRight');assert.equal(await page.evaluate(()=>ForgeCompanion.cursor),1);
    await page.keyboard.press('r');assert.equal(await page.evaluate(()=>ForgeCompanion.cursor),0);
    await page.locator('#scenario-nav button').nth(2).click();await page.locator('#preview').click();
    assert.match(await page.locator('#intent').textContent(),/"sideEffects": 0/);assert.match(await page.locator('#intent').textContent(),/"authorityIssued": false/);
    assert.deepEqual(errors,[]);
  } finally {await browser.close();}
});
test('companion has no horizontal overflow at 390px and honors reduced motion',async()=>{
  const browser=await chromium.launch({headless:true});
  try{const page=await browser.newPage({viewportSize:{width:390,height:844},reducedMotion:'reduce'});await page.goto(pathToFileURL(path.join(root,'companion/index.html')).href);assert.equal(await page.evaluate(()=>document.documentElement.scrollWidth<=document.documentElement.clientWidth),true);assert.equal(await page.evaluate(()=>getComputedStyle(document.body).animationName),'none');}finally{await browser.close();}
});

test('companion generated launch state never presents empty evidence as PASS',async()=>{
  const browser=await chromium.launch({headless:true});
  try{const page=await browser.newPage({viewportSize:{width:900,height:700}});await page.goto(pathToFileURL(path.join(root,'companion/index.html')).href);await page.evaluate(()=>{sessionStorage.setItem('forge-demo-cursor','0');location.reload();});await page.waitForLoadState('load');assert.match(await page.locator('#phase-outcome').textContent(),/INCOMPLETE/);}finally{await browser.close();}
});
