/* The renderer of tests/ext-skia.sh (NX-13): Skia's Ganesh GL backend, through the SkiaSharp C API as Avalonia uses
 * it, drawing into a window that the host application created and owns.
 *
 *   skia-gl --x11 <Display *> <Window> | --win32 <HWND> | --layer <CALayer *> | --pbuffer   [--size <w> <h>] [--frames <n>] [--trace]
 *
 * The host passes its native handles as numbers. The program initializes EGL on them (a Display * of 0 lets the EGL
 * implementation open its own connection to the X server), builds Skia's GrGLInterface from eglGetProcAddress, wraps
 * the window's framebuffer as an SkSurface and draws an animated scene for the given number of frames, reading each
 * frame back before eglSwapBuffers presents it. It prints the last frame's hash, the hash of the same scene drawn by
 * Skia's CPU rasterizer, how far the two are apart, and a few pixels of flat regions. In portable mode EGL and GLES are
 * the gles extension's; the native build links the host's libraries. */
#include "include/c/gr_context.h"
#include "include/c/sk_canvas.h"
#include "include/c/sk_image.h"
#include "include/c/sk_maskfilter.h"
#include "include/c/sk_paint.h"
#include "include/c/sk_path.h"
#include "include/c/sk_pathbuilder.h"
#include "include/c/sk_shader.h"
#include "include/c/sk_surface.h"

#include <EGL/egl.h>
#include <EGL/eglext.h>
#include <GLES2/gl2.h>

#include <stdint.h>
#include <stdio.h>
#include <stdlib.h>
#include <string.h>
#include <time.h>

#define FAIL(...) do { fprintf(stderr, "skia-gl: " __VA_ARGS__); fputc('\n', stderr); return 1; } while (0)

static double now(void) {
    struct timespec ts;
    clock_gettime(CLOCK_MONOTONIC, &ts);
    return ts.tv_sec + ts.tv_nsec * 1e-9;
}

static uint64_t fnv1a(const uint8_t *p, size_t n) {
    uint64_t h = 1469598103934665603ull;
    for (size_t i = 0; i < n; ++i) { h ^= p[i]; h *= 1099511628211ull; }
    return h;
}

static int trace;

static gr_gl_func_ptr get_proc(void *context, const char *name) {
    (void)context;
    gr_gl_func_ptr function = (gr_gl_func_ptr)eglGetProcAddress(name);
    if (trace && !function) fprintf(stderr, "eglGetProcAddress(%s): none\n", name);
    return function;
}

/* A UI-like frame: a gradient header, cards with rounded corners and shadows (a blur mask filter), circles, a stroked
 * cubic path, a radial gradient, a bitmap drawn scaled (a texture upload), a clip and a rotation that the frame number
 * animates. The flat regions sampled below are not antialiased, so every renderer must give them exactly. */
