# fd3video — lecture des vidéos Bink (.bik) du jeu

Petite bibliothèque native utilisée par `src/FD3.Video` : elle ouvre une vidéo Bink et rend, dans l'ordre du
fichier, les images (RGBA) et le son (16 bits entrelacés). Voir les commentaires de `fd3video.c` pour l'API.

- Décodage : **FFmpeg 7.1** compilé au minimum (démultiplexeur et décodeurs Bink, swscale, swresample), lié en
  statique dans la bibliothèque. Licence de FFmpeg : **LGPL 2.1 ou plus** (aucune partie GPL activée) ; sources :
  https://github.com/FFmpeg/FFmpeg (étiquette `n7.1`).
- Binaires fournis : `fd3video.dll` (Windows 64 bits), `libfd3video.so` (Linux 64 bits), copiés dans
  `src/FD3.Video/runtimes/<système>/native/`.
- Recompiler : `./build.sh` (Linux, avec gcc, make, nasm, mingw-w64, git).
- Portage macOS : compiler FFmpeg de la même façon sur un Mac, puis
  `clang -shared -O2 -I<ffmpeg>/include fd3video.c <les .a> -framework CoreFoundation -o libfd3video.dylib`,
  et le mettre dans `src/FD3.Video/runtimes/osx-x64/native/`. Le code C# ne change pas.
- Sans cette bibliothèque, le jeu démarre quand même : il passe simplement les vidéos.
