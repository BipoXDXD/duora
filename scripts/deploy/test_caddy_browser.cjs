// Sonda Chromium contra a fixture descartável de test_caddy_security_headers.py (Caddy real, upstream sintético).
// Sem microfone real nem TURN: captura sintética do Chromium. Rodar pelo teste Python com DUORA_TEST_BROWSER=1.
const assert = require('node:assert/strict');
const path = require('node:path');

const port = process.env.DUORA_TEST_PORT;
const frontend = process.env.DUORA_TEST_FRONTEND;
assert.ok(port && frontend, 'DUORA_TEST_PORT e DUORA_TEST_FRONTEND vêm do teste Python');
// O Playwright é dependência de desenvolvimento do frontend; nenhuma dependência nova entra no projeto.
const { chromium } = require(require.resolve('@playwright/test', { paths: [path.resolve(frontend)] }));
const origin = `https://localhost:${port}`;
// Mesma porta, outro host: outra origem, para provar que as fontes ficam só na própria.
const otherOrigin = `https://127.0.0.1:${port}`;

(async () => {
  // channel 'chromium' (headless novo): o headless shell não tem captura de mídia (getUserMedia dá NotSupportedError).
  const browser = await chromium.launch({
    channel: 'chromium',
    args: ['--use-fake-device-for-media-stream', '--autoplay-policy=document-user-activation-required'],
  });
  try {
    const context = await browser.newContext({ ignoreHTTPSErrors: true });
    const page = await context.newPage();
    const response = await page.goto(origin);
    assert.equal(response.status(), 200);
    const cdp = await context.newCDPSession(page);
    // O page.evaluate do Playwright pode simular ativação do usuário; o Runtime.evaluate do CDP sem userGesture não.
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
    check('Web Audio fetch and decode needs no blob source', await evaluate(`
      const c = new AudioContext();
      try { const a = await fetch('/assets/probe.wav'); const b = await c.decodeAudioData(await a.arrayBuffer()); return b.length > 0; }
      finally { await c.close(); }
    `), true);
    for (const [name, expression, directive] of [
      ['cross-origin media', `new Audio('${otherOrigin}/assets/probe.wav').load()`, 'media-src'],
      ['data media', `new Audio('data:audio/wav;base64,UklGRg==').load()`, 'media-src'],
      ['blob media', `const u = URL.createObjectURL(new Blob(['probe'], {type:'audio/wav'})); new Audio(u).load()`, 'media-src'],
      ['cross-origin fetch', `fetch('${otherOrigin}/api/ok').catch(() => {})`, 'connect-src'],
      ['cross-origin WebSocket', `try { new WebSocket('wss://127.0.0.1:${port}/hubs/ok') } catch {}`, 'connect-src'],
      ['inline style', `const s = document.createElement('style'); s.textContent = 'body{}'; document.head.append(s)`, 'style-src-elem'],
      ['WebAssembly', `WebAssembly.instantiate(new Uint8Array([0, 97, 115, 109, 1, 0, 0, 0])).catch(() => {})`, 'script-src'],
      ['cross-origin frame', `const f = document.createElement('iframe'); f.src = '${otherOrigin}/'; document.body.append(f)`, 'frame-src'],
    ]) {
      check(`${name} blocked by CSP`, await evaluate(`
        return await new Promise(resolve => {
          document.addEventListener('securitypolicyviolation', e => resolve(e.effectiveDirective), {once:true});
          ${expression};
        });
      `), directive);
    }
    check('only autoplay, microphone and speaker-selection allowed (client hints aside)', await evaluate(`
      return document.featurePolicy.allowedFeatures().filter(f => !f.startsWith('ch-')).sort();
    `), ['autoplay', 'microphone', 'speaker-selection']);
    check('fullscreen disabled by policy', await evaluate('return document.fullscreenEnabled;'), false);
    check('blob: fetch allowed for GLB textures', await evaluate(`
      const u = URL.createObjectURL(new Blob(['texture'], { type: 'image/png' }));
      try { return await (await fetch(u)).text(); } finally { URL.revokeObjectURL(u); }
    `), 'texture');
    check('HTML sink refuses strings (Trusted Types)', await evaluate(`
      try { document.createElement('div').innerHTML = '<b>x</b>'; return 'assigned'; } catch (e) { return e.name; }
    `), 'TypeError');
    check('only the lit-html policy can be created, once', await evaluate(`
      window.probePolicy = trustedTypes.createPolicy('lit-html', { createHTML: s => s, createScriptURL: s => s });
      const refused = [];
      for (const name of ['other', 'lit-html']) {
        try { trustedTypes.createPolicy(name, { createHTML: s => s }); refused.push('created'); }
        catch (e) { refused.push(e.name); }
      }
      return refused;
    `), ['TypeError', 'TypeError']);
    // A URL passa pelo Trusted Types com a política de teste, para a recusa vir do worker-src e não do sink.
    check('same-origin worker blocked by CSP', await evaluate(`
      return await new Promise(resolve => {
        document.addEventListener('securitypolicyviolation', e => resolve(e.effectiveDirective), { once: true });
        try { new Worker(window.probePolicy.createScriptURL('/assets/probe.js')); } catch {}
      });
    `), 'worker-src');
    const {targetInfo} = await cdp.send('Target.getTargetInfo');
    await cdp.send('Browser.setPermission', { permission: { name: 'microphone' }, setting:'denied', origin, browserContextId:targetInfo.browserContextId });
    check('denied microphone rejects', await evaluate(`
      try { const s = await navigator.mediaDevices.getUserMedia({audio:true}); s.getTracks().forEach(t => t.stop()); return 'granted'; }
      catch(e) { return e.name; }
    `), 'NotAllowedError');
    await context.grantPermissions(['microphone', 'camera'], { origin });
    check('camera denied by policy even with browser permission', await evaluate(`
      try { const s = await navigator.mediaDevices.getUserMedia({video:true}); s.getTracks().forEach(t => t.stop()); return 'granted'; }
      catch(e) { return e.name; }
    `), 'NotAllowedError');
    // srcObject não passa pelo Fetch: o MediaStream entra no <audio> sem fonte alguma na CSP. Sem play(): no
    // Chromium headless, play() sem saída de áudio fica pendente, e o autoplay é regra da SPA, não do cabeçalho.
    check('microphone policy does not impose a gesture and MediaStream needs no CSP source', await evaluate(`
      const active = navigator.userActivation.hasBeenActive;
      const s = await navigator.mediaDevices.getUserMedia({audio:true});
      const a = new Audio();
      const violations = [];
      document.addEventListener('securitypolicyviolation', e => violations.push(e.effectiveDirective));
      await new Promise(resolve => { a.onloadedmetadata = resolve; a.srcObject = s; });
      const live = s.getAudioTracks()[0].readyState;
      s.getTracks().forEach(t => t.stop());
      return [active, live, s.getAudioTracks()[0].readyState, violations];
    `), [false, 'live', 'ended', []]);
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
      headers['content-security-policy'] += `; frame-src ${otherOrigin}`;
      await route.fulfill({status:200, headers, body:'<!doctype html><body></body>'});
    });
    await context.route(`${otherOrigin}/frame-child`, route => route.fulfill({
      contentType:'text/html', body:'<!doctype html><body>synthetic cross-origin child</body>',
    }));
    await context.grantPermissions(['microphone'], { origin: otherOrigin });
    await page.goto(`${origin}/policy-frame-probe`);
    const navigated = page.waitForEvent('framenavigated', {predicate:f => f.url().endsWith('/frame-child')});
    await page.evaluate(src => {
      const f = document.createElement('iframe'); f.allow = 'microphone *';
      f.src = src; document.body.append(f);
    }, `${otherOrigin}/frame-child`);
    const child = await navigated;
    check('cross-origin child cannot override parent microphone policy', await child.evaluate(async () => {
      try { const s = await navigator.mediaDevices.getUserMedia({audio:true}); s.getTracks().forEach(t => t.stop()); return 'granted'; }
      catch(e) { return e.name; }
    }), 'NotAllowedError');
    console.log(JSON.stringify({browser:await browser.version(), passed, scope:'synthetic Caddy fixture; no TURN or application voice flow'}));
  } finally { await browser.close(); }
})().catch(error => { console.error(error); process.exitCode = 1; });
