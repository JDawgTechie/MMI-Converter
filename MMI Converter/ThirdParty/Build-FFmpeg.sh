#!/usr/bin/env bash
# Run in the extracted, unmodified FFmpeg 9.1 source with MSYS2 CLANG64 and GNU make.
set -eu
./configure --target-os=mingw32 --arch=x86_64 \
  --cc=clang --cxx=clang++ --ar=llvm-ar --nm=llvm-nm --ranlib=llvm-ranlib --strip=llvm-strip \
  --disable-autodetect --disable-everything --disable-network --disable-doc \
  --disable-ffprobe --disable-ffplay --disable-x86asm --enable-static --disable-shared \
  --enable-ffmpeg --enable-decoder=mjpeg --enable-parser=mjpeg --enable-demuxer=mjpeg \
  --enable-muxer=null --enable-encoder=wrapped_avframe --enable-protocol=pipe --enable-filter=null
make -j12 ffmpeg.exe
