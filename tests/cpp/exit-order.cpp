// Order of process termination work (BE-08). Native glibc order: thread_local destructors of the exiting
// thread, then atexit and __cxa_atexit handlers in reverse registration order, then .fini_array
// (destructor attributes, llvm.global_dtors) by descending priority.
#include <cstdio>
#include <cstdlib>
#include <cstring>
struct Noisy {
    const char *name;
    explicit Noisy(const char *value) : name(value) { std::printf("construct %s\n", name); }
    ~Noisy() { std::printf("destroy %s\n", name); }
};
static Noisy global_object("global");
static void handler_from_constructor() { std::printf("atexit registered by constructor\n"); }
static void handler_early() { std::printf("atexit registered early in main\n"); }
static void handler_late() { std::printf("atexit registered late in main\n"); }
static void handler_nested() { std::printf("atexit registered during exit\n"); }
static void handler_registering() { std::printf("atexit that registers another\n"); std::atexit(handler_nested); }
__attribute__((constructor)) static void register_from_constructor() { std::atexit(handler_from_constructor); }
__attribute__((destructor(200))) static void fini_low() { std::printf("fini priority 200\n"); }
__attribute__((destructor(300))) static void fini_high() { std::printf("fini priority 300\n"); }
static Noisy &local_static() { static Noisy object("function-local static"); return object; }
int main(int argc, char **argv)
{
    thread_local Noisy per_thread("thread_local");
    (void)per_thread.name;
    std::atexit(handler_early);
    local_static();
    std::atexit(handler_registering);
    std::atexit(handler_late);
    std::printf("main returns\n");
    std::fflush(stdout);
    if (argc > 1 && std::strcmp(argv[1], "exit") == 0) std::exit(0);
    return 0;
}
