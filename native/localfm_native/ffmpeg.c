/* FFmpeg decoder and prober. See localfm_native.h. */
#include "localfm_native.h"

#include <libavcodec/avcodec.h>
#include <libavformat/avformat.h>
#include <libavutil/channel_layout.h>
#include <libavutil/avstring.h>
#include <libavutil/opt.h>
#include <libswresample/swresample.h>

#include <stdlib.h>
#include <string.h>

#define OUT_CH 2

/* ================================================================ decoder */

struct lfm_decoder {
    AVFormatContext* fmt;
    AVCodecContext*  codec;
    SwrContext*      swr;
    AVPacket*        pkt;
    AVFrame*         frame;
    int              stream;
    AVRational       tb;
    int32_t          out_rate;

    float*  pend;        /* converted, not yet returned frames */
    int64_t pend_cap;    /* frames */
    int64_t pend_len;
    int64_t pend_off;

    int64_t origin_pts;  /* pts of the first decoded frame = position 0 */
    int     have_origin;
    int64_t seek_target; /* output frames; -1 = none */
    int     eof_sent;    /* flush packet sent to decoder */
    int     swr_drained;
    int64_t duration_frames;
};

static int setup_swr(lfm_decoder* d)
{
    AVChannelLayout out_layout = AV_CHANNEL_LAYOUT_STEREO;
    swr_free(&d->swr);
    if (swr_alloc_set_opts2(&d->swr, &out_layout, AV_SAMPLE_FMT_FLT, d->out_rate,
                            &d->codec->ch_layout, d->codec->sample_fmt, d->codec->sample_rate,
                            0, NULL) < 0)
        return -1;
    int ch = d->codec->ch_layout.nb_channels;
    if (ch == 1) av_opt_set_double(d->swr, "center_mix_level", 1.0, 0); /* mono: full level on both sides */
    if (ch > 2) av_opt_set_double(d->swr, "rematrix_maxval", 1.0, 0);   /* downmix without clipping */
    return swr_init(d->swr) < 0 ? -1 : 0;
}

static int ensure_pending(lfm_decoder* d, int64_t extra)
{
    if (d->pend_off > 0 && d->pend_off == d->pend_len) d->pend_off = d->pend_len = 0;
    if (d->pend_len + extra <= d->pend_cap) return 0;
    if (d->pend_off > 0) {
        memmove(d->pend, d->pend + d->pend_off * OUT_CH,
                (size_t)(d->pend_len - d->pend_off) * OUT_CH * sizeof(float));
        d->pend_len -= d->pend_off;
        d->pend_off = 0;
        if (d->pend_len + extra <= d->pend_cap) return 0;
    }
    int64_t cap = d->pend_cap ? d->pend_cap : 8192;
    while (cap < d->pend_len + extra) cap *= 2;
    float* nb = (float*)realloc(d->pend, (size_t)cap * OUT_CH * sizeof(float));
    if (!nb) return -1;
    d->pend = nb;
    d->pend_cap = cap;
    return 0;
}

static int64_t pts_to_frames(lfm_decoder* d, int64_t pts)
{
    return av_rescale_q_rnd(pts - d->origin_pts, d->tb, (AVRational){1, d->out_rate},
                            AV_ROUND_NEAR_INF | AV_ROUND_PASS_MINMAX);
}

/* Converts one decoded frame (or drains swr when f == NULL) into the pending buffer. */
static int convert(lfm_decoder* d, AVFrame* f)
{
    int in = f ? f->nb_samples : 0;
    int64_t room = swr_get_out_samples(d->swr, in) + 32;
    if (ensure_pending(d, room) < 0) return -1;
    uint8_t* outp = (uint8_t*)(d->pend + d->pend_len * OUT_CH);
    int n = swr_convert(d->swr, &outp, (int)room,
                        f ? (const uint8_t* const*)f->extended_data : NULL, in);
    if (n < 0) return -1;
    int64_t start = d->pend_len;
    d->pend_len += n;

    if (f && d->seek_target >= 0) {
        int64_t ts = f->best_effort_timestamp;
        if (ts != AV_NOPTS_VALUE) {
            int64_t pos = pts_to_frames(d, ts);
            int64_t drop = d->seek_target - pos;
            if (drop > 0) {
                /* the whole frame is before the target? keep waiting */
                if (drop >= n) { d->pend_len = start; return 0; }
                d->pend_off = start + drop;
                if (d->pend_off > d->pend_len) d->pend_off = d->pend_len;
            } else {
                d->pend_off = start;
            }
        }
        d->seek_target = -1;
    }
    return 0;
}

