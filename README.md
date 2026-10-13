# TracciaStems

Application Windows de mixage multipiste à partir de stems (WAV, MP3, AIFF, FLAC…), avec enregistrement du mix.

## Compiler l'application (une seule fois)

1. Installer le **SDK .NET 10** (gratuit) : `winget install Microsoft.DotNet.SDK.10`
   ou https://dotnet.microsoft.com/download/dotnet/10.0
2. Double-cliquer sur **`build.bat`**.
3. L'exécutable autonome est créé dans `publish\TracciaStems.exe` (aucune installation nécessaire, copiable sur un autre PC Windows 10/11 64 bits).

Vous pouvez aussi ouvrir `TracciaStems.sln` dans Visual Studio 2022/2026.

## Fonctions

| Fonction | Détail |
|---|---|
| Import | WAV, MP3, AIFF, FLAC, M4A… par bouton ou glisser-déposer (fichiers ou dossier entier). Mono → stéréo, rééchantillonnage automatique à la fréquence du projet. |
| Par piste | Volume (-∞ à +12 dB), pan, Mute, Solo (Ctrl+clic = solo exclusif), vu-mètre |
| EQ 3 bandes | Grave (shelf 200 Hz), Médium (cloche 1 kHz), Aigu (shelf 5 kHz), ±15 dB |
| Compresseur | Seuil, ratio, attaque, relâchement, gain ; affichage de la réduction de gain |
| Reverb | Envoi par piste vers une reverb commune (taille, amorti, niveau de retour) |
| Boucles par stem | Choisir la longueur (1 à 32 mesures) puis **⟳ Boucler ici** : le stem boucle à partir de la position de lecture (aimantée au temps). Recliquer pour reprendre le morceau. **⟳ Tout boucler ici** (touche L) fait la même chose sur tous les stems. **Maj + glisser** sur la forme d'onde pour dessiner une boucle précise. |
| Stem court répété | Bouton **∞ Répéter** : le fichier entier tourne en boucle sur toute la longueur du morceau (boucle de batterie, nappe…). Activé automatiquement à l'import pour un stem de moins d'une minute qui dure moins de la moitié du morceau. Un stem répété ne rallonge pas le morceau. |
| Calage sur les mesures | **🎯 Caler sur les mesures** (automatique à l'import d'un stem court) : détecte le tempo et le 1er temps du morceau, devine le nombre de mesures de la boucle (1, 2, 4, 8…), l'étire au tempo exact **sans changer la hauteur**, et place son 1er temps sur les 1ers temps du morceau. ◀ ▶ décalent la boucle d'un temps, ↺ annule. |
| BPM / mesures | « Détecter » trouve le tempo **et la position du 1er temps** ; la grille des formes d'onde et l'aimantation s'y calent. ◀ 1er temps ▶ corrige la grille d'un temps (les boucles calées suivent). Détection par stem (« BPM ? »), ÷2 / ×2 |
| Master | Volume, limiteur -0,3 dBFS (anti-saturation), vu-mètre, témoin de saturation |
| Enregistrement en direct | Bouton **● REC** : capture tout ce que vous entendez, y compris les mouvements de faders faits pendant la lecture → WAV 24 bits ou MP3 320 kbps |
| Export | Rendu exact hors temps réel → WAV 16 bits (CD, avec dither) / 24 bits / 32 bits flottant, MP3 320 ou 192 kbps |
| Projet | Fichier `.traccia` (les anciens `.stemmix` s'ouvrent toujours) qui garde les chemins des stems et tous les réglages. Si les stems sont déplacés avec le projet, ils sont retrouvés automatiquement ; sinon l'appli propose de les localiser. |

## Raccourcis

| Touche | Action |
|---|---|
| Espace | Lecture / pause |
| Origine | Retour au début |
| R | Démarrer / arrêter l'enregistrement en direct |
| L | Tout boucler ici / reprendre le morceau |
| Ctrl+S | Enregistrer le projet |
| Ctrl+O | Ajouter des stems |
| Double-clic sur un curseur | Valeur par défaut |
| Clic sur une forme d'onde | Déplacer la lecture |
| Maj + glisser sur une forme d'onde | Définir la boucle de ce stem |

## Conseils

- **Enregistrement en direct** : il démarre à la position actuelle. Faites ⏮ avant REC pour capturer le morceau depuis le début. Mettre en pause met aussi l'enregistrement en pause ; l'enregistrement s'arrête tout seul à la fin du morceau.
- **Prolonger un morceau avec des boucles** : renseignez « Durée (s) » dans la barre du haut (0 = durée du stem le plus long).
- **Tempo** : la détection est fiable sur un stem de batterie. Si le résultat est la moitié ou le double du tempo réel, utilisez ÷2 / ×2.
- **Mémoire** : les stems sont chargés en RAM pour une lecture instantanée (environ 21 Mo par minute de stem stéréo à 44,1 kHz).

## Structure du code

```
src/TracciaStems/
  Audio/            moteur audio (indépendant de l'interface)
    StemData.cs       chargement / conversion des fichiers, crêtes pour l'affichage
    MixEngine.cs      somme des pistes, bus reverb, master, limiteur
    TrackProcessor.cs lecture avec boucle, EQ, compresseur, volume/pan, envoi
    Dsp/              Biquad (EQ), Compressor, Freeverb, Limiter
    BpmDetector.cs    flux spectral + autocorrélation
    LiveRecorder.cs   capture en direct (écriture disque sur un thread séparé)
    Exporter.cs       rendu hors temps réel, WAV/MP3 (encodeur MP3 intégré à Windows)
  Project/ProjectFile.cs  sauvegarde JSON (.traccia)
  UI/               vues (forme d'onde, vu-mètre, logo animé) et view-models
  Assets/Logo/      couches du logo (fond sans éclairs, éclairs, halos) pour l'animation
  MainWindow.xaml   interface
```

Bibliothèque audio : [NAudio](https://github.com/naudio/NAudio) (licence MIT).
