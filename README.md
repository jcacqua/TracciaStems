# TracciaStems
Pour mixer des mp3

# TracciaStems (ex-StemMixer) — notes de projet

Application renommée **TracciaStems** le 2026-10-13 (zip : TracciaStems.zip, exe : publish\TracciaStems.exe).
Logo fourni par Jean-Christophe (néon rétro, éclairs) ; projets enregistrés en `.traccia` (les `.stemmix` s'ouvrent toujours).

## Choix
- C# / .NET 10 (WPF) + NAudio 2.2.1 ; compilation via build.bat → exe autonome win-x64
- Stems chargés entièrement en RAM (float stéréo, fréquence du projet = celle du 1er stem)
- Export WAV 16/24/32f et MP3 320/192 via l'encodeur Media Foundation de Windows

## Logo animé
- Couches extraites de l'image : fond sans éclairs (inpainting), 4 groupes d'éclairs, 4 halos (Assets/Logo/*.png)
- Contrôle UI/AnimatedLogo : salves de 2–4 flashs par groupe, halo qui s'estompe, éclairs faibles au repos
- Affiché dans la barre du haut et en grand sur l'écran d'accueil ; icône = emblème TM
- Limite : les éclairs qui traversent le texte restent fixes (seuls ceux sur le fond noir sont animés)

## Fonctions
- Volume/pan/mute/solo, EQ 3 bandes, compresseur, envoi reverb, limiteur master
- Boucles de zone (« Boucler ici », « Tout boucler ici » / L, Maj+glisser)
- Stems courts : « ∞ Répéter », calage auto sur les mesures (WSOLA sans changement de hauteur + phase sur le 1er temps)
- Détection tempo + grille (1er temps), ◀ ▶ pour corriger
- Enregistrement en direct du master (WAV 24 bits / MP3), export hors temps réel

## Bugs corrigés après retours
- MC4102 (propriété float[] dans un DataTemplate), DrawingGroup figé trop tôt, son qui boucle au Stop (tampon NAudio byte[] vu comme float[] : pas d'Array.Clear/Copy dessus), fondus anti-clic

## Vérifications
- Moteur audio, BPM, grille, étirement, calage testés sous Linux (banc de test) ; C# compilé contre les API WPF de référence ; XAML vérifié par un contrôleur maison ; pas d'exécution Windows de mon côté

## Pistes
Automation, export des stems séparés, choix du périphérique de sortie, départ d'une boucle à une mesure donnée.
