import test from 'node:test';
import assert from 'node:assert/strict';
import { readFileSync, mkdtempSync, writeFileSync, mkdirSync, rmSync, renameSync } from 'node:fs';
import { tmpdir } from 'node:os';
import { dirname, resolve, join } from 'node:path';
import { fileURLToPath } from 'node:url';
import { execFileSync } from 'node:child_process';
import { changedFiles, selectTests, plan } from './affected-tests.mjs';

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
