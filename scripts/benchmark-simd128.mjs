import { spawnSync } from 'node:child_process';
import { cpus, platform, release } from 'node:os';
import { existsSync, mkdirSync, readFileSync, readdirSync, statSync, writeFileSync } from 'node:fs';
import { basename, dirname, join, resolve } from 'node:path';
import { fileURLToPath } from 'node:url';

const root = resolve(dirname(fileURLToPath(import.meta.url)), '..');
const options = process.argv.slice(2);
const validateOnly = options.includes('--validate-only');
const measureOnly = options.includes('--measure-only');
if (validateOnly && measureOnly) throw new Error('Choose validation or measurement, not both.');
if (options.some(option => option.startsWith('--') && !['--validate-only', '--measure-only'].includes(option)))
    throw new Error('Usage: node scripts/benchmark-simd128.mjs [--validate-only|--measure-only] [videos...]');
function integer(name, fallback, minimum) {
    const text = process.env[name] ?? String(fallback);
    if (!/^\d+$/.test(text) || !Number.isSafeInteger(Number(text)) || Number(text) < minimum)
        throw new Error(`Invalid ${name}: ${text}`);
    return Number(text);
}
const threads = integer('VIDEO_BENCH_THREADS', 4, 1);
const repeats = integer('VIDEO_BENCH_REPEATS', 3, 1);
const limit = integer('VIDEO_BENCH_FRAMES', 0, 0);
const verificationLimit = integer('VIDEO_BENCH_VERIFY_FRAMES', 0, 0);
const timeout = integer('VIDEO_BENCH_TIMEOUT', 1800, 1) * 1000;
if (threads > 64) throw new Error('The decoder supports at most 64 threads.');
const native = process.env.VIDEO_BENCH_NATIVE ?? '/usr/bin/ffmpeg';
const output = resolve(process.env.VIDEO_BENCH_OUTPUT ?? join(root, 'artifacts', 'video-simd128', new Date().toISOString().replaceAll(':', '-')));
const argumentsFiles = options.filter(option => !option.startsWith('--'));
const files = argumentsFiles.length ? argumentsFiles.map(file => resolve(file)) :
    readdirSync('/home/david/Videos').filter(name => /^2026.*\.mp4$/.test(name)).sort().map(name => join('/home/david/Videos', name));