/* Decodes until at least one more chunk of output is pending. Returns 1 if progress, 0 on EOF. */
static int decode_more(lfm_decoder* d)
{
    for (;;) {
        int r = avcodec_receive_frame(d->codec, d->frame);
        if (r == 0) {
            if (!d->have_origin) {
                d->origin_pts = d->frame->best_effort_timestamp != AV_NOPTS_VALUE
                                ? d->frame->best_effort_timestamp : 0;
                d->have_origin = 1;
            }
            int c = convert(d, d->frame);
            av_frame_unref(d->frame);
            if (c < 0) return -1;
            if (d->pend_len > d->pend_off) return 1;
            continue;
        }
        if (r == AVERROR_EOF) {
            if (!d->swr_drained) {
                d->swr_drained = 1;
                if (convert(d, NULL) < 0) return -1;
                if (d->pend_len > d->pend_off) return 1;
            }
            return 0;
        }
        if (r != AVERROR(EAGAIN)) return -1;

        if (d->eof_sent) return 0;
        for (;;) {
            r = av_read_frame(d->fmt, d->pkt);
            if (r < 0) {
                avcodec_send_packet(d->codec, NULL);
                d->eof_sent = 1;
                break;
            }
            if (d->pkt->stream_index == d->stream) {
                int s = avcodec_send_packet(d->codec, d->pkt);
                av_packet_unref(d->pkt);
                /* corrupt packets are skipped, not fatal: a damaged frame must not end the track */
                (void)s;
                break;
            }
            av_packet_unref(d->pkt);
        }
    }
}

LFM_API int lfm_decoder_open(const char* path, int32_t output_rate, lfm_decoder** out)
{
    if (!out || !path) return -1;
    *out = NULL;
    lfm_decoder* d = (lfm_decoder*)calloc(1, sizeof *d);
    if (!d) return -2;
    d->out_rate = output_rate > 0 ? output_rate : 48000;
    d->seek_target = -1;
    d->duration_frames = -1;

    if (avformat_open_input(&d->fmt, path, NULL, NULL) < 0) goto fail;
    if (avformat_find_stream_info(d->fmt, NULL) < 0) goto fail;
    const AVCodec* codec = NULL;
    d->stream = av_find_best_stream(d->fmt, AVMEDIA_TYPE_AUDIO, -1, -1, &codec, 0);
    if (d->stream < 0 || !codec) goto fail;
    AVStream* st = d->fmt->streams[d->stream];
    d->tb = st->time_base;
    for (unsigned i = 0; i < d->fmt->nb_streams; i++)
        if ((int)i != d->stream) d->fmt->streams[i]->discard = AVDISCARD_ALL;

    d->codec = avcodec_alloc_context3(codec);
    if (!d->codec) goto fail;
    if (avcodec_parameters_to_context(d->codec, st->codecpar) < 0) goto fail;
    d->codec->pkt_timebase = st->time_base;
    if (avcodec_open2(d->codec, codec, NULL) < 0) goto fail;
    if (setup_swr(d) < 0) goto fail;

    d->pkt = av_packet_alloc();
    d->frame = av_frame_alloc();
    if (!d->pkt || !d->frame) goto fail;

    if (st->duration != AV_NOPTS_VALUE && st->duration > 0)
        d->duration_frames = av_rescale_q(st->duration, st->time_base, (AVRational){1, d->out_rate});
    else if (d->fmt->duration != AV_NOPTS_VALUE && d->fmt->duration > 0)
        d->duration_frames = av_rescale_q(d->fmt->duration, AV_TIME_BASE_Q, (AVRational){1, d->out_rate});

    /* prime: decode the first frame to establish the position origin */
    if (decode_more(d) < 0) goto fail;

    *out = d;
    return 0;
fail:
    lfm_decoder_close(d);
    return -3;
}

