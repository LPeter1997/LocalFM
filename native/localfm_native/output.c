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
#if defined(_WIN32)
#  include <windows.h>
#elif !defined(__APPLE__)
#  include <time.h>
#endif
#include <stdio.h>
#include <stdlib.h>
#include <string.h>

#define LFM_CHANNELS 2
/* Fade length for pause/resume and seek, in milliseconds. Long enough to avoid clicks,
 * short enough to feel instant. */
#define LFM_FADE_MS 8
#define LFM_EQ_XFADE_MS 20

#if defined(__SSE__) || defined(_M_X64) || defined(_M_IX86)
#  include <xmmintrin.h>
#  define LFM_HAVE_SSE 1
#endif

typedef struct { float b0, b1, b2, a1, a2; } lfm_biquad;
typedef struct {
    int32_t    count;   /* active biquads */
    float      pre;     /* linear preamp */
    lfm_biquad bq[LFM_EQ_MAX_BANDS];
} lfm_eq_coefs;
typedef float lfm_eq_state[LFM_EQ_MAX_BANDS][2][2]; /* band, channel, z1/z2 (transposed direct form II) */

struct lfm_output {
    /* device clock: what the last device callback took and when (seqlock: odd = being written) */
    _Atomic uint32_t clk_seq;
    _Atomic int64_t  clk_idx, clk_adv, clk_frames, clk_ns;
    int64_t          clk_prev;  /* callback-only */
    int64_t          fed_until_ns; /* callback-only: when the audio handed to the device so far runs out */
    int64_t          dev_buffer_ns; /* the most the device can hold ahead */

    /* diagnostics (see lfm_output_stats) */
    _Atomic int64_t late_callbacks, worst_gap_ns;
    _Atomic int64_t dev_stopped, dev_rerouted, dev_interruptions;

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

    /* equalizer: writers publish eq_pending under a seqlock; the consumer copies it */
    ma_mutex         eq_lock;     /* writers only, never taken by the audio thread */
    int32_t          has_eq_lock;
    lfm_eq_coefs     eq_pending;
    _Atomic uint32_t eq_seq;
    uint32_t         eq_seen;
    lfm_eq_coefs     eq_cur, eq_old;
    lfm_eq_state     eq_st, eq_st_old;
    int32_t          eq_xfade_left, eq_xfade_len;

    /* visualizer tap: the last LFM_TAP_FRAMES frames as heard (after EQ and pause/seek fades, before
     * volume, so the picture does not shrink with the volume knob). Written by the consumer only;
     * readers copy without locking and may see a torn window, which only costs one visual frame. */
    float            tap[LFM_TAP_FRAMES * LFM_CHANNELS];
    _Atomic int64_t  tap_pos;

    int32_t     has_device;
    ma_context  context;
    int32_t     has_context;
    ma_device   device;
};

static int64_t now_ns(void)
{
#if defined(_WIN32)
    static LARGE_INTEGER freq;
    LARGE_INTEGER t;
    if (freq.QuadPart == 0) QueryPerformanceFrequency(&freq);
    QueryPerformanceCounter(&t);
    return (int64_t)((double)t.QuadPart * 1e9 / (double)freq.QuadPart);
#elif defined(__APPLE__)
    return (int64_t)clock_gettime_nsec_np(CLOCK_UPTIME_RAW);
#else
    struct timespec ts;
    clock_gettime(CLOCK_MONOTONIC, &ts);
    return (int64_t)ts.tv_sec * 1000000000LL + ts.tv_nsec;
#endif
}

