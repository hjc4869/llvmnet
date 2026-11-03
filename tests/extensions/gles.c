/* The program of tests/ext-gles.sh and of the gles fixture of the tests/xp/ bundle (NX-05): OpenGL ES 2 and 3 through
 * EGL, headless, as a portable program linked with the gles native extension (-lEGL -lGLESv2) and as a native one.
 * It draws a triangle into a 64x64 pbuffer and reads pixels back, registers a GL_KHR_debug callback obtained through
 * eglGetProcAddress and provokes an error that the driver reports through it, uses GLES 3 entry points when the host
 * offers a GLES 3 context, and loads EGL a second time the way ANGLE's loader does (dlopen of libEGL.so.1, dlsym of
 * eglGetProcAddress, every other function through it). Standard output holds only lines that every conforming host
 * prints the same (tests/extensions/gles.expected); the vendor, renderer and debug message go to standard error.
 *   gles              all of the above
 *   gles bench <n>    time n glGetError calls, directly and through eglGetProcAddress's pointer (standard error)
 * Mesa is reached through its surfaceless platform; ANGLE (Chromium's, Electron's) through the default display. */
#include <dlfcn.h>
#include <stdio.h>
#include <stdlib.h>
#include <string.h>
#include <time.h>
#include <EGL/egl.h>
#include <EGL/eglext.h>
#include <GLES3/gl3.h>
#include <GLES2/gl2ext.h>
#ifdef __LLVMNET__
#include <llvmnet/ext.h>
#endif

static int callbacks;
static GLenum callback_type;
static char callback_message[512];
static int callback_user_ok = 1;

static void GL_APIENTRY on_debug(GLenum source, GLenum type, GLuint id, GLenum severity, GLsizei length, const GLchar *message,
                                 const void *user) {
    (void)source; (void)id; (void)severity; (void)length;
    callbacks++;
    callback_type = type;
    snprintf(callback_message, sizeof callback_message, "%s", message);
    if (user != (const void *)0x1234) callback_user_ok = 0;
}

static void fail(const char *what) {
    printf("FAIL: %s (EGL error 0x%x)\n", what, eglGetError());
    exit(1);
}

static GLuint compile(GLenum type, const char *source) {
    GLuint shader = glCreateShader(type);
    glShaderSource(shader, 1, &source, NULL);
    glCompileShader(shader);
    GLint ok = 0;
    glGetShaderiv(shader, GL_COMPILE_STATUS, &ok);
    if (!ok) {
        char log[1024];
        glGetShaderInfoLog(shader, sizeof log, NULL, log);
        printf("FAIL: shader: %s\n", log);
        exit(1);
    }
    return shader;
}

static EGLDisplay open_display(PFNEGLGETPROCADDRESSPROC get_proc_address, PFNEGLGETDISPLAYPROC get_display, PFNEGLINITIALIZEPROC initialize) {
    PFNEGLGETPLATFORMDISPLAYEXTPROC get_platform_display = (PFNEGLGETPLATFORMDISPLAYEXTPROC)get_proc_address("eglGetPlatformDisplayEXT");
    EGLDisplay display = EGL_NO_DISPLAY;
    EGLint major = 0, minor = 0;
    const char *client = eglQueryString(EGL_NO_DISPLAY, EGL_EXTENSIONS);
    if (get_platform_display && client && strstr(client, "EGL_MESA_platform_surfaceless"))
        display = get_platform_display(EGL_PLATFORM_SURFACELESS_MESA, EGL_DEFAULT_DISPLAY, NULL);
    if (display == EGL_NO_DISPLAY || !initialize(display, &major, &minor)) display = get_display(EGL_DEFAULT_DISPLAY);
    if (display == EGL_NO_DISPLAY || !initialize(display, &major, &minor)) return EGL_NO_DISPLAY;
    return display;
}

