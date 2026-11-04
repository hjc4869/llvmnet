#!/usr/bin/env python3
"""Writes a C program that calls every SSE to SSE4.2 intrinsic of the installed Clang's headers (xmmintrin.h to
nmmintrin.h, popcntintrin.h and crc32intrin.h, and the SSE2-based MMX functions they include) on random inputs and prints
one line per intrinsic: its name, a hash of the results and a hash with NaN results canonicalized (tests/x86-intrinsics.sh).
With the level v3 it calls every intrinsic of the x86-64-v3 headers instead: avxintrin.h, avx2intrin.h, fmaintrin.h,
f16cintrin.h, bmiintrin.h, bmi2intrin.h and lzcntintrin.h, with the AVX predicates of the SSE comparison macros.

    x86-headers.py <clang> <output.c> [v2|v3]

The functions are read from Clang's AST, the macros (the intrinsics with an immediate operand) from -dM, and each macro's
immediate takes the values of IMMEDIATES. Pointer operands point into aligned buffers of random bytes, which are hashed
after the call, so loads and stores are checked too."""
import json
import re
import subprocess
import sys

clang, output = sys.argv[1], sys.argv[2]
level = sys.argv[3] if len(sys.argv) > 3 else 'v2'
headers = '#include <smmintrin.h>\n#include <nmmintrin.h>\n' if level == 'v2' else '#include <immintrin.h>\n'
march = '-march=x86-64-v2' if level == 'v2' else '-march=x86-64-v3'
V3_HEADERS = ['avxintrin.h', 'avx2intrin.h', 'fmaintrin.h', 'f16cintrin.h', 'bmiintrin.h', 'bmi2intrin.h', 'lzcntintrin.h']

# Intrinsics left out: the MXCSR (llvmnet runs with the default control word and does not track exception flags),
# undefined contents, monitor/mwait, allocation, and the approximations rcpps and rsqrtps, whose bits differ between
# processors (x86-sse-conformance.c checks them against their documented error). The hints and fences have no result
# and run as calls.
EXCLUDED = {'_mm_setcsr', '_mm_getcsr', '_mm_monitor', '_mm_mwait', '_mm_malloc', '_mm_free', '_mm_undefined_ps',
            '_mm_undefined_pd', '_mm_undefined_si128', '_mm_rcp_ps', '_mm_rcp_ss', '_mm_rsqrt_ps', '_mm_rsqrt_ss',
            '_mm256_undefined_ps', '_mm256_undefined_pd', '_mm256_undefined_si256', '_mm256_rcp_ps', '_mm256_rsqrt_ps'}

