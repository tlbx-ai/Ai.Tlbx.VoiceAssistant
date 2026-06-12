import { spawn } from 'node:child_process';
import fs from 'node:fs/promises';
import os from 'node:os';
import path from 'node:path';

const args = new Set(process.argv.slice(2));
const chromePath = process.env.CHROME_PATH ?? 'C:\\Program Files\\Google\\Chrome\\Application\\chrome.exe';
const demoUrl = valueArg('--url') ?? 'http://127.0.0.1:5086/';
const timeoutMs = Number(valueArg('--timeout-ms') ?? 60000);
const startServer = args.has('--start-server');
const configuration = valueArg('--configuration') ?? 'Release';

let demoProcess;

try {
  if (startServer) {
    demoProcess = await startDemoServer(demoUrl, configuration, timeoutMs);
  } else {
    await waitForHttpOk(demoUrl, 10000);
  }

  const result = await runBrowserSmoke(demoUrl, timeoutMs);
  console.log(JSON.stringify(result, null, 2));

  const sessionOk = result.networkResponses.some(response =>
    response.url.includes('/api/voice/direct/session') && response.status === 200);
  const realtimeOk = result.networkResponses.some(response =>
    response.url === 'https://api.openai.com/v1/realtime/calls' && response.status === 201);
  const finalTextOk = result.finalText.includes('Listening') || result.finalText.includes('Connected to OpenAI');

  const blockingErrors = result.errors.filter(error =>
    !(error.type === 'log' && String(error.text ?? '').includes('404')));

  process.exitCode = result.clicked && sessionOk && realtimeOk && finalTextOk && blockingErrors.length === 0 ? 0 : 1;
} catch (error) {
  console.error(error instanceof Error ? error.stack ?? error.message : String(error));
  process.exitCode = 1;
} finally {
  if (demoProcess && !demoProcess.killed) {
    demoProcess.kill('SIGKILL');
  }
}

function valueArg(name) {
  const prefix = `${name}=`;
  const value = process.argv.slice(2).find(arg => arg.startsWith(prefix));
  return value ? value.slice(prefix.length) : undefined;
}

async function startDemoServer(url, configuration, timeoutMs) {
  const parsed = new URL(url);
  const outLog = path.join(os.tmpdir(), 'voiceassistant-direct-demo-smoke.out.log');
  const errLog = path.join(os.tmpdir(), 'voiceassistant-direct-demo-smoke.err.log');
  await fs.rm(outLog, { force: true });
  await fs.rm(errLog, { force: true });

  const out = await fs.open(outLog, 'a');
  const err = await fs.open(errLog, 'a');
  const child = spawn('dotnet', [
    'run',
    '--project',
    'Demo\\Ai.Tlbx.VoiceAssistant.Demo.Web\\Ai.Tlbx.VoiceAssistant.Demo.Web.csproj',
    '-c',
    configuration,
    '--no-build',
    '--urls',
    `${parsed.protocol}//${parsed.host}`
  ], {
    stdio: ['ignore', out.fd, err.fd],
    windowsHide: true
  });

  child.once('exit', async code => {
    await out.close().catch(() => {});
    await err.close().catch(() => {});
    if (code && process.exitCode === undefined) {
      process.exitCode = 1;
    }
  });

  try {
    await waitForHttpOk(url, timeoutMs);
    return child;
  } catch (error) {
    child.kill('SIGKILL');
    throw new Error(`Demo server did not become ready. Logs: ${outLog}, ${errLog}. ${error instanceof Error ? error.message : String(error)}`);
  }
}

async function runBrowserSmoke(url, timeoutMs) {
  const debuggingPort = 9555 + Math.floor(Math.random() * 1000);
  const userDataDir = await fs.mkdtemp(path.join(os.tmpdir(), 'voiceassistant-direct-demo-smoke-'));
  const chrome = spawn(chromePath, [
    '--headless=new',
    '--disable-gpu',
    '--no-first-run',
    '--no-default-browser-check',
    '--use-fake-ui-for-media-stream',
    '--use-fake-device-for-media-stream',
    '--autoplay-policy=no-user-gesture-required',
    `--remote-debugging-port=${debuggingPort}`,
    `--user-data-dir=${userDataDir}`,
    url
  ], {
    stdio: ['ignore', 'pipe', 'pipe'],
    windowsHide: true
  });

  let stderr = '';
  chrome.stderr.on('data', chunk => {
    stderr += chunk.toString();
  });

  try {
    return await runInChrome(debuggingPort, url, browserExpression(timeoutMs), timeoutMs);
  } catch (error) {
    const detail = stderr ? `\nChrome stderr:\n${stderr.slice(-2000)}` : '';
    throw new Error(`${error instanceof Error ? error.message : String(error)}${detail}`);
  } finally {
    chrome.kill('SIGKILL');
    await onceExit(chrome);
    await fs.rm(userDataDir, { recursive: true, force: true, maxRetries: 5, retryDelay: 100 });
  }
}

