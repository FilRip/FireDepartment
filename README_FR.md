# OpenFD — réimplémentation C# de Fire Department 2 et 3

Nouveau moteur qui lit les fichiers d'une installation originale de Fire Department 2 et 3.
Aucune donnée du jeu n'est incluse dans ce dépôt.

## Le jeu (Windows et Linux)

Double-cliquez sur `Lancer_FD3.bat` (sous Linux : `./lancer_fd3.sh "<dossier du jeu>"`).
La première compilation télécharge MonoGame (paquet NuGet `MonoGame.Framework.DesktopGL`) : il faut
une connexion Internet cette fois-là. Aucun « content pipeline » : textures, modèles, animations et sons
sont lus directement dans les fichiers du jeu installé. Une fenêtre s'ouvre avec la mission en 3D (décor, objets,
véhicules, civils, arbres, feu et fumée). Touches : Espace pause, 1 à 4 vitesse, Tab carte vue de
dessus, T toits, O objets, F fumée, B ombres, F5 sauvegarder, F9 charger la dernière sauvegarde, Alt + Entrée plein écran.
Le soleil (direction, opacité des ombres, nuit) vient de l'objet lumière `base_sun` de la carte
(composant FD2_LIGHT). Mini-carte en bas à gauche (image `Minimap` du fichier .mis) : clic pour y
regarder, clic droit pour y envoyer les unités choisies.
Les secours prévus par la mission arrivent (section [FIRESTATION] : équipe de départ, renforts) :
clic pour choisir une unité (Maj pour en ajouter, A pour tous les pompiers), clic droit pour la
déplacer, clic droit sur un feu pour qu'elle aille l'éteindre. Les pompiers ont une réserve d'eau
limitée : clic droit sur un camion pour la remplir. Civils : clic droit sur un civil pour aller le
chercher (s'il est inconscient, seul le médecin peut le réanimer), il suit alors l'unité ; clic droit
sur l'ambulance pour l'y déposer. Les textures des véhicules et des
pompiers viennent du cache que le jeu fabrique lui-même (Data\Gfx\TextureCache\High\Cache.pak).
Les sons viennent de Data\Sound (SoundDB.INI) ; les curseurs, de Gfx\Textures\_cache\MouseCursors.dds.
Si la carte graphique refuse l'anticrénelage, le jeu redémarre seul sans lui (`FD3_SANS_MSAA=1` pour le forcer).

## Voir la simulation dans le navigateur (Windows)

Double-cliquez sur `Lancer_visualiseur.bat` (le dossier du projet doit être dans le dossier du jeu,
ou modifiez le chemin dans le fichier). Le navigateur s'ouvre sur http://localhost:5173 :
choisissez une mission, « Charger la mission », puis une vitesse. Cliquez sur un feu pour l'arroser.
La vue 3D (lien « Ouvrir la vue 3D ») affiche le décor et les objets du jeu, avec le feu et la fumée
de la simulation en cours.
Il faut le kit .NET 8 (ou plus récent) : https://dotnet.microsoft.com/download

## Projets

| Projet | Contenu |
|---|---|
| `OpenFD.Formats` | Modèles 3D (`.mesh`/`.meshdata`, `Mesh.cs`), matériaux `.stdmat`, archives `.pak`/`.bank` (zip « QL »), fichiers libres prioritaires, `.level`, `.grid`, classes `.class`/`.cfg`/`.mis` avec héritage, pièces (volumes `_ROOM` du décor) |
| `OpenFD.Fire` | Simulation du feu, de la fumée et de l'extinction, sans affichage (voir `docs/MODELE_FEU.md`) |
| `OpenFD.Script` | Interpréteur des scripts de mission `.scp` (analyse, exécution, appels différés) |
| `OpenFD.Game` | Une partie : carte, feu, fumée, scripts de mission, unités qui arrosent |
| `OpenFD.MG` | Le jeu, version MonoGame DesktopGL : 3D, carte, interface, personnages animés, sons, curseurs |
| `OpenFD.EzTree` | Générateur d'arbres : portage en C# d'EZ-Tree 1.1 (licence MIT), utilisé par OpenFD.MG |
| `OpenFD.Viewer` | Visualiseur vue de dessus dans le navigateur (serveur local) |
| `OpenFD.Tools` | Outils en ligne de commande pour vérifier et comparer |

## Utilisation (.NET 8)

```
dotnet build
dotnet run --project src/OpenFD.Tools -- roundtrip "C:\Jeux\Fire Department 3\Data"
dotnet run --project src/OpenFD.Tools -- simulate  "C:\Jeux\Fire Department 3\Data" London 15 NORMAL
dotnet run --project src/OpenFD.Tools -- scripts   "C:\Jeux\Fire Department 3\Data"
dotnet run --project src/OpenFD.Tools -- mission   "C:\Jeux\Fire Department 3\Data" Fireworks 4 EASY pieces=auto fumee=auto
dotnet run --project src/OpenFD.Tools -- simulate  "C:\Jeux\Fire Department 3\Data" London 2.5 EASY pieces=auto sans-voisinage 3d "arroser=ground_l109@60.6-74"
dotnet run --project src/OpenFD.Tools -- pieces    "C:\Jeux\Fire Department 3\Data" London pieces_London.json
dotnet run --project src/OpenFD.Tools -- typesfeu  "C:\Jeux\Fire Department 3\Data"
```

`simulate` affiche l'évolution minute par minute et écrit un fichier CSV (intensité de chaque
foyer toutes les 10 secondes) dans le dossier temporaire.

Le fichier `nuget.config` vide la liste des sources de paquets (l'environnement de développement
n'a pas accès à nuget.org). Supprimez-le sur votre machine quand nous ajouterons l'affichage 3D.

`pieces=auto` et `fumee=auto` calculent les pièces à partir des graphismes de la mission
(plus besoin des scripts Python ni des fichiers JSON du dossier `tools`).

La vue 3D utilise three.js 0.170 et EZ-Tree 1.1 (arbres réalistes générés, Daniel Greenheck), tous deux sous
licence MIT, copiés dans `src/OpenFD.Viewer/wwwroot/lib`.
