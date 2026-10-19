// Chromium behavior probe against the real, disposable Caddy fixture. No real microphone or TURN.
// Run via test_caddy_security_headers.py with DUORA_TEST_CADDY and DUORA_TEST_CHROME.
const assert = require('node:assert/strict');
const { createRequire } = require('node:module');
const path = require('node:path');
let chromium;
try {
  ({ chromium } = createRequire(path.resolve(__dirname, '../../frontend/package.json'))('@playwright/test'));
} catch (error) {
  if (error.code !== 'MODULE_NOT_FOUND') throw error;
  ({ chromium } = require('playwright')); // Isolated tooling runtime; no application dependency change.
}

(async () => {
  const browser = await chromium.launch({
    executablePath: process.env.DUORA_TEST_CHROME,
    args: ['--use-fake-device-for-media-stream', '--autoplay-policy=document-user-activation-required'],
  });
  try {
    const context = await browser.newContext({ ignoreHTTPSErrors: true });
    const page = await context.newPage();
    const origin = 'https://localhost:8443';
    const response = await page.goto(origin);
    assert.equal(response.status(), 200);
    const cdp = await context.newCDPSession(page);
    // Playwright page.evaluate can supply user activation. CDP explicitly does not.
    async function evaluate(expression) {
      const result = await cdp.send('Runtime.evaluate', {
        expression: `Promise.race([(async () => { ${expression} })(), new Promise((_, reject) => setTimeout(() => reject(new Error('probe timeout')), 8000))])`,
        awaitPromise: true, returnByValue: true, userGesture: false,
      });
      assert.equal(result.exceptionDetails, undefined, JSON.stringify(result.exceptionDetails));
      return result.result.value;
    }
    let passed = 0;
    function check(name, actual, expected) {
      assert.deepEqual(actual, expected, name);
      console.log(`PASS ${name}`);
      passed++;
    }
    check('secure context without user gesture', await evaluate('return [isSecureContext, navigator.userActivation.hasBeenActive];'), [true, false]);
    check('same-origin media loads', await evaluate(`
      const a = new Audio('/assets/probe.wav');
      return await new Promise(resolve => { a.onloadeddata = () => resolve('loaded'); a.onerror = () => resolve('error'); a.load(); });
    `), 'loaded');
    check('audible playback without gesture rejected', await evaluate(`
      try { await new Audio('/assets/probe.wav').play(); return 'played'; } catch(e) { return e.name; }
    `), 'NotAllowedError');
    check('Web Audio fetch and decode needs no blob source', await evaluate(`
      const c = new AudioContext();
      try { const a = await fetch('/assets/probe.wav'); const b = await c.decodeAudioData(await a.arrayBuffer()); return b.length > 0; }
      finally { await c.close(); }
    `), true);
    for (const [name, expression, directive] of [
      ['cross-origin media', `new Audio('https://127.0.0.1:8443/assets/probe.wav').load()`, 'media-src'],
      ['data media', `new Audio('data:audio/wav;base64,UklGRg==').load()`, 'media-src'],
      ['blob media', `const u = URL.createObjectURL(new Blob(['probe'], {type:'audio/wav'})); new Audio(u).load()`, 'media-src'],
      ['cross-origin fetch', `fetch('https://127.0.0.1:8443/api/ok').catch(() => {})`, 'connect-src'],
      ['cross-origin WebSocket', `try { new WebSocket('wss://127.0.0.1:8443/hubs/ok') } catch {}`, 'connect-src'],
      ['cross-origin frame', `const f = document.createElement('iframe'); f.src = 'https://127.0.0.1:8443/'; document.body.append(f)`, 'frame-src'],
    ]) {
      check(`${name} blocked by CSP`, await evaluate(`
        return await new Promise(resolve => {
          document.addEventListener('securitypolicyviolation', e => resolve(e.effectiveDirective), {once:true});
          ${expression};
        });
      `), directive);
    }
    const {targetInfo} = await cdp.send('Target.getTargetInfo');
    await cdp.send('Browser.setPermission', { permission: {name:'audioCapture'}, setting:'denied', origin, browserContextId:targetInfo.browserContextId });
    check('denied microphone rejects', await evaluate(`
      try { const s = await navigator.mediaDevices.getUserMedia({audio:true}); s.getTracks().forEach(t => t.stop()); return 'granted'; }
      catch(e) { return e.name; }
    `), 'NotAllowedError');
    await context.grantPermissions(['microphone', 'camera'], { origin });
    check('camera denied by policy even with browser permission', await evaluate(`
      try { const s = await navigator.mediaDevices.getUserMedia({video:true}); s.getTracks().forEach(t => t.stop()); return 'granted'; }
      catch(e) { return e.name; }
    `), 'NotAllowedError');
    check('microphone policy does not impose a gesture', await evaluate(`
      const active = navigator.userActivation.hasBeenActive;
      const s = await navigator.mediaDevices.getUserMedia({audio:true});
      const a = new Audio(); a.srcObject = s;
      const live = s.getAudioTracks()[0].readyState;
      await a.play(); a.pause(); s.getTracks().forEach(t => t.stop());
      return [active, live, s.getAudioTracks()[0].readyState];
    `), [false, 'live', 'ended']);
    check('WebRTC gathers ICE despite restrictive default-src', await evaluate(`
      const p = new RTCPeerConnection({iceServers:[]});
      try {
        p.createDataChannel('synthetic');
        const gathered = new Promise(resolve => p.onicecandidate = e => { if(e.candidate) resolve(true); });
        await p.setLocalDescription(await p.createOffer());
        return await gathered;
      } finally { p.close(); }
    `), true);
    // Separate policy-layer diagnostic: allow frames only in this intercepted test document,
    // retaining microphone=(self). The production CSP itself forbids frame navigation above.
    await context.route(`${origin}/policy-frame-probe`, async route => {
      const headers = await response.allHeaders();
      delete headers['content-length']; delete headers['content-encoding'];
      headers['content-security-policy'] += '; frame-src https://127.0.0.1:8443';
      await route.fulfill({status:200, headers, body:'<!doctype html><body></body>'});
    });
    await context.route('https://127.0.0.1:8443/frame-child', route => route.fulfill({
      contentType:'text/html', body:'<!doctype html><body>synthetic cross-origin child</body>',
    }));
    await context.grantPermissions(['microphone'], {origin:'https://127.0.0.1:8443'});
    await page.goto(`${origin}/policy-frame-probe`);
    const navigated = page.waitForEvent('framenavigated', {predicate:f => f.url().endsWith('/frame-child')});
    await page.evaluate(() => {
      const f = document.createElement('iframe'); f.allow = 'microphone *';
      f.src = 'https://127.0.0.1:8443/frame-child'; document.body.append(f);
    });
    const child = await navigated;
    check('cross-origin child cannot override parent microphone policy', await child.evaluate(async () => {
      try { const s = await navigator.mediaDevices.getUserMedia({audio:true}); s.getTracks().forEach(t => t.stop()); return 'granted'; }
      catch(e) { return e.name; }
    }), 'NotAllowedError');
    console.log(JSON.stringify({browser:await browser.version(), passed, scope:'synthetic Caddy fixture; no TURN or application voice flow'}));
  } finally { await browser.close(); }
})().catch(error => { console.error(error); process.exitCode = 1; });