static void data_callback(ma_device* dev, void* out, const void* in, ma_uint32 frames)
{
    (void)in;
    lfm_output* o = (lfm_output*)dev->pUserData;
    lfm_output_render(o, (float*)out, (int32_t)frames);

    /* Everything handed over so far lasts until fed_until (never more than the device can hold ahead).
     * A callback well after that means the device, or the sound server behind it, ran dry in between: the
     * system did not run the audio thread in time. Bursts of callbacks that fill several periods are fine. */
    int64_t t = now_ns();
    if (o->fed_until_ns != 0 && t > o->fed_until_ns + 20000000LL) {
        int64_t gap = t - o->fed_until_ns;
        atomic_fetch_add_explicit(&o->late_callbacks, 1, memory_order_relaxed);
        if (gap > atomic_load_explicit(&o->worst_gap_ns, memory_order_relaxed))
            atomic_store_explicit(&o->worst_gap_ns, gap, memory_order_relaxed);
    }
    int64_t fed = (o->fed_until_ns > t ? o->fed_until_ns : t) + (int64_t)frames * 1000000000LL / o->rate;
    o->fed_until_ns = fed < t + o->dev_buffer_ns ? fed : t + o->dev_buffer_ns;

    /* Devices take audio a period at a time (up to 100 ms here); remembering when lets readers interpolate
     * the position in between. A flush makes the index jump, so the advance is capped at what was asked for. */
    int64_t idx = atomic_load_explicit(&o->read_idx, memory_order_relaxed);
    int64_t adv = idx - o->clk_prev;
    if (adv < 0) adv = 0;
    if (adv > (int64_t)frames) adv = frames;
    o->clk_prev = idx;
    uint32_t s = atomic_load_explicit(&o->clk_seq, memory_order_relaxed);
    atomic_store_explicit(&o->clk_seq, s + 1, memory_order_relaxed);
    atomic_thread_fence(memory_order_release);
    atomic_store_explicit(&o->clk_idx, idx, memory_order_relaxed);
    atomic_store_explicit(&o->clk_adv, adv, memory_order_relaxed);
    atomic_store_explicit(&o->clk_frames, (int64_t)frames, memory_order_relaxed);
    atomic_store_explicit(&o->clk_ns, t, memory_order_relaxed);
    atomic_store_explicit(&o->clk_seq, s + 2, memory_order_release);
}

/* Any thread miniaudio likes: only counts. */
static void notification_callback(const ma_device_notification* n)
{
    lfm_output* o = (lfm_output*)n->pDevice->pUserData;
    if (!o) return;
    switch (n->type) {
    case ma_device_notification_type_stopped:            atomic_fetch_add(&o->dev_stopped, 1); break;
    case ma_device_notification_type_rerouted:           atomic_fetch_add(&o->dev_rerouted, 1); break;
    case ma_device_notification_type_interruption_began: atomic_fetch_add(&o->dev_interruptions, 1); break;
    default: break;
    }
}

LFM_API void lfm_output_get_stats(lfm_output* o, lfm_output_stats* s)
{
    s->underruns = atomic_load(&o->underruns);
    s->late_callbacks = atomic_load(&o->late_callbacks);
    s->worst_gap_ns = atomic_exchange(&o->worst_gap_ns, 0);
    s->stopped = atomic_load(&o->dev_stopped);
    s->rerouted = atomic_load(&o->dev_rerouted);
    s->interruptions = atomic_load(&o->dev_interruptions);
}

LFM_API void lfm_output_describe(lfm_output* o, char* buf, int32_t size)
{
    if (!buf || size <= 0) return;
    if (!o->has_device) {
        snprintf(buf, (size_t)size, "offline, %d Hz", o->rate);
        return;
    }
    const ma_device* d = &o->device;
    snprintf(buf, (size_t)size, "%s, \"%s\", %d Hz (device %u Hz, %u ch), period %u x %u frames, ring %lld ms",
             ma_get_backend_name(o->context.backend), d->playback.name, o->rate,
             d->playback.internalSampleRate, d->playback.internalChannels,
             d->playback.internalPeriodSizeInFrames, d->playback.internalPeriods,
             (long long)(o->capacity * 1000 / o->rate));
}

LFM_API int64_t lfm_output_read_clock(lfm_output* o, int64_t* advance, int64_t* period, int64_t* age_ns)
{
    *advance = 0;
    *period = 0;
    *age_ns = 0;
    if (!o->has_device) return atomic_load_explicit(&o->read_idx, memory_order_acquire);
    for (;;) {
        uint32_t s1 = atomic_load_explicit(&o->clk_seq, memory_order_acquire);
        if (s1 & 1) continue;
        int64_t idx = atomic_load_explicit(&o->clk_idx, memory_order_relaxed);
        int64_t adv = atomic_load_explicit(&o->clk_adv, memory_order_relaxed);
        int64_t per = atomic_load_explicit(&o->clk_frames, memory_order_relaxed);
        int64_t ns  = atomic_load_explicit(&o->clk_ns, memory_order_relaxed);
        atomic_thread_fence(memory_order_acquire);
        if (atomic_load_explicit(&o->clk_seq, memory_order_relaxed) != s1) continue;
        if (ns == 0) return atomic_load_explicit(&o->read_idx, memory_order_acquire); /* no callback yet */
        *advance = adv;
        *period = per;
        *age_ns = now_ns() - ns;
        return idx;
    }
}

