// Runs the built packages the way npm lays them out, before anything is published: node_modules/pgembed and
// node_modules/@mysticmind/pgembed-<this platform>, then the launcher, as npx and a global install would.
//
//   node npm/smoke.mjs <out> <version>

import { spawnSync } from 'node:child_process';
import fs from 'node:fs';
import os from 'node:os';
import path from 'node:path';

const [out, version] = process.argv.slice(2);
if (!out || !version) {
  console.error('usage: node npm/smoke.mjs <out> <version>');
  process.exit(2);
}

const musl = process.platform === 'linux' && (() => {
  try { return fs.readFileSync('/usr/bin/ldd', 'utf8').includes('musl'); } catch { return false; }
})();
const platform = `${process.platform}-${process.arch}${musl ? '-musl' : ''}`;

const project = fs.mkdtempSync(path.join(os.tmpdir(), 'pgembed-npm-'));
const modules = path.join(project, 'node_modules');
fs.cpSync(path.join(out, 'pgembed'), path.join(modules, 'pgembed'), { recursive: true });
fs.cpSync(path.join(out, 'platforms', `pgembed-${platform}`), path.join(modules, '@mysticmind', `pgembed-${platform}`), { recursive: true });

const launcher = path.join(modules, 'pgembed', 'pgembed.js');
function run(args, input) {
  return spawnSync(process.execPath, [launcher, ...args], { encoding: 'utf8', input });
}

const failures = [];
function check(what, ok, result) {
  console.log(`${ok ? 'ok  ' : 'FAIL'} ${what}`);
  if (!ok) failures.push(`${what}\n  exit ${result.status}\n  stdout: ${result.stdout}\n  stderr: ${result.stderr}`);
}

const versionRun = run(['--version']);
check(`--version prints ${version}`, versionRun.status === 0 && versionRun.stdout.trim().split('+')[0] === version, versionRun);

// libpg_query loads (the parser), and the exit code passes through: a syntax error is not 0
const valid = run(['format', 'select 1']);
check('format runs (libpg_query loads)', valid.status === 0 && /SELECT 1/i.test(valid.stdout), valid);
const invalid = run(['sql', '--check', 'SELEC 1']);
check('an error exit code passes through', invalid.status !== 0, invalid);

// standard input reaches pgembed (an MCP client talks to it this way)
const piped = run(['format'], 'select 2');
check('standard input passes through', piped.status === 0 && /SELECT 2/i.test(piped.stdout), piped);

fs.rmSync(project, { recursive: true, force: true });
if (failures.length > 0) {
  console.error(`\n${failures.join('\n\n')}`);
  process.exit(1);
}
