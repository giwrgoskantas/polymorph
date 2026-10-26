# Polymorph — journal des versions

## 1.4.5 — octobre 2026

- **File > Export models** (et menu GTA) : export à la façon GIMS — liste des modèles avec une case chacun (la
  sélection est cochée), options (format .ydr / .yft, collisions oui / non, dossier), un fichier par modèle à son nom,
  textures intégrées, chaque modèle garde son pivot. Les meshes « _high_0 » prennent le nom de leur modèle.
- Grosse sélection : plus de chute à quelques images par seconde (les contours étaient reconstruits à chaque image
  au-delà de 48 objets) ; au-delà de 1,5 million de points sélectionnés, seulement les coins de sélection.
- Hide sur un Drawable cache aussi ses meshes ; Hide Unselected garde les meshes et le Drawable de la sélection.
- Clic droit : plus de saut de caméra dans les vues Top / Front / Left (un petit glissement en faisait une vue
  perspective reculée) ; il faut glisser de 10 px pour tourner la vue.
- Menus clic droit : Hide Selection / Hide Unselected retirés (en doublon avec l'œil) ; restent Isolate Selection et
  Unhide All.

## 1.4.4 — octobre 2026

- Un clic tombe toujours là où il est fait, même sans mouvement de souris juste avant (l'œil d'un Drawable cliqué
  juste après une autre fenêtre allait dans la vue 3D).
- Mode Vertex : tous les points visibles sont affichés jusqu'à 150 000 points (avant : seulement autour de la souris
  au-delà de 40 000, gênant sur les pièces GTA).
- Separate (Detach) en mode Vertex et Edge : prend les faces dont tous les points sont sélectionnés ; dans le clic droit.

## 1.4.3 — octobre 2026

- **Loop Cut** : plusieurs coupes d'un coup (molette, + / -, curseur du panneau), Tab tourne la coupe dans l'autre sens.
- **Knife** : panneau d'options — pas d'angle (libre, 15°, 45°, 90° ; Shift = 45°), coupe droite sur les axes du monde,
  faces visibles seulement. Une coupe peut commencer au milieu d'une face.
