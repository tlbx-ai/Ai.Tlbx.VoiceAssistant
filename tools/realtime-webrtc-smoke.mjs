import http from 'node:http';
import { spawn } from 'node:child_process';
import fs from 'node:fs/promises';
import os from 'node:os';
import path from 'node:path';

const apiKey = process.env.OPENAI_API_KEY;
const providedClientSecret = process.env.OPENAI_REALTIME_CLIENT_SECRET;
if (!apiKey && !providedClientSecret) {
  console.error('OPENAI_API_KEY is not set.');
  process.exit(2);
}

const chromePath = process.env.CHROME_PATH ?? 'C:\\Program Files\\Google\\Chrome\\Application\\chrome.exe';
const model = process.env.OPENAI_REALTIME_MODEL ?? 'gpt-realtime-2';
const voice = process.env.OPENAI_REALTIME_VOICE ?? 'marin';
const includeLogprobs = process.argv.includes('--include-logprobs');
const daiInputConfig = process.argv.includes('--dai-input-config');

const session = {
  type: 'realtime',
  model,
  audio: {
    output: { voice }
  }
};

if (includeLogprobs) {
  session.include = ['item.input_audio_transcription.logprobs'];
}

if (daiInputConfig) {
  session.output_modalities = ['audio'];
  session.max_output_tokens = 'inf';
  session.tool_choice = 'auto';
  session.parallel_tool_calls = true;
  session.audio.input = {
    format: { type: 'audio/pcm', rate: 24000 },
    noise_reduction: { type: 'near_field' },
    transcription: {
      model: 'gpt-realtime-whisper',
      language: 'de'
    },
    turn_detection: {
      type: 'server_vad',
      threshold: 0.5,
      prefix_padding_ms: 300,
      silence_duration_ms: 500,
      create_response: true,
      interrupt_response: true
    }
  };
}

async function createClientSecret() {
  if (providedClientSecret) {
    return providedClientSecret;
  }

  const response = await fetch('https://api.openai.com/v1/realtime/client_secrets', {
    method: 'POST',
    headers: {
      Authorization: `Bearer ${apiKey}`,
      'Content-Type': 'application/json',
      'OpenAI-Safety-Identifier': 'voiceassistant-local-webrtc-smoke'
    },
    body: JSON.stringify({
      session,
      expires_after: {
        anchor: 'created_at',
        seconds: 600
      }
    })
  });

  const text = await response.text();
  if (!response.ok) {
    throw new Error(`client_secrets failed: ${response.status} ${text}`);
  }

  const payload = JSON.parse(text);
  if (!payload.value) {
    throw new Error(`client_secrets response did not include value: ${text}`);
  }

  return payload.value;
}

function browserExpression(baseUrl) {
  return `(async () => {
try {
  const tokenResponse = await fetch(${JSON.stringify(new URL('/token', baseUrl).toString())});
  const tokenPayload = await tokenResponse.json();
  if (!tokenResponse.ok) {
    throw new Error('token failed: ' + JSON.stringify(tokenPayload));
  }

  const pc = new RTCPeerConnection();
  const stream = await navigator.mediaDevices.getUserMedia({ audio: true });
  pc.addTrack(stream.getAudioTracks()[0], stream);
  pc.createDataChannel('oai-events');

  const offer = await pc.createOffer();
  await pc.setLocalDescription(offer);

  const abort = new AbortController();
  const timeout = setTimeout(() => abort.abort('openai-calls-timeout'), 8000);
  const response = await fetch('https://api.openai.com/v1/realtime/calls', {
    method: 'POST',
    headers: {
      Authorization: 'Bearer ' + tokenPayload.value,
      'Content-Type': 'application/sdp'
    },
    body: offer.sdp,
    signal: abort.signal
  });
  clearTimeout(timeout);

  const body = await response.text();

  if (response.ok) {
    await pc.setRemoteDescription({ type: 'answer', sdp: body });
  }

  for (const track of stream.getTracks()) track.stop();
  pc.close();
  return {
    status: response.status,
    ok: response.ok,
    contentType: response.headers.get('content-type'),
    body: body.slice(0, 1000)
  };
} catch (error) {
  return { error: error instanceof Error ? error.message : String(error) };
}
})()`;
}

function html() {
  return '<!doctype html><html><body>Realtime WebRTC smoke</body></html>';
}

const server = http.createServer(async (request, response) => {
  try {
    if (request.url === '/token') {
      const value = await createClientSecret();
      response.writeHead(200, {
        'Content-Type': 'application/json',
        'Access-Control-Allow-Origin': '*'
      });
      response.end(JSON.stringify({ value }));
      return;
    }

    response.writeHead(200, { 'Content-Type': 'text/html' });
    response.end(html());
  } catch (error) {
    response.writeHead(500, { 'Content-Type': 'application/json' });
    response.end(JSON.stringify({ error: error instanceof Error ? error.message : String(error) }));
  }
});

server.listen(0, '127.0.0.1', async () => {
  const address = server.address();
  const url = `http://127.0.0.1:${address.port}/`;
  const debuggingPort = 9555 + Math.floor(Math.random() * 1000);
  const userDataDir = await fs.mkdtemp(path.join(os.tmpdir(), 'voiceassistant-webrtc-smoke-'));
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
    stdio: ['ignore', 'pipe', 'pipe']
  });

  let stderr = '';
  chrome.stderr.on('data', chunk => {
    stderr += chunk.toString();
  });

  try {
    const result = await runInChrome(debuggingPort, url, browserExpression(url), 30000);
    console.log(JSON.stringify(result, null, 2));
    process.exitCode = result?.ok === true ? 0 : 1;
  } catch (error) {
    console.error(error instanceof Error ? error.message : String(error));
    if (stderr) {
      console.error(stderr.slice(-2000));
    }
    process.exitCode = 1;
  } finally {
    chrome.kill('SIGKILL');
    await new Promise(resolve => chrome.once('exit', resolve));
    server.close();
    await fs.rm(userDataDir, { recursive: true, force: true, maxRetries: 5, retryDelay: 100 });
  }
});

async function runInChrome(port, url, expression, timeoutMs) {
  const deadline = Date.now() + timeoutMs;
  let page;
  while (Date.now() < deadline) {
    try {
      const response = await fetch(`http://127.0.0.1:${port}/json/list`);
      const targets = await response.json();
      page = targets.find(target => target.type === 'page' && target.webSocketDebuggerUrl);
      if (page) {
        break;
      }
    } catch {
      await delay(100);
    }
  }

  if (!page) {
    throw new Error('Chrome DevTools page target did not become available.');
  }

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
  ws.addEventListener('message', event => {
    const message = JSON.parse(event.data);
    const request = pending.get(message.id);
    if (!request) {
      return;
    }

    pending.delete(message.id);
    if (message.error) {
      request.reject(new Error(JSON.stringify(message.error)));
    } else {
      request.resolve(message.result);
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
    await send('Page.navigate', { url });
    await delay(500);
    const result = await send('Runtime.evaluate', {
      expression,
      awaitPromise: true,
      returnByValue: true,
      timeout: timeoutMs
    });

    if (result.exceptionDetails) {
      throw new Error(JSON.stringify(result.exceptionDetails));
    }

    return result.result?.value;
  } finally {
    ws.close();
  }
}

function delay(ms) {
  return new Promise(resolve => setTimeout(resolve, ms));
}
