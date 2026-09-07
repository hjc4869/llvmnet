#include <stdio.h>
#include <errno.h>
#include <dav1d/dav1d.h>
int main(void)
{
    Dav1dSettings settings;
    dav1d_default_settings(&settings);
    settings.n_threads = 1;
    settings.max_frame_delay = 1;
    Dav1dContext *context = NULL;
    int result = dav1d_open(&context, &settings);
    if (result || !context) return 1;
    Dav1dPicture picture = {0};
    if (dav1d_get_picture(context, &picture) != DAV1D_ERR(EAGAIN)) return 2;
    dav1d_flush(context);
    dav1d_close(&context);
    if (context) return 3;
    printf("dav1d %s initialized and closed\n", dav1d_version());
    return 0;
}