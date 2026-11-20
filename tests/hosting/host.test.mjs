import assert from 'node:assert/strict';
import { spawn } from 'node:child_process';
import { once } from 'node:events';
import { mkdir, mkdtemp, rm, symlink, writeFile } from 'node:fs/promises';
import { resolve } from 'node:path';
import { setTimeout as sleep } from 'node:timers/promises';
import test from 'node:test';

const assembly = resolve('src/StuntRacer.Host/bin/Release/net10.0/StuntRacer.Host.dll');
const bundle = resolve('src/StuntRacer.Browser/bin/Release/net10.0/browser-wasm/AppBundle');

async function withHost(mount, check, sourcePackage = '', catalog = '', flightPackage = '', worldFlight = '') {
  const child = spawn('bash', ['scripts/dotnet.sh', assembly, '--urls', 'http://127.0.0.1:0'], {
    env: { ...process.env, BASE_URL: mount, STUNT_BUNDLE: bundle,
      STUNT_AMIGA_PACKAGE: sourcePackage, STUNT_TRACK_CATALOG: catalog, STUNT_FLIGHT_PACKAGE: flightPackage,
      STUNT_WORLD_FLIGHT: worldFlight },
    stdio: ['ignore', 'pipe', 'pipe'],
  });
  let log = '', timeout;
  const listening = new Promise((resolve, reject) => {
    child.stdout.on('data', data => {
      log += data.toString();
      const match = log.match(/Now listening on: (http:\/\/127\.0\.0\.1:\d+)/);
      if (match) resolve(match[1]);
    });
    child.stderr.on('data', data => { log += data.toString(); });
    child.once('error', reject);
    child.once('exit', code => reject(new Error(`Host exited ${code}. Build the project first.\n${log}`)));
    timeout = setTimeout(() => reject(new Error(`Host did not listen.\n${log}`)), 15000);
  });
  try {
    const url = await listening;
    clearTimeout(timeout);
    await check(url);
  } finally {
    clearTimeout(timeout);
    if (child.exitCode === null && child.signalCode === null) {
      const exited = once(child, 'exit');
      child.kill('SIGTERM');
      await Promise.race([exited, sleep(5000, undefined, { ref: false })]);
      if (child.exitCode === null && child.signalCode === null) {
        child.kill('SIGKILL');
        await exited;
      }
    }
  }
}

test('root deployment serves the actual WASM resources with correct MIME types and compression', async () => {
  await withHost('', async url => {
    const page = await fetch(url);
    assert.equal(page.status, 200);
    assert.match(await page.text(), /src="\.\/main\.js"/);
    const symbols = await fetch(`${url}/_framework/dotnet.native.js.symbols`);
    assert.equal(symbols.status, 200, 'the runtime requests this deployed symbol file during startup');
    assert.equal(symbols.headers.get('content-type'), 'application/octet-stream');
    const wasm = await fetch(`${url}/_framework/dotnet.native.wasm`, { headers: { 'Accept-Encoding': 'br' } });
    assert.equal(wasm.status, 200);
    assert.equal(wasm.headers.get('content-type'), 'application/wasm');
    assert.equal(wasm.headers.get('content-encoding'), 'br');
    assert.deepEqual([...new Uint8Array(await wasm.arrayBuffer()).slice(0, 4)], [0, 97, 115, 109]);
    assert.equal((await (await fetch(`${url}/healthz`)).json()).baseUrl, '');
    assert.equal((await (await fetch(`${url}/runtime-config.json`)).json()).sourcePackageAvailable, false);
    assert.equal((await fetch(`${url}/source-package.json`)).status, 404);
    assert.equal((await fetch(`${url}/flight-package.json`)).status, 404);
    assert.equal((await fetch(`${url}/world-flight/manifest.json`)).status, 404);
    assert.equal((await fetch(`${url}/.env`)).status, 404);
  });
});

