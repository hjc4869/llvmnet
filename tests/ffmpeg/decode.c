#include <stdio.h>
#include <stdlib.h>
#include <string.h>
#include <libavcodec/avcodec.h>
#include <libavformat/avformat.h>
#include <libavutil/imgutils.h>
#include <libavutil/md5.h>
#include <libavutil/samplefmt.h>

static int frame_count;
static int benchmark;

static int receive(AVCodecContext *decoder, AVFrame *frame, int limit)
{
    while (frame_count < limit) {
        int result = avcodec_receive_frame(decoder, frame);
        if (result == AVERROR(EAGAIN) || result == AVERROR_EOF)
            return 0;
        if (result < 0)
            return result;
        if (benchmark) {
            frame_count++;
            av_frame_unref(frame);
            continue;
        }
        unsigned char digest[16];
        if (decoder->codec_type == AVMEDIA_TYPE_AUDIO) {
            int channels = frame->ch_layout.nb_channels;
            int planar = av_sample_fmt_is_planar(frame->format);
            int plane_size = frame->nb_samples * av_get_bytes_per_sample(frame->format) * (planar ? 1 : channels);
            struct AVMD5 *hash = av_md5_alloc();
            if (!hash)
                return AVERROR(ENOMEM);
            av_md5_init(hash);
            for (int plane = 0; plane < (planar ? channels : 1); plane++)
                av_md5_update(hash, frame->extended_data[plane], plane_size);
            av_md5_final(hash, digest);
            av_free(hash);
            printf("%d %lld samples=%d channels=%d format=%d ", frame_count, (long long)frame->pts,
                   frame->nb_samples, channels, frame->format);
        } else {
            int size = av_image_get_buffer_size(frame->format, frame->width, frame->height, 1);
            if (size < 0)
                return size;
            unsigned char *pixels = av_malloc(size);
            if (!pixels)
                return AVERROR(ENOMEM);
            result = av_image_copy_to_buffer(pixels, size, (const uint8_t *const *)frame->data,
                                            frame->linesize, frame->format, frame->width, frame->height, 1);
            if (result < 0) {
                av_free(pixels);
                return result;
            }
            av_md5_sum(digest, pixels, size);
            av_free(pixels);
            printf("%d %lld %d %d %d ", frame_count, (long long)frame->pts,
                   frame->width, frame->height, frame->format);
        }
        for (int index = 0; index < 16; index++)
            printf("%02x", digest[index]);
        putchar('\n');
        frame_count++;
        av_frame_unref(frame);
        if (result < 0)
            return result;
    }
    return 0;
}

int main(int argc, char **argv)
{
    if (argc < 2 || argc > 7) {
        puts("usage: decode input [frame-limit] [video|audio] [threads:1-64] [auto|frame|slice] [hash|bench]");
        return 2;
    }
    if (argc == 7) {
        if (!strcmp(argv[6], "bench"))
            benchmark = 1;
        else if (strcmp(argv[6], "hash"))
            return 2;
    }
    int limit = argc >= 3 ? atoi(argv[2]) : 10;
    if (limit <= 0)
        return 2;
    int thread_count = 1;
    if (argc >= 5) {
        char *end;
        long count = strtol(argv[4], &end, 10);
        if (end == argv[4] || *end || count < 1 || count > 64)
            return 2;
        thread_count = (int)count;
    }
    int thread_type = FF_THREAD_FRAME | FF_THREAD_SLICE;
    if (argc >= 6) {
        if (!strcmp(argv[5], "frame"))
            thread_type = FF_THREAD_FRAME;
        else if (!strcmp(argv[5], "slice"))
            thread_type = FF_THREAD_SLICE;
        else if (strcmp(argv[5], "auto"))
            return 2;
    }
    enum AVMediaType media_type = argc >= 4 && !strcmp(argv[3], "audio") ? AVMEDIA_TYPE_AUDIO : AVMEDIA_TYPE_VIDEO;
    av_log_set_level(AV_LOG_ERROR);
    AVFormatContext *input = NULL;
    AVCodecContext *decoder = NULL;
    AVPacket *packet = NULL;
    AVFrame *frame = NULL;
    int result = avformat_open_input(&input, argv[1], NULL, NULL);
    if (result < 0)
        goto done;
    result = avformat_find_stream_info(input, NULL);
    if (result < 0)
        goto done;
    const AVCodec *codec = NULL;
    int stream = av_find_best_stream(input, media_type, -1, -1, &codec, 0);
    if (stream < 0) {
        result = stream;
        goto done;
    }
    decoder = avcodec_alloc_context3(codec);
    if (!decoder) {
        result = AVERROR(ENOMEM);
        goto done;
    }
    result = avcodec_parameters_to_context(decoder, input->streams[stream]->codecpar);
    if (result < 0)
        goto done;
    decoder->thread_count = thread_count;
    decoder->thread_type = thread_count > 1 ? thread_type : 0;
    result = avcodec_open2(decoder, codec, NULL);
    if (result < 0)
        goto done;
    if (argc >= 5)
        fprintf(stderr, "decoder=%s threads=%d thread_type=%d\n", codec->name,
                decoder->thread_count, decoder->active_thread_type);
    packet = av_packet_alloc();
    frame = av_frame_alloc();
    if (!packet || !frame) {
        result = AVERROR(ENOMEM);
        goto done;
    }
    while (frame_count < limit && (result = av_read_frame(input, packet)) >= 0) {
        if (packet->stream_index == stream) {
            result = avcodec_send_packet(decoder, packet);
            if (result >= 0)
                result = receive(decoder, frame, limit);
        }
        av_packet_unref(packet);
        if (result < 0)
            goto done;
    }
    if (frame_count < limit) {
        if (result != AVERROR_EOF)
            goto done;
        result = avcodec_send_packet(decoder, NULL);
        if (result >= 0)
            result = receive(decoder, frame, limit);
    } else {
        result = 0;
    }
done:
    av_frame_free(&frame);
    av_packet_free(&packet);
    avcodec_free_context(&decoder);
    avformat_close_input(&input);
    if (result < 0) {
        printf("decode failed: %d\n", result);
        return 1;
    }
    printf("frames=%d\n", frame_count);
    return frame_count == 0;
}