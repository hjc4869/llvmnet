/* The synthetic API of tests/ext-gen.sh (NX-04), declared as registry.xml describes it. The native test libraries
 * (native.c, native-aux.c) implement it; the generated extension proxies it for portable programs. */
#ifndef LLVMNET_GEN_H
#define LLVMNET_GEN_H

#include <stdint.h>

typedef int8_t GENbyte;
typedef uint8_t GENubyte;
typedef int16_t GENshort;
typedef uint16_t GENushort;
typedef int GENint;
typedef unsigned int GENuint;
typedef int64_t GENint64;
typedef uint64_t GENuint64;
typedef intptr_t GENintptr;
typedef uintptr_t GENsizeiptr;
typedef int GENsizei;
typedef float GENfloat;
typedef double GENdouble;
typedef char GENchar;
typedef struct GENobject_T *GENobject;
#ifdef _WIN32
typedef void *GENplatform;
#else
typedef unsigned int GENplatform;
#endif
typedef GENint (*GENcallback)(GENint value, GENubyte flag, GENshort level, void *user);
typedef void (*GENlogproc)(const GENchar *message, GENshort level);
typedef void (*GENotherproc)(void);
typedef void (*GENproc)(void);

#if defined(_WIN32) && defined(LLVMNET_GEN_BUILD)
#define GENAPI __declspec(dllexport)
#elif defined(_WIN32)
#define GENAPI __declspec(dllimport)
#else
#define GENAPI
#endif

GENAPI GENint genAdd(GENint a, GENint b);
GENAPI GENubyte genLow(GENuint value);
GENAPI GENbyte genNegateByte(GENbyte value);
GENAPI GENshort genMix(GENubyte a, GENshort b, GENushort c);
GENAPI GENushort genSwap(GENushort value);
GENAPI GENint64 genWide(GENint64 value, GENuint64 shift);
GENAPI GENsizeiptr genSpan(GENintptr start, GENsizeiptr length);
GENAPI GENdouble genScale(GENdouble value, GENfloat factor);
GENAPI GENfloat genHalve(GENfloat value);
GENAPI const GENchar *genName(void);
GENAPI GENsizei genFormat(GENchar *buffer, GENsizei size, const GENchar *text, GENint value);
GENAPI GENobject genCreate(GENint id);
GENAPI GENint genObjectId(GENobject object);
GENAPI void genDestroy(GENobject object);
GENAPI void genRegister(GENcallback callback, void *user);
GENAPI GENint genFire(GENint value);
/* Calls the registered callback on a thread the library starts, and waits for it. */
GENAPI GENint genFireOnThread(GENint value);
GENAPI void genSetLogger(GENlogproc logger);
GENAPI void genLog(const GENchar *message, GENshort level);
GENAPI void genSetOther(GENotherproc other);
GENAPI GENotherproc genGetOther(void);
GENAPI GENint genPlatformValue(GENplatform platform);
GENAPI GENint genStatus(void);
GENAPI GENproc genGetProcAddress(const GENchar *name);
/* GEN_EXT_twice: genTwiceEXT is found through genGetProcAddress only; no host implements genMissingEXT. */
GENAPI GENint genTwiceEXT(GENint value);
GENAPI GENint genMissingEXT(GENint value);
/* The second library. */
GENAPI GENint genAuxValue(void);

#endif
