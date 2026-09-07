import { readFileSync, writeFileSync } from 'node:fs';

const [modulePath, inputPath] = process.argv.slice(2);
const { instance } = await WebAssembly.instantiate(readFileSync(modulePath));
const records = [];
const edge = Buffer.alloc(128);
edge.set([0, 1, 2, 3, 127, 128, 129, 254, 255, 10, 100, 200, 40, 50, 60, 70], 0);
edge.set([255, 254, 253, 252, 129, 128, 127, 2, 1, 250, 200, 100, 40, 200, 220, 250], 16);
edge.set([0, 1, 15, 16, 17, 127, 128, 255, 14, 13, 8, 6, 5, 4, 3, 2], 32);
[-32768, -129, -128, -1, 0, 127, 128, 32767].forEach((value, index) => edge.writeInt16LE(value, 48 + index * 2));
[-32768, -1, 0, 1, 255, 256, 300, 32767].forEach((value, index) => edge.writeInt16LE(value, 64 + index * 2));
[0, 0x80000000, 0x7fc00001, 0x3f800000].forEach((value, index) => edge.writeUInt32LE(value, 80 + index * 4));
[0x80000000, 0, 0x40000000, 0x7fc00002].forEach((value, index) => edge.writeUInt32LE(value, 96 + index * 4));
[NaN, Infinity, -Infinity, 2147483648].forEach((value, index) => edge.writeFloatLE(value, 112 + index * 4));
records.push(edge);
const boundaries = Buffer.from(edge);
[-0.5, -1, 4294967296, 2147483520].forEach((value, index) => boundaries.writeFloatLE(value, 112 + index * 4));
records.push(boundaries, Buffer.alloc(128));
let seed = 0x128;
for (let record = 0; record < 64; record++) {
    const input = Buffer.alloc(128);
    for (let offset = 0; offset < input.length; offset += 4) {
        seed = (Math.imul(seed, 1664525) + 1013904223) >>> 0;
        input.writeUInt32LE(seed, offset);
    }
    records.push(input);
}
writeFileSync(inputPath, Buffer.concat(records));
for (const [record, input] of records.entries()) {
    new Uint8Array(instance.exports.memory.buffer, instance.exports.input_address(), 128).set(input);
    const count = instance.exports.run();
    if (count !== 21) throw new Error(`Unexpected SIMD result count: ${count}`);
    for (let operation = 0; operation < count; operation++) {
        const bytes = Buffer.from(instance.exports.memory.buffer, instance.exports.output_address() + operation * 16, 16);
        console.log(`${record}:${operation} ${bytes.toString('hex').toUpperCase()}`);
    }
}