LFM_API void lfm_decoder_close(lfm_decoder* d)
{
    if (!d) return;
    av_frame_free(&d->frame);
    av_packet_free(&d->pkt);
    swr_free(&d->swr);
    avcodec_free_context(&d->codec);
    avformat_close_input(&d->fmt);
    free(d->pend);
    free(d);
}

LFM_API void lfm_decoder_get_info(lfm_decoder* d, lfm_decoder_info* info)
{
    info->duration_frames = d->duration_frames;
    info->source_rate = d->codec->sample_rate;
    info->source_channels = d->codec->ch_layout.nb_channels;
    info->output_rate = d->out_rate;
}

LFM_API int32_t lfm_decoder_read(lfm_decoder* d, float* out, int32_t count)
{
    int32_t done = 0;
    while (done < count) {
        int64_t avail = d->pend_len - d->pend_off;
        if (avail <= 0) {
            int r = decode_more(d);
            if (r < 0) return done > 0 ? done : -1;
            if (r == 0) break;
            continue;
        }
        int64_t n = avail < count - done ? avail : count - done;
        memcpy(out + (int64_t)done * OUT_CH, d->pend + d->pend_off * OUT_CH,
               (size_t)n * OUT_CH * sizeof(float));
        d->pend_off += n;
        done += (int32_t)n;
    }
    return done;
}

LFM_API int lfm_decoder_seek(lfm_decoder* d, int64_t frame)
{
    if (frame < 0) frame = 0;
    int64_t ts = d->origin_pts + av_rescale_q(frame, (AVRational){1, d->out_rate}, d->tb);
    if (avformat_seek_file(d->fmt, d->stream, INT64_MIN, ts, ts, 0) < 0 &&
        av_seek_frame(d->fmt, d->stream, ts, AVSEEK_FLAG_BACKWARD) < 0)
        return -1;
    avcodec_flush_buffers(d->codec);
    if (setup_swr(d) < 0) return -1;
    d->pend_len = d->pend_off = 0;
    d->eof_sent = 0;
    d->swr_drained = 0;
    d->seek_target = frame;
    return 0;
}

/* ================================================================ prober */

struct lfm_probe {
    AVFormatContext* fmt;
    AVPacket* pkt;
    int stream;
    int pic_stream;
    lfm_probe_info info;
    char** keys;
    char** values;
};

static void collect_tags(lfm_probe* p, const AVDictionary* dict, int* cap)
{
    const AVDictionaryEntry* e = NULL;
    while ((e = av_dict_iterate(dict, e))) {
        if (p->info.tag_count == *cap) {
            *cap = *cap ? *cap * 2 : 32;
            p->keys = (char**)realloc(p->keys, sizeof(char*) * (size_t)*cap);
            p->values = (char**)realloc(p->values, sizeof(char*) * (size_t)*cap);
        }
        p->keys[p->info.tag_count] = strdup(e->key);
        p->values[p->info.tag_count] = strdup(e->value);
        p->info.tag_count++;
    }
}

static void copy_name(char* dst, size_t n, const char* src)
{
    if (!src) src = "";
    strncpy(dst, src, n - 1);
    dst[n - 1] = 0;
}