static void draw(sk_canvas_t *canvas, int w, int h, int frame, sk_image_t *image) {
    sk_canvas_clear(canvas, 0xFFF3F4F6);
    sk_paint_t *paint = sk_paint_new();
    sk_paint_set_antialias(paint, true);

    sk_point_t points[2] = {{0, 0}, {(float)w, 0}};
    sk_color_t colors[2] = {0xFF3B82F6, 0xFF9333EA};
    sk_shader_t *gradient = sk_shader_new_linear_gradient(points, colors, NULL, 2, CLAMP_SK_SHADER_TILEMODE, NULL);
    sk_paint_set_shader(paint, gradient);
    sk_rect_t header = {0, 0, (float)w, 48};
    sk_canvas_draw_rect(canvas, &header, paint);
    sk_paint_set_shader(paint, NULL);
    sk_shader_unref(gradient);

    sk_maskfilter_t *blur = sk_maskfilter_new_blur(NORMAL_SK_BLUR_STYLE, 4.0f);
    for (int i = 0; i < 6; ++i) {
        float x = 16 + (i % 3) * ((w - 32) / 3.0f), y = 64 + (i / 3) * 120.0f;
        sk_rect_t card = {x + 4, y + 4, x + (w - 32) / 3.0f - 12, y + 108};
        sk_rect_t shadow = {card.left + 3, card.top + 4, card.right + 3, card.bottom + 4};
        sk_paint_set_color(paint, 0x40000000);
        sk_paint_set_maskfilter(paint, blur);
        sk_canvas_draw_round_rect(canvas, &shadow, 10, 10, paint);
        sk_paint_set_maskfilter(paint, NULL);
        sk_paint_set_color(paint, 0xFFFFFFFF);
        sk_canvas_draw_round_rect(canvas, &card, 10, 10, paint);
        sk_paint_set_color(paint, 0xFF10B981 + (uint32_t)i * 0x00100810);
        sk_canvas_draw_circle(canvas, x + 28, y + 30, 14, paint);
    }
    sk_maskfilter_unref(blur);

    sk_paint_set_style(paint, STROKE_SK_PAINT_STYLE);
    sk_paint_set_stroke_width(paint, 5);
    sk_paint_set_stroke_cap(paint, ROUND_SK_STROKE_CAP);
    sk_paint_set_color(paint, 0xFFEF4444);
    sk_pathbuilder_t *builder = sk_pathbuilder_new();
    sk_pathbuilder_move_to(builder, 24, (float)h - 40);
    sk_pathbuilder_cubic_to(builder, w * 0.3f, (float)h - 140 - frame * 4, w * 0.6f, (float)h + 40, (float)w - 24, (float)h - 90);
    sk_path_t *path = sk_pathbuilder_detach_path(builder);
    sk_canvas_draw_path(canvas, path, paint);
    sk_path_delete(path);
    sk_pathbuilder_delete(builder);
    sk_paint_set_style(paint, FILL_SK_PAINT_STYLE);

    sk_canvas_save(canvas);
    sk_rect_t clip = {(float)w - 150, (float)h - 150, (float)w - 10, (float)h - 10};
    sk_canvas_clip_rect_with_operation(canvas, &clip, INTERSECT_SK_CLIPOP, true);
    sk_point_t center = {(float)w - 80, (float)h - 80};
    sk_color_t rings[3] = {0xFFFDE047, 0xFFF97316, 0x00F97316};
    sk_shader_t *radial = sk_shader_new_radial_gradient(&center, 90, rings, NULL, 3, CLAMP_SK_SHADER_TILEMODE, NULL);
    sk_paint_set_shader(paint, radial);
    sk_canvas_draw_rect(canvas, &clip, paint);
    sk_paint_set_shader(paint, NULL);
    sk_shader_unref(radial);
    sk_canvas_translate(canvas, center.x, center.y);
    sk_canvas_rotate_degrees(canvas, 15.0f * frame);
    sk_rect_t square = {-30, -30, 30, 30};
    sk_paint_set_color(paint, 0xC01E3A8A);
    sk_canvas_draw_rect(canvas, &square, paint);
    sk_canvas_restore(canvas);

    sk_rect_t source = {0, 0, 16, 16}, target = {16, (float)h - 120, 112, (float)h - 24};
    sk_sampling_options_t sampling = {0};
    sk_canvas_draw_image_rect(canvas, image, &source, &target, &sampling, NULL);
    sk_paint_delete(paint);
}

/* A 16x16 checkerboard with a diagonal, drawn with nearest sampling: the scaled cells are flat and exact. */
static sk_image_t *checkerboard(void) {
    static uint32_t pixels[16 * 16];
    for (int y = 0; y < 16; ++y)
        for (int x = 0; x < 16; ++x)
            pixels[y * 16 + x] = x == y ? 0xFF0000FFu : ((x / 4 + y / 4) & 1) ? 0xFF404040u : 0xFFE0E0E0u;
    sk_imageinfo_t info = {NULL, 16, 16, RGBA_8888_SK_COLORTYPE, PREMUL_SK_ALPHATYPE};
    return sk_image_new_raster_copy(&info, pixels, 16 * 4);
}

