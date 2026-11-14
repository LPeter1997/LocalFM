/*
 * localfm_native — thin C layer under LocalFM.
 *
 * Two independent parts:
 *   1. Output: a lock-free SPSC ring buffer of interleaved float32 frames that a miniaudio
 *      device callback (or an offline caller, for tests) drains. Pause, volume and seek
 *      fades are applied here, on the audio thread, so they never depend on managed code
 *      being scheduled (GC pauses only ever eat into the ring's headroom).
 *   2. FFmpeg wrappers: a decoder that produces interleaved float32 at a requested rate,
 *      and a prober that exposes tags, stream info, embedded art and raw audio packets
 *      (for content hashing). Wrapping these in C keeps FFmpeg struct layouts out of C#.
 *
 * All strings are UTF-8. All functions return 0 on success and a negative value on error
 * unless documented otherwise.
 */
#ifndef LOCALFM_NATIVE_H
#define LOCALFM_NATIVE_H

#include <stdint.h>

#if defined(_WIN32)
#  define LFM_API __declspec(dllexport)
#else
#  define LFM_API __attribute__((visibility("default")))
#endif

#ifdef __cplusplus
extern "C" {
#endif

/* ---------------------------------------------------------------- output */

typedef struct lfm_output lfm_output;

enum {
    LFM_OUTPUT_DEVICE  = 0, /* default system playback device */
    LFM_OUTPUT_NULL    = 1, /* miniaudio null backend (real-time pacing, discards audio) */
    LFM_OUTPUT_OFFLINE = 2  /* no device; caller pulls with lfm_output_render */
};

typedef struct lfm_output_info {
    int32_t sample_rate;
    int32_t channels;
    int64_t ring_frames;
} lfm_output_info;

/* rate 0 = device native rate (falls back to 48000 for offline). Channels are always 2. */
LFM_API int  lfm_output_open(int32_t mode, int32_t rate, int32_t ring_ms, lfm_output** out);
LFM_API void lfm_output_close(lfm_output* o);
LFM_API void lfm_output_get_info(lfm_output* o, lfm_output_info* info);

/* Producer side (one thread). Non-blocking; returns frames accepted. */
LFM_API int32_t lfm_output_write(lfm_output* o, const float* frames, int32_t count);
LFM_API int64_t lfm_output_writable(lfm_output* o);
/* Total frames ever written (monotonic "write index"). */
LFM_API int64_t lfm_output_write_index(lfm_output* o);
/* Drop everything queued so far; the consumer fades out briefly, skips, then fades in. */
LFM_API void lfm_output_flush(lfm_output* o);

/* Discards queued-but-unplayed frames from `write_index` onward (producer only), so a different
 * continuation can be written. Fails (returns 0) when that point is too close to — or already past —
 * the playhead; returns 1 on success. Used to undo a gapless splice when the user changes "next". */
LFM_API int32_t lfm_output_truncate(lfm_output* o, int64_t write_index);

/* Consumer position in write-index space (includes flushed frames). */
LFM_API int64_t lfm_output_read_index(lfm_output* o);
LFM_API int64_t lfm_output_underruns(lfm_output* o);

LFM_API void lfm_output_set_volume(lfm_output* o, float linear);
LFM_API void lfm_output_set_paused(lfm_output* o, int32_t paused);

/* Consumer side: what the device callback calls. Exposed for offline rendering/tests. */
LFM_API void lfm_output_render(lfm_output* o, float* out, int32_t frames);

/* ---------------------------------------------------------------- decoder */

typedef struct lfm_decoder lfm_decoder;

typedef struct lfm_decoder_info {
    int64_t duration_frames; /* at output rate; -1 if unknown */
    int32_t source_rate;
    int32_t source_channels;
    int32_t output_rate;
} lfm_decoder_info;

LFM_API int  lfm_decoder_open(const char* path, int32_t output_rate, lfm_decoder** out);
LFM_API void lfm_decoder_close(lfm_decoder* d);
LFM_API void lfm_decoder_get_info(lfm_decoder* d, lfm_decoder_info* info);
/* Reads up to `count` stereo frames. Returns frames read; 0 = end of stream; <0 = error. */
LFM_API int32_t lfm_decoder_read(lfm_decoder* d, float* out, int32_t count);
/* Sample-accurate seek, position in output-rate frames. */
LFM_API int lfm_decoder_seek(lfm_decoder* d, int64_t frame);

/* ---------------------------------------------------------------- prober */

typedef struct lfm_probe lfm_probe;

typedef struct lfm_probe_info {
    int64_t duration_ms;   /* -1 if unknown */
    int64_t bit_rate;      /* bits/s, 0 if unknown */
    int32_t sample_rate;
    int32_t channels;
    int32_t bits_per_sample; /* 0 if not meaningful (lossy) */
    int32_t tag_count;
    int32_t picture_size;  /* bytes of embedded front cover, 0 if none */
    char    codec[32];
    char    container[32];
} lfm_probe_info;

LFM_API int  lfm_probe_open(const char* path, lfm_probe** out);
LFM_API void lfm_probe_close(lfm_probe* p);
LFM_API void lfm_probe_get_info(lfm_probe* p, lfm_probe_info* info);
/* Pointers stay valid until lfm_probe_close. Keys are as FFmpeg reports them. */
LFM_API int  lfm_probe_get_tag(lfm_probe* p, int32_t index, const char** key, const char** value);
LFM_API const uint8_t* lfm_probe_get_picture(lfm_probe* p, const char** mime);
/* Iterates audio packets of the selected stream. Returns size (>0), 0 at end, <0 on error.
 * The data pointer stays valid until the next call. */
LFM_API int32_t lfm_probe_next_packet(lfm_probe* p, const uint8_t** data);

LFM_API const char* lfm_version(void);

#ifdef __cplusplus
}
#endif
#endif
