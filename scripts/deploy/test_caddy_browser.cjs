// Sonda Chromium contra a fixture descartável de test_caddy_security_headers.py (Caddy real, upstream sintético).
// Sem microfone real nem TURN: captura sintética do Chromium. Rodar pelo teste Python com DUORA_TEST_BROWSER=1.
const assert = require('node:assert/strict');
const path = require('node:path');
const { pathToFileURL } = require('node:url');

const port = process.env.DUORA_TEST_PORT;
const frontend = process.env.DUORA_TEST_FRONTEND;
assert.ok(port && frontend, 'DUORA_TEST_PORT e DUORA_TEST_FRONTEND vêm do teste Python');
// O Playwright é dependência de desenvolvimento do frontend; nenhuma dependência nova entra no projeto.
const { chromium, expect } = require(require.resolve('@playwright/test', { paths: [path.resolve(frontend)] }));
const origin = `https://localhost:${port}`;
// Mesma porta, outro host: outra origem, para provar que as fontes ficam só na própria.
const otherOrigin = `https://127.0.0.1:${port}`;
const focusFeature = 'focus-without-user-activation';

// A avaliação do Playwright pode simular ativação. A sonda usa o CDP com userGesture: false.
async function evaluateWithoutGesture(cdp, expression) {
  const result = await cdp.send('Runtime.evaluate', {
    expression: `Promise.race([(async () => { ${expression} })(), new Promise((_, reject) => setTimeout(() => reject(new Error('probe timeout')), 8000))])`,
    awaitPromise: true, returnByValue: true, userGesture: false,
  });
  assert.equal(result.exceptionDetails, undefined, JSON.stringify(result.exceptionDetails));
  return result.result.value;
}