# Values of each macro's immediate operand, by macro name (the last parameter unless named).
SHUFFLE = [0, 1, 0x1b, 0x4e, 0xb1, 0xe4, 0x55, 0xff, 0x39, 0x93]
IMMEDIATES = {
    '_mm_alignr_epi8': [0, 1, 5, 15, 16, 17, 31, 32, 255], '_mm_alignr_pi8': [0, 1, 5, 7, 8, 9, 15, 16, 255],
    '_mm_blend_epi16': SHUFFLE, '_mm_blend_pd': [0, 1, 2, 3], '_mm_blend_ps': list(range(16)),
    '_mm_bslli_si128': [0, 1, 7, 15, 16, 255], '_mm_bsrli_si128': [0, 1, 7, 15, 16, 255],
    '_mm_slli_si128': [0, 1, 7, 15, 16, 255], '_mm_srli_si128': [0, 1, 7, 15, 16, 255],
    '_mm_cmp_ps': list(range(8)), '_mm_cmp_pd': list(range(8)), '_mm_cmp_ss': list(range(8)), '_mm_cmp_sd': list(range(8)),
    '_mm_dp_ps': [0xff, 0xf1, 0x71, 0x3f, 0x5a, 0x0f, 0xf0, 0x11, 0x88], '_mm_dp_pd': [0x33, 0x31, 0x13, 0x22, 0x11, 0x30, 0x03],
    '_mm_extract_epi16': list(range(8)), '_mm_extract_epi8': list(range(16)), '_mm_extract_epi32': list(range(4)),
    '_mm_extract_epi64': [0, 1], '_mm_extract_ps': list(range(4)), '_mm_extract_pi16': list(range(4)),
    '_mm_insert_epi16': list(range(8)), '_mm_insert_epi8': list(range(16)), '_mm_insert_epi32': list(range(4)),
    '_mm_insert_epi64': [0, 1], '_mm_insert_pi16': list(range(4)), '_mm_insert_ps': [0, 0x10, 0x20, 0x30, 0x40, 0x80, 0xc0, 0x0f, 0x5a, 0xff, 0x0e],
    '_mm_mpsadbw_epu8': list(range(8)), '_mm_round_ps': list(range(16)), '_mm_round_pd': list(range(16)),
    '_mm_round_ss': list(range(16)), '_mm_round_sd': list(range(16)),
    '_mm_shuffle_epi32': SHUFFLE, '_mm_shufflehi_epi16': SHUFFLE, '_mm_shufflelo_epi16': SHUFFLE, '_mm_shuffle_ps': SHUFFLE,
    '_mm_shuffle_pd': [0, 1, 2, 3], '_mm_shuffle_pi16': SHUFFLE,
}
STRING_CONTROLS = [0x00, 0x01, 0x02, 0x03, 0x04, 0x05, 0x06, 0x08, 0x09, 0x0c, 0x0d, 0x10, 0x14, 0x18, 0x1c, 0x30, 0x34, 0x38, 0x3c, 0x40, 0x44, 0x48, 0x4c, 0x70, 0x7d]
for name in ['_mm_cmpestra', '_mm_cmpestrc', '_mm_cmpestri', '_mm_cmpestrm', '_mm_cmpestro', '_mm_cmpestrs', '_mm_cmpestrz',
             '_mm_cmpistra', '_mm_cmpistrc', '_mm_cmpistri', '_mm_cmpistrm', '_mm_cmpistro', '_mm_cmpistrs', '_mm_cmpistrz']:
    IMMEDIATES[name] = STRING_CONTROLS

# The x86-64-v3 macros' immediates; the gathers' scale is their immediate, and their indices stay within the buffers.
LANES = [0x00, 0x01, 0x10, 0x11, 0x20, 0x31, 0x08, 0x80, 0x28, 0x88, 0x13, 0x02, 0x30]
IMMEDIATES_V3 = {
    '_mm256_alignr_epi8': [0, 1, 5, 15, 16, 17, 31, 32, 255], '_mm256_blend_epi16': SHUFFLE, '_mm256_blend_epi32': SHUFFLE,
    '_mm256_blend_pd': list(range(16)), '_mm256_blend_ps': SHUFFLE, '_mm_blend_epi32': list(range(16)),
    '_mm256_bslli_epi128': [0, 1, 7, 15, 16, 255], '_mm256_bsrli_epi128': [0, 1, 7, 15, 16, 255],
    '_mm256_slli_si256': [0, 1, 7, 15, 16, 255], '_mm256_srli_si256': [0, 1, 7, 15, 16, 255],
    '_mm256_cmp_pd': list(range(32)), '_mm256_cmp_ps': list(range(32)), '_mm_cmp_ps': list(range(32)), '_mm_cmp_pd': list(range(32)),
    '_mm_cmp_ss': list(range(32)), '_mm_cmp_sd': list(range(32)),
    '_mm256_cvtps_ph': [0, 1, 2, 3, 4, 8, 11], '_mm_cvtps_ph': [0, 1, 2, 3, 4, 8, 11], '_cvtss_sh': [0, 1, 2, 3, 4],
    '_mm256_dp_ps': [0xff, 0xf1, 0x71, 0x3f, 0x5a, 0x0f, 0xf0, 0x11, 0x88],
    '_mm256_extract_epi8': list(range(32)), '_mm256_extract_epi16': list(range(16)), '_mm256_extract_epi32': list(range(8)),
    '_mm256_extract_epi64': list(range(4)), '_mm256_extractf128_pd': [0, 1], '_mm256_extractf128_ps': [0, 1],
    '_mm256_extractf128_si256': [0, 1], '_mm256_extracti128_si256': [0, 1],
    '_mm256_insert_epi8': list(range(32)), '_mm256_insert_epi16': list(range(16)), '_mm256_insert_epi32': list(range(8)),
    '_mm256_insert_epi64': list(range(4)), '_mm256_insertf128_pd': [0, 1], '_mm256_insertf128_ps': [0, 1],
    '_mm256_insertf128_si256': [0, 1], '_mm256_inserti128_si256': [0, 1],
    '_mm256_mpsadbw_epu8': [0, 1, 5, 7, 8, 0x15, 0x2a, 0x3f], '_mm256_permute2f128_pd': LANES, '_mm256_permute2f128_ps': LANES,
    '_mm256_permute2f128_si256': LANES, '_mm256_permute2x128_si256': LANES, '_mm256_permute4x64_epi64': SHUFFLE,
    '_mm256_permute4x64_pd': SHUFFLE, '_mm256_permute_pd': list(range(16)), '_mm256_permute_ps': SHUFFLE,
    '_mm_permute_pd': list(range(4)), '_mm_permute_ps': SHUFFLE, '_mm256_round_pd': list(range(16)), '_mm256_round_ps': list(range(16)),
    '_mm256_shuffle_epi32': SHUFFLE, '_mm256_shufflehi_epi16': SHUFFLE, '_mm256_shufflelo_epi16': SHUFFLE,
    '_mm256_shuffle_pd': list(range(16)), '_mm256_shuffle_ps': SHUFFLE,
}
for width in ('_mm', '_mm256'):
    for kind in ('epi32', 'epi64', 'pd', 'ps'):
        for index in ('i32', 'i64'):
            IMMEDIATES_V3[f'{width}_{index}gather_{kind}'] = [1, 2, 4, 8]
            IMMEDIATES_V3[f'{width}_mask_{index}gather_{kind}'] = [1, 2, 4, 8]
