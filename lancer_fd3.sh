#!/bin/sh
# Lance le jeu FD3 sous Linux (version MonoGame). Usage : ./lancer_fd3.sh "/chemin/vers/Fire Department 3"
cd "$(dirname "$0")"
JEU="${1:-$(pwd)/..}"
exec dotnet run --project src/OpenFD.MG -c Release -- "$JEU"
