/* Output ring buffer + miniaudio device. See localfm_native.h. */
#define MINIAUDIO_IMPLEMENTATION
#define MA_NO_DECODING
#define MA_NO_ENCODING
#define MA_NO_GENERATION
#define MA_NO_RESOURCE_MANAGER
#define MA_NO_NODE_GRAPH
#define MA_NO_ENGINE
#include "../third_party/miniaudio/miniaudio.h"

#include "localfm_native.h"

#include <math.h>
#include <stdatomic.h>
#include <stdlib.h>
#include <string.h>

#define LFM_CHANNELS 2
/* Fade length for pause/resume and seek, in milliseconds. Long enough to avoid clicks,
 * short enough to feel instant. */
#define LFM_FADE_MS 8

struct lfm_output {
    float*  buf;            /* capacity * LFM_CHANNELS */
    int64_t capacity;       /* frames */
    int32_t rate;

    _Atomic int64_t write_idx;
    _Atomic int64_t read_idx;
    _Atomic int64_t flush_to;   /* consumer skips to here (write-index space) */
    _Atomic int64_t underruns;
    _Atomic float   volume;
    _Atomic int32_t paused;

    /* consumer-only state */
    float   env;            /* pause/seek envelope 0..1 */
    float   env_step;
    float   vol;            /* smoothed volume */
    float   vol_coef;
    int32_t skipping;
    int64_t skip_to;
    int32_t starving;

    int32_t     has_device;
    ma_context  context;
    int32_t     has_context;
    ma_device   device;
};

static void data_callback(ma_device* dev, void* out, const void* in, ma_uint32 frames)
{
    (void)in;
    lfm_output_render((lfm_output*)dev->pUserData, (float*)out, (int32_t)frames);
}

static void init_consumer_state(lfm_output* o)
{
    o->env = 1.0f;
    o->starving = 1; /* an empty ring before any audio is not an underrun */
    o->env_step = 1.0f / (float)((o->rate * LFM_FADE_MS) / 1000);
    o->vol = atomic_load(&o->volume);
    /* one-pole smoothing with ~10 ms time constant */
    o->vol_coef = 1.0f - expf(-1.0f / (0.010f * (float)o->rate));
}

LFM_API int lfm_output_open(int32_t mode, int32_t rate, int32_t ring_ms, lfm_output** out)
{
    if (!out) return -1;
    *out = NULL;
    lfm_output* o = (lfm_output*)calloc(1, sizeof *o);
    if (!o) return -2;
    atomic_store(&o->volume, 1.0f);
    atomic_store(&o->flush_to, -1);

    if (mode == LFM_OUTPUT_DEVICE || mode == LFM_OUTPUT_NULL) {
        if (mode == LFM_OUTPUT_NULL) {
            ma_backend backends[] = { ma_backend_null };
            if (ma_context_init(backends, 1, NULL, &o->context) != MA_SUCCESS) { free(o); return -3; }
        } else {
            if (ma_context_init(NULL, 0, NULL, &o->context) != MA_SUCCESS) { free(o); return -3; }
        }
        o->has_context = 1;

        ma_device_config cfg = ma_device_config_init(ma_device_type_playback);
        cfg.playback.format   = ma_format_f32;
        cfg.playback.channels = LFM_CHANNELS;
        cfg.sampleRate        = (ma_uint32)(rate > 0 ? rate : 0);
        cfg.dataCallback      = data_callback;
        cfg.pUserData         = o;
        cfg.performanceProfile = ma_performance_profile_conservative;
        if (ma_device_init(&o->context, &cfg, &o->device) != MA_SUCCESS) {
            ma_context_uninit(&o->context);
            free(o);
            return -4;
        }
        o->has_device = 1;
        o->rate = (int32_t)o->device.sampleRate;
    } else {
        o->rate = rate > 0 ? rate : 48000;
    }

    if (ring_ms <= 0) ring_ms = 1000;
    o->capacity = (int64_t)o->rate * ring_ms / 1000;
    o->buf = (float*)calloc((size_t)(o->capacity * LFM_CHANNELS), sizeof(float));
    if (!o->buf) { lfm_output_close(o); return -2; }
    init_consumer_state(o);

    if (o->has_device && ma_device_start(&o->device) != MA_SUCCESS) {
        lfm_output_close(o);
        return -5;
    }
    *out = o;
    return 0;
}

LFM_API void lfm_output_close(lfm_output* o)
{
    if (!o) return;
    if (o->has_device) ma_device_uninit(&o->device);
    if (o->has_context) ma_context_uninit(&o->context);
    free(o->buf);
    free(o);
}

LFM_API void lfm_output_get_info(lfm_output* o, lfm_output_info* info)
{
    info->sample_rate = o->rate;
    info->channels = LFM_CHANNELS;
    info->ring_frames = o->capacity;
}