/* A context of the given GLES major version current on a 64x64 pbuffer, or EGL_NO_CONTEXT. */
static EGLContext make_current(EGLDisplay display, int version, EGLSurface *surface) {
    const EGLint config_attributes[] = {EGL_SURFACE_TYPE, EGL_PBUFFER_BIT, EGL_RENDERABLE_TYPE,
                                        version >= 3 ? EGL_OPENGL_ES3_BIT : EGL_OPENGL_ES2_BIT, EGL_RED_SIZE, 8, EGL_GREEN_SIZE, 8,
                                        EGL_BLUE_SIZE, 8, EGL_ALPHA_SIZE, 8, EGL_NONE};
    EGLConfig config;
    EGLint count = 0;
    if (!eglChooseConfig(display, config_attributes, &config, 1, &count) || count < 1) return EGL_NO_CONTEXT;
    const EGLint pbuffer[] = {EGL_WIDTH, 64, EGL_HEIGHT, 64, EGL_NONE};
    *surface = eglCreatePbufferSurface(display, config, pbuffer);
    const EGLint context_attributes[] = {EGL_CONTEXT_CLIENT_VERSION, version, EGL_NONE};
    EGLContext context = eglCreateContext(display, config, EGL_NO_CONTEXT, context_attributes);
    if (*surface == EGL_NO_SURFACE || context == EGL_NO_CONTEXT || !eglMakeCurrent(display, *surface, *surface, context)) return EGL_NO_CONTEXT;
    return context;
}

static void draw_and_read(const char *label) {
    GLuint program = glCreateProgram();
    glAttachShader(program, compile(GL_VERTEX_SHADER, "attribute vec2 p; void main() { gl_Position = vec4(p, 0.0, 1.0); }"));
    glAttachShader(program, compile(GL_FRAGMENT_SHADER, "precision mediump float; void main() { gl_FragColor = vec4(1.0, 0.4, 0.2, 1.0); }"));
    glBindAttribLocation(program, 0, "p");
    glLinkProgram(program);
    GLint linked = 0;
    glGetProgramiv(program, GL_LINK_STATUS, &linked);
    if (!linked) fail("program link");
    glUseProgram(program);
    const GLfloat triangle[] = {-1, -1, 1, -1, -1, 1};
    GLuint buffer = 0;
    glGenBuffers(1, &buffer);
    glBindBuffer(GL_ARRAY_BUFFER, buffer);
    glBufferData(GL_ARRAY_BUFFER, sizeof triangle, triangle, GL_STATIC_DRAW);
    glVertexAttribPointer(0, 2, GL_FLOAT, GL_FALSE, 0, NULL);
    glEnableVertexAttribArray(0);
    glViewport(0, 0, 64, 64);
    glClearColor(0, 0, 1, 1);
    glClear(GL_COLOR_BUFFER_BIT);
    glDrawArrays(GL_TRIANGLES, 0, 3);
    unsigned char pixels[2][4];
    glReadPixels(4, 4, 1, 1, GL_RGBA, GL_UNSIGNED_BYTE, pixels[0]);
    glReadPixels(60, 60, 1, 1, GL_RGBA, GL_UNSIGNED_BYTE, pixels[1]);
    printf("%s: inside %d,%d,%d,%d outside %d,%d,%d,%d, glGetError 0x%x\n", label, pixels[0][0], pixels[0][1], pixels[0][2],
           pixels[0][3], pixels[1][0], pixels[1][1], pixels[1][2], pixels[1][3], glGetError());
    glDeleteBuffers(1, &buffer);
    glDeleteProgram(program);
}