function browserExpression(timeoutMs) {
  return `(async () => {
const startedAt = performance.now();
const sleep = ms => new Promise(resolve => setTimeout(resolve, ms));
function text() { return document.body?.innerText ?? ''; }
function visibleButtons() {
  return [...document.querySelectorAll('button')].map((button, index) => ({
    index,
    text: button.innerText.trim(),
    disabled: button.disabled
  })).slice(0, 60);
}
while (performance.now() - startedAt < ${Math.min(timeoutMs, 20000)}) {
  if (text().includes('Controls') && [...document.querySelectorAll('button')].some(button => button.innerText.trim() === 'Start')) {
    break;
  }
  await sleep(250);
}
const startButton = [...document.querySelectorAll('button')].find(button => button.innerText.trim() === 'Start' && !button.disabled);
if (!startButton) {
  return {
    clicked: false,
    reason: 'start button not found or disabled',
    finalText: text().slice(0, 6000),
    buttons: visibleButtons()
  };
}
startButton.click();
let finalText = text();
const snapshots = [];
while (performance.now() - startedAt < ${timeoutMs}) {
  finalText = text();
  if (snapshots.length === 0 || snapshots[snapshots.length - 1] !== finalText) {
    snapshots.push(finalText.slice(0, 2000));
    if (snapshots.length > 12) {
      snapshots.shift();
    }
  }
  if (finalText.includes('Listening') || finalText.includes('Connected to OpenAI')) {
    break;
  }
  if (finalText.includes('Error') || finalText.includes('Direct realtime session failed') || finalText.includes('OpenAI Realtime WebRTC call failed')) {
    break;
  }
  await sleep(500);
}
const stopButton = [...document.querySelectorAll('button')].find(button => button.innerText.trim() === 'Stop' && !button.disabled);
if (stopButton) {
  stopButton.click();
  await sleep(500);
}
return {
  clicked: true,
  finalText: finalText.slice(0, 6000),
  buttons: visibleButtons(),
  snapshots
};
})()`;
}

async function runInChrome(port, url, expression, timeoutMs) {
  const page = await waitForPage(port, timeoutMs);
  const ws = new WebSocket(page.webSocketDebuggerUrl);
  await new Promise((resolve, reject) => {
    const timer = setTimeout(() => reject(new Error('DevTools websocket timeout.')), 5000);
    ws.addEventListener('open', () => {
      clearTimeout(timer);
      resolve();
    }, { once: true });
    ws.addEventListener('error', () => {
      clearTimeout(timer);
      reject(new Error('DevTools websocket error.'));
    }, { once: true });
  });

  let id = 0;
  const pending = new Map();
  const networkResponses = [];
  const webSockets = [];
  const errors = [];
  const consoleMessages = [];

  ws.addEventListener('message', event => {
    const message = JSON.parse(event.data);
    const request = pending.get(message.id);
    if (request) {
      pending.delete(message.id);
      if (message.error) {
        request.reject(new Error(JSON.stringify(message.error)));
      } else {
        request.resolve(message.result);
      }
      return;
    }

    if (message.method === 'Network.responseReceived') {
      const response = message.params?.response;
      if (response?.url?.includes('/api/voice/direct/') || response?.url === 'https://api.openai.com/v1/realtime/calls') {
        networkResponses.push({
          url: response.url,
          status: response.status,
          mimeType: response.mimeType
        });
      }
    } else if (message.method === 'Network.webSocketCreated') {
      const requestUrl = message.params?.url ?? '';
      if (requestUrl.includes('/api/voice/direct/control/')) {
        webSockets.push(requestUrl);
      }
    } else if (message.method === 'Network.loadingFailed') {
      const errorText = message.params?.errorText ?? '';
      if (!errorText.includes('net::ERR_ABORTED')) {
        errors.push({ type: 'network', errorText });
      }
    } else if (message.method === 'Runtime.exceptionThrown') {
      errors.push({ type: 'exception', text: message.params?.exceptionDetails?.text });
    } else if (message.method === 'Runtime.consoleAPICalled') {
      const type = message.params?.type;
      const text = (message.params?.args ?? []).map(arg => arg.value ?? arg.description ?? '').join(' ');
      consoleMessages.push({ type, text });
      if (type === 'error') {
        errors.push({ type: 'console', text });
      }
    } else if (message.method === 'Log.entryAdded') {
      const entry = message.params?.entry;
      if (entry?.level === 'error') {
        errors.push({ type: 'log', text: entry.text });
      }
    }
  });

  const send = (method, params = {}) => new Promise((resolve, reject) => {
    const messageId = ++id;
    pending.set(messageId, { resolve, reject });
    ws.send(JSON.stringify({ id: messageId, method, params }));
  });

  try {
    await send('Page.enable');
    await send('Runtime.enable');
    await send('Log.enable');
    await send('Network.enable');
    await send('Page.navigate', { url });
    await delay(1000);

    const result = await send('Runtime.evaluate', {
      expression,
      awaitPromise: true,
      returnByValue: true,
      timeout: timeoutMs
    });

    if (result.exceptionDetails) {
      errors.push({ type: 'evaluate', text: JSON.stringify(result.exceptionDetails) });
    }

    return {
      ...(result.result?.value ?? {}),
      networkResponses,
      webSockets,
      errors,
      consoleMessages: consoleMessages.slice(-30)
    };
  } finally {
    ws.close();
  }
}

async function waitForPage(port, timeoutMs) {
  const deadline = Date.now() + timeoutMs;
  while (Date.now() < deadline) {
    try {
      const response = await fetch(`http://127.0.0.1:${port}/json/list`);
      const targets = await response.json();
      const page = targets.find(target => target.type === 'page' && target.webSocketDebuggerUrl);
      if (page) {
        return page;
      }
    } catch {
    }
    await delay(100);
  }

  throw new Error('Chrome DevTools page target did not become available.');
}

async function waitForHttpOk(url, timeoutMs) {
  const deadline = Date.now() + timeoutMs;
  while (Date.now() < deadline) {
    try {
      const response = await fetch(url);
      if (response.ok) {
        return;
      }
    } catch {
    }
    await delay(250);
  }

  throw new Error(`${url} did not become ready.`);
}

function onceExit(child) {
  if (child.exitCode !== null || child.signalCode !== null) {
    return Promise.resolve();
  }

  return new Promise(resolve => child.once('exit', resolve));
}

function delay(ms) {
  return new Promise(resolve => setTimeout(resolve, ms));
}
