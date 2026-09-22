// Repository-local selection shared by the local runner and CI. No external packages.
import { execFileSync } from 'node:child_process';
import { readFileSync, existsSync, readdirSync } from 'node:fs';
import { dirname, resolve, relative } from 'node:path';
import { fileURLToPath } from 'node:url';

const slash = p => p.replaceAll('\\', '/');
export function changedFiles(root, { base, head = 'HEAD', committedOnly = false } = {}) {
  const git = args => execFileSync('git', ['-C', root, ...args], { encoding: 'utf8', maxBuffer: 16 * 1024 * 1024 });
  // --no-renames deliberately reports both the old and new paths. NUL separation
  // preserves spaces, Unicode, quotes and newlines in Git filenames.
  const ancestor = git(['merge-base', base, head]).trim();
  let files = git(['diff', '--name-only', '--no-renames', '-z', ancestor, head]).split('\0');
  if (!committedOnly) {
    files.push(...git(['diff', '--name-only', '--no-renames', '-z', head]).split('\0'));
    files.push(...git(['ls-files', '--others', '--exclude-standard', '-z']).split('\0'));
  }
  return [...new Set(files.filter(Boolean))].sort();
}

export function selectTests(root, config, files, forcedReason = '') {
  if (!Array.isArray(config.clientOwners) || config.clientOwners.some(p => typeof p !== 'string') ||
      !Array.isArray(config.nodes) || config.nodes.some(n => typeof n !== 'string')) {
    throw new Error('Invalid selection map: clientOwners and nodes must be flat string arrays');
  }
  const suites = new Set(), nodes = new Set(), builds = new Set(), reasons = [];
  let full = false, client = false, sourceGuards = false;
  const all = reason => { full = true; reasons.push(reason); };
  const projects = config.projects;
  const solutionPath = resolve(root, config.solution);
  if (existsSync(solutionPath)) {
    const solution = readFileSync(solutionPath, 'utf8');
    const members = [...solution.matchAll(/<Project\b[^>]*\bPath\s*=\s*(['"])(.*?)\1/g)].map(m => slash(m[2]));
    const unmapped = members.filter(path => !projects.some(p => p.path === path));
    // Running only the old manifest after adding a test project is NOT a full
    // fallback. Require registering its runner/count guard before accepting it.
    if (unmapped.length) throw new Error(`Update the affected-test map for solution projects: ${unmapped.join(', ')}`);
  }
  const graph = new Map();
  // Read actual references; a newly introduced or unmodelled reference falls back
  // to the solution gate instead of trusting a stale hand-maintained graph.
  for (const project of projects) {
    const path = resolve(root, project.path);
    if (!existsSync(path)) { all(`Missing project: ${project.path}`); continue; }
    const xml = readFileSync(path, 'utf8');
    const tags = [...xml.matchAll(/<ProjectReference\b[^>]*>/g)];
    const includes = tags.map(m => /\bInclude\s*=\s*(['"])(.*?)\1/s.exec(m[0]));
    if (includes.some(m => !m)) all(`Unsupported project reference syntax: ${project.path}`);
    const refs = includes.filter(Boolean).map(m => slash(relative(root, resolve(dirname(path), slash(m[2])))));
    graph.set(project.path, refs);
    if (refs.some(ref => !projects.some(p => p.path === ref))) all(`Unmodelled project reference: ${project.path}`);
  }
  function consumers(path) {
    const affected = new Set([path]);
    let previous;
    do {
      previous = affected.size;
      for (const [project, refs] of graph) if (refs.some(ref => affected.has(ref))) affected.add(project);
    } while (previous !== affected.size);
    for (const p of projects) if (p.suite && affected.has(p.path)) suites.add(p.suite);
  }
  if (forcedReason) all(forcedReason);
  for (const raw of files) {
    const path = slash(raw);
    if (/^(\/|[A-Za-z]:)|(^|\/)\.\.(\/|$)/.test(path)) { all(`Invalid relative path: ${path}`); continue; }
    if (/\.(csproj|props|targets|slnx?|runsettings)$|(^|\/)(global\.json|NuGet\.Config|nuget\.config|Directory\.Packages\.props)$/.test(path) ||
        /(^|\/)(affected-tests\.(mjs|json|test\.mjs)|Invoke-AffectedTests\.ps1|Invoke-CurioSuites\.ps1|Test-NativeSuiteIsolation\.ps1)$/.test(path) || path.startsWith('.github/')) {
      all(`Build, dependency or verification contract: ${path}`); continue;
    }
    // Documentation is not a blanket path exemption: executable artboards and
    // schema contracts still exercise their owners below (or fall back to full).
    if ((/^(README|CLAUDE|AGENTS|ROADMAP|CONTEXT)\.md$/.test(path) || /^docs\/.*\.md$/.test(path)) &&
        !path.startsWith('docs/aftermath/') && !/SCHEMA|SOURCE-ROOTS/.test(path)) {
      reasons.push(`Narrative documentation: ${path}`); continue;
    }
    if (config.kind === 'curio') {
      if (path.startsWith('src/')) { sourceGuards = true; nodes.add('verify-web-judge-vocabulary'); }
      if (path.startsWith('docs/aftermath/')) {
        for (const n of config.nodes) nodes.add(n); reasons.push(`Artboard/reference contracts: ${path}`); continue;
      }
      const node = config.nodeFiles[path];
      if (node) { nodes.add(node); nodes.add('verify-web-judge-vocabulary'); reasons.push(`Node test: ${path}`); continue; }
    }
    // The web client lives inside the Hub project directory but is not Hub code: it gets the
    // client checks, the tests registered as readers of its served output, and a build of the
    // project whose ClientBuild target typechecks and bundles it.
    const subtree = (config.clientSubtrees ?? []).find(s => path.startsWith(s.path));
    if (subtree) {
      client = true; builds.add(subtree.build);
      for (const reader of config.contractReaders ?? []) suites.add(reader.suite);
      reasons.push(`Client subtree and registered readers: ${path}`); continue;
    }
    const owner = projects.find(p => path.startsWith(dirname(p.path) + '/'));
    if (owner) {
      consumers(owner.path);
      reasons.push(`Project and transitive test consumers: ${path}`);
      if (config.clientOwners.includes(owner.path)) {
        if (config.kind === 'chopitup') client = true;
        else for (const n of config.nodes) nodes.add(n);
      }
      continue;
    }
    if (/\.test\.[cm]?js$/.test(path)) throw new Error(`Register the new Node test before verification: ${path}`);
    all(`Unmapped path: ${path}`);
  }
  if (full) {
    for (const p of projects) if (p.suite) suites.add(p.suite);
    for (const n of config.nodes) nodes.add(n);
    client = config.kind === 'chopitup'; sourceGuards = false;
  }
  return { version: 1, mode: full ? 'full' : 'affected', files, suites: [...suites].sort(),
    nodes: [...nodes].sort(), builds: [...builds].sort(), client, sourceGuards, reasons,
    noProductTests: suites.size === 0 && nodes.size === 0 && !client && !sourceGuards };
}

// A test that mentions served client output, the client directory or the WebView bridge must be
// classified: a registered contract reader (selected by client edits) or a reviewed non-reader.
const clientReaderPattern = /wwwroot|index\.html|ClientOutDir|ChopItUp\.Hub[\\/]client|chrome\.webview|WebMessageReceived|PostWebMessage/;
export function unregisteredClientReaders(root, config) {
  const readers = (config.contractReaders ?? []).map(r => r.path), reviewed = Object.keys(config.nonReaders ?? {});
  const testsDir = resolve(root, 'tests'), problems = [];
  const files = existsSync(testsDir) ? readdirSync(testsDir, { recursive: true }).map(f => `tests/${slash(f)}`) : [];
  for (const file of files.filter(f => f.endsWith('.cs') && !/\/(bin|obj)\//.test(f)).sort()) {
    if (clientReaderPattern.test(readFileSync(resolve(root, file), 'utf8')) && !readers.includes(file) && !reviewed.includes(file)) {
      problems.push(`Unregistered client reader: ${file}`);
    }
  }
  for (const file of readers) if (!existsSync(resolve(root, file))) problems.push(`Registered client reader is missing: ${file}`);
  for (const file of reviewed) if (!existsSync(resolve(root, file))) problems.push(`Reviewed non-reader is missing: ${file}`);
  return problems;
}

export function plan(root, options = {}) {
  const config = JSON.parse(readFileSync(resolve(root, options.configPath ?? 'tools/affected-tests.json'), 'utf8'));
  let files = [], reason = options.full ? 'Explicit full verification requested' : '';
  try { files = changedFiles(root, { ...options, base: options.base || config.defaultBase }); }
  catch (error) { reason = `Cannot establish complete Git change range; full fallback: ${error.message.split('\n')[0]}`; }
  return selectTests(root, config, files, reason);
}

if (process.argv[1] && resolve(process.argv[1]) === fileURLToPath(import.meta.url)) {
  const args = process.argv.slice(2), options = {};
  let root = resolve(dirname(fileURLToPath(import.meta.url)), '..');
  for (let i = 0; i < args.length; i++) {
    const arg = args[i];
    if (arg === '--full') options.full = true;
    else if (arg === '--committed-only') options.committedOnly = true;
    else if (['--base', '--head', '--root', '--config'].includes(arg)) {
      const value = args[++i]; if (!value || value.startsWith('--')) throw new Error(`Missing value for ${arg}`);
      if (arg === '--root') root = resolve(value);
      else options[arg === '--config' ? 'configPath' : arg.slice(2)] = value;
    } else throw new Error(`Unknown argument: ${arg}`);
  }
  console.log(JSON.stringify(plan(root, options), null, 2));
}