- **Sélection de faces** : un 2e clic rapide ajoute une face, il ne prend plus toute la boucle (sur un escalier, tout l'objet).
- **Ctrl + ↑ / Ctrl + ↓** agrandit / réduit la sélection ; Ctrl + ↓ juste après Ctrl + ↑ revient à la sélection d'avant.
  Les flèches seules déplacent toujours la caméra.
- Réduire la sélection marche sur les objets ouverts (escaliers, bandes) et en mode arêtes.
- Boutons UV Editor : erreur « conflicting ID » corrigée, et 20 autres libellés en double.
- Tests : chaque bug remonté devient un test qui rejoue les gestes de l'utilisateur.

## 1.4.0 — octobre 2026

- **Scans en Gaussian splats** : import .ply (3DGS ou nuage de points), .spz (v1 à v4) et .splat. Le scan s'affiche
  dans toutes les vues, trié et mélangé avec vos modèles, comme référence pour modéliser par-dessus.
  Réglages simples : taille des blobs, opacité, luminosité, nettoyage du brouillard, redresser, poser au sol,
  garder une boîte. Jamais exporté vers GTA. Enregistré dans la scène.
- **Rendu photo** (F12, menu Rendering ou menu de la vue Realistic) : photo path tracée de la vue active,
  calculée en arrière-plan par un moteur gratuit. Réglages en mots simples : qualité, taille, lumière (comme la vue,
  jour, soir, nuit, studio), luminosité, lumière du ciel, puissance des lampes, grain, ombres douces, fond transparent.
- Constructeur de pièces : plusieurs portes et fenêtres sur un même mur.

## 1.0.0 — octobre 2026

Première version publique. Polymorph est gratuit, par Molo Modding.

### Modélisation
- Edit Poly et Unwrap UVW en modificateurs, insérables n'importe où dans la pile (non destructif).
- Chamfer, bridge, knife / slice, booléens, extrude avec flèche et panneau « Adjust Last Operation ».
- Cube Projection (UV + matériau), Smart UV, éditeur UV, densité de texel.
- Sculpt (Smooth, Relax, Flatten, Draw, Inflate, Grab, Pinch, symétrie) pour nettoyer les meshes générés.
- Instances (Alt+D, Shift-drag), snaps, isolate, caméra de marche aux flèches, raccourcis compatibles avec les grands logiciels 3D.
- Scene Explorer qui tient des milliers d'objets, sélection au cadre, filtres par type GTA.

### GTA V / FiveM
- Import et export .ydr, .yft, .ydd, .ybn, .ytyp, .ymap (ressource FiveM prête : fxmanifest, stream, textures intégrées).
- LOD automatiques, collision automatique, vertex colors selon les règles du jeu, DDS 1024 avec mipmaps.
- Vérificateur GTA avant export avec corrections en un clic (shell en limbo, pièces fermées, normales, 60k vertex…).
- Constructeur de pièces MLO : plan dessiné ou lu depuis une image, portails automatiques, matériaux par surface,
  plinthes, salissures et usure des coins, références de taille.
- Explorateur des fichiers du jeu (modèles, textures, collisions par matériau).
- Envoi direct vers ton serveur FiveM de test.

### Mode MAP
- Toute la carte de GTA V affichée avec ses modèles lointains (LOD / SLOD), comme dans CodeWalker.
- Un clic charge les .ymap de la zone pour l'édition : déplacer, supprimer, poser des props collés aux surfaces.
- Édition des intérieurs du jeu (MLO) : ouvrir un intérieur, déplacer / supprimer / ajouter des objets, le .ytyp est réécrit.
- Sauvegarde en ressource FiveM (les fichiers du jeu gardent leur nom : FiveM utilise ta version).
- Modèles lus une fois puis gardés en cache : réouverture de la carte en quelques secondes.

### Ajouts de la dernière ligne droite
- Snaps sur vertex / arêtes / faces avec points visibles ; on attrape un point de l'objet et on le pose sur la cible, contact sur les faces (plus d'objet qui rentre dans l'autre).
- Caméra « jeu vidéo » : clic droit maintenu + ZQSD/WASD, E/Espace monter, C descendre, Shift rapide, molette = vitesse.
- Edit mode : Shift + déplacement copie les faces / arêtes / vertex ; sur des arêtes ça extrude dans le sens du glisser.
- Bridge refait (faces, groupes de faces, chaînes d'arêtes) et Flip Normals dans le clic droit.
- Menu UV au clic droit : Unwrap, Smart UV, Lightmap, Cube, Cylindre, Sphère, Projection depuis la vue, Pack, Reset…
- Clic droit objet simplifié : « Clone… » (copie / instance), plus d'entrées en double.
- Matériaux : créer ne les applique plus, on les glisse-dépose sur l'objet ; les matériaux du jeu ouverts par le mode MAP sont masqués.
- Sélection au travers comme dans les grands logiciels, option « Visible only » (Alt+Z) ; les faces arrière cachées ne volent plus le clic.
- ViewCube : maintenir et glisser fait tourner la vue.
- Lumières Point / Spot / Capsule, éclairage proche du jeu, toutes les propriétés Sollumz (flags, fades, volume, culling plane…) exportées.
- Constructeur MLO entièrement en direct, encadrements de portes et fenêtres variés (5 styles, identiques des deux côtés).
- Pièces connectées : à une porte entre deux pièces, un chambranle traverse l'espace et relie les deux encadrements (option « Connect the rooms »).
- Mode MAP : HD chargée automatiquement autour de la caméra, beaucoup plus fluide sur les grandes zones ; les LOD restent affichés tant que la HD n'est pas arrivée.
- Modéliser directement dans la carte : tous les outils de modélisation dans le mode MAP, le Room Builder posé au sol (MLO construit sur place), et un modèle du jeu ouvert en copie personnelle à sa place ; tout part avec la ressource map (stream/build : modèles, ytyp, MLO, entités).
- Weapon Forge : un modèle 3D devient une arme GTA (squelette d'une arme du jeu, ajustement auto, remplacement ou arme add-on avec metas clonés).
- Vehicle Forge : un modèle de voiture devient un véhicule FiveM (roues détectées, calage sur les roues d'une voiture du jeu, yft + metas clonés + textures).
- Vidéos tutos par catégorie et trailer.

### Rendu
- Shader du jeu, onglet Lumière (32 lumières, ombres du soleil, courbe filmique), mode orientation des faces.
- Molo Paint : peinture de textures par calques, masques intelligents, bake high-poly, export DDS.

### Pilotage
- API Python et serveur MCP : Claude peut piloter Polymorph (créer, modifier, UV, vérifier, exporter, captures).

### Limites connues
- Les intérieurs du jeu s'éditent objet par objet ; les pièces et portails eux-mêmes ne se redessinent pas dans le mode MAP
  (pour ça : importer le .ytyp dans le mode MODEL).
- Les nouveaux archétypes d'une ressource lancée pendant que tu es connecté ne s'affichent qu'après une reconnexion (comportement de FiveM).