int main(int argc, char **argv) {
    setvbuf(stdout, NULL, _IONBF, 0);
    const char *kind = NULL;
    uintptr_t native_display = 0, native_window = 0;
    int w = 480, h = 360, frames = 8;
    for (int i = 1; i < argc; ++i) {
        if (!strcmp(argv[i], "--x11") && i + 2 < argc) {
            kind = "x11";
            native_display = (uintptr_t)strtoull(argv[++i], NULL, 0);
            native_window = (uintptr_t)strtoull(argv[++i], NULL, 0);
        } else if ((!strcmp(argv[i], "--win32") || !strcmp(argv[i], "--layer")) && i + 1 < argc) {
            kind = argv[i] + 2;
            native_window = (uintptr_t)strtoull(argv[++i], NULL, 0);
        } else if (!strcmp(argv[i], "--pbuffer")) {
            kind = "pbuffer";
        } else if (!strcmp(argv[i], "--size") && i + 2 < argc) {
            w = atoi(argv[++i]);
            h = atoi(argv[++i]);
        } else if (!strcmp(argv[i], "--frames") && i + 1 < argc) {
            frames = atoi(argv[++i]);
        } else if (!strcmp(argv[i], "--trace")) {
            trace = 1;
        } else {
            FAIL("usage: skia-gl --x11 <display> <window> | --win32 <hwnd> | --layer <calayer> | --pbuffer [--size <w> <h>] [--frames <n>]");
        }
    }
    if (!kind) FAIL("name the window with --x11, --win32 or --layer, or use --pbuffer");

    EGLDisplay display = !strcmp(kind, "x11")
        ? eglGetPlatformDisplay(EGL_PLATFORM_X11_KHR, (void *)native_display, NULL)
        : eglGetDisplay(EGL_DEFAULT_DISPLAY);
    EGLint major = 0, minor = 0;
    if (display == EGL_NO_DISPLAY || !eglInitialize(display, &major, &minor)) FAIL("no EGL display (0x%x)", eglGetError());
    eglBindAPI(EGL_OPENGL_ES_API);
    int window = strcmp(kind, "pbuffer") != 0;
    const EGLint config_attributes[] = {
        EGL_SURFACE_TYPE, window ? EGL_WINDOW_BIT : EGL_PBUFFER_BIT, EGL_RENDERABLE_TYPE, EGL_OPENGL_ES2_BIT,
        EGL_RED_SIZE, 8, EGL_GREEN_SIZE, 8, EGL_BLUE_SIZE, 8, EGL_ALPHA_SIZE, 8, EGL_STENCIL_SIZE, 8, EGL_NONE};
    EGLConfig config;
    EGLint configs = 0;
    if (!eglChooseConfig(display, config_attributes, &config, 1, &configs) || configs == 0) FAIL("no EGL config (0x%x)", eglGetError());
    EGLSurface surface;
    if (window) {
        surface = eglCreateWindowSurface(display, config, (EGLNativeWindowType)native_window, NULL);
    } else {
        const EGLint pbuffer_attributes[] = {EGL_WIDTH, w, EGL_HEIGHT, h, EGL_NONE};
        surface = eglCreatePbufferSurface(display, config, pbuffer_attributes);
    }
    if (surface == EGL_NO_SURFACE) FAIL("no %s surface (0x%x)", kind, eglGetError());
    const EGLint context_attributes[] = {EGL_CONTEXT_CLIENT_VERSION, 2, EGL_NONE};
    EGLContext context = eglCreateContext(display, config, EGL_NO_CONTEXT, context_attributes);
    if (context == EGL_NO_CONTEXT || !eglMakeCurrent(display, surface, surface, context)) FAIL("no GLES context (0x%x)", eglGetError());
    eglSwapInterval(display, 0);
    EGLint surface_width = 0, surface_height = 0, samples = 0, stencil = 0;
    eglQuerySurface(display, surface, EGL_WIDTH, &surface_width);
    eglQuerySurface(display, surface, EGL_HEIGHT, &surface_height);
    eglGetConfigAttrib(display, config, EGL_SAMPLES, &samples);
    eglGetConfigAttrib(display, config, EGL_STENCIL_SIZE, &stencil);
    /* The window's size is in points; a layer on a Retina display has more pixels (its backing scale), and the frame is
     * drawn scaled to them, as Avalonia draws. */
    const int lw = w, lh = h;
    if (surface_width != w || surface_height != h) {
        if (surface_width < w || surface_width * lh != surface_height * lw)
            FAIL("the %s surface is %dx%d, not %dx%d or a multiple of it", kind, surface_width, surface_height, w, h);
        w = surface_width;
        h = surface_height;
    }
    fprintf(stderr, "EGL %d.%d (%s), %s, %s\n", major, minor, eglQueryString(display, EGL_VENDOR),
            (const char *)glGetString(GL_RENDERER), (const char *)glGetString(GL_VERSION));

    const gr_glinterface_t *gl = gr_glinterface_assemble_gles_interface(NULL, get_proc);
    if (trace) fprintf(stderr, "GL_EXTENSIONS: %s\n", (const char *)glGetString(GL_EXTENSIONS));
    if (!gl || !gr_glinterface_validate(gl)) FAIL("Skia could not assemble a GLES interface from eglGetProcAddress");
    gr_direct_context_t *gpu = gr_direct_context_make_gl(gl);
    if (!gpu) FAIL("Skia could not make a GL context");
    GLint framebuffer = 0;
    glGetIntegerv(GL_FRAMEBUFFER_BINDING, &framebuffer);
    gr_gl_framebufferinfo_t framebuffer_info = {(unsigned)framebuffer, 0x8058 /* GL_RGBA8 */, false};
    gr_backendrendertarget_t *target = gr_backendrendertarget_new_gl(w, h, samples, stencil, &framebuffer_info);
    sk_surface_t *gpu_surface = sk_surface_new_backend_render_target((gr_recording_context_t *)gpu, target, BOTTOM_LEFT_GR_SURFACE_ORIGIN,
                                                                     RGBA_8888_SK_COLORTYPE, NULL, NULL);
    if (!gpu_surface) FAIL("Skia could not wrap the window's framebuffer");
    if (w == lw) printf("surface: %s %dx%d, Skia's GL interface from eglGetProcAddress\n", kind, w, h);
    else printf("surface: %s %dx%d pixels for %dx%d (backing scale %g), Skia's GL interface from eglGetProcAddress\n", kind, w, h, lw, lh, (double)w / lw);

    sk_image_t *image = checkerboard();
    size_t size = (size_t)w * h * 4;
    uint8_t *pixels = malloc(size), *row = malloc((size_t)w * 4);
    double start = now(), first = 0;
    for (int frame = 0; frame < frames; ++frame) {
        sk_canvas_t *canvas = sk_surface_get_canvas(gpu_surface);
        sk_canvas_save(canvas);
        sk_canvas_scale(canvas, (float)w / lw, (float)h / lh);
        draw(canvas, lw, lh, frame, image);
        sk_canvas_restore(canvas);
        gr_direct_context_flush_and_submit(gpu, false);
        if (frame == frames - 1) {
            glReadPixels(0, 0, w, h, GL_RGBA, GL_UNSIGNED_BYTE, pixels);
            for (int y = 0; y < h / 2; ++y) {   /* GL's rows go up; the hash is of the image top down */
                memcpy(row, pixels + (size_t)y * w * 4, (size_t)w * 4);
                memcpy(pixels + (size_t)y * w * 4, pixels + (size_t)(h - 1 - y) * w * 4, (size_t)w * 4);
                memcpy(pixels + (size_t)(h - 1 - y) * w * 4, row, (size_t)w * 4);
            }
        }
        if (!eglSwapBuffers(display, surface)) FAIL("eglSwapBuffers failed in frame %d (0x%x)", frame, eglGetError());
        if (frame == 0) first = now() - start;
    }
    double elapsed = now() - start;
    if (glGetError() != GL_NO_ERROR) FAIL("GL reported an error");
    printf("presented %d frames with eglSwapBuffers\n", frames);
    printf("frame hash: %016llx\n", (unsigned long long)fnv1a(pixels, size));

    sk_imageinfo_t info = {NULL, w, h, RGBA_8888_SK_COLORTYPE, PREMUL_SK_ALPHATYPE};
    sk_surface_t *raster = sk_surface_new_raster(&info, (size_t)w * 4, NULL);
    uint8_t *reference = malloc(size);
    sk_canvas_t *raster_canvas = sk_surface_get_canvas(raster);
    sk_canvas_scale(raster_canvas, (float)w / lw, (float)h / lh);
    draw(raster_canvas, lw, lh, frames - 1, image);
    sk_surface_read_pixels(raster, &info, reference, (size_t)w * 4, 0, 0);
    printf("raster hash: %016llx\n", (unsigned long long)fnv1a(reference, size));
    size_t apart = 0;
    int largest = 0;
    for (size_t i = 0; i < size; i += 4) {
        int difference = 0;
        for (int c = 0; c < 4; ++c) {
            int d = abs((int)pixels[i + c] - (int)reference[i + c]);
            if (d > difference) difference = d;
        }
        if (difference > largest) largest = difference;
        if (difference > 32) apart++;
    }
    /* Antialiased edges and blurs differ between Skia's GPU and CPU paths; the bound is generous on purpose. */
    printf("GPU against raster: %s (largest difference %d, %zu of %d pixels apart by more than 32)\n",
           apart * 100 <= (size_t)w * h ? "close" : "far apart", largest, apart, w * h);
    static const int probes[][2] = {{200, 8}, {6, 200}, {40, 0}, {28, 300}, {60, 300}, {420, 60}};
    printf("pixels:");
    for (size_t p = 0; p < sizeof probes / sizeof probes[0]; ++p) {
        int x = probes[p][0] * lw / 480, y = probes[p][1] * lh / 360;
        if (p == 3 || p == 4) y = lh - 120 + (probes[p][1] - 240) * 96 / 120;
        const uint8_t *q = pixels + ((size_t)(y * h / lh) * w + x * w / lw) * 4;
        printf(" (%d,%d)=%02x%02x%02x%02x", x, y, q[0], q[1], q[2], q[3]);
    }
    printf("\n");
    fprintf(stderr, "time: first frame %.1f ms, %.2f ms per frame after it\n", first * 1e3,
            frames > 1 ? (elapsed - first) * 1e3 / (frames - 1) : 0.0);

    free(reference);
    sk_surface_unref(raster);
    sk_image_unref(image);
    free(row);
    free(pixels);
    sk_surface_unref(gpu_surface);
    gr_backendrendertarget_delete(target);
    gr_recording_context_unref((gr_recording_context_t *)gpu);
    gr_glinterface_unref(gl);
    eglMakeCurrent(display, EGL_NO_SURFACE, EGL_NO_SURFACE, EGL_NO_CONTEXT);
    eglDestroyContext(display, context);
    eglDestroySurface(display, surface);
    eglTerminate(display);
    return apart * 100 <= (size_t)w * h ? 0 : 2;
}