async function checkAppFocus(browser, appOrigin) {
  const { withSessionContext } = await import(pathToFileURL(path.resolve(frontend, 'e2e/support/private-context.ts')));
  const context = await browser.newContext({ ignoreHTTPSErrors: true, locale: 'pt-BR' });
  context.setDefaultTimeout(10_000);
  try {
    const page = await context.newPage();
    await expect.poll(async () => {
      try { return (await context.request.get(appOrigin, {timeout: 1000})).status(); }
      catch { return 0; }
    }, {timeout: 15_000}).toBe(200);
    const account = withSessionContext({
      id: '0199f1a2-3b4c-7d5e-8f60-718293a4b5c6', email: 'focus@example.test', status: 'Active',
      emailConfirmed: true, mfaEnabled: true, roles: ['participant'],
    });
    let signedIn = false;
    await page.route(`${appOrigin}/api/v1/**`, route => {
      switch (new URL(route.request().url()).pathname) {
        case '/api/v1/me':
          return signedIn ? route.fulfill({json: account}) : route.fulfill({status: 401});
        case '/api/v1/me/sessions':
          return route.fulfill({json: {items: []}});
        case '/api/v1/me/selfie-verifications/current':
          return route.fulfill({json: {id: null, state: 'NotStarted', nextStep: 'Start', feedbackCode: null, expiresAt: null}});
        default:
          return route.fulfill({status: 404});
      }
    });
    const home = await page.goto(appOrigin);
    assert.equal(home.status(), 200);
    assert.match(await home.headerValue('permissions-policy'), /focus-without-user-activation=\(self\)/);
    await expect(page.getByRole('heading', {level: 1, name: 'Duora'})).toBeVisible();
    await page.keyboard.press('Tab');
    await expect(page.getByRole('link', {name: 'Pular para o conteúdo'})).toBeFocused();
    await page.keyboard.press('Enter');
    await expect(page.getByRole('main')).toBeFocused();
    console.log('PASS app keyboard skip link focuses main through Caddy');

    await page.goto(appOrigin);
    const cdp = await context.newCDPSession(page);
    const evaluate = expression => evaluateWithoutGesture(cdp, expression);
    await expect.poll(() => evaluate(`return Boolean(document.querySelector('main a[href="/login"]'));`)).toBe(true);
    const hasLoginChunk = `performance.getEntriesByType('resource').some(({name}) => {
      const path = new URL(name).pathname;
      return path.startsWith('/assets/LoginPage-') && path.endsWith('.js');
    })`;
    assert.deepEqual(await evaluate(`
      const activation = () => [navigator.userActivation.isActive, navigator.userActivation.hasBeenActive];
      const before = activation();
      const chunkBefore = ${hasLoginChunk};
      document.addEventListener('focusin', event => {
        if (event.target === document.querySelector('main h1') && event.target.textContent.trim() === 'Entrar') {
          globalThis.duoraProbeFocusActivation = activation();
        }
      }, {capture: true});
      document.querySelector('main a[href="/login"]').click();
      return {before, after: activation(), chunkBefore};
    `), {before: [false, false], after: [false, false], chunkBefore: false});
    // As leituras de foco e anúncio também precisam permanecer sem userGesture enquanto o chunk chega.
    await expect.poll(() => evaluate(`
      const heading = document.querySelector('main h1');
      return {
        pathname: location.pathname,
        chunkLoaded: ${hasLoginChunk},
        focused: heading === document.activeElement ? heading?.textContent.trim() : null,
        announcement: document.querySelector('[data-testid="anuncio-de-rota"]')?.textContent,
        focusActivation: globalThis.duoraProbeFocusActivation ?? null,
        observedActivation: [navigator.userActivation.isActive, navigator.userActivation.hasBeenActive],
      };
    `)).toEqual({
      pathname: '/login', chunkLoaded: true, focused: 'Entrar', announcement: 'Página carregada: Entrar · Duora',
      focusActivation: [false, false], observedActivation: [false, false],
    });
    console.log('PASS app lazy SPA navigation focuses and announces through Caddy without activation');

    signedIn = true;
    await page.goto(`${appOrigin}/account`);
    const opener = page.getByRole('button', {name: 'Desativar 2FA', exact: true});
    await opener.press('Enter');
    await expect(page.getByRole('group', {name: 'Desativar a autenticação em duas etapas?'})).toBeFocused();
    await page.getByRole('button', {name: 'Manter 2FA', exact: true}).press('Enter');
    await expect(opener).toBeFocused();
    console.log('PASS app MFA cancel restores focus through Caddy');

    const navigateDocument = async (name, pathname) => {
      const [result] = await Promise.all([
        page.waitForResponse(candidate => candidate.request().isNavigationRequest()
          && new URL(candidate.url()).pathname === pathname),
        page.getByRole('link', {name, exact: true}).press('Enter'),
      ]);
      assert.equal(result.status(), 200);
      return await result.headerValue('permissions-policy');
    };
    const selfie = await navigateDocument('Confirmar presença', '/account/selfie');
    assert.match(selfie, /camera=\(self\)/);
    assert.match(selfie, /focus-without-user-activation=\(self\)/);
    const returned = await navigateDocument('Voltar à conta', '/account');
    assert.match(returned, /camera=\(\)/);
    assert.match(returned, /focus-without-user-activation=\(self\)/);
    await expect(opener).toBeVisible();
    console.log('PASS app selfie entry and return load documents with their Caddy policies');
  } finally {
    await context.close();
  }
}

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
    const evaluate = expression => evaluateWithoutGesture(cdp, expression);
    let passed = 0;
    function check(name, actual, expected) {
      assert.deepEqual(actual, expected, name);
      console.log(`PASS ${name}`);
      passed++;
    }
    async function checkDocumentPolicy(name, {camera = false} = {}) {
      const {allowed, focusSupported, ownFocus, crossOriginFocus} = await evaluate(`
        const policy = document.featurePolicy;
        const focusSupported = policy.features().includes('${focusFeature}');
        return {
          allowed: policy.allowedFeatures().filter(feature => !feature.startsWith('ch-')).sort(),
          focusSupported,
          ownFocus: focusSupported && policy.allowsFeature('${focusFeature}'),
          crossOriginFocus: focusSupported && policy.allowsFeature('${focusFeature}', '${otherOrigin}'),
        };
      `);
      const expected = ['autoplay', 'microphone', 'speaker-selection'];
      if (focusSupported) expected.push(focusFeature);
      if (camera) expected.push('camera');
      check(`${name} allows exactly the documented features`, allowed, expected.sort());
      check(`${name} limits focus without activation to its origin`, [ownFocus, crossOriginFocus],
        [focusSupported, false]);
      return focusSupported;
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
    check('private audio refuses a request without the owned proof', await evaluate(`
      return (await fetch('/api/v1/rooms/probe/audio/cue')).status;
    `), 403);
    check('private PCM decodes through owned fetch under the unchanged CSP', await evaluate(`
      const c = new AudioContext({sampleRate:48000});
      try {
        const a = await fetch('/api/v1/rooms/probe/audio/cue', {headers:{'X-XSRF-TOKEN':'synthetic-private-audio-proof'}});
        if (a.status !== 200 || a.headers.get('Cache-Control') !== 'no-store') return false;
        const b = await c.decodeAudioData(await a.arrayBuffer());
        return b.duration === 1 && b.numberOfChannels === 1 && c.sampleRate <= 48000;
      } finally { await c.close(); }
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
    const focusSupported = await checkDocumentPolicy('regular document');
    if (process.env.DUORA_TEST_APP_PORT) assert.equal(focusSupported, true, 'o Chromium do E2E reconhece a capacidade de foco');
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
    // Selfie (issue #93): só o documento /account/selfie recebe WASM, câmera e os dois hosts do Face Liveness.
    // O CDN é interceptado (nenhum byte da AWS); o WebSocket só precisa não ser recusado pela CSP.
    await context.route('https://cdn.liveness.rekognition.amazonaws.com/**', route => route.fulfill({
      status: 200, contentType: 'application/json', headers: { 'Access-Control-Allow-Origin': '*' }, body: '{}',
    }));
    const selfie = await page.goto(`${origin}/account/selfie`);
    assert.equal(selfie.status(), 200);
    await checkDocumentPolicy('selfie document', {camera: true});
    await context.grantPermissions(['camera'], { origin });
    check('selfie document compiles WebAssembly', await evaluate(`
      const m = await WebAssembly.instantiate(new Uint8Array([0, 97, 115, 109, 1, 0, 0, 0]));
      return m.instance instanceof WebAssembly.Instance;
    `), true);
    check('selfie document allows the camera for its own origin', await evaluate(`
      const s = await navigator.mediaDevices.getUserMedia({video:true});
      const live = s.getVideoTracks()[0].readyState;
      s.getTracks().forEach(t => t.stop());
      return [live, document.featurePolicy.allowsFeature('camera'), document.featurePolicy.allowsFeature('camera', '${otherOrigin}')];
    `), ['live', true, false]);
    check('selfie document reaches only the Face Liveness hosts', await evaluate(`
      const violations = [];
      document.addEventListener('securitypolicyviolation', e => violations.push(e.blockedURI));
      const cdn = await (await fetch('https://cdn.liveness.rekognition.amazonaws.com/face-detection/probe.json')).text();
      const socket = new WebSocket('wss://streaming-rekognition.us-east-1.amazonaws.com/probe');
      socket.onerror = () => {};
      socket.close();
      await fetch('https://streaming-rekognition.us-west-2.amazonaws.com/').catch(() => {});
      await new Promise(resolve => setTimeout(resolve, 300));
      return [cdn, violations];
    `), ['{}', ['https://streaming-rekognition.us-west-2.amazonaws.com/']]);
    check('selfie document still blocks workers', await evaluate(`
      return await new Promise(resolve => {
        document.addEventListener('securitypolicyviolation', e => resolve(e.effectiveDirective), { once: true });
        try { new Worker(trustedTypes.createPolicy('lit-html', { createScriptURL: s => s }).createScriptURL('/assets/probe.js')); } catch {}
      });
    `), 'worker-src');
    await page.goto(`${origin}/account`);
    await checkDocumentPolicy('document after leaving selfie');
    check('leaving the selfie document drops WebAssembly and camera', await evaluate(`
      const wasm = await WebAssembly.instantiate(new Uint8Array([0, 97, 115, 109, 1, 0, 0, 0])).then(() => 'compiled', e => e.name);
      return [wasm, document.featurePolicy.allowsFeature('camera')];
    `), ['CompileError', false]);
    // Separate policy-layer diagnostic: allow frames only in this intercepted test document,
    // retaining the real Permissions-Policy. The production CSP itself forbids frame navigation above.
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
      const f = document.createElement('iframe'); f.allow = 'microphone *; focus-without-user-activation *';
      f.src = src; document.body.append(f);
    }, `${otherOrigin}/frame-child`);
    const child = await navigated;
    if (focusSupported) {
      check('cross-origin child cannot override parent focus policy', await child.evaluate(
        feature => document.featurePolicy.allowedFeatures().includes(feature), focusFeature), false);
    }
    check('cross-origin child cannot override parent microphone policy', await child.evaluate(async () => {
      try { const s = await navigator.mediaDevices.getUserMedia({audio:true}); s.getTracks().forEach(t => t.stop()); return 'granted'; }
      catch(e) { return e.name; }
    }), 'NotAllowedError');
    if (process.env.DUORA_TEST_APP_PORT) {
      await checkAppFocus(browser, `https://localhost:${process.env.DUORA_TEST_APP_PORT}`);
    }
    console.log(JSON.stringify({browser:await browser.version(), passed, scope:'synthetic Caddy fixture; no TURN or application voice flow'}));
  } finally { await browser.close(); }
})().catch(error => { console.error(error); process.exitCode = 1; });