/* Calls of entry points the program has not called before, with arguments that change no state that matters. */
#define STATE_CALLS 26
static void state_calls(void) {
    glLineWidth(1.0f); glDepthMask(GL_TRUE); glColorMask(1, 1, 1, 1); glStencilMask(0xffffffffu); glPolygonOffset(0, 0);
    glSampleCoverage(1.0f, GL_FALSE); glBlendColor(0, 0, 0, 0); glClearDepthf(1.0f); glClearStencil(0); glFrontFace(GL_CCW);
    glCullFace(GL_BACK); glHint(GL_GENERATE_MIPMAP_HINT, GL_DONT_CARE); glDepthFunc(GL_LESS); glDepthRangef(0, 1);
    glBlendEquation(GL_FUNC_ADD); glBlendFunc(GL_ONE, GL_ZERO); glPixelStorei(GL_PACK_ALIGNMENT, 4); glActiveTexture(GL_TEXTURE0);
    glStencilFunc(GL_ALWAYS, 0, 0xff); glStencilOp(GL_KEEP, GL_KEEP, GL_KEEP); glIsTexture(0); glIsBuffer(0); glIsProgram(0);
    glIsShader(0); glIsFramebuffer(0); glIsRenderbuffer(0);
}

static double now(void) {
    struct timespec time;
    clock_gettime(CLOCK_MONOTONIC, &time);
    return time.tv_sec * 1e9 + time.tv_nsec;
}