# Macros without an immediate: their operand and result types.
PLAIN_V3 = {'_mm256_ceil_pd': ('__m256d', ['__m256d']), '_mm256_ceil_ps': ('__m256', ['__m256']), '_mm256_floor_pd': ('__m256d', ['__m256d']),
            '_mm256_floor_ps': ('__m256', ['__m256']), '_mm_broadcastsi128_si256': ('__m256i', ['__m128i']),
            '__lzcnt16': ('unsigned short', ['unsigned short']), '__lzcnt64': ('unsigned long long', ['unsigned long long'])}
if level == 'v3':
    IMMEDIATES = IMMEDIATES_V3


# The C type of each operand and result, as generated and hashed.
VALUES = {
    '__m128': 'random_v4f()', '__m128d': 'random_v2d()', '__m128i': 'random_m128i()', '__m64': 'random_m64()',
    'int': 'random_int()', 'unsigned int': '(unsigned)random_int()', 'long long': '(long long)next()',
    'unsigned long long': 'next()', 'float': 'random_float()', 'double': 'random_double()', 'short': '(short)next()',
    'char': '(char)next()', 'unsigned char': '(unsigned char)next()', 'unsigned short': '(unsigned short)next()',
    'long': '(long)next()', 'unsigned long': '(unsigned long)next()',
    '__m256': 'random_v8f()', '__m256d': 'random_v4d()', '__m256i': 'random_m256i()',
}


