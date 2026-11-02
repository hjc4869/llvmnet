/* dlopen, dlsym, dlclose, dlerror and dladdr over a native extension the program registers itself (NX-02,
 * tests/extensions/registry), and the errors of each, through musl's port and the host entry points. */
#define _GNU_SOURCE
#include <dlfcn.h>
#include <pthread.h>
#include <stdio.h>
#include <string.h>

void llvmnet_test_register(void);

static void report(const char *label, const void *result)
{
	const char *error = dlerror();
	printf("%-36s %s: %s\n", label, result ? "ok" : "null", error ? error : "(no error)");
}

static void *other_thread(void *argument)
{
	(void)argument;
	report("thread: dlopen unknown", dlopen("libllvmnet-thread.so", RTLD_NOW));
	return 0;
}

int main(void)
{
	report("dlopen before registration", dlopen("libllvmnet-registry.so.1", RTLD_NOW));
	report("dlopen(NULL)", dlopen(0, RTLD_NOW));
	llvmnet_test_register();
	llvmnet_test_register();
	void *handle = dlopen("/opt/llvmnet/lib/libllvmnet-registry.so.1", RTLD_LAZY | RTLD_LOCAL);
	report("dlopen by path", handle);
	printf("%-36s %s\n", "dlopen again, same handle", dlopen("libllvmnet-registry.so.1", RTLD_NOW) == handle ? "yes" : "no");
	report("dlopen unknown", dlopen("libllvmnet-unknown.so.2", RTLD_NOW));
	report("dlopen absent host library", dlopen("libllvmnet-absent.so.1", RTLD_NOW));
	report("dlopen RTLD_NOLOAD, not loaded", dlopen("libllvmnet-registry.so", RTLD_NOW | RTLD_NOLOAD));
	report("dlopen RTLD_NOLOAD, loaded", dlopen("libllvmnet-registry.so.1", RTLD_NOW | RTLD_NOLOAD));

	int (*add)(int, int) = (int (*)(int, int))dlsym(handle, "registry_add");
	report("dlsym registry_add", add);
	double (*scale)(double, int) = (double (*)(double, int))dlsym(handle, "registry_scale");
	const char *(*greeting)(void) = (const char *(*)(void))dlsym(handle, "registry_greeting");
	printf("%-36s %d %g %s\n", "calls through the stubs", add(2, 40), scale(1.5, 3), greeting());
	report("dlsym missing", dlsym(handle, "registry_missing"));
	report("dlsym resolver failure", dlsym(handle, "registry_fail"));
	report("dlsym invalid handle", dlsym((void *)0x1234, "registry_add"));
	report("dlsym RTLD_DEFAULT, nothing global", dlsym(RTLD_DEFAULT, "registry_add"));
	void *global = dlopen("libllvmnet-registry.so", RTLD_NOW | RTLD_GLOBAL);
	report("dlopen second name, RTLD_GLOBAL", global);
	printf("%-36s %s\n", "second name, another handle", global != handle ? "yes" : "no");
	report("dlsym RTLD_DEFAULT", dlsym(RTLD_DEFAULT, "registry_add"));
	report("dlsym RTLD_NEXT missing", dlsym(RTLD_NEXT, "registry_missing"));

	Dl_info info;
	int found = dladdr((void *)add, &info);
	printf("%-36s %d %s %s %s %s\n", "dladdr of a stub", found, info.dli_fname, info.dli_sname,
		info.dli_saddr == (void *)add ? "same-address" : "other-address", info.dli_fbase == handle ? "handle-base" : "other-base");
	printf("%-36s %d\n", "dladdr of main", dladdr((void *)main, &info));

	pthread_t thread;
	dlopen("libllvmnet-unknown.so.3", RTLD_NOW);
	pthread_create(&thread, 0, other_thread, 0);
	pthread_join(thread, 0);
	report("main thread keeps its own error", (void *)1);
	printf("%-36s %d\n", "dlclose", dlclose(handle));
	report("dlsym after dlclose", dlsym(handle, "registry_add"));
	printf("%-36s %d\n", "dlclose invalid handle", dlclose((void *)0x1234));
	report("error of dlclose", 0);
	report("dlerror cleared", (void *)1);
	return 0;
}