LFM_API int lfm_probe_open(const char* path, lfm_probe** out)
{
    if (!out || !path) return -1;
    *out = NULL;
    lfm_probe* p = (lfm_probe*)calloc(1, sizeof *p);
    if (!p) return -2;
    p->pic_stream = -1;
    if (avformat_open_input(&p->fmt, path, NULL, NULL) < 0) goto fail;
    if (avformat_find_stream_info(p->fmt, NULL) < 0) goto fail;
    p->stream = av_find_best_stream(p->fmt, AVMEDIA_TYPE_AUDIO, -1, -1, NULL, 0);
    if (p->stream < 0) goto fail;
    p->pkt = av_packet_alloc();
    if (!p->pkt) goto fail;

    AVStream* st = p->fmt->streams[p->stream];
    AVCodecParameters* cp = st->codecpar;
    p->info.sample_rate = cp->sample_rate;
    p->info.channels = cp->ch_layout.nb_channels;
    p->info.bits_per_sample = cp->bits_per_raw_sample;
    p->info.bit_rate = cp->bit_rate > 0 ? cp->bit_rate : p->fmt->bit_rate;
    p->info.duration_ms = -1;
    if (st->duration != AV_NOPTS_VALUE && st->duration > 0)
        p->info.duration_ms = av_rescale_q(st->duration, st->time_base, (AVRational){1, 1000});
    else if (p->fmt->duration != AV_NOPTS_VALUE && p->fmt->duration > 0)
        p->info.duration_ms = p->fmt->duration / 1000;
    copy_name(p->info.codec, sizeof p->info.codec, avcodec_get_name(cp->codec_id));
    copy_name(p->info.container, sizeof p->info.container, p->fmt->iformat->name);

    int cap = 0;
    collect_tags(p, p->fmt->metadata, &cap);
    collect_tags(p, st->metadata, &cap);

    for (unsigned i = 0; i < p->fmt->nb_streams; i++) {
        AVStream* s = p->fmt->streams[i];
        if (!(s->disposition & AV_DISPOSITION_ATTACHED_PIC) || s->attached_pic.size <= 0) continue;
        const AVDictionaryEntry* c = av_dict_get(s->metadata, "comment", NULL, 0);
        int is_front = c && av_strcasecmp(c->value, "Cover (front)") == 0;
        if (p->pic_stream < 0 || is_front) p->pic_stream = (int)i;
        if (is_front) break;
    }
    if (p->pic_stream >= 0)
        p->info.picture_size = p->fmt->streams[p->pic_stream]->attached_pic.size;

    for (unsigned i = 0; i < p->fmt->nb_streams; i++)
        if ((int)i != p->stream) p->fmt->streams[i]->discard = AVDISCARD_ALL;

    *out = p;
    return 0;
fail:
    lfm_probe_close(p);
    return -3;
}

LFM_API void lfm_probe_close(lfm_probe* p)
{
    if (!p) return;
    for (int i = 0; i < p->info.tag_count; i++) { free(p->keys[i]); free(p->values[i]); }
    free(p->keys);
    free(p->values);
    av_packet_free(&p->pkt);
    avformat_close_input(&p->fmt);
    free(p);
}

LFM_API void lfm_probe_get_info(lfm_probe* p, lfm_probe_info* info) { *info = p->info; }

LFM_API int lfm_probe_get_tag(lfm_probe* p, int32_t index, const char** key, const char** value)
{
    if (index < 0 || index >= p->info.tag_count) return -1;
    *key = p->keys[index];
    *value = p->values[index];
    return 0;
}

LFM_API const uint8_t* lfm_probe_get_picture(lfm_probe* p, const char** mime)
{
    if (p->pic_stream < 0) { if (mime) *mime = NULL; return NULL; }
    AVStream* s = p->fmt->streams[p->pic_stream];
    if (mime) {
        switch (s->codecpar->codec_id) {
            case AV_CODEC_ID_PNG:  *mime = "image/png"; break;
            case AV_CODEC_ID_WEBP: *mime = "image/webp"; break;
            case AV_CODEC_ID_BMP:  *mime = "image/bmp"; break;
            case AV_CODEC_ID_GIF:  *mime = "image/gif"; break;
            default:               *mime = "image/jpeg"; break;
        }
    }
    return s->attached_pic.data;
}

LFM_API int32_t lfm_probe_next_packet(lfm_probe* p, const uint8_t** data)
{
    av_packet_unref(p->pkt);
    for (;;) {
        int r = av_read_frame(p->fmt, p->pkt);
        if (r == AVERROR_EOF) return 0;
        if (r < 0) return -1;
        if (p->pkt->stream_index == p->stream && p->pkt->size > 0) {
            *data = p->pkt->data;
            return p->pkt->size;
        }
        av_packet_unref(p->pkt);
    }
}

LFM_API const char* lfm_version(void) { return "localfm_native 0.1"; }
