#!/bin/sh
# Compile openfdvideo (FFmpeg minimal pour le Bink, lié en statique) pour Windows 64 bits et Linux 64 bits.
# Prérequis (Debian/Ubuntu) : gcc, make, nasm, mingw-w64, git.
# Résultat : ../../src/OpenFD.Video/runtimes/<système>/native/
set -e
HERE=$(cd "$(dirname "$0")" && pwd); WORK=${WORK:-$HERE/build}; mkdir -p "$WORK"; cd "$WORK"
[ -d ffmpeg-src ] || git clone --depth 1 --branch n7.1 https://github.com/FFmpeg/FFmpeg.git ffmpeg-src
COMMON="--disable-everything --enable-demuxer=bink --enable-decoder=bink,binkaudio_dct,binkaudio_rdft --enable-protocol=file
  --enable-swscale --enable-swresample --disable-programs --disable-doc --enable-static --disable-shared
  --disable-avdevice --disable-avfilter --disable-network --disable-autodetect"
# Linux
mkdir -p linux && (cd linux && ../ffmpeg-src/configure --prefix=$WORK/linux/out $COMMON --enable-pic && make -j8 && make install)
L=$WORK/linux/out
gcc -O2 -shared -fPIC -fvisibility=hidden -I$L/include "$HERE/openfdvideo.c" -o "$HERE/libopenfdvideo.so" \
  $L/lib/libavformat.a $L/lib/libavcodec.a $L/lib/libswscale.a $L/lib/libswresample.a $L/lib/libavutil.a -lm -lpthread -Wl,-Bsymbolic
# Windows (compilation croisée)
mkdir -p win && (cd win && ../ffmpeg-src/configure --prefix=$WORK/win/out --target-os=mingw32 --arch=x86_64 \
  --cross-prefix=x86_64-w64-mingw32- --enable-cross-compile $COMMON --disable-w32threads --disable-pthreads && make -j8 && make install)
W=$WORK/win/out
x86_64-w64-mingw32-gcc -O2 -shared -I$W/include "$HERE/openfdvideo.c" -o "$HERE/openfdvideo.dll" \
  $W/lib/libavformat.a $W/lib/libavcodec.a $W/lib/libswscale.a $W/lib/libswresample.a $W/lib/libavutil.a -lbcrypt -lm -static-libgcc
cp "$HERE/libopenfdvideo.so" "$HERE/../../src/OpenFD.Video/runtimes/linux-x64/native/"
cp "$HERE/openfdvideo.dll" "$HERE/../../src/OpenFD.Video/runtimes/win-x64/native/"
