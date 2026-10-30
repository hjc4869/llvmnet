/* Portable mode has no Linux kernel headers (ADR-14). 523.xalancbmk_r and 623.xalancbmk_s include this one only for
   PATH_MAX, which the C library's <limits.h> defines with the same value. */
#include <limits.h>
