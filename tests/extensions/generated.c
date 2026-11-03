/* The program of tests/ext-gen.sh (NX-04): calls every kind of entry point of the synthetic API in
 * generated/registry.xml, directly, through dlopen and dlsym, and through the API's own lookup function, with callbacks
 * back into the program. Built natively against the native test libraries and in portable mode against the generated
 * extension, it must print the same lines; the portable-only modes check what only the extension does.
 *   generated            every entry point (the output both builds compare)
 *   generated portable   availability, a direct call bound through genGetProcAddress, defaults (portable only)
 *   generated system     availability in system mode (built with -DLLVMNET_SYSTEM_TEST)
 *   generated exhaust    a third distinct logger, beyond the extension's pool of two
 *   generated other      a callback type the extension has no reverse thunks for
 *   generated returned   an entry point that returns a host function pointer
 *   generated threads    callbacks on threads the host library starts: errno, stdio, malloc, thread-local storage,
 *                        pthread_self and pthread keys work there, for 3 threads and then 200 more */
#include <dlfcn.h>
#include <errno.h>
#include <pthread.h>
#include <stdlib.h>
#include <inttypes.h>
#include <llvmnet-gen.h>
#include <stdio.h>
#include <string.h>
#if defined(__LLVMNET__) || defined(LLVMNET_SYSTEM_TEST)
#include <llvmnet/ext.h>
#endif

static GENint on_event(GENint value, GENubyte flag, GENshort level, void *user) {
    printf("callback value %d flag %u level %d user %s\n", value, flag, level, (const char *)user);
    return value + flag + level;
}

static GENint on_other_event(GENint value, GENubyte flag, GENshort level, void *user) {
    (void)user;
    return -(value + flag + level);
}

static _Thread_local int calls_here;
static pthread_key_t key;
static pthread_t main_thread;
static int thread_checks_failed;

/* Runs on a thread the host library started; everything here needs musl's thread state. */
static GENint on_thread_event(GENint value, GENubyte flag, GENshort level, void *user) {
    calls_here++;
    errno = 0;
    (void)strtol("99999999999999999999999", NULL, 10);
    int range = errno == ERANGE;
    char *text = malloc(64);
    snprintf(text, 64, "thread event %d (%s)", value, (const char *)user);
    int own = !pthread_equal(pthread_self(), main_thread);
    void *before = pthread_getspecific(key);
    pthread_setspecific(key, text);
    int kept = pthread_getspecific(key) == text;
    if (!own || !range || calls_here != 1 || before || !kept) thread_checks_failed++;
    if (value < 100)
        printf("%s: own thread %s, errno %s, thread-local calls %d, key %s then %s\n", text, own ? "yes" : "no",
               range ? "ERANGE" : "wrong", calls_here, before ? "set" : "unset", kept ? "kept" : "lost");
    pthread_setspecific(key, NULL);
    free(text);
    return value + flag + level;
}

static void log_a(const GENchar *message, GENshort level) { printf("log a: %s (%d)\n", message, level); }
static void log_b(const GENchar *message, GENshort level) { printf("log b: %s (%d)\n", message, level); }
static void log_c(const GENchar *message, GENshort level) { printf("log c: %s (%d)\n", message, level); }
static void other(void) {}

