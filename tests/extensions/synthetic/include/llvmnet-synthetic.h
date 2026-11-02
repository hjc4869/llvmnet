/* The API of the synthetic native extension of tests/extensions.sh (NX-03): a native test library built with clang for
 * the host, reached from portable code through LlvmNet.Ext.Synthetic. Fixed-width and pointer-sized types only, as
 * extension headers require, with narrow integers whose extension the host's calling convention decides. */
#ifndef LLVMNET_SYNTHETIC_H
#define LLVMNET_SYNTHETIC_H

#include <stddef.h>
#include <stdint.h>

#ifdef __cplusplus
extern "C" {
#endif

const char *synthetic_version(void);
int32_t synthetic_add(int32_t left, int32_t right);
int32_t synthetic_mix(uint8_t byte, int16_t half, uint32_t word);
uint8_t synthetic_low(uint32_t value);
int64_t synthetic_wide(int64_t value, uint64_t shift);
double synthetic_scale(double value, float factor);
size_t synthetic_format(char *buffer, size_t size, const char *name, int32_t value);

#ifdef __cplusplus
}
#endif

#endif
