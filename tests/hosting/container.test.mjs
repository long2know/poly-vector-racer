import assert from 'node:assert/strict';
import { execFileSync } from 'node:child_process';
import { basename } from 'node:path';
import { setTimeout as sleep } from 'node:timers/promises';
import test from 'node:test';

const image = process.env.STUNT_CONTAINER_IMAGE;
const docker = args => execFileSync('docker', args, { encoding: 'utf8' }).trim();

test('non-root container can read every published file and serve its actual WASM boot configuration', {
  skip: !image, timeout: 60000,
}, async () => {
  assert.notEqual(docker(['run', '--rm', '--entrypoint', 'id', image, '-u']), '0');
  const unreadable = docker(['run', '--rm', '--entrypoint', 'find', image,
    '/app', '-type', 'f', '!', '-readable', '-print']);
  assert.ok(!unreadable, `Published files are unreadable by the runtime user:\n${unreadable.split('\n').slice(0, 8).join('\n')}`);
  const id = docker(['run', '--detach', '--read-only', '--tmpfs', '/tmp',
    '--cap-drop', 'ALL', '--security-opt', 'no-new-privileges:true',
    '--publish', '127.0.0.1::8080', '--env', 'BASE_URL=/poly-vector-racer', image]);
  try {
    const binding = docker(['port', id, '8080/tcp']).match(/^127\.0\.0\.1:(\d+)$/);
    assert.ok(binding, 'the test container must bind only a dynamic loopback port');
    const root = `http://127.0.0.1:${binding[1]}`;
    let response;
    for (let attempt = 0; attempt < 100; attempt++) {
      try { response = await fetch(`${root}/healthz`); } catch (error) {
        if (!['ECONNREFUSED', 'ECONNRESET', 'UND_ERR_SOCKET'].includes(error.cause?.code)) throw error;
      }
      if (response?.ok) break;
      await sleep(100);
    }
    assert.ok(response?.ok, 'the hardened container must become responsive');
    assert.deepEqual(await response.json(), { status: 'ready', game: 'poly-vector-racer', baseUrl: '/poly-vector-racer' });
    const index = await fetch(`${root}/poly-vector-racer/`);
    assert.equal(index.status, 200);
    assert.match(await index.text(), /<title>Poly Vector Racer<\/title>/);
    const bootFiles = docker(['exec', id, 'find', '/app/wwwroot/_framework',
      '-maxdepth', '1', '-type', 'f', '-name', 'dotnet.boot.*'])
      .split('\n').filter(path => /\.(js|json)$/.test(path));
    assert.ok(bootFiles.length > 0, 'inspect the actual SDK-generated boot metadata, not a guessed SDK filename');
    for (const path of bootFiles) {
      const response = await fetch(`${root}/poly-vector-racer/_framework/${basename(path)}`);
      assert.equal(response.status, 200, `Cannot serve ${path}`);
      assert.match(response.headers.get('content-type'), path.endsWith('.js') ? /javascript/ : /application\/json/);
      const expected = execFileSync('docker', ['exec', id, 'cat', path], { encoding: 'utf8' });
      assert.equal(await response.text(), expected, `Served boot metadata differs: ${path}`);
    }
  } finally { docker(['rm', '--force', id]); }
});
