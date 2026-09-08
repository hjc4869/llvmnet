import fs from 'node:fs';
import path from 'node:path';

const root = path.resolve(process.argv[2] ?? 'artifacts/spec-ordered');
const latest = new Map();
for (const entry of fs.readdirSync(root, { recursive: true }).sort()) {
    if (!entry.endsWith('/results.tsv')) continue;
    const file = path.join(root, entry);
    const environment = path.join(path.dirname(file), 'environment.txt');
    const started = fs.existsSync(environment)
        ? fs.readFileSync(environment, 'utf8').match(/^started=(.+)$/m)?.[1] : undefined;
    if (!started) throw new Error(`Missing start timestamp: ${environment}`);
    const lines = fs.readFileSync(file, 'utf8').trim().split('\n');
    const headings = lines.shift().split('\t');
    for (const line of lines) {
        const row = Object.fromEntries(line.split('\t').map((value, index) => [headings[index], value]));
        if (row.runtime !== 'portable') continue;
        const key = `${row.suite}:${row.benchmark}`;
        const previous = latest.get(key);
        if (!previous || previous.started < started)
            latest.set(key, { row, file, started });
    }
}
if (!latest.size) throw new Error(`No portable results in ${root}`);

console.log('benchmark\tresult\tfirst_diagnostic\tresults\tbuild_log\tstarted\tthreads\tthreading_profile');
let failures = 0;
for (const { row, file, started } of [...latest.values()].sort((left, right) => left.row.benchmark.localeCompare(right.row.benchmark))) {
    const build = path.join(path.dirname(file), `${row.benchmark}-portable-build`);
    const logs = fs.existsSync(build)
        ? fs.readdirSync(build).filter(name => name.endsWith('.out')).sort().map(name => path.join(build, name)) : [];
    logs.push(row.log);
    let diagnostic = '';
    let evidence = row.log;
    if (row.result !== 'PASS') {
        failures++;
        for (const log of logs) {
            if (!fs.existsSync(log)) continue;
            diagnostic = fs.readFileSync(log, 'utf8').split('\n')
                .find(line => /^llvmnet:|^.*?:\d+:\d+: (?:fatal )?error:/.test(line)) ?? '';
            if (diagnostic) {
                evidence = log;
                break;
            }
        }
        if (!diagnostic) throw new Error(`Unclassified failure: ${row.benchmark} (${row.log})`);
    }
    console.log([row.benchmark, row.result, diagnostic, file, evidence, started, row.threads ?? '1',
        row.threading_profile ?? 'unspecified'].map(value => value.replaceAll('\t', ' ')).join('\t'));
}
console.error(`${latest.size} distinct benchmarks: ${latest.size - failures} PASS, ${failures} non-PASS. Historical first blockers, not current full-suite results.`);