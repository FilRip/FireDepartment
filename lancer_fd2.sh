#!/bin/sh
# Lance le jeu FD3 sous Linux (version MonoGame). Usage : ./lancer_fd2.sh "/chemin/vers/Fire Department 2"
cd "$(dirname "$0")"
JEU="${1:-$(pwd)/..}"
exec dotnet run --project src/OpenFD.MG -c Release -- "$JEU"
