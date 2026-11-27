// SPDX-License-Identifier: GPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 KC2RGW Chris
// SimpleScanner's audio output: one playback device fed from a ring buffer. miniaudio does the
// device work (Core Audio, WASAPI, PulseAudio/ALSA) and converts our rate to the device's.
#define MINIAUDIO_IMPLEMENTATION
#define MA_NO_DECODING
#define MA_NO_ENCODING
#define MA_NO_GENERATION
#define MA_NO_RESOURCE_MANAGER
#define MA_NO_NODE_GRAPH
#define MA_NO_ENGINE
#include "miniaudio.h"
#include <stdlib.h>
#include <string.h>

#if defined(_WIN32)
#define SS_API __declspec(dllexport)
#else
#define SS_API __attribute__((visibility("default")))
#endif

typedef struct {
    ma_context context;
    ma_device_id device_id;
    ma_device device;
    ma_pcm_rb rb;
    ma_uint32 channels;
    volatile ma_uint32 underruns;
} ss_audio;

static void on_data(ma_device *dev, void *out, const void *in, ma_uint32 frames)
{
    ss_audio *a = (ss_audio *)dev->pUserData;
    float *dst = (float *)out;
    ma_uint32 done = 0;
    (void)in;
    while (done < frames) {
        ma_uint32 n = frames - done;
        void *src;
        if (ma_pcm_rb_acquire_read(&a->rb, &n, &src) != MA_SUCCESS || n == 0) break;
        memcpy(dst + done * a->channels, src, n * a->channels * sizeof(float));
        ma_pcm_rb_commit_read(&a->rb, n);
        done += n;
    }
    if (done < frames) {
        memset(dst + done * a->channels, 0, (frames - done) * a->channels * sizeof(float));
        a->underruns++;
    }
}

// The names of the playback devices, one per line, into buf. Returns how many there are.
SS_API int ss_audio_devices(char *buf, int len)
{
    ma_context ctx;
    ma_device_info *infos;
    ma_uint32 count = 0, i;
    int used = 0;
    if (len > 0) buf[0] = 0;
    if (ma_context_init(NULL, 0, NULL, &ctx) != MA_SUCCESS) return 0;
    if (ma_context_get_devices(&ctx, &infos, &count, NULL, NULL) != MA_SUCCESS) count = 0;
    for (i = 0; i < count; i++) {
        int n = (int)strlen(infos[i].name);
        if (used + n + 2 > len) { count = i; break; }
        memcpy(buf + used, infos[i].name, (size_t)n);
        used += n;
        buf[used++] = '\n';
        buf[used] = 0;
    }
    ma_context_uninit(&ctx);
    return (int)count;
}

// Open a playback device by name; NULL or "" (or a name no longer present) is the system's
// default. ring_frames is the most audio held waiting. NULL on failure.
SS_API ss_audio *ss_audio_open(const char *device_name, int sample_rate, int channels, int ring_frames)
{
    ss_audio *a = (ss_audio *)calloc(1, sizeof(ss_audio));
    ma_device_info *infos;
    ma_uint32 count = 0, i;
    int found = 0;
    if (!a) return NULL;
    a->channels = (ma_uint32)channels;
    if (ma_context_init(NULL, 0, NULL, &a->context) != MA_SUCCESS) {
        free(a);
        return NULL;
    }
    if (device_name && device_name[0] &&
        ma_context_get_devices(&a->context, &infos, &count, NULL, NULL) == MA_SUCCESS) {
        for (i = 0; i < count; i++) {
            if (strcmp(infos[i].name, device_name) == 0) {
                a->device_id = infos[i].id;
                found = 1;
                break;
            }
        }
    }
    if (ma_pcm_rb_init(ma_format_f32, a->channels, (ma_uint32)ring_frames, NULL, NULL, &a->rb) != MA_SUCCESS) {
        ma_context_uninit(&a->context);
        free(a);
        return NULL;
    }
    ma_device_config cfg = ma_device_config_init(ma_device_type_playback);
    if (found) cfg.playback.pDeviceID = &a->device_id;
    cfg.playback.format = ma_format_f32;
    cfg.playback.channels = a->channels;
    cfg.sampleRate = (ma_uint32)sample_rate;
    cfg.dataCallback = on_data;
    cfg.pUserData = a;
    if (ma_device_init(&a->context, &cfg, &a->device) != MA_SUCCESS) {
        ma_pcm_rb_uninit(&a->rb);
        ma_context_uninit(&a->context);
        free(a);
        return NULL;
    }
    if (ma_device_start(&a->device) != MA_SUCCESS) {
        ma_device_uninit(&a->device);
        ma_pcm_rb_uninit(&a->rb);
        ma_context_uninit(&a->context);
        free(a);
        return NULL;
    }
    return a;
}

// Queue frames (interleaved float). Returns how many were taken; the rest did not fit.
SS_API int ss_audio_write(ss_audio *a, const float *frames, int count)
{
    ma_uint32 done = 0;
    while (done < (ma_uint32)count) {
        ma_uint32 n = (ma_uint32)count - done;
        void *dst;
        if (ma_pcm_rb_acquire_write(&a->rb, &n, &dst) != MA_SUCCESS || n == 0) break;
        memcpy(dst, frames + done * a->channels, n * a->channels * sizeof(float));
        ma_pcm_rb_commit_write(&a->rb, n);
        done += n;
    }
    return (int)done;
}

// Frames queued and not yet played.
SS_API int ss_audio_queued(ss_audio *a) { return (int)ma_pcm_rb_available_read(&a->rb); }

// Times the device wanted audio and the queue was empty.
SS_API int ss_audio_underruns(ss_audio *a) { return (int)a->underruns; }

SS_API void ss_audio_close(ss_audio *a)
{
    if (!a) return;
    ma_device_uninit(&a->device);
    ma_pcm_rb_uninit(&a->rb);
    ma_context_uninit(&a->context);
    free(a);
}