LFM_API int64_t lfm_output_writable(lfm_output* o)
{
    int64_t w = atomic_load_explicit(&o->write_idx, memory_order_relaxed);
    int64_t r = atomic_load_explicit(&o->read_idx, memory_order_acquire);
    return o->capacity - (w - r);
}

LFM_API int32_t lfm_output_write(lfm_output* o, const float* frames, int32_t count)
{
    int64_t w = atomic_load_explicit(&o->write_idx, memory_order_relaxed);
    int64_t r = atomic_load_explicit(&o->read_idx, memory_order_acquire);
    int64_t space = o->capacity - (w - r);
    if (count > space) count = (int32_t)space;
    for (int32_t done = 0; done < count;) {
        int64_t pos = (w + done) % o->capacity;
        int64_t run = o->capacity - pos;
        if (run > count - done) run = count - done;
        memcpy(o->buf + pos * LFM_CHANNELS, frames + (int64_t)done * LFM_CHANNELS,
               (size_t)run * LFM_CHANNELS * sizeof(float));
        done += (int32_t)run;
    }
    atomic_store_explicit(&o->write_idx, w + count, memory_order_release);
    return count;
}

LFM_API int64_t lfm_output_write_index(lfm_output* o)
{
    return atomic_load_explicit(&o->write_idx, memory_order_relaxed);
}

LFM_API void lfm_output_flush(lfm_output* o)
{
    atomic_store_explicit(&o->flush_to, atomic_load_explicit(&o->write_idx, memory_order_relaxed),
                          memory_order_release);
}

LFM_API int32_t lfm_output_truncate(lfm_output* o, int64_t t)
{
    int64_t w = atomic_load_explicit(&o->write_idx, memory_order_relaxed);
    if (t >= w) return 1;
    int64_t r = atomic_load_explicit(&o->read_idx, memory_order_acquire);
    int64_t fl = atomic_load_explicit(&o->flush_to, memory_order_acquire);
    /* the consumer may be mid-callback with an older write-index snapshot; stay well clear of it */
    int64_t margin = o->rate / 4;
    if (t < r + margin) return 0;
    if (fl > r && t < fl) return 0;
    atomic_store_explicit(&o->write_idx, t, memory_order_release);
    return 1;
}

LFM_API int64_t lfm_output_read_index(lfm_output* o)
{
    return atomic_load_explicit(&o->read_idx, memory_order_acquire);
}

LFM_API int64_t lfm_output_underruns(lfm_output* o) { return atomic_load(&o->underruns); }
LFM_API void lfm_output_set_volume(lfm_output* o, float linear) { atomic_store(&o->volume, linear); }
LFM_API void lfm_output_set_paused(lfm_output* o, int32_t paused) { atomic_store(&o->paused, paused); }

LFM_API void lfm_output_render(lfm_output* o, float* out, int32_t frames)
{
    if (!o) { memset(out, 0, (size_t)frames * LFM_CHANNELS * sizeof(float)); return; }

    int64_t r = atomic_load_explicit(&o->read_idx, memory_order_relaxed);
    int64_t w = atomic_load_explicit(&o->write_idx, memory_order_acquire);
    int64_t fl = atomic_load_explicit(&o->flush_to, memory_order_acquire);
    int32_t paused = atomic_load_explicit(&o->paused, memory_order_relaxed);
    float vol_target = atomic_load_explicit(&o->volume, memory_order_relaxed);

    if (fl > r && (!o->skipping || fl > o->skip_to)) {
        o->skipping = 1;
        o->skip_to = fl;
    }

    int32_t starved_now = 0;
    for (int32_t i = 0; i < frames; i++) {
        float target = (paused || o->skipping) ? 0.0f : 1.0f;
        if (o->env < target) { o->env += o->env_step; if (o->env > 1.0f) o->env = 1.0f; }
        else if (o->env > target) { o->env -= o->env_step; if (o->env < 0.0f) o->env = 0.0f; }

        if (o->env <= 0.0f && o->skipping) {
            if (o->skip_to > r) r = o->skip_to;
            o->skipping = 0;
        }
        if (o->env <= 0.0f && (paused || o->skipping)) {
            out[i * 2] = out[i * 2 + 1] = 0.0f;
            continue; /* hold position */
        }

        o->vol += (vol_target - o->vol) * o->vol_coef;
        if (r < w) {
            const float* s = o->buf + (r % o->capacity) * LFM_CHANNELS;
            float g = o->env * o->vol;
            out[i * 2] = s[0] * g;
            out[i * 2 + 1] = s[1] * g;
            r++;
            o->starving = 0;
        } else {
            out[i * 2] = out[i * 2 + 1] = 0.0f;
            if (!o->starving) { o->starving = 1; starved_now = 1; }
        }
    }
    atomic_store_explicit(&o->read_idx, r, memory_order_release);
    if (starved_now) atomic_fetch_add(&o->underruns, 1);
}
