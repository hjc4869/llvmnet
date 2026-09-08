#ifndef __LLVMNET_NETINET_IN_H
#define __LLVMNET_NETINET_IN_H
#include <stdint.h>
__LLVMNET_BEGIN_C
static inline uint16_t htons(uint16_t value) { return __builtin_bswap16(value); }
static inline uint16_t ntohs(uint16_t value) { return __builtin_bswap16(value); }
static inline uint32_t htonl(uint32_t value) { return __builtin_bswap32(value); }
static inline uint32_t ntohl(uint32_t value) { return __builtin_bswap32(value); }
__LLVMNET_END_C
#endif