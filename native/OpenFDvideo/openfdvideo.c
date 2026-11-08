/*
 * openfdvideo : petite bibliothèque native de lecture des vidéos du jeu (Bink, .bik) avec FFmpeg.
 * Une API C très simple, appelée depuis C# (FD3.Video) :
 *   fv_open(chemin UTF-8)  -> poignée (ou NULL)
 *   fv_width / fv_height / fv_fps / fv_duration / fv_audio_rate / fv_audio_channels
 *   fv_next(poignée, rgba, &pts, audio, capacité, &échantillons)
 *       -> 1 : une image décodée dans rgba (largeur × hauteur × 4 octets), pts en secondes
 *       -> 2 : du son décodé dans audio (16 bits entrelacés, « échantillons » par canal)
 *       -> 0 : fin de la vidéo ; < 0 : erreur
 *   fv_rewind(poignée) : retour au début (lecture en boucle)
 *   fv_close(poignée)
 * FFmpeg est compilé au minimum (démultiplexeur et décodeurs Bink, swscale, swresample) et lié en statique.
 * Licence de FFmpeg : LGPL 2.1 ou plus (sans les parties GPL).
 */
#include <stdlib.h>
#include <string.h>
#include <libavformat/avformat.h>
#include <libavcodec/avcodec.h>
#include <libswscale/swscale.h>
#include <libswresample/swresample.h>
#include <libavutil/imgutils.h>
#include <libavutil/channel_layout.h>

#ifdef _WIN32
#define EXPORT __declspec(dllexport)
#else
#define EXPORT __attribute__((visibility("default")))
#endif

typedef struct FV {
    AVFormatContext *fmt;
    AVCodecContext *vdec, *adec;
    int vs, as;
    struct SwsContext *sws;
    SwrContext *swr;
    AVPacket *pkt;
    AVFrame *frm;
    int pending;          /* un paquet est en cours de décodage (plusieurs images possibles) */
    int pendingStream;
    int eof;
    int outChannels;
    int outW, outH;       /* taille des images rendues : celle annoncée à l'ouverture (le tableau de l'appelant) */
    uint8_t *img;         /* image convertie, avec de la marge : swscale écrit parfois un peu après la fin de l'image
                             (instructions SIMD), il ne faut donc jamais lui donner directement le tableau de l'appelant */
    int imgStride;
} FV;

static AVCodecContext *open_codec(AVFormatContext *fmt, int idx)
{
    if (idx < 0) return NULL;
    AVStream *st = fmt->streams[idx];
    const AVCodec *c = avcodec_find_decoder(st->codecpar->codec_id);
    if (!c) return NULL;
    AVCodecContext *ctx = avcodec_alloc_context3(c);
    if (!ctx) return NULL;
    if (avcodec_parameters_to_context(ctx, st->codecpar) < 0 || avcodec_open2(ctx, c, NULL) < 0) { avcodec_free_context(&ctx); return NULL; }
    ctx->pkt_timebase = st->time_base;
    return ctx;
}

EXPORT void fv_close(FV *v)
{
    if (!v) return;
    if (v->sws) sws_freeContext(v->sws);
    if (v->swr) swr_free(&v->swr);
    if (v->vdec) avcodec_free_context(&v->vdec);
    if (v->adec) avcodec_free_context(&v->adec);
    if (v->pkt) av_packet_free(&v->pkt);
    if (v->frm) av_frame_free(&v->frm);
    if (v->img) av_free(v->img);
    if (v->fmt) avformat_close_input(&v->fmt);
    free(v);
}

EXPORT FV *fv_open(const char *path)
{
    FV *v = calloc(1, sizeof(FV));
    if (!v) return NULL;
    av_log_set_level(AV_LOG_ERROR);
    if (avformat_open_input(&v->fmt, path, NULL, NULL) < 0) { free(v); return NULL; }
    if (avformat_find_stream_info(v->fmt, NULL) < 0) { fv_close(v); return NULL; }
    v->vs = av_find_best_stream(v->fmt, AVMEDIA_TYPE_VIDEO, -1, -1, NULL, 0);
    v->as = av_find_best_stream(v->fmt, AVMEDIA_TYPE_AUDIO, -1, v->vs, NULL, 0);
    v->vdec = open_codec(v->fmt, v->vs);
    if (!v->vdec) { fv_close(v); return NULL; }
    v->adec = open_codec(v->fmt, v->as);
    v->outW = v->vdec->width;
    v->outH = v->vdec->height;
    if (v->outW <= 0 || v->outH <= 0) { fv_close(v); return NULL; }
    /* lignes alignées sur 64 octets, et 64 lignes de marge en plus à la fin */
    v->imgStride = (v->outW * 4 + 63) & ~63;
    v->img = av_mallocz((size_t)v->imgStride * (v->outH + 64));
    if (!v->img) { fv_close(v); return NULL; }
    if (v->adec) {
        AVChannelLayout out = AV_CHANNEL_LAYOUT_STEREO;
        v->outChannels = v->adec->ch_layout.nb_channels >= 2 ? 2 : 1;
        if (v->outChannels == 1) av_channel_layout_default(&out, 1);
        if (swr_alloc_set_opts2(&v->swr, &out, AV_SAMPLE_FMT_S16, v->adec->sample_rate,
                                &v->adec->ch_layout, v->adec->sample_fmt, v->adec->sample_rate, 0, NULL) < 0 || swr_init(v->swr) < 0) {
            swr_free(&v->swr); avcodec_free_context(&v->adec);
        }
    }
    v->pkt = av_packet_alloc();
    v->frm = av_frame_alloc();
    return v;
}