if (!files.length) throw new Error('No video inputs found.');
if (new Set(files.map(file => basename(file))).size !== files.length) throw new Error('Video basenames must be unique.');
mkdirSync(output, { recursive: true });
const available = new Map([['native', { native: true }]]);
for (const runtime of ['system', 'portable']) {
    for (const simd of [false, true]) {
        const profile = `${runtime}-${simd ? 'simd128' : 'scalar'}`;
        const build = join(root, 'artifacts', `ffmpeg-browser-${runtime}${simd ? '-simd128' : ''}`);
        available.set(profile, { command: ['dotnet', join(build, 'decode.dll')], build, runtime, simd });
        available.set(`${profile}-aot`, { command: [join(build, 'decode-aot')], build, runtime, simd });
        available.set(`${profile}-aot-host`, { command: [join(build, 'decode-aot-host')], build, runtime, simd });
    }
}
const engines = (process.env.VIDEO_BENCH_ENGINES ?? [...available.keys()].filter(name => !name.endsWith('-host')).join(',')).split(',');
if (new Set(engines).size !== engines.length || engines.some(name => !available.has(name))) throw new Error('Invalid VIDEO_BENCH_ENGINES.');
for (const name of engines.filter(name => name !== 'native')) {
    const engine = available.get(name);
    if (!existsSync(engine.command.at(-1))) throw new Error(`Build ${name} first: ${engine.command.at(-1)}`);
    if (readFileSync(join(engine.build, 'llvmnet-runtime'), 'utf8').trim() !== engine.runtime ||
        readFileSync(join(engine.build, 'llvmnet-simd128'), 'utf8').trim() !== String(Number(engine.simd)))
        throw new Error(`Unexpected build configuration for ${name}`);
}
function execute(command, label) {
    const result = spawnSync(command[0], command.slice(1), { encoding: 'utf8', timeout, maxBuffer: 32 * 1024 * 1024 });
    if (result.error || result.status !== 0) throw new Error(`${label} failed (${result.status ?? result.signal}): ${result.error?.message ?? ''}\n${result.stderr}`);
    return result;
}
function commandFor(name, file, frames, hash) {
    const engine = available.get(name);
    if (!engine.native) return [...engine.command, file, String(frames || 2147483647), 'video', String(threads), 'frame', hash ? 'hash' : 'bench'];
    const command = [native, '-nostdin', '-hide_banner', '-loglevel', 'error', '-nostats', '-noautorotate', '-hwaccel', 'none',
        '-threads:v', String(threads), '-thread_type', 'frame', '-c:v', 'hevc', '-i', file,
        '-map', '0:v:0', '-an', '-sn', '-dn', '-fps_mode', 'passthrough'];
    if (frames) command.push('-frames:v', String(frames));
    if (hash) command.push('-c:v', 'rawvideo', '-threads:v', '1', '-f', 'framemd5', '-hash', 'md5', 'pipe:1');
    else command.push('-stats_period', '3600', '-progress', 'pipe:1', '-f', 'null', '-');
    return command;
}
function hashes(text, nativeOutput) {
    const lines = text.trim().split(/\r?\n/);
    const values = nativeOutput ? lines.filter(line => !line.startsWith('#') && line.includes(',')).map(line => line.split(',').at(-1).trim()) :
        lines.filter(line => /^\d+ /.test(line)).map(line => line.split(' ').at(-1));
    if (!values.length || values.some(value => !/^[0-9a-f]{32}$/i.test(value))) throw new Error('Missing or invalid frame hashes.');
    if (!nativeOutput && !lines.includes(`frames=${values.length}`)) throw new Error('Frame count/hash count differs.');
    return values.map(value => value.toLowerCase());
}
function frameCount(text, nativeOutput) {
    const matches = [...text.matchAll(nativeOutput ? /^frame=\s*(\d+)\s*$/gm : /^frames=(\d+)$/gm)];
    if (!matches.length || nativeOutput && !text.includes('progress=end')) throw new Error('Decoder did not report completed frame count.');
    return Number(matches.at(-1)[1]);
}
const metadata = {
    started: new Date().toISOString(), threads, repeats, limit, verificationLimit, engines,
    machine: { platform: platform(), release: release(), cpu: cpus()[0]?.model, logicalProcessors: cpus().length },
    nativeVersion: execute([native, '-version'], 'native version').stdout,
    dotnetVersion: execute(['dotnet', '--version'], '.NET version').stdout.trim(),
    files: files.map(file => ({ file, bytes: statSync(file).size,
        probe: JSON.parse(execute(['/usr/bin/ffprobe', '-v', 'error', '-select_streams', 'v:0', '-show_streams', '-show_format', '-of', 'json', file], 'ffprobe').stdout) })),
    timing: 'Serial external processes; GNU time wall/user/system/RSS. No hashing during measurement. Includes startup, JIT for DLL runners, file I/O and demuxing. Native CLI uses software HEVC, native CPU assembly, null output and no autorotation. Managed builds use O1 generic C plus optional upstream wasm HEVC IDCT/SAO.',
};
writeFileSync(join(output, 'metadata.json'), JSON.stringify(metadata, null, 2));
const results = [];
const validation = [];
const expectedFrames = new Map();
for (const file of files) {
    const name = basename(file);
    if (!measureOnly) {
        console.log(`VALIDATE ${name}: native frame hashes`);
        const reference = execute(commandFor('native', file, verificationLimit, true), `${name} native hashes`);
        writeFileSync(join(output, `${name}-native-hashes.txt`), reference.stdout);
        writeFileSync(join(output, `${name}-native-hashes.log`), reference.stderr);
        const expected = hashes(reference.stdout, true);
        if (verificationLimit === 0) expectedFrames.set(file, expected.length);
        for (const engine of engines.filter(engine => engine !== 'native')) {
            console.log(`VALIDATE ${name}: ${engine}`);
            const result = execute(commandFor(engine, file, verificationLimit, true), `${name} ${engine} hashes`);
            writeFileSync(join(output, `${name}-${engine}-hashes.txt`), result.stdout);
            writeFileSync(join(output, `${name}-${engine}-hashes.log`), result.stderr);
            const actual = hashes(result.stdout, false);
            if (actual.length !== expected.length || actual.some((hash, index) => hash !== expected[index]))
                throw new Error(`${name} ${engine}: frame hashes differ from installed native ffmpeg.`);
            validation.push({ file, engine, frames: actual.length, matchesNative: true });
            writeFileSync(join(output, 'validation.json'), JSON.stringify(validation, null, 2));
        }
        console.log(`PASS: ${name} ${expected.length} frame hashes match installed native ffmpeg`);
    }
    if (validateOnly) continue;
    for (const engine of engines) execute(commandFor(engine, file, Math.min(limit || 12, 12), false), `${name} ${engine} warmup`);
    for (let repeat = 0; repeat < repeats; repeat++) {
        const order = repeat % 2 ? [...engines].reverse() : engines;
        for (const engine of order) {
            const label = `${name}-${engine}-${repeat + 1}`;
            console.log(`MEASURE ${label}`);
            const timePath = join(output, `${label}-time.json`);
            const command = commandFor(engine, file, limit, false);
            const result = execute(['/usr/bin/time', '-f', '{"elapsed":%e,"user":%U,"system":%S,"rssKiB":%M}', '-o', timePath, ...command], label);
            writeFileSync(join(output, `${label}.txt`), result.stdout);
            writeFileSync(join(output, `${label}.log`), result.stderr);
            const count = frameCount(result.stdout, engine === 'native');
            if (engine === 'native' && limit === 0 && !expectedFrames.has(file)) expectedFrames.set(file, count);
            const expected = expectedFrames.get(file);
            if (count <= 0 || limit > 0 && expected !== undefined && count !== Math.min(limit, expected) || limit === 0 && expected !== undefined && count !== expected)
                throw new Error(`${label}: unexpected frame count ${count} (reference ${expected}).`);
            const timing = JSON.parse(readFileSync(timePath, 'utf8'));
            if (!(timing.elapsed > 0)) throw new Error(`${label}: invalid elapsed time`);
            results.push({ file, engine, repeat: repeat + 1, frames: count, ...timing, fps: count / timing.elapsed, command });
            writeFileSync(join(output, 'results.json'), JSON.stringify(results, null, 2));
            console.log(`RESULT ${engine} ${name}: ${timing.elapsed.toFixed(2)}s ${(count / timing.elapsed).toFixed(2)}fps frames=${count}`);
        }
    }
}
if (!validateOnly) {
    const summary = [];
    for (const file of files) {
        for (const engine of engines) {
            const runs = results.filter(result => result.file === file && result.engine === engine);
            const ordered = runs.map(result => result.elapsed).sort((left, right) => left - right);
            const middle = Math.floor(ordered.length / 2);
            const median = ordered.length % 2 ? ordered[middle] : (ordered[middle - 1] + ordered[middle]) / 2;
            summary.push({ file, engine, frames: runs[0].frames, medianSeconds: median, minimumSeconds: ordered[0], maximumSeconds: ordered.at(-1), fps: runs[0].frames / median });
        }
    }
    writeFileSync(join(output, 'summary.json'), JSON.stringify(summary, null, 2));
    const markdown = ['| Video | Engine | Frames | Median Seconds | FPS | Range Seconds |', '| --- | --- | ---: | ---: | ---: | ---: |',
        ...summary.map(result => `| ${basename(result.file)} | ${result.engine} | ${result.frames} | ${result.medianSeconds.toFixed(2)} | ${result.fps.toFixed(2)} | ${result.minimumSeconds.toFixed(2)}-${result.maximumSeconds.toFixed(2)} |`)];
    writeFileSync(join(output, 'summary.md'), `${markdown.join('\n')}\n`);
}
writeFileSync(join(output, 'completion.json'), JSON.stringify({ completed: new Date().toISOString(), validationCases: validation.length, measurements: results.length }, null, 2));
console.log(`PASS: results saved in ${output}`);