test('geographic terrain serves only declared detail patches and city databases at the mounted revision', async () => {
  const root = resolve('artifacts/hosting');
  await mkdir(root, { recursive: true });
  const folder = await mkdtemp(resolve(root, 'world-flight-'));
  const manifest = resolve(folder, 'manifest.json'), version = '0123456789abcdef0123';
  const regions = ['conus', 'alaska', 'hawaii'].map(id => ({
    id, coverage: [[0, 0, 1, 1], [0, 0, 2, 2], [0, 0, 4, 4]]
  }));
  const conus = regions[0];
  conus.coverage.push([0, 0, 2, 2]);
  conus.patches = [[], [], [], [[4, 0, 6, 2]]];
  conus.cities = [{ id: 'seattle' }, { id: 'san-francisco' }];
  const catalogData = { schemaVersion: 1, kind: 'world-flight-terrain', side: 33, version,
    spacing: Array.from({ length: 9 }, (_, i) => 8192 >> i), regions };
  const body = JSON.stringify(catalogData);
  const tile = Buffer.alloc(16 + 33 * 33 * 3);
  tile.write('WFT1');
  tile.writeInt16LE(745, 16);
  try {
    await writeFile(manifest, body);
    const footprints = '{"license":"ODbL-1.0","buildings":[]}';
    for (const city of [...conus.cities.map(city => city.id), 'other'])
      await writeFile(resolve(folder, `${city}-buildings.json`), footprints);
    for (const id of ['conus', 'alaska', 'hawaii']) {
      const tiles = resolve(folder, `tiles/${id}/0/0`);
      await mkdir(tiles, { recursive: true });
      await writeFile(resolve(tiles, '0.bin'), tile);
    }
    for (const x of [2, 4]) {
      const tiles = resolve(folder, `tiles/conus/3/${x}`);
      await mkdir(tiles, { recursive: true });
      await writeFile(resolve(tiles, '0.bin'), tile);
    }
    await withHost('/map', async url => {
      const config = await (await fetch(`${url}/map/runtime-config.json`)).json();
      assert.equal(config.worldFlightUrl, './world-flight/manifest.json');
      const catalog = await fetch(new URL(config.worldFlightUrl, `${url}/map/`));
      assert.equal(await catalog.text(), body);
      assert.equal(catalog.headers.get('cache-control'), 'no-store');
      const base = `${url}/map/world-flight/${version}`;
      const heightData = await fetch(`${base}/tiles/conus/3/4/0.bin`);
      assert.equal(heightData.status, 200);
      assert.match(heightData.headers.get('cache-control'), /immutable/);
      assert.deepEqual(Buffer.from(await heightData.arrayBuffer()), tile);
      for (const { id } of conus.cities)
        assert.equal(await (await fetch(`${base}/${id}-buildings.json`)).text(), footprints);
      for (const path of [`${base}/tiles/conus/3/2/0.bin`, `${base}/tiles/unknown/0/0/0.bin`, `${base}/other-buildings.json`,
        `${url}/map/world-flight/old/tiles/conus/0/0/0.bin`, `${url}/world-flight/manifest.json`])
        assert.equal((await fetch(path)).status, 404);
    }, '', '', '', manifest);
    conus.patches[3][0] = [0, 0, 4, 2];
    await writeFile(manifest, JSON.stringify(catalogData));
    await assert.rejects(withHost('', () => assert.fail('Overlapping patches must be rejected'),
      '', '', '', manifest), /detail patches must not overlap/);
  } finally { await rm(folder, { recursive: true, force: true }); }
});

test('original flight data is opt-in, mount-relative, non-cached and never served from a caller-selected path', async () => {
    const root = resolve('artifacts/hosting');
    await mkdir(root, { recursive: true });
    const directory = await mkdtemp(resolve(root, 'flight-'));
    const path = resolve(directory, 'original-flight.json');
    const payload = JSON.stringify({ schemaVersion: 1, kind: 'fa18-amiga-runtime', probe: 'synthetic-transport-only' });
    await writeFile(path, payload);
    try {
      await withHost('/stunt-car-racer', async url => {
        const base = `${url}/stunt-car-racer/`;
        const config = await (await fetch(`${base}runtime-config.json`)).json();
        assert.equal(config.flightPackageAvailable, true);
        assert.equal(config.flightPackageUrl, './flight-package.json');
        assert.ok(!JSON.stringify(config).includes(directory));
        const response = await fetch(new URL(config.flightPackageUrl, base));
        assert.equal(response.status, 200);
        assert.equal(response.headers.get('cache-control'), 'no-store');
        assert.equal(await response.text(), payload);
        assert.equal((await fetch(`${url}/flight-package.json`)).status, 404);
        assert.equal((await fetch(`${base}original-flight.json`)).status, 404);
        assert.equal(await (await fetch(`${base}flight-package.json?path=../../other.json`)).text(), payload);
      }, '', '', path);
      await writeFile(path, JSON.stringify({ schemaVersion: 1, kind: 'not-flight' }));
      await assert.rejects(withHost('', () => assert.fail('Wrong-kind original data must not start'), '', '', path),
        /not a supported fa18-amiga-runtime/);
    } finally { await rm(directory, { recursive: true, force: true }); }
});