EXPORT int fv_width(FV *v) { return v ? v->outW : 0; }
EXPORT int fv_height(FV *v) { return v ? v->outH : 0; }
EXPORT double fv_fps(FV *v)
{
    if (!v) return 0;
    AVRational r = v->fmt->streams[v->vs]->avg_frame_rate;
    if (r.num <= 0 || r.den <= 0) r = v->fmt->streams[v->vs]->r_frame_rate;
    return r.num > 0 && r.den > 0 ? av_q2d(r) : 25.0;
}
EXPORT double fv_duration(FV *v) { return v && v->fmt->duration > 0 ? v->fmt->duration / (double)AV_TIME_BASE : 0; }
EXPORT int fv_audio_rate(FV *v) { return v && v->adec ? v->adec->sample_rate : 0; }
EXPORT int fv_audio_channels(FV *v) { return v && v->adec ? v->outChannels : 0; }

/* image ou son décodé depuis le paquet en cours ; 0 : il faut un nouveau paquet */
static int receive(FV *v, unsigned char *rgba, double *pts, short *audio, int cap, int *samples)
{
    AVCodecContext *dec = v->pendingStream == v->vs ? v->vdec : v->adec;
    int r = avcodec_receive_frame(dec, v->frm);
    if (r == AVERROR(EAGAIN) || r == AVERROR_EOF) { v->pending = 0; return 0; }
    if (r < 0) { v->pending = 0; return 0; }
    AVStream *st = v->fmt->streams[v->pendingStream];
    int64_t ts = v->frm->best_effort_timestamp != AV_NOPTS_VALUE ? v->frm->best_effort_timestamp : v->frm->pts;
    *pts = ts != AV_NOPTS_VALUE ? ts * av_q2d(st->time_base) : 0;
    if (dec == v->vdec) {
        /* l'image décodée peut changer de taille ou de format en cours de route : elle est toujours mise à la taille
           annoncée à l'ouverture (outW × outH), jamais plus grande que le tableau de l'appelant */
        if (v->frm->width <= 0 || v->frm->height <= 0 || !v->frm->data[0]) { av_frame_unref(v->frm); return 0; }
        v->sws = sws_getCachedContext(v->sws, v->frm->width, v->frm->height, (enum AVPixelFormat)v->frm->format,
                                      v->outW, v->outH, AV_PIX_FMT_RGBA, SWS_BILINEAR, NULL, NULL, NULL);
        if (!v->sws) { av_frame_unref(v->frm); return 0; }
        uint8_t *dst[4] = { v->img, NULL, NULL, NULL };
        int stride[4] = { v->imgStride, 0, 0, 0 };
        sws_scale(v->sws, (const uint8_t * const *)v->frm->data, v->frm->linesize, 0, v->frm->height, dst, stride);
        /* recopie exacte : outW × outH × 4 octets, pas un de plus */
        for (int y = 0; y < v->outH; y++)
            memcpy(rgba + (size_t)y * v->outW * 4, v->img + (size_t)y * v->imgStride, (size_t)v->outW * 4);
        av_frame_unref(v->frm);
        return 1;
    }
    if (!v->swr || cap <= 0) { av_frame_unref(v->frm); *samples = 0; return 2; }
    int max = cap / (v->outChannels > 0 ? v->outChannels : 1);
    uint8_t *out[1] = { (uint8_t *)audio };
    int n = swr_convert(v->swr, out, max, (const uint8_t **)v->frm->extended_data, v->frm->nb_samples);
    av_frame_unref(v->frm);
    *samples = n > 0 ? n : 0;
    return 2;
}

EXPORT int fv_next(FV *v, unsigned char *rgba, double *pts, short *audio, int cap, int *samples)
{
    if (!v) return -1;
    *samples = 0;
    for (;;) {
        if (v->pending) {
            int r = receive(v, rgba, pts, audio, cap, samples);
            if (r) return r;
        }
        if (v->eof) return 0;
        int r = av_read_frame(v->fmt, v->pkt);
        if (r < 0) {
            /* fin : on vide le décodeur d'images */
            v->eof = 1;
            avcodec_send_packet(v->vdec, NULL);
            v->pending = 1; v->pendingStream = v->vs;
            continue;
        }
        if (v->pkt->stream_index == v->vs || (v->adec && v->pkt->stream_index == v->as)) {
            AVCodecContext *dec = v->pkt->stream_index == v->vs ? v->vdec : v->adec;
            if (avcodec_send_packet(dec, v->pkt) >= 0) { v->pending = 1; v->pendingStream = v->pkt->stream_index; }
        }
        av_packet_unref(v->pkt);
    }
}

EXPORT int fv_rewind(FV *v)
{
    if (!v) return -1;
    if (av_seek_frame(v->fmt, v->vs, 0, AVSEEK_FLAG_BACKWARD) < 0 &&
        avformat_seek_file(v->fmt, -1, INT64_MIN, 0, 0, 0) < 0) return -1;
    avcodec_flush_buffers(v->vdec);
    if (v->adec) avcodec_flush_buffers(v->adec);
    v->pending = 0; v->eof = 0;
    return 0;
}
