/* The main host library of the synthetic API (tests/ext-gen.sh): libllvmnet-gen.so.1 and its equivalents, linked
 * against the second one, so that loading it by path needs the second one loaded first. */
#define LLVMNET_GEN_BUILD 1
#include <llvmnet-gen.h>
#include <pthread.h>
#include <stdio.h>
#include <stdlib.h>
#include <string.h>

struct GENobject_T { GENint id; };
static GENcallback callback;
static void *callback_user;
static GENlogproc logger;
static GENotherproc other;

GENint genAdd(GENint a, GENint b) { return a + b; }
GENubyte genLow(GENuint value) { return (GENubyte)value; }
GENbyte genNegateByte(GENbyte value) { return (GENbyte)-value; }
GENshort genMix(GENubyte a, GENshort b, GENushort c) { return (GENshort)(a - b + c); }
GENushort genSwap(GENushort value) { return (GENushort)(value << 8 | value >> 8); }
GENint64 genWide(GENint64 value, GENuint64 shift) { return value >> shift; }
GENsizeiptr genSpan(GENintptr start, GENsizeiptr length) { return (GENsizeiptr)start + length; }
GENdouble genScale(GENdouble value, GENfloat factor) { return value * factor; }
GENfloat genHalve(GENfloat value) { return value / 2; }
const GENchar *genName(void) { return "llvmnet-gen 1.0"; }
GENsizei genFormat(GENchar *buffer, GENsizei size, const GENchar *text, GENint value) { return snprintf(buffer, (size_t)size, "%s=%d", text, value); }
GENobject genCreate(GENint id) { GENobject object = malloc(sizeof *object); object->id = id; return object; }
GENint genObjectId(GENobject object) { return object->id; }
void genDestroy(GENobject object) { free(object); }
void genRegister(GENcallback function, void *user) { callback = function; callback_user = user; }
GENint genFire(GENint value) { return callback ? callback(value, (GENubyte)value, (GENshort)-value, callback_user) : -1; }
struct fire { GENint value, result; };
static void *fire_thread(void *argument) {
    struct fire *fire = argument;
    fire->result = callback ? callback(fire->value, (GENubyte)fire->value, (GENshort)-fire->value, callback_user) : -1;
    return NULL;
}
GENint genFireOnThread(GENint value) {
    struct fire fire = { value, 0 };
    pthread_t thread;
    if (pthread_create(&thread, NULL, fire_thread, &fire)) return -2;
    pthread_join(thread, NULL);
    return fire.result;
}
void genSetLogger(GENlogproc function) { logger = function; }
void genLog(const GENchar *message, GENshort level) { if (logger) logger(message, level); }
void genSetOther(GENotherproc function) { other = function; }
GENotherproc genGetOther(void) { return other; }
GENint genPlatformValue(GENplatform platform) { return (GENint)(GENintptr)platform; }
GENint genStatus(void) { return genAuxValue() + 13; }
static GENint twice(GENint value) { return 2 * value; }
GENproc genGetProcAddress(const GENchar *name) {
    if (strcmp(name, "genTwiceEXT") == 0) return (GENproc)twice;
    if (strcmp(name, "genAdd") == 0) return (GENproc)genAdd;
    return NULL;
}