test('external source packages are opt-in, mount-relative and never cached or copied into static assets', async () => {
    const root = resolve('artifacts/hosting');
    await mkdir(root, { recursive: true });
    const directory = await mkdtemp(resolve(root, 'source-'));
    const path = resolve(directory, 'package.json');
    const payload = JSON.stringify({ schemaVersion: 1, kind: 'stunt-amiga-source-runtime', probe: 'synthetic-host-fixture' });
    await writeFile(path, payload);
    try {
      await withHost('/stunt-car-racer', async url => {
        const base = `${url}/stunt-car-racer/`;
        const configuration = await fetch(`${base}runtime-config.json`);
        assert.equal(configuration.headers.get('cache-control'), 'no-store');
        const config = await configuration.json();
        assert.equal(config.sourcePackageAvailable, true);
        assert.equal(config.sourcePackageUrl, './source-package.json?id=reference');
        assert.deepEqual(config.tracks.map(({ id, status }) => ({ id, status })),
          [{ id: 'reference', status: 'available' }]);
        const response = await fetch(new URL(config.sourcePackageUrl, base));
        assert.equal(response.status, 200);
        assert.equal(response.headers.get('cache-control'), 'no-store');
        assert.equal(await response.text(), payload);
        assert.equal((await fetch(`${url}/source-package.json`)).status, 404);
        assert.equal((await fetch(`${base}artifacts/hosting/package.json`)).status, 404);
      }, path);
    } finally {
      await rm(directory, { recursive: true, force: true });
    }
  });

test('external catalog exposes distinct mounted packages and explicit unsupported entries without filesystem paths', async () => {
    const root = resolve('artifacts/hosting');
    await mkdir(root, { recursive: true });
    const directory = await mkdtemp(resolve(root, 'catalog-'));
    const manifest = resolve(directory, 'track-catalog.json');
    const packageFor = name => JSON.stringify({ schemaVersion: 1, kind: 'stunt-amiga-source-runtime', name });
    const payloads = { first: packageFor('First fixture'), second: packageFor('Second fixture') };
    await writeFile(resolve(directory, 'first.json'), payloads.first);
    await writeFile(resolve(directory, 'second.json'), payloads.second);
    await writeFile(manifest, JSON.stringify({ schemaVersion: 1, tracks: [
      { id: 'first', name: 'First fixture', group: 'Reference', status: 'available', packagePath: 'first.json' },
      { id: 'second', name: 'Second fixture', group: 'TNT', status: 'available', packagePath: 'second.json' },
      { id: 'unsupported', name: 'Unsupported fixture', group: 'TNT', status: 'unsupported', packagePath: null,
        reason: 'Missing source height samples; no replacement road was invented.' },
    ] }));
    try {
      await withHost('/stunt-car-racer', async url => {
        const base = `${url}/stunt-car-racer/`;
        const response = await fetch(`${base}runtime-config.json`);
        assert.equal(response.headers.get('cache-control'), 'no-store');
        const raw = await response.text(), config = JSON.parse(raw);
        assert.equal(config.sourcePackageUrl, './source-package.json?id=first');
        assert.equal(config.tracks.length, 3);
        assert.ok(!raw.includes(directory) && !raw.includes('packagePath'), 'configuration must not expose host paths');
        for (const id of ['first', 'second']) {
          const track = config.tracks.find(track => track.id === id);
          assert.equal(track.sourcePackageUrl, `./source-package.json?id=${id}`);
          const packageResponse = await fetch(new URL(track.sourcePackageUrl, base));
          assert.equal(packageResponse.headers.get('cache-control'), 'no-store');
          assert.equal(await packageResponse.text(), payloads[id]);
        }
        const unsupported = config.tracks.find(track => track.id === 'unsupported');
        assert.equal(unsupported.status, 'unsupported');
        assert.equal(unsupported.sourcePackageUrl, null);
        assert.match(unsupported.reason, /Missing source height samples/);
        for (const id of ['unsupported', 'unknown', '../first.json']) {
          assert.equal((await fetch(`${base}source-package.json?id=${encodeURIComponent(id)}`)).status, 404);
        }
        assert.equal((await fetch(`${url}/source-package.json?id=first`)).status, 404);
        assert.equal((await fetch(`${base}first.json`)).status, 404);
      }, '', manifest);
    } finally { await rm(directory, { recursive: true, force: true }); }
  });