int main(int argc, char **argv) {
    const char *mode = argc > 1 ? argv[1] : "all";
    setvbuf(stdout, NULL, _IONBF, 0);
#ifdef __LLVMNET__
    if (!llvmnet_ext_available("gles")) {
        printf("FAIL: the gles extension is not available:\n%s\n", llvmnet_ext_library("gles"));
        return 1;
    }
    fprintf(stderr, "%s\n", llvmnet_ext_library("gles"));
#endif
    EGLDisplay display = open_display(eglGetProcAddress, eglGetDisplay, eglInitialize);
    if (display == EGL_NO_DISPLAY) fail("eglInitialize");
    fprintf(stderr, "EGL vendor %s, version %s\n", eglQueryString(display, EGL_VENDOR), eglQueryString(display, EGL_VERSION));
    printf("EGL initialized; GLES API bound: %s\n", eglBindAPI(EGL_OPENGL_ES_API) ? "yes" : "no");
    EGLSurface surface = EGL_NO_SURFACE;
    EGLContext context = make_current(display, 2, &surface);
    if (context == EGL_NO_CONTEXT) fail("GLES 2 context");
    fprintf(stderr, "GL_RENDERER %s\nGL_VERSION %s\n", glGetString(GL_RENDERER), glGetString(GL_VERSION));
    if (strcmp(mode, "bench") == 0) {
        long count = argc > 2 ? atol(argv[2]) : 1000000;
        double start = now();
        for (long i = 0; i < count; i++) glGetError();
        double direct = (now() - start) / count;
        PFNGLGETERRORPROC get_error = (PFNGLGETERRORPROC)eglGetProcAddress("glGetError");
        start = now();
        for (long i = 0; i < count; i++) get_error();
        double pointer = (now() - start) / count;
        printf("glGetError: %.1f ns per direct call, %.1f ns through eglGetProcAddress's pointer\n", direct, pointer);
        /* The first call of an entry point binds it (and, under the JIT, compiles its stub); later calls do not. */
        double first = now();
        state_calls();
        first = now() - first;
        double again = now();
        state_calls();
        again = now() - again;
        printf("first calls: %.1f us for %d entry points, %.2f us for the same calls again\n", first / 1e3, STATE_CALLS, again / 1e3);
        return 0;
    }
    draw_and_read("GLES 2 pixels");
    /* GL_KHR_debug through eglGetProcAddress, with a callback into this program. */
    PFNGLDEBUGMESSAGECALLBACKKHRPROC debug_callback = (PFNGLDEBUGMESSAGECALLBACKKHRPROC)eglGetProcAddress("glDebugMessageCallbackKHR");
    const char *extensions = (const char *)glGetString(GL_EXTENSIONS);
    if (debug_callback && extensions && strstr(extensions, "GL_KHR_debug")) {
        glEnable(GL_DEBUG_OUTPUT_KHR);
        glEnable(GL_DEBUG_OUTPUT_SYNCHRONOUS_KHR);
        debug_callback(on_debug, (const void *)0x1234);
        glEnable(0x12345);
        GLenum error = glGetError();
        fprintf(stderr, "debug message: %s\n", callback_message);
        printf("GL_KHR_debug: %s, type %s, user pointer %s, glGetError 0x%x\n", callbacks >= 1 ? "callback received" : "no callback",
               callback_type == GL_DEBUG_TYPE_ERROR_KHR ? "GL_DEBUG_TYPE_ERROR" : "other", callback_user_ok ? "kept" : "changed", error);
        debug_callback(NULL, NULL);
        glDisable(GL_DEBUG_OUTPUT_KHR);
    } else {
        printf("GL_KHR_debug: not offered\n");
    }
    /* An EGL name: libglvnd's eglGetProcAddress returns a dispatch stub for any unknown gl name. */
    printf("unknown entry point: %s\n", eglGetProcAddress("eglNoSuchFunctionLLVMNET") ? "found" : "null");
    eglMakeCurrent(display, EGL_NO_SURFACE, EGL_NO_SURFACE, EGL_NO_CONTEXT);
    eglDestroyContext(display, context);
    eglDestroySurface(display, surface);
    /* GLES 3: a vertex array object and an indexed string, which GLES 2 does not have. */
    context = make_current(display, 3, &surface);
    if (context != EGL_NO_CONTEXT) {
        GLuint array = 0;
        glGenVertexArrays(1, &array);
        glBindVertexArray(array);
        GLint count = 0;
        glGetIntegerv(GL_NUM_EXTENSIONS, &count);
        printf("GLES 3: vertex array %s, extension strings %s\n", glIsVertexArray(array) ? "bound" : "missing",
               count > 0 && glGetStringi(GL_EXTENSIONS, 0) ? "listed" : "missing");
        draw_and_read("GLES 3 pixels");
        glDeleteVertexArrays(1, &array);
        eglMakeCurrent(display, EGL_NO_SURFACE, EGL_NO_SURFACE, EGL_NO_CONTEXT);
        eglDestroyContext(display, context);
        eglDestroySurface(display, surface);
    } else {
        printf("GLES 3: not offered\n");
    }
    eglTerminate(display);
    /* EGL loaded the way ANGLE's loader does: by name, with every function from the library's eglGetProcAddress. */
    void *library = dlopen("libEGL.so.1", RTLD_NOW);
    PFNEGLGETPROCADDRESSPROC get_proc_address = library ? (PFNEGLGETPROCADDRESSPROC)dlsym(library, "eglGetProcAddress") : NULL;
    if (!get_proc_address) {
        printf("dlopen libEGL.so.1: %s\n", library ? "no eglGetProcAddress" : dlerror());
        return 1;
    }
    PFNEGLGETDISPLAYPROC get_display = (PFNEGLGETDISPLAYPROC)get_proc_address("eglGetDisplay");
    PFNEGLINITIALIZEPROC initialize = (PFNEGLINITIALIZEPROC)get_proc_address("eglInitialize");
    PFNEGLTERMINATEPROC terminate = (PFNEGLTERMINATEPROC)get_proc_address("eglTerminate");
    PFNGLGETSTRINGPROC get_string = (PFNGLGETSTRINGPROC)get_proc_address("glGetString");
    display = get_display && initialize ? open_display(get_proc_address, get_display, initialize) : EGL_NO_DISPLAY;
    printf("dlopen libEGL.so.1: eglGetProcAddress %s, display %s, glGetString %s\n", get_proc_address ? "found" : "null",
           display != EGL_NO_DISPLAY ? "initialized" : "failed", get_string ? "found" : "null");
    if (display != EGL_NO_DISPLAY && terminate) terminate(display);
    return callbacks >= 1 || !(debug_callback && extensions && strstr(extensions, "GL_KHR_debug")) ? 0 : 1;
}
