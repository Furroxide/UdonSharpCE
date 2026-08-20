// Builds a VPM listing (index.json) from this repo's GitHub releases.
//
// Written in-repo rather than using vrchat-community/package-list-action because the inherited
// build-listing.yml never worked here: it referenced steps.deployment.outputs.page_url with no
// step of that id, had no upload/deploy step at all, and its source.json + Website/ inputs have
// never existed on master. A ~100-line generator we own is easier to verify than reverse-engineering
// someone else's Nuke pipeline, and it lets the listing be published as a subdirectory of the docs
// site (GitHub Pages binds one custom domain per repo, not one per path).
//
// Usage: node build-listing.mjs <outputDir>
//   env GITHUB_REPOSITORY  owner/repo      (defaults to the source.json githubRepo)
//   env GITHUB_TOKEN       optional; raises the API rate limit

import { createHash } from 'node:crypto';
import { mkdir, readFile, writeFile } from 'node:fs/promises';
import { dirname, join } from 'node:path';
import { fileURLToPath } from 'node:url';

const here = dirname(fileURLToPath(import.meta.url));
const outputDir = process.argv[2] ?? join(here, 'build');

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

const packages = {};
let releases = [];

try {
  releases = await githubJson(`/repos/${repo}/releases?per_page=100`);
} catch (error) {
  // A brand new repo has no releases yet. That is not a failure: emit a valid, empty listing so the
  // endpoint exists and VCC can add it, and let it populate as releases appear.
  console.warn(`could not read releases (${error.message}); emitting an empty listing`);
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
    manifest = JSON.parse(await res.text());
  } catch (error) {
    console.warn(`skipping release ${release.tag_name}: ${error.message}`);
    continue;
  }

  if (!source.packages.includes(manifest.name)) {
    console.warn(`skipping ${manifest.name}: not listed in source.json`);
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
