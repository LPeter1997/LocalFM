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

/* What the device has been through, for diagnostics (all counters are totals since open). */
typedef struct lfm_output_stats {
    int64_t underruns;          /* the device found the ring empty while playing */
    int64_t late_callbacks;     /* the device asked for audio well after everything we gave it had played out:
                                 * the system did not run the audio thread in time (CPU load, suspend), a gap */
    int64_t worst_gap_ns;       /* the longest such gap since the previous get_stats */
    int64_t stopped;            /* the device stopped (also our own close) */
    int64_t rerouted;           /* the system moved the stream to another device */
    int64_t interruptions;      /* the system took the device away (calls, other apps on mobile) */
} lfm_output_stats;
LFM_API void lfm_output_get_stats(lfm_output* o, lfm_output_stats* stats);

/* One line about the device in use (backend, name, rates, period), for the session log. */
LFM_API void lfm_output_describe(lfm_output* o, char* buf, int32_t size);
/* The read index as of the last device callback, how many frames that callback took from the ring
 * (`advance`), how many it asked for (`period`, also when the ring was empty) and how long ago it ran
 * (`age_ns`). The taken frames play out over about one period, so idx - advance + min(advance, age * rate)
 * is a smooth position between callbacks, and the next callback is due in about period/rate - age.
 * Offline: read index and zeros. */
LFM_API int64_t lfm_output_read_clock(lfm_output* o, int64_t* advance, int64_t* period, int64_t* age_ns);

/* Frames between the ring's read position and the speaker (device buffer), 0 when unknown/offline.
 * Callers subtract it to get the position actually heard. */
LFM_API int64_t lfm_output_latency_frames(lfm_output* o);

LFM_API void lfm_output_set_volume(lfm_output* o, float linear);
LFM_API void lfm_output_set_paused(lfm_output* o, int32_t paused);

/* Equalizer, applied on the audio thread right before output, so changes are heard at once.
 * Setting it never blocks the audio thread: new coefficients are picked up at the next callback
 * and cross-faded from the old ones over ~20 ms (no zipper noise while dragging a slider). */
enum { LFM_EQ_PEAK = 0, LFM_EQ_LOWSHELF = 1, LFM_EQ_HIGHSHELF = 2 };
typedef struct lfm_eq_band {
    int32_t type;
    float   freq;    /* Hz */
    float   q;       /* bandwidth; ~1.41 for octave bands, ~0.71 for shelves */
    float   gain_db;
} lfm_eq_band;
#define LFM_EQ_MAX_BANDS 16
/* count 0 (or all gains 0 and preamp 0) = flat, which costs nothing. Thread-safe against itself. */
LFM_API void lfm_output_set_eq(lfm_output* o, const lfm_eq_band* bands, int32_t count, float preamp_db);

/* Visualizer tap: copies the most recent `frames` frames (<= LFM_TAP_FRAMES, interleaved stereo) as heard,
 * before volume. Lock-free and safe from any thread; returns the total frames rendered so far, so a caller
 * can tell whether anything new arrived. */
#define LFM_TAP_FRAMES 4096
LFM_API int64_t lfm_output_tap(lfm_output* o, float* out, int32_t frames);

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
/* Opens from caller-supplied IO instead of a path (e.g. a file arriving over the network).
 * read: bytes read into buf, 0 = end of stream, <0 = error. May block.
 * seek: whence 0/1/2 = SEEK_SET/CUR/END (returns the new position), LFM_SEEK_SIZE = return the total
 *       size (or <0 if unknown). <0 = error.
 * The callbacks are called from whichever thread opens, reads or seeks the decoder, and `user` must stay
 * valid until lfm_decoder_close. */
#define LFM_SEEK_SIZE 0x10000
typedef int32_t (*lfm_io_read)(void* user, uint8_t* buf, int32_t size);
typedef int64_t (*lfm_io_seek)(void* user, int64_t offset, int32_t whence);
LFM_API int lfm_decoder_open_io(lfm_io_read read, lfm_io_seek seek, void* user, int32_t output_rate, lfm_decoder** out);
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