PRELUDE = r"""
#include <stdint.h>
#include <stdio.h>
#include <string.h>

#define ROUNDS 200

static uint64_t state = 0x9e3779b97f4a7c15ull;
static uint64_t next(void)
{
    state ^= state >> 12;
    state ^= state << 25;
    state ^= state >> 27;
    return state * 0x2545f4914f6cdd1dull;
}

static float float_bits(uint32_t bits) { float value; memcpy(&value, &bits, 4); return value; }
static double double_bits(uint64_t bits) { double value; memcpy(&value, &bits, 8); return value; }

static const uint32_t special_floats[] = {0x00000000, 0x80000000, 0x7f800000, 0xff800000, 0x7fc00000, 0x7fa00001, 0x00000001,
    0x00800000, 0x7f7fffff, 0x3f000000, 0xbfc00000, 0x40200000, 0x4f000000, 0xcf000000, 0x5f000000, 0x3f800000};
static const uint64_t special_doubles[] = {0x0000000000000000, 0x8000000000000000, 0x7ff0000000000000, 0xfff0000000000000,
    0x7ff8000000000000, 0x7ff4000000000001, 0x0000000000000001, 0x0010000000000000, 0x7fefffffffffffff, 0x3fe0000000000000,
    0xbff8000000000000, 0x4004000000000000, 0x41e0000000000000, 0xc1e0000000000000, 0x43e0000000000000, 0x3ff0000000000000};

static float random_float(void)
{
    uint64_t choice = next();
    switch (choice % 6) {
    case 0: return float_bits((uint32_t)(choice >> 32));
    case 1: return float_bits(special_floats[(choice >> 8) % 16]);
    default: return (float)((int64_t)((choice >> 8) % 2001) - 1000) / (float)(1u << ((choice >> 20) % 8));
    }
}

static double random_double(void)
{
    uint64_t choice = next();
    switch (choice % 6) {
    case 0: return double_bits(next());
    case 1: return double_bits(special_doubles[(choice >> 8) % 16]);
    default: return (double)((int64_t)((choice >> 8) % 2001) - 1000) / (double)(1u << ((choice >> 20) % 8));
    }
}

/* Integer operands: random bits, small values (shift counts, lane indices, string lengths) and the bounds. */
static int random_int(void)
{
    static const int special[] = {0, 1, -1, 2147483647, -2147483647 - 1, 15, 16, 17, 31, 32, 63, 64, -16, 8, 3, 255};
    uint64_t choice = next();
    return choice % 3 == 0 ? special[(choice >> 8) % 16] : choice % 3 == 1 ? (int)((choice >> 8) % 70) - 3 : (int)(choice >> 32);
}

static __m128i random_m128i(void)
{
    static const unsigned short special[] = {0, 1, 0x7fff, 0x8000, 0xffff, 0x00ff, 0xff00, 0x0080};
    unsigned short words[8];
    uint64_t choice = next();
    for (int lane = 0; lane < 8; lane++) {
        uint64_t element = next();
        words[lane] = choice % 4 == 0 ? (unsigned short)((element >> 8) % 40) : element % 4 == 0 ? special[(element >> 8) % 8] : (unsigned short)(element >> 32);
        if (choice % 4 == 1 && element % 6 == 0) words[lane] &= 0xff;
    }
    __m128i value;
    memcpy(&value, words, 16);
    return value;
}

static __m128 random_v4f(void) { return (__m128){random_float(), random_float(), random_float(), random_float()}; }
static __m128d random_v2d(void) { return (__m128d){random_double(), random_double()}; }
static __m64 random_m64(void) { __m128i value = random_m128i(); __m64 result; memcpy(&result, &value, 8); return result; }

#ifdef __AVX__
static __m256 random_v8f(void) { return (__m256){random_float(), random_float(), random_float(), random_float(), random_float(), random_float(), random_float(), random_float()}; }
static __m256d random_v4d(void) { return (__m256d){random_double(), random_double(), random_double(), random_double()}; }
static __m256i random_m256i(void) { return _mm256_set_m128i(random_m128i(), random_m128i()); }
/* Gather indices within the buffers: 0 to 7 elements of up to 8 bytes. */
static __m128i random_index128(void) { return _mm_and_si128(random_m128i(), _mm_set1_epi32(7)); }
static __m256i random_index256(void) { return _mm256_and_si256(random_m256i(), _mm256_set1_epi32(7)); }
static __m128i random_qindex128(void) { return _mm_and_si128(random_m128i(), _mm_set1_epi64x(7)); }
static __m256i random_qindex256(void) { return _mm256_and_si256(random_m256i(), _mm256_set1_epi64x(7)); }
#endif

static _Alignas(32) unsigned char buffer0[96], buffer1[96], buffer2[96], buffer3[96];
static void fill(unsigned char *buffer) { for (int index = 0; index < 96; index++) buffer[index] = (unsigned char)next(); }

static uint64_t exact, portable;
static void mix(uint64_t *hash, const void *data, size_t size)
{
    const unsigned char *bytes = data;
    for (size_t index = 0; index < size; index++) {
        *hash ^= bytes[index];
        *hash *= 0x100000001b3ull;
    }
}

static void begin(void) { exact = portable = 0xcbf29ce484222325ull; }
static void end(const char *name) { printf("%s %016llx %016llx\n", name, (unsigned long long)exact, (unsigned long long)portable); }
static void hash_bits(const void *data, size_t size) { mix(&exact, data, size); mix(&portable, data, size); }

static void hash_v4f(__m128 value)
{
    mix(&exact, &value, 16);
    for (int lane = 0; lane < 4; lane++) {
        float element = value[lane];
        if (element != element) element = float_bits(0x7fc00000);
        mix(&portable, &element, 4);
    }
}

static void hash_v2d(__m128d value)
{
    mix(&exact, &value, 16);
    for (int lane = 0; lane < 2; lane++) {
        double element = value[lane];
        if (element != element) element = double_bits(0x7ff8000000000000);
        mix(&portable, &element, 8);
    }
}

#ifdef __AVX__
static void hash_v8f(__m256 value) { hash_v4f(_mm256_castps256_ps128(value)); hash_v4f(_mm256_extractf128_ps(value, 1)); }
static void hash_v4d(__m256d value) { hash_v2d(_mm256_castpd256_pd128(value)); hash_v2d(_mm256_extractf128_pd(value, 1)); }
#endif

"""


