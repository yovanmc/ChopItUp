import test from 'node:test';
import assert from 'node:assert/strict';
import { readFileSync, mkdtempSync, writeFileSync, mkdirSync, rmSync, renameSync } from 'node:fs';
import { tmpdir } from 'node:os';
import { dirname, resolve, join } from 'node:path';
import { fileURLToPath } from 'node:url';
import { execFileSync } from 'node:child_process';
import { changedFiles, selectTests, plan, unregisteredClientReaders } from './affected-tests.mjs';

const root = resolve(dirname(fileURLToPath(import.meta.url)), '..');
const config = JSON.parse(readFileSync(join(root, 'tools/affected-tests.json'), 'utf8'));
const choose = paths => selectTests(root, config, paths);
test('narrative docs and an empty tree do not schedule product tests', () => {
  for (const paths of [[], ['README.md', 'docs/BUGS.md'], ['docs/test-efficiency.md']]) {
    assert.equal(choose(paths).noProductTests, true);
  }
});
test('unknown, build, selection and CI changes fail closed', () => {
  for (const path of ['new-area/a.cs', '.github/workflows/ci.yml', 'Directory.Build.props', 'tools/affected-tests.json', 'tools/Invoke-AffectedTests.ps1', 'tests/Shared/TestTempRoot.cs', 'docs/SCHEMA.md']) {
    const result = choose([path]);
    assert.equal(result.mode, 'full', path);
    assert.equal(result.suites.length, config.projects.filter(p => p.suite).length);
    assert.equal(result.noProductTests, false);
  }
});
test('the selection narrative doc is narrative; the selector files themselves still fail closed', () => {
  const doc = choose(['docs/affected-tests.md']);
  assert.equal(doc.noProductTests, true);
  assert.deepEqual(doc.reasons, ['Narrative documentation: docs/affected-tests.md']);
  for (const path of ['tools/affected-tests.mjs', 'tools/affected-tests.json', 'tools/affected-tests.test.mjs']) {
    assert.equal(choose([path]).mode, 'full', path);
  }
});
test('a client-subtree edit selects the client, its registered readers and the Hub build, not Hub or Desktop tests', () => {
  const readerSuites = [...new Set(config.contractReaders.map(r => r.suite))].sort();
  for (const path of ['src/ChopItUp.Hub/client/src/styles.css', 'src/ChopItUp.Hub/client/src/Thread.test.tsx']) {
    const result = choose([path]);
    assert.equal(result.mode, 'affected', path);
    assert.equal(result.client, true, path);
    assert.deepEqual(result.suites, readerSuites, path);
    assert.deepEqual(result.builds, ['src/ChopItUp.Hub/ChopItUp.Hub.csproj'], path);
  }
});
test('Hub contract files outside the client subtree still select Hub tests', () => {
  for (const path of ['src/ChopItUp.Hub/Realtime/RoomHub.cs', 'src/ChopItUp.Hub/Web/SpaFiles.cs']) {
    const result = choose([path]);
    assert.ok(result.suites.includes('ChopItUp.Hub.Tests'), path);
    assert.equal(result.client, true, path);
  }
});
test('every test that mentions served client output is registered as a reader or a reviewed non-reader', () => {
  assert.deepEqual(unregisteredClientReaders(root, config), []);
});
test('an unregistered client reader or a vanished registration fails the check', () => {
  const temp = mkdtempSync(join(tmpdir(), 'affected readers '));
  try {
    mkdirSync(join(temp, 'tests/Hub.Tests'), { recursive: true });
    writeFileSync(join(temp, 'tests/Hub.Tests/ServedShellTests.cs'), 'var page = File.ReadAllText(Path.Combine(root, "wwwroot", "index.html"));');
    const c = { ...config, contractReaders: [], nonReaders: {} };
    assert.deepEqual(unregisteredClientReaders(temp, c), ['Unregistered client reader: tests/Hub.Tests/ServedShellTests.cs']);
    const registered = { ...c, contractReaders: [{ path: 'tests/Hub.Tests/ServedShellTests.cs', suite: 'Hub.Tests' }] };
    assert.deepEqual(unregisteredClientReaders(temp, registered), []);
    rmSync(join(temp, 'tests/Hub.Tests/ServedShellTests.cs'));
    assert.deepEqual(unregisteredClientReaders(temp, registered), ['Registered client reader is missing: tests/Hub.Tests/ServedShellTests.cs']);
    const reviewed = { ...c, nonReaders: { 'tests/Hub.Tests/Gone.cs': 'fabricated wwwroot' } };
    assert.deepEqual(unregisteredClientReaders(temp, reviewed), ['Reviewed non-reader is missing: tests/Hub.Tests/Gone.cs']);
  } finally { rmSync(join(temp), { recursive: true, force: true }); }
});
test('test-only edit selects its owning suite, not every product consumer', () => {
  for (const p of config.projects.filter(p => p.suite)) {
    const result = choose([`${dirname(p.path)}/ExampleTests.cs`]);
    assert.deepEqual(result.suites, [p.suite]);
    assert.equal(result.client, false);
  }
});
test('Core changes select all transitive consumers and UI contracts', () => {
  const prefix = config.kind === 'curio' ? 'Curio' : 'ChopItUp';
  const result = choose([`src/${prefix}.Core/Storage/Example.cs`]);
  assert.deepEqual(result.suites, config.projects.filter(p => p.suite).map(p => p.suite).sort());
  assert.equal(result.noProductTests, false);
  if (config.kind === 'chopitup') assert.equal(result.client, true);
  else assert.deepEqual(result.nodes, [...config.nodes].sort());
});
test('leaf changes omit unrelated expensive suites and retain cross-source contracts', () => {
  if (config.kind === 'chopitup') {
    assert.deepEqual(choose(['src/ChopItUp.Desktop/MainWindow.xaml']).suites, ['ChopItUp.Desktop.Tests']);
    const hub = choose(['src/ChopItUp.Hub/Program.cs']);
    assert.deepEqual(hub.suites, ['ChopItUp.Desktop.Tests', 'ChopItUp.Hub.Tests']);
    assert.equal(hub.client, true);
  } else {
    const shell = choose(['src/Curio.Shell/MainWindow.xaml']);
    assert.deepEqual(shell.suites, ['Curio.Shell.Tests']);
    assert.equal(shell.sourceGuards, true);
    assert.deepEqual(shell.nodes, ['verify-web-judge-vocabulary']);
    const web = choose(['src/Curio.Web/Program.cs']);
    assert.deepEqual(web.suites, ['Curio.Shell.Tests', 'Curio.Web.Tests']);
    assert.deepEqual(web.nodes, [...config.nodes].sort());
    assert.deepEqual(choose(['src/Curio.Web/client/src/App.tsx']).nodes, [...config.nodes].sort());
    const companion = choose(['src/Curio.Companion/Program.cs']);
    assert.deepEqual(companion.suites, ['Curio.App.Tests', 'Curio.Companion.Tests', 'Curio.Shell.Tests', 'Curio.Web.Tests']);
    assert.deepEqual(companion.nodes, [...config.nodes].sort());
    assert.deepEqual(choose(['docs/aftermath/artboards/HubBuild.dc.html']).nodes, [...config.nodes].sort());
  }
});
test('nested client owner arrays fail rather than omit client coverage', () => {
  assert.throws(() => selectTests(root, { ...config, clientOwners: [['nested']] }, ['README.md']), /flat string arrays/);
});
test('a new project reference cannot silently escape the map', () => {
  const temp = mkdtempSync(join(tmpdir(), 'affected graph '));
  try {
    writeFileSync(join(temp, 'A.csproj'), '<Project><ProjectReference Include="../New/New.csproj" /></Project>');
    const c = { ...config, projects: [{ path: 'A.csproj', suite: 'A.Tests' }] };
    assert.equal(selectTests(temp, c, ['README.md']).mode, 'full');
  } finally { rmSync(temp, { recursive: true, force: true }); }
});
test('a new solution test project cannot green with only the old suite list', () => {
  const temp = mkdtempSync(join(tmpdir(), 'affected solution '));
  try {
    writeFileSync(join(temp, config.solution), '<Solution><Project Path="tests/New.Tests/New.Tests.csproj" /></Solution>');
    assert.throws(() => selectTests(temp, config, ['README.md']), /Update the affected-test map/);
  } finally { rmSync(temp, { recursive: true, force: true }); }
  assert.throws(() => choose(['verify/new.test.mjs']), /Register the new Node test/);
});
test('Git range includes every branch commit, dirty index/worktree, deletes, renames and Unicode', () => {
  const temp = mkdtempSync(join(tmpdir(), 'affected git '));
  const git = args => execFileSync('git', ['-C', temp, ...args], { encoding: 'utf8' }).trim();
  const commit = () => { git(['add', '-A']); git(['-c', 'user.name=Test', '-c', 'user.email=test@example.invalid', 'commit', '-qm', 'fixture']); };
  try {
    git(['init', '-q']);
    writeFileSync(join(temp, 'old.cs'), 'initial'); commit();
    const base = git(['rev-parse', 'HEAD']);
    writeFileSync(join(temp, 'first.cs'), 'first'); commit();
    renameSync(join(temp, 'old.cs'), join(temp, 'renamed.cs')); commit();
    writeFileSync(join(temp, 'staged.cs'), 'staged'); git(['add', 'staged.cs']);
    writeFileSync(join(temp, 'first.cs'), 'dirty');
    writeFileSync(join(temp, 'spaced ü file.cs'), 'untracked');
    assert.deepEqual(changedFiles(temp, { base }), ['first.cs', 'old.cs', 'renamed.cs', 'spaced ü file.cs', 'staged.cs']);
    assert.deepEqual(changedFiles(temp, { base, committedOnly: true }), ['first.cs', 'old.cs', 'renamed.cs']);
    mkdirSync(join(temp, 'tools')); writeFileSync(join(temp, 'tools/affected-tests.json'), JSON.stringify(config));
    assert.equal(plan(temp, { base: 'nonexistent-ref' }).mode, 'full');
  } finally { rmSync(temp, { recursive: true, force: true }); }
});
