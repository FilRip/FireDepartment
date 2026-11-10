#!/bin/sh
# Compile la version Linux (64 bits) du jeu et de l'éditeur de missions dans publish/linux-x64.
# Il faut le kit .NET 8 (https://dotnet.microsoft.com/download) ; sur la machine Linux qui lance le jeu,
# le « .NET 8 Runtime » suffit.
# Lancer ensuite :   publish/linux-x64/OpenFD "/chemin/vers/Fire Department 3"
#                    publish/linux-x64/OpenFD.Editor "/chemin/vers/Fire Department 3"
set -e
cd "$(dirname "$0")"
OUT=publish/linux-x64
dotnet publish src/OpenFD.MG -c Release -r linux-x64 --self-contained false -o "$OUT" "$@"
dotnet publish src/OpenFD.Editor -c Release -r linux-x64 --self-contained false -o "$OUT" "$@"
chmod +x "$OUT/OpenFD" "$OUT/OpenFD.Editor" 2>/dev/null || true
echo "Version Linux prête dans $OUT"