static void init_consumer_state(lfm_output* o)
{
    o->env = 1.0f;
    o->starving = 1; /* an empty ring before any audio is not an underrun */
    o->env_step = 1.0f / (float)((o->rate * LFM_FADE_MS) / 1000);
    o->vol = atomic_load(&o->volume);
    /* one-pole smoothing with ~10 ms time constant */
    o->vol_coef = 1.0f - expf(-1.0f / (0.010f * (float)o->rate));
    o->eq_cur.pre = o->eq_old.pre = o->eq_pending.pre = 1.0f;
    o->eq_xfade_len = (o->rate * LFM_EQ_XFADE_MS) / 1000;
}

static void eq_run(const lfm_eq_coefs* c, lfm_eq_state st, float* l, float* r)
{
    float x0 = *l * c->pre, x1 = *r * c->pre;
    for (int32_t b = 0; b < c->count; b++) {
        const lfm_biquad* q = &c->bq[b];
        float y0 = q->b0 * x0 + st[b][0][0];
        st[b][0][0] = q->b1 * x0 - q->a1 * y0 + st[b][0][1];
        st[b][0][1] = q->b2 * x0 - q->a2 * y0;
        float y1 = q->b0 * x1 + st[b][1][0];
        st[b][1][0] = q->b1 * x1 - q->a1 * y1 + st[b][1][1];
        st[b][1][1] = q->b2 * x1 - q->a2 * y1;
        x0 = y0; x1 = y1;
    }
    *l = x0; *r = x1;
}

/* Picks up newly published EQ settings (consumer). The old chain keeps running for a short cross-fade. */
static void eq_poll(lfm_output* o)
{
    uint32_t s1 = atomic_load_explicit(&o->eq_seq, memory_order_acquire);
    if (s1 == o->eq_seen || (s1 & 1u)) return;
    lfm_eq_coefs fresh;
    memcpy(&fresh, &o->eq_pending, sizeof fresh);
    atomic_thread_fence(memory_order_acquire);
    if (atomic_load_explicit(&o->eq_seq, memory_order_relaxed) != s1) return; /* torn: try next callback */
    o->eq_seen = s1;
    o->eq_old = o->eq_cur;
    memcpy(o->eq_st_old, o->eq_st, sizeof o->eq_st);
    /* Same band layout (a slider moved): keep filter memory for continuity. Otherwise start fresh;
     * the cross-fade hides the start-up transient. */
    if (fresh.count != o->eq_cur.count) memset(o->eq_st, 0, sizeof o->eq_st);
    o->eq_cur = fresh;
    o->eq_xfade_left = o->eq_xfade_len;
}

static lfm_biquad design(int32_t type, double fs, double f, double q, double gain_db)
{
    const double pi = 3.14159265358979323846;
    if (f > fs * 0.45) f = fs * 0.45;
    if (f < 10.0) f = 10.0;
    if (q <= 0.05) q = 0.05;
    double A = pow(10.0, gain_db / 40.0), w0 = 2.0 * pi * f / fs, cw = cos(w0), alpha = sin(w0) / (2.0 * q);
    double b0, b1, b2, a0, a1, a2, sA = sqrt(A);
    switch (type) {
    case LFM_EQ_LOWSHELF:
        b0 = A * ((A + 1) - (A - 1) * cw + 2 * sA * alpha);
        b1 = 2 * A * ((A - 1) - (A + 1) * cw);
        b2 = A * ((A + 1) - (A - 1) * cw - 2 * sA * alpha);
        a0 = (A + 1) + (A - 1) * cw + 2 * sA * alpha;
        a1 = -2 * ((A - 1) + (A + 1) * cw);
        a2 = (A + 1) + (A - 1) * cw - 2 * sA * alpha;
        break;
    case LFM_EQ_HIGHSHELF:
        b0 = A * ((A + 1) + (A - 1) * cw + 2 * sA * alpha);
        b1 = -2 * A * ((A - 1) + (A + 1) * cw);
        b2 = A * ((A + 1) + (A - 1) * cw - 2 * sA * alpha);
        a0 = (A + 1) - (A - 1) * cw + 2 * sA * alpha;
        a1 = 2 * ((A - 1) - (A + 1) * cw);
        a2 = (A + 1) - (A - 1) * cw - 2 * sA * alpha;
        break;
    default: /* peaking */
        b0 = 1 + alpha * A; b1 = -2 * cw; b2 = 1 - alpha * A;
        a0 = 1 + alpha / A; a1 = -2 * cw; a2 = 1 - alpha / A;
        break;
    }
    lfm_biquad r = { (float)(b0 / a0), (float)(b1 / a0), (float)(b2 / a0), (float)(a1 / a0), (float)(a2 / a0) };
    return r;
}