int main(int argc, char **argv) {
    const char *mode = argc > 1 ? argv[1] : "all";
    setvbuf(stdout, NULL, _IONBF, 0);
    if (strcmp(mode, "exhaust") == 0) {
        genSetLogger(log_a);
        genLog("first", 1);
        genSetLogger(log_b);
        genLog("second", 2);
        genSetLogger(log_a);
        genLog("first again", 3);
        genSetLogger(log_c);
        genLog("third", 4);
        return 0;
    }
    if (strcmp(mode, "threads") == 0) {
        main_thread = pthread_self();
        pthread_key_create(&key, NULL);
        genRegister(on_thread_event, "x");
        for (int i = 1; i <= 3; i++) printf("fire on thread %d\n", genFireOnThread(i * 10));
        long sum = 0;
        for (int i = 0; i < 200; i++) sum += genFireOnThread(100 + i);
        printf("200 more threads: sum %ld, failed checks %d, main thread's calls %d\n", sum, thread_checks_failed, calls_here);
        return 0;
    }
    if (strcmp(mode, "other") == 0) {
        genSetOther(NULL);
        printf("a null callback passes\n");
        genSetOther(other);
        return 0;
    }
    if (strcmp(mode, "returned") == 0) {
        printf("other: %s\n", genGetOther() == NULL ? "null" : "set");
        return 0;
    }
#ifdef LLVMNET_SYSTEM_TEST
    if (strcmp(mode, "system") == 0) {
        printf("available: %d\n", llvmnet_ext_available("generated"));
        printf("library: %s\n", llvmnet_ext_library("generated"));
        return 0;
    }
#endif
#ifdef __LLVMNET_PORTABLE__
    if (strcmp(mode, "portable") == 0) {
        int available = llvmnet_ext_available("generated");
        printf("available: %d\n", available);
        printf("library: %s\n", llvmnet_ext_library("generated"));
        printf("not linked: %d %s\n", llvmnet_ext_available("other-extension"), llvmnet_ext_library("other-extension") == NULL ? "null" : "set");
        printf("status: 0x%x\n", genStatus());
        printf("lookup: %s\n", genGetProcAddress("genAdd") == NULL ? "null" : "set");
        printf("dlopen: %s\n", dlopen("libllvmnet-gen.so.1", RTLD_NOW) == NULL ? dlerror() : "set");
        /* Bound through genGetProcAddress, since the host library does not export it; without the host library, the
         * first call of an entry point that has no default ends the program. */
        printf("twice direct: %d\n", genTwiceEXT(21));
        return 0;
    }
#endif
    printf("add %d\n", genAdd(40, 2));
    printf("low %u\n", genLow(0x1234abcdu));
    printf("negate %d %d %d\n", genNegateByte(-5), genNegateByte(127), genNegateByte(-128));
    printf("mix %d %d\n", genMix(200, -2, 65535), genMix(255, 32767, 1));
    printf("swap 0x%04x\n", genSwap(0x1234));
    printf("wide %" PRId64 " %" PRId64 "\n", genWide(-1234567890123LL, 4), genWide(INT64_MAX, 62));
    printf("span %" PRIuPTR " %" PRIuPTR "\n", genSpan(-100, 1000), genSpan((GENintptr)1 << 40, 5));
    printf("scale %.4f halve %.4f\n", genScale(1.5, 2.5f), (double)genHalve(3.0f));
    printf("name %s\n", genName());
    char buffer[16];
    GENsizei length = genFormat(buffer, sizeof buffer, "value", 123456);
    printf("format %d %s\n", length, buffer);
    length = genFormat(buffer, 8, "truncated", 7);
    printf("format %d %s\n", length, buffer);
    GENobject object = genCreate(77);
    printf("object %d\n", genObjectId(object));
    genDestroy(object);
    genRegister(on_event, "first");
    printf("fire %d\n", genFire(300));
    genRegister(on_other_event, NULL);
    printf("fire %d\n", genFire(5));
    genRegister(on_event, "again");
    printf("fire %d\n", genFire(-1));
    genSetLogger(log_a);
    genLog("hello", -7);
    genSetLogger(log_b);
    genLog("world", 32767);
    printf("status 0x%x aux %d\n", genStatus(), genAuxValue());
    /* The API's lookup function: an extension entry point only it provides, a core one, and ones the host lacks. */
    GENint (*twice)(GENint) = (GENint (*)(GENint))genGetProcAddress("genTwiceEXT");
    printf("lookup twice %s %d\n", twice ? "set" : "null", twice ? twice(21) : 0);
    GENint (*add)(GENint, GENint) = (GENint (*)(GENint, GENint))genGetProcAddress("genAdd");
    printf("lookup add %s %d\n", add ? "set" : "null", add ? add(5, 6) : 0);
    printf("lookup missing %s unknown %s\n", genGetProcAddress("genMissingEXT") ? "set" : "null",
           genGetProcAddress("genNoSuchFunction") ? "set" : "null");
    /* dlopen and dlsym by the Linux names; the main library depends on the second, as natively. */
    void *library = dlopen("libllvmnet-gen.so.1", RTLD_NOW);
    printf("dlopen %s\n", library ? "set" : "null");
    if (library) {
        GENint (*dl_add)(GENint, GENint) = (GENint (*)(GENint, GENint))dlsym(library, "genAdd");
        printf("dlsym add %s %d\n", dl_add ? "set" : "null", dl_add ? dl_add(20, 22) : 0);
        GENint (*dl_aux)(void) = (GENint (*)(void))dlsym(library, "genAuxValue");
        printf("dlsym aux through the main library %s %d\n", dl_aux ? "set" : "null", dl_aux ? dl_aux() : 0);
        printf("dlsym twice %s missing %s\n", dlsym(library, "genTwiceEXT") ? "set" : "null", dlsym(library, "genMissingEXT") ? "set" : "null");
    }
    void *aux = dlopen("libllvmnet-gen-aux.so.1", RTLD_NOW);
    GENint (*aux_value)(void) = aux ? (GENint (*)(void))dlsym(aux, "genAuxValue") : NULL;
    printf("dlopen aux %s %d\n", aux ? "set" : "null", aux_value ? aux_value() : 0);
    printf("dlopen unknown %s\n", dlopen("libllvmnet-no-such-library.so.1", RTLD_NOW) ? "set" : "null");
    return 0;
}
