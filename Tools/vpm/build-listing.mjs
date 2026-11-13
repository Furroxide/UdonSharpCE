// Builds a VPM listing (index.json) from this repo's GitHub releases.
//
// Written in-repo rather than using vrchat-community/package-list-action because the inherited
// build-listing.yml never worked here: it referenced steps.deployment.outputs.page_url with no
// step of that id, had no upload/deploy step at all, and its source.json + Website/ inputs have
// never existed on master. A ~100-line generator we own is easier to verify than reverse-engineering
// someone else's Nuke pipeline, and it lets the listing be published as a subdirectory of the docs
// site (GitHub Pages binds one custom domain per repo, not one per path).
//
// Usage: node build-listing.mjs <outputDir> [--allow-unreachable]
//   env GITHUB_REPOSITORY  owner/repo      (defaults to the source.json githubRepo)
//   env GITHUB_TOKEN       optional; raises the API rate limit
//
//   --allow-unreachable    if the releases API cannot be read, write an empty listing and exit 0
//                          instead of failing. For checks that only care that generation works
//                          (validate.yml); never for a run whose output gets deployed.

import { createHash } from 'node:crypto';
import { mkdir, readFile, writeFile } from 'node:fs/promises';
import { dirname, join } from 'node:path';
import { fileURLToPath } from 'node:url';

const here = dirname(fileURLToPath(import.meta.url));
const args = process.argv.slice(2);
const allowUnreachable = args.includes('--allow-unreachable');
const outputDir = args.find((a) => !a.startsWith('--')) ?? join(here, 'build');

const source = JSON.parse(await readFile(join(here, 'source.json'), 'utf8'));
const repo = process.env.GITHUB_REPOSITORY || source.githubRepo;
const token = process.env.GITHUB_TOKEN;

const headers = {
  accept: 'application/vnd.github+json',
  'user-agent': 'udonsharpce-listing-builder',
  ...(token ? { authorization: `Bearer ${token}` } : {}),
};

async function githubJson(path) {
  const res = await fetch(`https://api.github.com${path}`, { headers });
  if (!res.ok) throw new Error(`GitHub ${path} -> ${res.status} ${res.statusText}`);
  return res.json();
}

/** Downloads an asset and returns its sha256, which VPM requires for every version entry. */
async function sha256Of(url) {
  const res = await fetch(url, { headers: { ...headers, accept: 'application/octet-stream' } });
  if (!res.ok) throw new Error(`asset ${url} -> ${res.status}`);
  return createHash('sha256').update(Buffer.from(await res.arrayBuffer())).digest('hex');
}

/** Every release, following pagination: one page is 100, and a truncated list would silently drop the oldest versions. */
async function allReleases() {
  const all = [];
  for (let page = 1; ; page++) {
    const batch = await githubJson(`/repos/${repo}/releases?per_page=100&page=${page}`);
    all.push(...batch);
    if (batch.length < 100) return all;
  }
}

const packages = {};
let releases = [];

try {
  // A repo with no releases answers 200 with [], which yields a valid empty listing below. That is
  // the only "empty" that is not a failure.
  releases = await allReleases();
} catch (error) {
  // An unreadable API (outage, rate limit, bad token) is NOT "no releases". This used to fall
  // through and write an empty listing, which publish-site.yml then deployed -- one API blip during
  // a docs push would have made every published version disappear from VCC until the next deploy.
  // Thrown rather than process.exit(1): exiting while the failed fetch's socket is still closing
  // trips a libuv assertion on Windows, and an uncaught top-level error exits 1 on every platform.
  if (!allowUnreachable) {
    throw new Error(
      `could not read releases for ${repo}: ${error.message} -- refusing to write a listing that ` +
        'would replace the published one with nothing',
    );
  }
  console.warn(`could not read releases (${error.message}); emitting an empty listing (--allow-unreachable)`);
}

for (const release of releases) {
  if (release.draft) continue;

  // Pair each release's package.json asset with its zip. release.yml uploads both.
  const manifestAsset = release.assets.find((a) => a.name === 'package.json');
  const zipAsset = release.assets.find((a) => a.name.endsWith('.zip'));
  if (!manifestAsset || !zipAsset) {
    console.warn(`skipping release ${release.tag_name}: needs both package.json and a .zip asset`);
    continue;
  }

  let manifest;
  try {
    const res = await fetch(manifestAsset.browser_download_url, { headers });
    if (!res.ok) throw new Error(`package.json asset -> ${res.status} ${res.statusText}`);
    manifest = JSON.parse(await res.text());
  } catch (error) {
    if (!allowUnreachable) throw new Error(`could not read manifest for ${release.tag_name}: ${error.message} -- refusing to publish an incomplete listing`);
    console.warn(`skipping release ${release.tag_name}: ${error.message}`);
    continue;
  }

  if (!source.packages.includes(manifest.name)) {
    console.warn(`skipping ${manifest.name}: not listed in source.json`);
    continue;
  }

  // Releases arrive newest first. If a version was released twice, the newest is the one already
  // recorded; letting an older re-release overwrite it would pair the version with a stale zip.
  if (packages[manifest.name]?.versions[manifest.version]) {
    console.warn(`skipping release ${release.tag_name}: ${manifest.name}@${manifest.version} is already listed from a newer release`);
    continue;
  }

  manifest.url = zipAsset.browser_download_url;
  manifest.zipSHA256 = await sha256Of(zipAsset.url);

  packages[manifest.name] ??= { versions: {} };
  packages[manifest.name].versions[manifest.version] = manifest;
  console.log(`added ${manifest.name}@${manifest.version}`);
}

const listing = {
  name: source.name,
  id: source.id,
  url: source.url,
  author: source.author,
  description: source.description,
  infoLink: source.infoLink,
  packages,
};

await mkdir(outputDir, { recursive: true });
await writeFile(join(outputDir, 'index.json'), `${JSON.stringify(listing, null, 2)}\n`);

const count = Object.values(packages).reduce((n, p) => n + Object.keys(p.versions).length, 0);
console.log(`wrote ${join(outputDir, 'index.json')} (${count} package version(s))`);
