#!/usr/bin/env node
// Runs the native pgembed for this machine. npm installs one platform package next to this one (the others
// are optional dependencies whose os, cpu and libc do not match); this finds it and runs its executable with
// the same arguments, standard streams and exit code.
'use strict';

const { spawn } = require('node:child_process');
const fs = require('node:fs');
const path = require('node:path');

const SUPPORTED = ['darwin-arm64', 'darwin-x64', 'linux-arm64', 'linux-arm64-musl', 'linux-x64', 'linux-x64-musl', 'win32-x64'];

function isMusl() {
  // Alpine's ldd is a script that runs the musl loader; glibc's names the GNU C Library
  try {
    return fs.readFileSync('/usr/bin/ldd', 'utf8').includes('musl');
  } catch {
    // no ldd (a distroless image): Node reports the glibc it runs on, and none on musl
    try {
      return !process.report.getReport().header.glibcVersionRuntime;
    } catch {
      return false;
    }
  }
}

function fail(message) {
  process.stderr.write(`pgembed: ${message}\n`);
  process.exit(1);
}

const platform = `${process.platform}-${process.arch}${process.platform === 'linux' && isMusl() ? '-musl' : ''}`;
if (!SUPPORTED.includes(platform)) {
  fail(`there is no native build for ${platform} on npm (builds: ${SUPPORTED.join(', ')}). ` +
    'The .NET tool runs on other platforms too: dotnet tool install --global pgembed (needs the .NET 10 SDK).');
}

const packageName = `@mysticmind/pgembed-${platform}`;
let folder;
try {
  folder = path.dirname(require.resolve(`${packageName}/package.json`));
} catch {
  fail(`${packageName}, which holds the executable for this machine, is not installed. It is an optional ` +
    'dependency of pgembed: reinstall pgembed without --omit=optional or --no-optional.');
}

const executable = path.join(folder, process.platform === 'win32' ? 'pgembed.exe' : 'pgembed');
if (process.platform !== 'win32') {
  // a package manager that unpacked it without the executable bit
  try {
    fs.accessSync(executable, fs.constants.X_OK);
  } catch {
    try { fs.chmodSync(executable, 0o755); } catch { /* spawn reports it */ }
  }
}

const child = spawn(executable, process.argv.slice(2), { stdio: 'inherit' });

// Ctrl+C reaches pgembed from the terminal directly (it cancels a running query in the prompt); this process
// only has to stay alive until pgembed exits. Signals sent to this process alone are passed on.
process.on('SIGINT', () => {});
for (const signal of ['SIGTERM', 'SIGHUP']) {
  process.on(signal, () => child.kill(signal));
}

child.on('error', (error) => fail(`could not run ${executable}: ${error.message}`));
child.on('exit', (code, signal) => {
  if (signal) {
    // end the same way, so a shell or a client sees the signal
    process.removeAllListeners(signal);
    process.kill(process.pid, signal);
  } else {
    process.exit(code ?? 1);
  }
});