def header_names():
    """The functions and macros the x86-64-v3 headers define, by name, read from their text."""
    resource = subprocess.run([clang, '-print-resource-dir'], capture_output=True, text=True, check=True).stdout.strip()
    functions, macros = set(), set()
    for header in V3_HEADERS:
        text = open(f'{resource}/include/{header}').read()
        functions.update(re.findall(r'static\s+__inline__?[^;{]*?\b(_[A-Za-z]\w*)\s*\(', text))
        macros.update(re.findall(r'#define (_\w+)\(', text))
    return functions, macros


V3_FUNCTIONS, V3_MACROS = header_names() if level == 'v3' else (set(), set())


def functions():
    """Each _mm_ and _m_ function of the headers with its result and parameter types, from Clang's AST."""
    dump = subprocess.run([clang, march, '-fsyntax-only', '-Xclang', '-ast-dump=json', '-Xclang', '-ast-dump-filter=_m',
                           '-x', 'c', '-'], input=headers, capture_output=True, text=True, check=True).stdout
    decoder, position, found = json.JSONDecoder(), 0, {}
    while True:
        while position < len(dump) and dump[position] != '{':
            position += 1
        if position >= len(dump):
            break
        node, position = decoder.raw_decode(dump, position)
        name = node.get('name', '')
        if node.get('kind') != 'FunctionDecl' or name in EXCLUDED or (not re.match(r'_mm?_', name) if level == 'v2' else name not in V3_FUNCTIONS):
            continue
        parameters = [inner['type']['qualType'] for inner in node.get('inner', []) if inner.get('kind') == 'ParmVarDecl']
        result = node['type']['qualType'].split('(')[0].strip()
        found[name] = (result, parameters)
    return found


def macros():
    """Each _mm_ macro with an immediate (IMMEDIATES), with its operand and result types, read from the casts of its
    expansion: an operand is cast to its type where the expansion uses it, the result where the expansion starts."""
    defines = subprocess.run([clang, march, '-dM', '-E', '-x', 'c', '-'], input=headers, capture_output=True, text=True, check=True).stdout
    known = r'(__m256i|__m256d|__m256|__m128i|__m128d|__m128|__m64|long long const \*|int const \*|float const \*|double const \*|long long|int|__v1di|__v4hi|__v2si|__v8qi)'
    found = {}
    for line in defines.splitlines():
        match = re.match(r'#define (_[a-z][a-z0-9_]*)\(([^)]*)\) (.*)', line)
        if not match or match.group(1) not in IMMEDIATES or level == 'v3' and match.group(1) not in V3_MACROS | {'_mm_cmp_ps', '_mm_cmp_pd', '_mm_cmp_ss', '_mm_cmp_sd'}:
            continue
        name, parameters, body = match.group(1), [parameter.strip() for parameter in match.group(2).split(',')], match.group(3)
        types = []
        for parameter in parameters[:-1]:
            cast = re.search(r'\(' + known + r'\)\s*\(?' + re.escape(parameter) + r'\b', body)
            types.append({'__v1di': '__m64', '__v4hi': '__m64', '__v2si': '__m64', '__v8qi': '__m64'}.get(cast.group(1), cast.group(1)) if cast else '__m128')
        start = re.match(r'\(\(' + known + r'\)', body) or re.match(r'__builtin_bit_cast\(\s*(int)', body)
        result = start.group(1) if start else '__m128'
        found[name] = MACRO_TYPES.get(name, (result, types + ['int']))
        names[name] = parameters
    return found


