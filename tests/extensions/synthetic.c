/* A program that uses the synthetic native extension (NX-03) directly and through dlopen. Natively it links the test
 * library; in portable mode it links the extension with --extension=synthetic or -lllvmnet-synthetic, and in system mode
 * the extension's Linux library. Every build prints the same. With "probe", it only reports whether dlopen finds the
 * host library. */
#include <dlfcn.h>
#include <stdio.h>
#include <string.h>
#include <llvmnet-synthetic.h>

int main(int argc, char **argv)
{
	void *handle = dlopen("libllvmnet-synthetic.so.1", RTLD_NOW);
	if (argc > 1 && strcmp(argv[1], "probe") == 0) {
		printf("dlopen: %s\n", handle ? "available" : dlerror());
		return 0;
	}
	char buffer[16];
	printf("version: %s\n", synthetic_version());
	printf("add: %d\n", synthetic_add(40, 2));
	printf("mix: %d\n", synthetic_mix(200, -3, 65543));
	printf("low: %u\n", synthetic_low(0x1234));
	printf("wide: %lld\n", (long long)synthetic_wide(-5, 1ull << 40));
	printf("scale: %.4f\n", synthetic_scale(1.25, 3.5f));
	size_t length = synthetic_format(buffer, sizeof buffer, "a-long-name", 123456);
	printf("format: %zu %s\n", length, buffer);
	printf("dlopen: %s\n", handle ? "ok" : dlerror());
	int32_t (*add)(int32_t, int32_t) = (int32_t (*)(int32_t, int32_t))dlsym(handle, "synthetic_add");
	printf("dlsym synthetic_add: %d\n", add ? add(1, 2) : -1);
	int32_t (*mix)(uint8_t, int16_t, uint32_t) = (int32_t (*)(uint8_t, int16_t, uint32_t))dlsym(handle, "synthetic_mix");
	printf("dlsym synthetic_mix: %d\n", mix ? mix(255, -1, 0) : 999);
	uint8_t (*low)(uint32_t) = (uint8_t (*)(uint32_t))dlsym(handle, "synthetic_low");
	printf("dlsym synthetic_low: %u\n", low ? low(0x1ff) : 999);
	printf("dlsym synthetic_missing: %s\n", dlsym(handle, "synthetic_missing") ? "found" : "null");
	printf("dlclose: %d\n", dlclose(handle));
	return 0;
}
