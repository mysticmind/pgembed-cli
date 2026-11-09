// Builds the npm packages from the Native AOT builds: one package per platform, holding the executable and
// libpg_query, and the pgembed package, whose launcher runs the one npm installed for the machine.
//
//   node npm/build.mjs <builds> <version> <out> [--all]
//
// <builds> has a folder per .NET runtime identifier (osx-arm64, linux-musl-x64, ...) with the files that ship:
// tools/any/<rid>/ of the NuGet package, or the output of dotnet publish -r <rid>. With --all (publishing),
// every platform must be there; without it (CI), the platforms present are built.
// <out>/platforms/<name> and <out>/pgembed are the packages; publish the platform ones first.

import fs from 'node:fs';
import path from 'node:path';
import { fileURLToPath } from 'node:url';

const here = path.dirname(fileURLToPath(import.meta.url));
const root = path.resolve(here, '..');

// .NET runtime identifier -> npm platform, as the launcher names it: <process.platform>-<process.arch>[-musl]
const PLATFORMS = {
  'osx-arm64': { name: 'darwin-arm64', os: 'darwin', cpu: 'arm64', label: 'macOS arm64' },
  'osx-x64': { name: 'darwin-x64', os: 'darwin', cpu: 'x64', label: 'macOS x64' },
  'linux-x64': { name: 'linux-x64', os: 'linux', cpu: 'x64', libc: 'glibc', label: 'Linux x64 (glibc)' },
  'linux-arm64': { name: 'linux-arm64', os: 'linux', cpu: 'arm64', libc: 'glibc', label: 'Linux arm64 (glibc)' },
  'linux-musl-x64': { name: 'linux-x64-musl', os: 'linux', cpu: 'x64', libc: 'musl', label: 'Linux x64 (musl, Alpine)' },
  'linux-musl-arm64': { name: 'linux-arm64-musl', os: 'linux', cpu: 'arm64', libc: 'musl', label: 'Linux arm64 (musl, Alpine)' },
  'win-x64': { name: 'win32-x64', os: 'win32', cpu: 'x64', label: 'Windows x64' },
};

const [builds, version, out, ...flags] = process.argv.slice(2);
if (!builds || !version || !out) {
  console.error('usage: node npm/build.mjs <builds> <version> <out> [--all]');
  process.exit(2);
}
if (!/^\d+\.\d+\.\d+(-[0-9A-Za-z.-]+)?$/.test(version)) {
  console.error(`not a version: ${version}`);
  process.exit(2);
}
const all = flags.includes('--all');

const template = JSON.parse(fs.readFileSync(path.join(here, 'pgembed', 'package.json'), 'utf8'));
const shared = {
  version,
  homepage: template.homepage,
  bugs: template.bugs,
  repository: template.repository,
  license: template.license,
  author: template.author,
};

fs.rmSync(out, { recursive: true, force: true });
const built = [];

for (const [rid, platform] of Object.entries(PLATFORMS)) {
  const source = path.join(builds, rid);
  if (!fs.existsSync(source)) {
    if (all) throw new Error(`no build for ${rid} in ${builds}`);
    continue;
  }

  // only the executable and the parser library: publish output also has symbols
  const executable = rid.startsWith('win-') ? 'pgembed.exe' : 'pgembed';
  const library = rid.startsWith('win-') ? 'libpg_query.dll' : rid.startsWith('osx-') ? 'libpg_query.dylib' : 'libpg_query.so';
  const packageName = `@mysticmind/pgembed-${platform.name}`;
  const target = path.join(out, 'platforms', `pgembed-${platform.name}`);
  fs.mkdirSync(target, { recursive: true });
  for (const file of [executable, library]) {
    const from = path.join(source, file);
    if (!fs.existsSync(from)) throw new Error(`${rid}: ${file} is missing in ${source}`);
    fs.copyFileSync(from, path.join(target, file));
  }
  fs.chmodSync(path.join(target, executable), 0o755);

  const manifest = {
    name: packageName,
    ...shared,
    description: `The pgembed executable for ${platform.label}. Install pgembed, which picks the right one: npm install --global pgembed`,
    files: [executable, library],
    os: [platform.os],
    cpu: [platform.cpu],
    ...(platform.libc ? { libc: [platform.libc] } : {}),
    preferUnplugged: true,
  };
  fs.writeFileSync(path.join(target, 'package.json'), JSON.stringify(manifest, null, 2) + '\n');
  fs.writeFileSync(path.join(target, 'README.md'),
    `# ${packageName}\n\nThe [pgembed](https://www.npmjs.com/package/pgembed) executable for ${platform.label}. ` +
    'Do not install it directly: install `pgembed`, which installs the package for your platform and runs it.\n');
  fs.copyFileSync(path.join(root, 'LICENSE'), path.join(target, 'LICENSE'));
  built.push(packageName);
}

if (built.length === 0) throw new Error(`no builds in ${builds}`);

// the launcher package depends on every platform package, so a platform built here alone (CI) still resolves
const main = path.join(out, 'pgembed');
fs.mkdirSync(main, { recursive: true });
fs.copyFileSync(path.join(here, 'pgembed', 'pgembed.js'), path.join(main, 'pgembed.js'));
fs.chmodSync(path.join(main, 'pgembed.js'), 0o755);
const optionalDependencies = Object.fromEntries(
  Object.values(PLATFORMS).map((p) => [`@mysticmind/pgembed-${p.name}`, version]));
fs.writeFileSync(path.join(main, 'package.json'),
  JSON.stringify({ ...template, version, optionalDependencies }, null, 2) + '\n');
fs.copyFileSync(path.join(root, 'README.md'), path.join(main, 'README.md'));
fs.copyFileSync(path.join(root, 'LICENSE'), path.join(main, 'LICENSE'));

console.log(`pgembed ${version}: ${built.join(', ')}`);
