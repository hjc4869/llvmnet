#!/usr/bin/env python3
"""Writes a C program (or with --library, a library and its consumer) with more functions than the CLR allows in one
type, for tests/many-functions.sh. Usage: many-functions.py <count> <program.c> | --library <count> <library.c> <consumer.c>

Function f<i> calls f<i-1> unless i is a multiple of 8, so calls cross every boundary between the classes the backend
spreads functions over; all of them are reached through a table of function pointers, count their calls in a
thread-local variable and read an initialized global. The program's last functions call the C library: printf, and
qsort with a callback."""
import sys

library = sys.argv[1] == '--library'
args = sys.argv[2:] if library else sys.argv[1:]
count = int(args[0])


def functions(out):
    out.write('typedef unsigned long (*step)(unsigned long);\n')
    out.write('static _Thread_local unsigned long calls;\n')
    out.write('static const unsigned char salt[256] = {%s};\n' % ', '.join(str((i * 167 + 13) & 255) for i in range(256)))
    for i in range(count):
        body = f'f{i - 1}(x ^ {i}ul)' if i % 8 else 'x * 31ul'
        out.write(f'__attribute__((noinline)) static unsigned long f{i}(unsigned long x) '
                  f'{{ calls++; return {body} + salt[{i & 255}]; }}\n')
    out.write('static const step table[%d] = {%s};\n' % (count, ', '.join(f'f{i}' for i in range(count))))
    out.write('static unsigned long sum_all(void) {\n'
              '    unsigned long sum = 0;\n'
              f'    for (unsigned long i = 0; i < {count}ul; i++) sum = sum * 1099511628211ul + table[i](i);\n'
              '    return sum;\n'
              '}\n')


if library:
    with open(args[1], 'w') as out:
        functions(out)
        out.write('__attribute__((visibility("default"))) unsigned long many_sum(void) { return sum_all() ^ calls; }\n')
    with open(args[2], 'w') as out:
        out.write('#include <stdio.h>\nunsigned long many_sum(void);\n'
                  'int main(void) { printf("%lu\\n", many_sum()); return 0; }\n')
else:
    with open(args[1], 'w') as out:
        out.write('#include <stdio.h>\n#include <stdlib.h>\n')
        functions(out)
        out.write('static int compare(const void *a, const void *b) {\n'
                  '    unsigned long x = *(const unsigned long *)a, y = *(const unsigned long *)b;\n'
                  '    return (x > y) - (x < y);\n'
                  '}\n'
                  'static int report(unsigned long sum) {\n'
                  '    unsigned long values[8];\n'
                  '    for (int i = 0; i < 8; i++) values[i] = table[(sum >> (i * 7)) % ' + str(count) + 'ul](i);\n'
                  '    qsort(values, 8, sizeof values[0], compare);\n'
                  '    printf("%lu %lu %lu %lu\\n", sum, calls, values[0], values[7]);\n'
                  '    return 0;\n'
                  '}\n'
                  'int main(void) { return report(sum_all()); }\n')
