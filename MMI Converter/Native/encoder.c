#include <limits.h>
#include <stdio.h>
#include <stdlib.h>
#include <wchar.h>
#include <windows.h>
#include "mmi.h"
#include "util.h"
#include "turbojpeg.h"

static const int width = 910, height = 525;
static const int qualities[] = {80, 85, 89, 92, 94, 96, 97, 98, 99, 100};

typedef struct {
    tjhandle codec;
    unsigned char *frame;
    unsigned char *scratch;
    size_t scratch_size;
    unsigned char *jpeg;
    size_t jpeg_size;
} encoder;

static int compress_frame(encoder *enc, int quality)
{
    return tj3Set(enc->codec, TJPARAM_QUALITY, quality) ||
        tj3Compress8(enc->codec, enc->frame, width, width * 3, height,
            TJPF_RGB, &enc->jpeg, &enc->jpeg_size) ||
        enc->jpeg_size > UINT32_MAX;
}

static int read_frame(encoder *enc, qon_stream *source, uint32_t frame)
{
    return qon_read_frame(source, frame, enc->frame, &enc->scratch, &enc->scratch_size);
}

static int encode(encoder *enc, qon_stream *source, uint32_t first, uint32_t count, int quality, const wchar_t *path)
{
    FILE *output = _wfopen(path, L"wb");
    if (!output) return 1;
    const int lines[] = {15, 16, 278, 279};
    int failed = 0;
    for (uint32_t i = 0; i < count; ++i) {
        if (read_frame(enc, source, first + i) || compress_frame(enc, quality)) {
            fprintf(stderr, "Could not encode source frame %u.\n", first + i);
            failed = 1;
            break;
        }
        uint8_t record[20];
        wr32le(record, (uint32_t)enc->jpeg_size);
        for (unsigned line = 0; line < 4; ++line)
            wr32le(record + 4 + line * 4, vbi_decode_line(enc->frame, width, width, 0, lines[line]));
        if (fwrite(record, 1, sizeof record, output) != sizeof record ||
            fwrite(enc->jpeg, 1, enc->jpeg_size, output) != enc->jpeg_size) {
            fprintf(stderr, "Could not write the temporary JPEG data. Check available disk space.\n");
            failed = 1;
            break;
        }
        if ((i + 1) % 64 == 0 || i + 1 == count) {
            printf("%u\n", i + 1);
            fflush(stdout);
        }
    }
    return fclose(output) || failed;
}

static int sample(encoder *enc, qon_stream *source, uint32_t first_window, uint32_t end_window)
{
    enum { points = sizeof qualities / sizeof *qualities, window_count = 18, sample_count = 18 };
    double estimates[points] = {0};
    uint32_t frames = source->hdr.frame_count;
    uint32_t windows = frames < window_count ? frames : window_count;
    if (first_window >= end_window || end_window > windows) return 1;
    for (uint32_t window = first_window; window < end_window; ++window) {
        uint32_t start = (uint64_t)window * frames / windows;
        uint32_t end = (uint64_t)(window + 1) * frames / windows;
        uint32_t span = end - start;
        uint32_t count = span < sample_count ? span : sample_count;
        uint64_t sizes[points] = {0};
        for (uint32_t i = start + (span - count) / 2; i < start + (span - count) / 2 + count; ++i) {
            if (read_frame(enc, source, i)) return 1;
            for (int point = 0; point < points; ++point) {
                if (compress_frame(enc, qualities[point])) return 1;
                sizes[point] += enc->jpeg_size;
            }
        }
        for (int point = 0; point < points; ++point)
            estimates[point] += sizes[point] * ((double)span / count);
    }
    for (int point = 0; point < points; ++point) printf("%d %.0f\n", qualities[point], estimates[point]);
    return 0;
}

int wmain(int argc, wchar_t **argv)
{
    if (argc != 4 && argc != 6 && argc != 8) return 1;
    char error[256], entry_name[512];
    if (!WideCharToMultiByte(CP_UTF8, WC_ERR_INVALID_CHARS, argv[3], -1, entry_name, sizeof entry_name, NULL, NULL))
        return 1;
    zip_file archive;
    if (zip_open(&archive, argv[2], error, sizeof error)) {
        fprintf(stderr, "%s\n", error);
        return 1;
    }
    const zip_entry *entry = zip_find(&archive, entry_name);
    qon_stream source;
    if (!entry || entry->method || qon_open(&source, archive.fd, entry->data_off, entry->uncomp_size, error, sizeof error)) {
        fprintf(stderr, "The MMI must contain a directly seekable QON video stream.\n");
        zip_close(&archive);
        return 1;
    }
    int status = 1;
    if (source.hdr.width != width || source.hdr.height != height || source.hdr.channels != 3 ||
        !source.hdr.frame_count) {
        fprintf(stderr, "Unsupported video raster. Expected 910 × 525 RGB.\n");
    } else if (argc == 4 && !wcscmp(argv[1], L"probe")) {
        printf("%u\n", source.hdr.frame_count);
        status = 0;
    } else {
        encoder enc = {0};
        enc.codec = tj3Init(TJINIT_COMPRESS);
        enc.frame = malloc((size_t)width * height * 3);
        if (enc.codec && enc.frame && !tj3Set(enc.codec, TJPARAM_SUBSAMP, TJSAMP_444) &&
            !tj3Set(enc.codec, TJPARAM_OPTIMIZE, 1)) {
            if (argc == 6 && !wcscmp(argv[1], L"sample")) {
                status = sample(&enc, &source, wcstoul(argv[4], NULL, 10), wcstoul(argv[5], NULL, 10));
            } else if (argc == 8 && !wcscmp(argv[1], L"encode")) {
                uint32_t first = wcstoul(argv[4], NULL, 10), count = wcstoul(argv[5], NULL, 10);
                int quality = _wtoi(argv[6]);
                if (first < source.hdr.frame_count && count <= source.hdr.frame_count - first &&
                    count && quality >= 80 && quality <= 100)
                    status = encode(&enc, &source, first, count, quality, argv[7]);
            }
        }
        if (status) fprintf(stderr, "The JPEG operation did not complete.\n");
        tj3Free(enc.jpeg);
        if (enc.codec) tj3Destroy(enc.codec);
        free(enc.frame);
        free(enc.scratch);
    }
    qon_close(&source);
    zip_close(&archive);
    return status;
}