LFM_API void lfm_output_set_eq(lfm_output* o, const lfm_eq_band* bands, int32_t count, float preamp_db)
{
    if (!o) return;
    lfm_eq_coefs c;
    memset(&c, 0, sizeof c);
    c.pre = powf(10.0f, preamp_db / 20.0f);
    for (int32_t i = 0; i < count && c.count < LFM_EQ_MAX_BANDS; i++) {
        if (fabsf(bands[i].gain_db) < 0.01f) continue; /* a flat band is an identity filter: skip it */
        c.bq[c.count++] = design(bands[i].type, (double)o->rate, bands[i].freq, bands[i].q, bands[i].gain_db);
    }
    ma_mutex_lock(&o->eq_lock);
    uint32_t s = atomic_load_explicit(&o->eq_seq, memory_order_relaxed);
    atomic_store_explicit(&o->eq_seq, s + 1, memory_order_relaxed);  /* odd: writing */
    atomic_thread_fence(memory_order_release);
    o->eq_pending = c;
    atomic_store_explicit(&o->eq_seq, s + 2, memory_order_release); /* even: published */
    ma_mutex_unlock(&o->eq_lock);
}

LFM_API int lfm_output_open(int32_t mode, int32_t rate, int32_t ring_ms, lfm_output** out)
{
    if (!out) return -1;
    *out = NULL;
    lfm_output* o = (lfm_output*)calloc(1, sizeof *o);
    if (!o) return -2;
    atomic_store(&o->volume, 1.0f);
    atomic_store(&o->flush_to, -1);
    if (ma_mutex_init(&o->eq_lock) != MA_SUCCESS) { free(o); return -2; }
    o->has_eq_lock = 1;

    if (mode == LFM_OUTPUT_DEVICE || mode == LFM_OUTPUT_NULL) {
        if (mode == LFM_OUTPUT_NULL) {
            ma_backend backends[] = { ma_backend_null };
            if (ma_context_init(backends, 1, NULL, &o->context) != MA_SUCCESS) { lfm_output_close(o); return -3; }
        } else {
            if (ma_context_init(NULL, 0, NULL, &o->context) != MA_SUCCESS) { lfm_output_close(o); return -3; }
        }
        o->has_context = 1;

        ma_device_config cfg = ma_device_config_init(ma_device_type_playback);
        cfg.playback.format   = ma_format_f32;
        cfg.playback.channels = LFM_CHANNELS;
        cfg.sampleRate        = (ma_uint32)(rate > 0 ? rate : 0);
        cfg.dataCallback      = data_callback;
        cfg.notificationCallback = notification_callback;
        cfg.pUserData         = o;
        cfg.performanceProfile = ma_performance_profile_conservative;
        if (ma_device_init(&o->context, &cfg, &o->device) != MA_SUCCESS) {
            lfm_output_close(o);
            return -4;
        }
        o->has_device = 1;
        o->rate = (int32_t)o->device.sampleRate;
        ma_uint32 per = o->device.playback.internalPeriodSizeInFrames, n = o->device.playback.internalPeriods,
                  irate = o->device.playback.internalSampleRate;
        o->dev_buffer_ns = per && irate ? (int64_t)per * (n ? n : 1) * 1000000000LL / irate : 200000000LL;
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
    if (o->has_eq_lock) ma_mutex_uninit(&o->eq_lock);
    free(o->buf);
    free(o);
}

LFM_API int64_t lfm_output_latency_frames(lfm_output* o)
{
    if (!o->has_device) return 0;
    ma_uint32 period = o->device.playback.internalPeriodSizeInFrames;
    ma_uint32 periods = o->device.playback.internalPeriods;
    ma_uint32 rate = o->device.playback.internalSampleRate;
    if (period == 0 || rate == 0) return 0;
    /* in the device's rate; convert to ours */
    return (int64_t)period * (periods ? periods : 1) * o->rate / rate;
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

LFM_API int64_t lfm_output_tap(lfm_output* o, float* out, int32_t frames)
{
    if (!o || frames <= 0) return 0;
    if (frames > LFM_TAP_FRAMES) frames = LFM_TAP_FRAMES;
    int64_t end = atomic_load_explicit(&o->tap_pos, memory_order_acquire);
    int64_t start = end - frames;
    for (int32_t i = 0; i < frames; i++) {
        int64_t k = start + i;
        if (k < 0) { out[i * 2] = out[i * 2 + 1] = 0.0f; continue; }
        const float* t = o->tap + (k & (LFM_TAP_FRAMES - 1)) * LFM_CHANNELS;
        out[i * 2] = t[0];
        out[i * 2 + 1] = t[1];
    }
    return end;
}
LFM_API void lfm_output_set_volume(lfm_output* o, float linear) { atomic_store(&o->volume, linear); }
LFM_API void lfm_output_set_paused(lfm_output* o, int32_t paused) { atomic_store(&o->paused, paused); }

LFM_API void lfm_output_render(lfm_output* o, float* out, int32_t frames)
{
    if (!o) { memset(out, 0, (size_t)frames * LFM_CHANNELS * sizeof(float)); return; }
#ifdef LFM_HAVE_SSE
    /* IIR filter tails decay into denormals, which are very slow on x86: flush them to zero. */
    _mm_setcsr(_mm_getcsr() | 0x8040);
#endif
    eq_poll(o);

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
    int64_t tp = atomic_load_explicit(&o->tap_pos, memory_order_relaxed);
    for (int32_t i = 0; i < frames; i++, tp++) {
        float* t = o->tap + (tp & (LFM_TAP_FRAMES - 1)) * LFM_CHANNELS;
        float target = (paused || o->skipping) ? 0.0f : 1.0f;
        if (o->env < target) { o->env += o->env_step; if (o->env > 1.0f) o->env = 1.0f; }
        else if (o->env > target) { o->env -= o->env_step; if (o->env < 0.0f) o->env = 0.0f; }

        if (o->env <= 0.0f && o->skipping) {
            if (o->skip_to > r) r = o->skip_to;
            o->skipping = 0;
        }
        if (o->env <= 0.0f && (paused || o->skipping)) {
            out[i * 2] = out[i * 2 + 1] = 0.0f;
            t[0] = t[1] = 0.0f;
            continue; /* hold position */
        }

        o->vol += (vol_target - o->vol) * o->vol_coef;
        if (r < w) {
            const float* s = o->buf + (r % o->capacity) * LFM_CHANNELS;
            float g = o->env * o->vol;
            float l = s[0], rr = s[1];
            if (o->eq_cur.count > 0 || o->eq_cur.pre != 1.0f || o->eq_xfade_left > 0) {
                float nl = l, nr = rr;
                eq_run(&o->eq_cur, o->eq_st, &nl, &nr);
                if (o->eq_xfade_left > 0) {
                    float ol = l, orr = rr;
                    eq_run(&o->eq_old, o->eq_st_old, &ol, &orr);
                    float t = (float)o->eq_xfade_left / (float)o->eq_xfade_len;
                    nl = nl + (ol - nl) * t;
                    nr = nr + (orr - nr) * t;
                    o->eq_xfade_left--;
                }
                l = nl; rr = nr;
            }
            out[i * 2] = l * g;
            out[i * 2 + 1] = rr * g;
            t[0] = l * o->env;
            t[1] = rr * o->env;
            r++;
            o->starving = 0;
        } else {
            out[i * 2] = out[i * 2 + 1] = 0.0f;
            t[0] = t[1] = 0.0f;
            if (!o->starving) { o->starving = 1; starved_now = 1; }
        }
    }
    atomic_store_explicit(&o->tap_pos, tp, memory_order_release);
    atomic_store_explicit(&o->read_idx, r, memory_order_release);
    if (starved_now) atomic_fetch_add(&o->underruns, 1);
}
