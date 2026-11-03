/* The program of tests/ext-headers.sh (NX-09): GLFW 3.4 through an extension generated from its C header, on GLFW's
 * headless null platform, so that it runs anywhere and prints the same lines natively and in portable mode. It uses a
 * custom allocator (a structure of callbacks), the error callback (and the previous callback a setter returns), window
 * and framebuffer callbacks, the window user pointer, the null monitor's video mode (a structure the library owns),
 * gamma ramps (a structure of pointers) and the timer. */
#define GLFW_INCLUDE_NONE 1
#include <GLFW/glfw3.h>
#include <stdio.h>
#include <stdlib.h>
#include <string.h>

static int marker;
static int errors;

static void on_error(int code, const char *description) {
    errors++;
    printf("error callback: 0x%x %s\n", code, description);
}

struct counts { long allocations, reallocations, frees; };
static void *allocate(size_t size, void *user) {
    ((struct counts *)user)->allocations++;
    return malloc(size);
}
static void *reallocate(void *block, size_t size, void *user) {
    ((struct counts *)user)->reallocations++;
    return realloc(block, size);
}
static void deallocate(void *block, void *user) {
    if (block) ((struct counts *)user)->frees++;
    free(block);
}

static void on_size(GLFWwindow *window, int width, int height) {
    printf("window size callback: %dx%d, user pointer %s\n", width, height, glfwGetWindowUserPointer(window) == &marker ? "kept" : "lost");
}
static void on_framebuffer(GLFWwindow *window, int width, int height) {
    (void)window;
    printf("framebuffer size callback: %dx%d\n", width, height);
}
static void on_position(GLFWwindow *window, int x, int y) {
    (void)window;
    printf("window position callback: %d,%d\n", x, y);
}
static void on_focus(GLFWwindow *window, int focused) {
    (void)window;
    printf("focus callback: %s\n", focused ? "focused" : "unfocused");
}

int main(void) {
    setvbuf(stdout, NULL, _IONBF, 0);
    int major = 0, minor = 0, revision = 0;
    glfwGetVersion(&major, &minor, &revision);
    printf("GLFW %d.%d.%d, version string %s\n", major, minor, revision, strncmp(glfwGetVersionString(), "3.4", 3) == 0 ? "3.4" : "other");
    GLFWerrorfun previous = glfwSetErrorCallback(on_error);
    printf("previous error callback: %s\n", previous ? "set" : "none");
    printf("error callback returned by the next setter: %s\n", glfwSetErrorCallback(on_error) == on_error ? "the program's own" : "another");
    struct counts counts = {0};
    const GLFWallocator allocator = {allocate, reallocate, deallocate, &counts};
    glfwInitAllocator(&allocator);
    glfwInitHint(GLFW_PLATFORM, GLFW_PLATFORM_NULL);
    if (!glfwInit()) {
        printf("glfwInit failed\n");
        return 1;
    }
    printf("platform: %s\n", glfwGetPlatform() == GLFW_PLATFORM_NULL ? "null" : "other");
    glfwWindowHint(0x12345, 0);
    printf("errors so far: %d\n", errors);
    glfwWindowHint(GLFW_CLIENT_API, GLFW_NO_API);
    GLFWwindow *window = glfwCreateWindow(320, 240, "llvmnet", NULL, NULL);
    if (!window) {
        printf("glfwCreateWindow failed\n");
        return 1;
    }
    glfwSetWindowUserPointer(window, &marker);
    glfwSetWindowSizeCallback(window, on_size);
    glfwSetFramebufferSizeCallback(window, on_framebuffer);
    glfwSetWindowPosCallback(window, on_position);
    glfwSetWindowFocusCallback(window, on_focus);
    glfwSetWindowSize(window, 640, 480);
    glfwSetWindowPos(window, 10, 20);
    int width = 0, height = 0, x = 0, y = 0;
    glfwGetWindowSize(window, &width, &height);
    glfwGetWindowPos(window, &x, &y);
    printf("window: %dx%d at %d,%d, title %s\n", width, height, x, y, strcmp(glfwGetWindowTitle(window), "llvmnet") == 0 ? "kept" : "changed");
    printf("size callback returned by the next setter: %s\n", glfwSetWindowSizeCallback(window, NULL) == on_size ? "the program's own" : "another");
    glfwSetWindowShouldClose(window, GLFW_TRUE);
    printf("should close: %s\n", glfwWindowShouldClose(window) ? "yes" : "no");
    GLFWmonitor *monitor = glfwGetPrimaryMonitor();
    const GLFWvidmode *mode = monitor ? glfwGetVideoMode(monitor) : NULL;
    if (mode) printf("monitor %s: %dx%d at %d Hz, %d-%d-%d bits\n", glfwGetMonitorName(monitor), mode->width, mode->height, mode->refreshRate,
                     mode->redBits, mode->greenBits, mode->blueBits);
    const GLFWgammaramp *ramp = monitor ? glfwGetGammaRamp(monitor) : NULL;
    if (ramp) printf("gamma ramp: %u entries, last red %u\n", ramp->size, ramp->size ? ramp->red[ramp->size - 1] : 0);
    double before = glfwGetTime();
    glfwSetTime(100.0);
    printf("timer: set to 100 s, reads %s\n", glfwGetTime() >= 100.0 && before >= 0.0 ? "at least 100 s" : "less");
    glfwPollEvents();
    glfwDestroyWindow(window);
    glfwTerminate();
    printf("allocator: used %s, every allocation freed %s\n", counts.allocations > 0 ? "yes" : "no", counts.allocations == counts.frees ? "yes" : "no");
    return 0;
}
