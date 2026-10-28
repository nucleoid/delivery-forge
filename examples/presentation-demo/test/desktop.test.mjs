import test from 'node:test';
import assert from 'node:assert/strict';
import path from 'node:path';
import { pathToFileURL, fileURLToPath } from 'node:url';
import { chromium } from 'playwright';

const root = path.resolve(path.dirname(fileURLToPath(import.meta.url)), '..');
const url = (page, query = '') => `${pathToFileURL(path.join(root, 'desktop', page)).href}${query ? `?${query}` : ''}`;

async function withPage(fn, viewport = { width: 1440, height: 900 }) {
  const browser = await chromium.launch({ headless: true });
  try { const page = await browser.newPage({ viewport: viewport }); const errors = []; page.on('pageerror', (e) => errors.push(String(e))); await fn(page, errors); } finally { await browser.close(); }
}

for (const file of ['claude-desktop.html', 'codex-desktop.html']) {
  test(`${file}: panel and inline card render the same projected facts without page errors`, async () => {
    await withPage(async (page, errors) => {
      await page.goto(url(file, 'scenario=failed-gate'));
      await page.waitForSelector('.forge-rail');
      const rail = await page.locator('.forge-rail').textContent();
      assert.match(rail, /demo-014/); assert.match(rail, /IMPLEMENT/); assert.match(rail, /DEMO • SYNTHETIC/);
      assert.match(await page.locator('.forge-focus h1').textContent(), /Mutation evidence is incomplete/);
      assert.match(await page.locator('.forge-focus .state').textContent(), /EVALUATE · INCOMPLETE/);
      const rows = await page.locator('.gate-table tr').allTextContents();
      assert.equal(rows.length, 6, 'five gates plus the review row');
      assert.match(rows.join('\n'), /INCOMPLETE\s*Mutate4CSharp/); assert.match(rows.join('\n'), /N\/A\s*Migration/);
      const card = await page.locator('.forge-card').textContent();
      assert.match(card, /EVALUATE · INCOMPLETE/); assert.match(card, /Inspect missing evidence/); assert.match(card, /0\.0442/);
      assert.match(await page.locator('.forge-section.violet .usage-head').textContent(), /USD 0\.044225/);
      assert.match(await page.locator('.caption').textContent(), /DESKTOP MOCKUP · NOT AN INSTALLED-HOST CAPTURE/);
      assert.deepEqual(errors, []);
    });
  });
}

test('desktop panel keyboard: arrows select evidence, Enter opens detail with the full head, P previews a bound intent', async () => {
  await withPage(async (page, errors) => {
    await page.goto(url('claude-desktop.html', 'scenario=failed-gate'));
    await page.waitForSelector('.forge');
    await page.locator('.forge').focus();
    await page.keyboard.press('ArrowDown'); await page.keyboard.press('ArrowDown'); await page.keyboard.press('ArrowDown');
    assert.match(await page.locator('.gate-table tr[aria-selected="true"]').textContent(), /Mutate4CSharp/);
    assert.match(await page.locator('.gate-detail').textContent(), /b45e00000000000000000000000000000000002/);
    assert.match(await page.locator('.gate-detail').textContent(), /2 of 20 discovered mutants/);
    await page.keyboard.press('p');
    const intent = await page.locator('.intent.open').textContent();
    assert.match(intent, /"status": "PREVIEW_ONLY"/); assert.match(intent, /"sideEffects": 0/); assert.match(intent, /"authorityIssued": false/); assert.match(intent, /"action": "request.repair"/);
    await page.keyboard.press('3');
    assert.match(await page.locator('.forge-tabs [aria-selected="true"]').textContent(), /Usage/);
    assert.match(await page.locator('.model-table').textContent(), /72%/);
    assert.deepEqual(errors, []);
  });
});

test('desktop panel never offers a consequential button when cleanup is unknown or the view is stale', async () => {
  await withPage(async (page) => {
    for (const scenario of ['cancel-unknown', 'stale-reconnected']) {
      await page.goto(url('codex-desktop.html', `scenario=${scenario}`));
      await page.waitForSelector('.forge');
      assert.equal(await page.locator('.action-footer button').isDisabled(), true, scenario);
      assert.match(await page.locator('.actions').textContent(), /UNAVAILABLE/, scenario);
      assert.match(await page.locator('.forge-focus .state .src').textContent(), scenario === 'stale-reconnected' ? /stale · unknown/ : /seq 11/);
    }
    await page.goto(url('codex-desktop.html', 'scenario=head-drift&tab=evidence'));
    await page.waitForSelector('.forge');
    const text = await page.locator('.gate-table').textContent();
    assert.match(text, /PASS STALE/); assert.match(text, /PASS STALE\s*Review/); assert.doesNotMatch(await page.locator('.forge-focus .state').textContent(), /· PASS/);
  });
});

test('desktop panel shows publication boundaries at PR receipt and the frozen plan at PLAN', async () => {
  await withPage(async (page) => {
    await page.goto(url('codex-desktop.html', 'scenario=pr-receipt'));
    await page.waitForSelector('.forge');
    const pub = await page.locator('.forge-section.violet').first().textContent();
    assert.match(pub, /merge\s*NOT authorized/); assert.match(pub, /CI\s*INCOMPLETE/); assert.match(pub, /PR is not deployment/);
    await page.goto(url('claude-desktop.html', 'scenario=plan&tab=plan'));
    await page.waitForSelector('.forge');
    const plan = await page.locator('.plan-grid').textContent();
    assert.match(plan, /Add CSV representation/); assert.match(plan, /No deployment/);
    assert.match(await page.locator('.plan-note').textContent(), /preview is not approval/);
  });
});
