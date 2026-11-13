import assert from 'node:assert/strict';
import { createHash } from 'node:crypto';
import { readFile } from 'node:fs/promises';
import { dirname, join } from 'node:path';
import { fileURLToPath } from 'node:url';
import test from 'node:test';

const scriptUrl = new URL('./build-listing.mjs', import.meta.url);
const source = JSON.parse(await readFile(new URL('./source.json', import.meta.url), 'utf8'));
const script = (await readFile(scriptUrl, 'utf8'))
  .replace(/^import .*;\r?\n/gm, '')
  .replace('fileURLToPath(import.meta.url)', 'fileURLToPath(scriptUrl)');
const AsyncFunction = Object.getPrototypeOf(async function () {}).constructor;

function release(version, id = version) {
  return { tag_name: `v${version}`, assets: [
    { name: 'package.json', browser_download_url: `https://test/manifest/${id}` },
    { name: 'package.zip', browser_download_url: `https://test/download/${id}`, url: `https://test/zip/${id}` },
  ] };
}

async function run({ releases = [], allow = false, failApi = false, failManifest = false } = {}) {
  const writes = new Map();
  const requested = [];
  const fetch = async (url) => {
    requested.push(url);
    if (url.startsWith('https://api.github.com/')) {
      if (failApi) return { ok: false, status: 503, statusText: 'unavailable' };
      return { ok: true, json: async () => url.endsWith('page=1') ? releases : [] };
    }
    if (url.includes('/manifest/')) {
      if (failManifest) return { ok: false, status: 503, statusText: 'unavailable' };
      const id = url.split('/').pop();
      return { ok: true, text: async () => JSON.stringify({ name: source.packages[0], version: id.split('_')[0] }) };
    }
    return { ok: true, arrayBuffer: async () => new Uint8Array([1, 2, 3]).buffer };
  };
  const dependencies = {
    scriptUrl, createHash, dirname, join, fileURLToPath, Buffer, fetch,
    process: { argv: ['node', 'listing', 'out', ...(allow ? ['--allow-unreachable'] : [])], env: {} },
    console: { log() {}, warn() {} },
    readFile: async () => JSON.stringify(source), mkdir: async () => {},
    writeFile: async (path, body) => writes.set(path, body),
  };
  const execute = new AsyncFunction('deps', `const {${Object.keys(dependencies).join(',')}} = deps;\n${script}`);
  let error;
  try { await execute(dependencies); } catch (failure) { error = failure; }
  return { writes, requested, error, listing: writes.size ? JSON.parse([...writes.values()][0]) : null };
}

test('production API failure does not replace a listing', async () => {
  const result = await run({ failApi: true });
  assert.match(result.error.message, /refusing/);
  assert.equal(result.writes.size, 0);
});

test('production manifest failure does not publish a partial listing', async () => {
  const result = await run({ releases: [release('1.2.0')], failManifest: true });
  assert.match(result.error.message, /incomplete listing/);
  assert.equal(result.writes.size, 0);
});

test('explicit validation fallback permits unreachable manifests', async () => {
  const result = await run({ releases: [release('1.2.0')], failManifest: true, allow: true });
  assert.equal(result.error, undefined);
  assert.deepEqual(result.listing.packages, {});
});

test('duplicate versions retain the newest release and checksum', async () => {
  const result = await run({ releases: [release('1.2.0', '1.2.0_new'), release('1.2.0', '1.2.0_old')] });
  assert.equal(result.error, undefined);
  const version = result.listing.packages[source.packages[0]].versions['1.2.0'];
  assert.equal(version.url, 'https://test/download/1.2.0_new');
  assert.equal(version.zipSHA256, createHash('sha256').update(Buffer.from([1, 2, 3])).digest('hex'));
});

test('release pagination reads beyond the first hundred entries', async () => {
  const result = await run({ releases: Array.from({ length: 100 }, (_, i) => release(`1.0.${i}`)) });
  assert.equal(result.error, undefined);
  assert.equal(result.requested.filter(url => url.startsWith('https://api.github.com/')).length, 2);
  assert.equal(Object.keys(result.listing.packages[source.packages[0]].versions).length, 100);
});