# Macros whose expansion casts no operand to its type.
MACRO_TYPES = {'_cvtss_sh': ('unsigned short', ['float', 'int'])}
# Each macro's parameter names, for the gathers' indices.
names = {}


def pointee(type_name):
    return re.sub(r'\b(const|volatile|__restrict)\b', '', type_name.rstrip('*').strip()).strip()


def operand(type_name, index, setup):
    """An expression for an operand; a pointer gets a buffer of its own, hashed after the call."""
    if type_name.endswith('*') and index < 0:
        return f'random_{"q" if index == -2 else ""}index{"256" if type_name == "__m256i*" else "128"}()'
    if type_name.endswith('*'):
        setup.append(f'fill(buffer{index});')
        return f'({type_name})buffer{index}'
    base = re.sub(r'\bconst\b', '', type_name).strip()
    base = {'__m128i_u': '__m128i', '__m128_u': '__m128', '__m128d_u': '__m128d', '__m64_u': '__m64', '__m256i_u': '__m256i',
            '__m256_u': '__m256', '__m256d_u': '__m256d'}.get(base, base)
    if base not in VALUES:
        raise SystemExit(f'x86-headers.py: no generator for operand type {type_name}')
    return VALUES[base]


def hash_result(type_name, expression):
    base = type_name.strip()
    if base == 'void':
        return f'{expression};'
    if base == '__m128':
        return f'hash_v4f({expression});'
    if base == '__m128d':
        return f'hash_v2d({expression});'
    if base == '__m256':
        return f'hash_v8f({expression});'
    if base == '__m256d':
        return f'hash_v4d({expression});'
    if base in ('float', 'double'):
        return f'{{ {base} value = {expression}; hash_{"v4f" if base == "float" else "v2d"}(({"__m128" if base == "float" else "__m128d"}){{value}}); }}'
    return f'{{ {base} value = {expression}; hash_bits(&value, sizeof value); }}'


def call(name, result, parameters, immediate=None, immediate_index=None):
    setup, arguments = [], []
    for index, type_name in enumerate(parameters):
        if index == immediate_index:
            arguments.append(str(immediate))
            continue
        if 'gather' in name and names.get(name, [''] * len(parameters))[index] == 'i':
            arguments.append(operand(type_name + '*', -2 if 'i64gather' in name else -1, setup))
            continue
        arguments.append(operand(type_name, index, setup))
    body = ' '.join(setup) + ' ' + hash_result(result, f'{name}({", ".join(arguments)})')
    for index, type_name in enumerate(parameters):
        if type_name.endswith('*'):
            body += f' hash_bits(buffer{index}, sizeof buffer{index});'
    return body


declared = functions()
defined = macros()
tests = [(name, [call(name, result, parameters)]) for name, (result, parameters) in sorted(declared.items())]
tests += [(name, [call(name, result, parameters, value, len(parameters) - 1) for value in IMMEDIATES[name]])
          for name, (result, parameters) in sorted(defined.items())]
if level == 'v3':
    tests += [(name, [call(name, result, parameters)]) for name, (result, parameters) in sorted(PLAIN_V3.items())]
missing = sorted(set(IMMEDIATES) - set(defined))
if missing:
    raise SystemExit('x86-headers.py: macros not found in the headers: ' + ', '.join(missing))

with open(output, 'w') as program:
    program.write(headers + PRELUDE)
    for name, calls in tests:
        program.write(f'static void test_{name}(void)\n{{\n    begin();\n    for (int round = 0; round < ROUNDS; round++) {{\n')
        for body in calls:
            program.write(f'        {{ {body} }}\n')
        program.write(f'    }}\n    end("{name}");\n}}\n\n')
    program.write('int main(void)\n{\n')
    for name, _ in tests:
        program.write(f'    test_{name}();\n')
    program.write('    return 0;\n}\n')
