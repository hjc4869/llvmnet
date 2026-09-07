#include <stdio.h>
#include <stdint.h>
#include <libavutil/md5.h>
#include <libavutil/channel_layout.h>
#include <libavfilter/avfilter.h>
#include <libavdevice/avdevice.h>
#include <libswscale/swscale.h>
#include <libswresample/swresample.h>

int main(void)
{
    uint8_t input[16 * 16 * 3];
    uint8_t output[8 * 8 * 4];
    for (int index = 0; index < (int)sizeof(input); index++) input[index] = (uint8_t)(index * 13);
    SwsContext *scale = sws_getContext(16, 16, AV_PIX_FMT_RGB24, 8, 8, AV_PIX_FMT_RGBA,
                                     SWS_BILINEAR | SWS_BITEXACT, NULL, NULL, NULL);
    if (!scale) return 1;
    const uint8_t *source[] = {input, NULL, NULL, NULL};
    uint8_t *destination[] = {output, NULL, NULL, NULL};
    int source_stride[] = {48, 0, 0, 0};
    int destination_stride[] = {32, 0, 0, 0};
    if (sws_scale(scale, source, source_stride, 0, 16, destination, destination_stride) != 8) return 2;
    sws_freeContext(scale);
    uint8_t digest[16];
    av_md5_sum(digest, output, sizeof(output));
    printf("scale=");
    for (int index = 0; index < 16; index++) printf("%02x", digest[index]);
    putchar('\n');
    AVChannelLayout layout = AV_CHANNEL_LAYOUT_MONO;
    SwrContext *resample = NULL;
    if (swr_alloc_set_opts2(&resample, &layout, AV_SAMPLE_FMT_S16, 24000,
                           &layout, AV_SAMPLE_FMT_S16, 48000, 0, NULL) < 0) return 3;
    if (swr_init(resample) < 0) return 4;
    int16_t samples[512], converted[512];
    for (int index = 0; index < 512; index++) samples[index] = (int16_t)((index * 113) % 32768 - 16384);
    const uint8_t *audio_source[] = {(const uint8_t *)samples};
    uint8_t *audio_output[] = {(uint8_t *)converted};
    int count = swr_convert(resample, audio_output, 512, audio_source, 512);
    if (count <= 0) return 5;
    av_md5_sum(digest, (const uint8_t *)converted, count * sizeof(int16_t));
    printf("resample=%d ", count);
    for (int index = 0; index < 16; index++) printf("%02x", digest[index]);
    putchar('\n');
    swr_free(&resample);
    AVFilterGraph *graph = avfilter_graph_alloc();
    if (!graph) return 6;
    avfilter_graph_free(&graph);
    avdevice_register_all();
    return 0;
}