// Isolated browser acceptance. Set HANDOFF_LIVE_SMOKE=1 explicitly for paid API/audio coverage.
// HANDOFF_REQUEST_WAV and HANDOFF_FOLLOWUP_WAV must contain natural speech without tool-name hints.
const fs = require('node:fs');
const path = require('node:path');
const { chromium } = require(process.env.PLAYWRIGHT_MODULE_PATH || 'playwright');
(async () => {
    const artifacts = process.env.HANDOFF_ARTIFACTS || path.resolve('artifacts/handoff');
    fs.mkdirSync(artifacts, { recursive: true });
    const browser = await chromium.launch({ channel: 'msedge', headless: true });
    const context = await browser.newContext({ ignoreHTTPSErrors: true, viewport: { width: 1280, height: 900 } });
    const page = await context.newPage();
    page.setDefaultTimeout(15000);
    page.setDefaultNavigationTimeout(30000);
    const errors = [];
    page.on('pageerror', e => errors.push(e.message));
    page.on('console', e => { if (e.type() === 'error') errors.push(e.text() + ' (' + e.location().url + ')'); });
    const live = process.env.HANDOFF_LIVE_SMOKE === '1';
    try {
        await page.goto(process.env.HANDOFF_URL || 'https://100.91.240.65:7087/', { waitUntil: 'domcontentloaded' });
        await page.waitForTimeout(1500); // Allow the initial server-interactive circuit to attach.
        await page.waitForSelector('#voice-model');
        await page.locator('#voice-model').selectOption('gpt-live-1');
        await page.locator('#live-backend-mode').selectOption('agent');
        await page.waitForSelector('#handoff-cancel');
        if (await page.locator('a[href="/agent-handoff"]').count()) throw new Error('Dedicated handoff link remains');
        await page.waitForTimeout(1500);
        if (await page.locator('#blazor-error-ui').isVisible()) throw new Error('Blazor circuit failed: ' + errors.join('; '));
        await page.screenshot({ path: path.join(artifacts, 'desktop.png'), fullPage: true });
        await page.setViewportSize({ width: 390, height: 844 });
        if (await page.evaluate(() => document.documentElement.scrollWidth > innerWidth)) throw new Error('Horizontal overflow on mobile');
        await page.screenshot({ path: path.join(artifacts, 'mobile.png'), fullPage: true });
        await page.setViewportSize({ width: 1280, height: 900 });
        if (live) {
            const request = fs.readFileSync(process.env.HANDOFF_REQUEST_WAV).toString('base64');
            const followup = fs.readFileSync(process.env.HANDOFF_FOLLOWUP_WAV).toString('base64');
            await page.evaluate(async () => {
                window.__handoffEvents = [];
                window.__handoffPeers = [];
                const OriginalPeer = window.RTCPeerConnection;
                window.RTCPeerConnection = class extends OriginalPeer {
                    constructor(...args) { super(...args); window.__handoffPeers.push(this); }
                    createDataChannel(...args) {
                        const channel = super.createDataChannel(...args);
                        channel.addEventListener('message', event => {
                            try {
                                const value = JSON.parse(event.data);
                                if (/audio[._]delta/.test(value.type || '')) return;
                                window.__handoffEvents.push({ type: value.type, at: performance.now(),
                                    delegationId: value.delegation?.id || value.delegation_id,
                                    errorCode: value.error?.code });
                                if (window.__handoffEvents.length > 200) window.__handoffEvents.shift();
                            } catch {}
                        });
                        return channel;
                    }
                };
                const audio = new AudioContext(); await audio.resume();
                const gain = audio.createGain(), oscillator = audio.createOscillator(), mute = audio.createGain();
                mute.gain.value = 0; oscillator.connect(mute); mute.connect(gain); oscillator.start();
                navigator.mediaDevices.getUserMedia = async () => {
                    const destination = audio.createMediaStreamDestination(); gain.connect(destination); return destination.stream;
                };
                window.__handoffFeed = async base64 => {
                    window.__handoffEvents.push({ type: 'test.speech.feed', at: performance.now(), audioState: audio.state });
                    const bytes = Uint8Array.from(atob(base64), c => c.charCodeAt(0));
                    const source = audio.createBufferSource(); source.buffer = await audio.decodeAudioData(bytes.buffer);
                    source.connect(gain); source.start();
                };
                window.__handoffAudio = audio;
            });
            await page.getByRole('button', { name: 'Start', exact: true }).click();
            await page.waitForFunction(() => [...document.querySelectorAll('button')].some(b => b.textContent.trim() === 'Stop' && !b.disabled), null, { timeout: 40000 });
            await page.evaluate(wav => window.__handoffFeed(wav), request);
            await page.waitForFunction(() => /ZEDER.?8426/i.test([...document.querySelectorAll('[data-chat-role=agent]')].map(e => e.innerText).join('\n')) &&
                [...document.querySelectorAll('[data-chat-role=agent]')].map(e => e.innerText).join('\n').includes('863'), null, { timeout: 120000 });
            await page.waitForFunction(() => /8426/.test([...document.querySelectorAll('[data-chat-role=assistant]:not([data-tool-name])')].map(e => e.innerText).join('\n')) &&
                /863/.test([...document.querySelectorAll('[data-chat-role=assistant]:not([data-tool-name])')].map(e => e.innerText).join('\n')), null, { timeout: 60000 });
            const toolResponse = page.locator('[data-chat-role=tool][data-tool-name=get_large_project_dossier]');
            await toolResponse.locator('summary').click();
            const completeToolOutput = await toolResponse.locator('pre').innerText();
            if (completeToolOutput.length < 40000 || !/ZEDER.?8426/i.test(completeToolOutput))
                throw new Error('Complete dossier tool output is not inspectable in the normal chat');
            if (!(await page.locator('[data-chat-role=assistant][data-tool-name=get_large_project_dossier]').innerText()).includes('Arguments:') &&
                !(await page.locator('[data-chat-role=assistant][data-tool-name=get_large_project_dossier] pre').count()))
                throw new Error('Agent tool request arguments missing');
            const firstResult = await page.locator('[data-chat-role=agent]').allInnerTexts().then(v => v.join('\n'));
            await page.waitForTimeout(2000);
            const before = await page.locator('[data-chat-role=assistant]:not([data-tool-name])').allInnerTexts().then(v => v.join('\n'));
            await page.evaluate(wav => window.__handoffFeed(wav), followup);
            await page.waitForFunction(() => ([...document.querySelectorAll('[data-chat-role=activity]')].map(e => e.innerText).join('\n').match(/completed/g) || []).length >= 2 &&
                /836/.test([...document.querySelectorAll('[data-chat-role=agent]')].map(e => e.innerText).join('\n')), null, { timeout: 120000 });
            await page.waitForFunction(previous => /836/.test([...document.querySelectorAll('[data-chat-role=assistant]:not([data-tool-name])')].map(e => e.innerText).join('\n').slice(previous.length)), before, { timeout: 60000 });
            await page.getByRole('button', { name: 'Stop', exact: true }).click();
            await page.waitForFunction(() => [...document.querySelectorAll('button')].some(b => b.textContent.trim() === 'Start' && !b.disabled), null, { timeout: 20000 });
            if (await page.locator('#live-session-usage').getAttribute('data-final-usage') !== 'true')
                throw new Error('Session stopped without provider-confirmed final usage');
            const result = await page.locator('[data-chat-role=agent]').allInnerTexts().then(v => v.join('\n'));
            fs.writeFileSync(path.join(artifacts, 'acceptance.json'), JSON.stringify({
                usage: await page.locator('#live-session-usage').innerText(),
                activity: await page.locator('[data-chat-role=activity]').allInnerTexts().then(v => v.join('\n')),
                spoken: await page.locator('[data-chat-role=assistant]:not([data-tool-name])').allInnerTexts().then(v => v.join('\n')),
                normalChatToolOutputInspectable: true,
                normalChatProgressVisible: await page.locator('[data-chat-role=activity]').count() > 0,
                fullResultHasTailFacts: /ZEDER.?8426/i.test(firstResult) && firstResult.includes('863'),
                followupResultHas836: result.includes('836'), finalUsageConfirmed: true, errors
            }, null, 2));
            await page.screenshot({ path: path.join(artifacts, 'live-result.png'), fullPage: true });
        }
        if (errors.length) throw new Error(errors.join('; '));
        console.log(live ? 'Agent handoff: real speech, complete dossier tail facts, spoken result, follow-up and finalization passed.' : 'Agent handoff: direct Blazor circuit, desktop and mobile layout passed.');
    } catch (error) {
        const transport = await page.evaluate(async () => {
            const peers = [];
            for (const peer of window.__handoffPeers || []) {
                const stats = await peer.getStats();
                peers.push({ connection: peer.connectionState, ice: peer.iceConnectionState,
                    audio: [...stats.values()].filter(s => /^(inbound|outbound)-rtp$/.test(s.type) && s.kind === 'audio')
                        .map(s => ({ type: s.type, bytesSent: s.bytesSent, bytesReceived: s.bytesReceived })) });
            }
            return { audioState: window.__handoffAudio?.state, peers, events: window.__handoffEvents || [] };
        }).catch(e => ({ unavailable: e.message }));
        fs.writeFileSync(path.join(artifacts, 'failure.json'), JSON.stringify({ error: error.message, errors, transport,
            status: await page.locator('#live-session-usage').innerText().catch(() => ''),
            activity: await page.locator('[data-chat-role=activity]').allInnerTexts().then(v => v.join('\n')).catch(() => ''),
            usage: await page.locator('#live-session-usage').innerText().catch(() => ''),
            user: await page.locator('[data-chat-role=user]').allInnerTexts().then(v => v.join('\n')).catch(() => ''),
            assistant: await page.locator('[data-chat-role=assistant]:not([data-tool-name])').allInnerTexts().then(v => v.join('\n')).catch(() => ''),
            result: await page.locator('[data-chat-role=agent]').allInnerTexts().then(v => v.join('\n')).catch(() => '')
        }, null, 2));
        await page.screenshot({ path: path.join(artifacts, 'failure.png'), fullPage: true }).catch(() => {});
        throw error;
    } finally {
        try {
            if (!(await page.getByRole('button', { name: 'Stop', exact: true }).isDisabled())) {
                await page.getByRole('button', { name: 'Stop', exact: true }).click();
                await page.waitForFunction(() => [...document.querySelectorAll('button')].some(b => b.textContent.trim() === 'Start' && !b.disabled), null, { timeout: 20000 });
            }
        } catch {}
        await page.close(); await context.close(); await browser.close();
    }
})().catch(e => { console.error(e.stack); process.exitCode = 1; });