test('invalid catalogs fail startup instead of serving ambiguous, escaping or linked packages', async t => {
    const root = resolve('artifacts/hosting');
    await mkdir(root, { recursive: true });
    const directory = await mkdtemp(resolve(root, 'invalid-catalog-'));
    const manifest = resolve(directory, 'track-catalog.json');
    const packagePath = resolve(directory, 'package.json');
    const entry = { id: 'fixture', name: 'Fixture', group: 'Reference', status: 'available', packagePath: 'package.json' };
    await writeFile(packagePath, JSON.stringify({ schemaVersion: 1, kind: 'stunt-amiga-source-runtime', name: 'Fixture' }));
    await symlink(packagePath, resolve(directory, 'linked.json'));
    try {
      for (const [name, tracks, expected] of [
        ['duplicate identities', [entry, entry], /duplicate external track id/],
        ['parent traversal', [{ ...entry, packagePath: '../package.json' }], /beneath the catalog directory/],
        ['absolute package paths', [{ ...entry, packagePath }], /beneath the catalog directory/],
        ['linked packages', [{ ...entry, packagePath: 'linked.json' }], /beneath the catalog directory/],
        ['unsupported without explanation', [{ ...entry, status: 'unsupported', packagePath: null }], /reason/],
      ]) {
        await t.test(name, async () => {
          await writeFile(manifest, JSON.stringify({ schemaVersion: 1, tracks }));
          await assert.rejects(withHost('', () => assert.fail('Invalid configuration must not start'), '', manifest), expected);
        });
      }
      await t.test('conflicting single and catalog configuration', async () => {
        await assert.rejects(withHost('', () => assert.fail('Conflicting configuration must not start'), packagePath, manifest),
          /either STUNT_TRACK_CATALOG or STUNT_AMIGA_PACKAGE/);
      });
    } finally { await rm(directory, { recursive: true, force: true }); }
});

test('runtime BASE_URL mounts HTML, modules and WASM without rebuilding the bundle', async () => {
  await withHost('/stunt-car-racer/', async url => {
    const canonical = await fetch(`${url}/stunt-car-racer?from=proxy`, { redirect: 'manual' });
    assert.equal(canonical.status, 308);
    assert.equal(canonical.headers.get('location'), '/stunt-car-racer/?from=proxy');
    assert.equal((await fetch(`${url}/`)).status, 404);
    assert.equal((await fetch(`${url}/main.js`)).status, 404);
    assert.equal((await fetch(`${url}/stunt-car-racer-extra/main.js`)).status, 404);
    const page = await fetch(`${url}/stunt-car-racer/`);
    assert.equal(page.status, 200);
    assert.match(await page.text(), /href="\.\/styles\.css"/);
    const entry = await fetch(`${url}/stunt-car-racer/main.js`);
    assert.equal(entry.status, 200);
    assert.match(await entry.text(), /import\('\.\/_framework\/dotnet\.js'\)/);
    assert.equal((await fetch(`${url}/stunt-car-racer/engine/input.js`)).status, 200);
    assert.equal((await fetch(`${url}/stunt-car-racer/_framework/StuntRacer.Core.wasm`)).status, 200);
    const health = await (await fetch(`${url}/healthz`)).json();
    assert.equal(health.status, 'ready');
    assert.equal(health.baseUrl, '/stunt-car-racer');
  });
});
