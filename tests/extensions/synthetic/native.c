/* The native test library behind the synthetic extension (libllvmnet-synthetic.so.1), built with clang for the host by
 * tests/extensions.sh; the one native library the tests compile. */
#include <stdio.h>
#include "llvmnet-synthetic.h"

const char *synthetic_version(void) { return "synthetic 1.0.0 (native)"; }

int32_t synthetic_add(int32_t left, int32_t right) { return left + right; }

int32_t synthetic_mix(uint8_t byte, int16_t half, uint32_t word) { return (int32_t)(byte + half + word); }

uint8_t synthetic_low(uint32_t value) { return (uint8_t)value; }

int64_t synthetic_wide(int64_t value, uint64_t shift) { return value * (int64_t)(shift >> 30); }

double synthetic_scale(double value, float factor) { return value * factor; }

size_t synthetic_format(char *buffer, size_t size, const char *name, int32_t value)
{
	return (size_t)snprintf(buffer, size, "%s=%d", name, value);
}
