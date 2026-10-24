# Architecture — étape 1

## Choix techniques

| Choix | Pourquoi | Alternative écartée |
|---|---|---|
| **WinUI 3** (.NET 10, non empaqueté) | Application Windows seulement, pensée pour le tactile (zoom au pincement natif, clavier tactile, gros contrôles). Accès direct aux API Windows d'OCR et de PDF. Livrée comme un simple dossier à copier : pas de Store, pas d'installation. | **.NET MAUI** : ajoute une couche multiplateforme (Android, iOS, Mac) sans valeur ici, et sur Windows il tourne de toute façon sur WinUI 3. |
| **Windows.Media.Ocr** | Intégré à Windows : rien à embarquer, aucune licence, fonctionne sur Surface Intel et ARM, hors ligne. | **Tesseract embarqué** : ~30 Mo de données + bibliothèque native par architecture. Gardé en réserve si la qualité Windows déçoit sur le terrain. |
| **Traitement d'image maison** (redressement, filets) | Quelques centaines de lignes C# testées, sans dépendance native. | **OpenCV** : ~60 Mo natifs, pas de version ARM64 officielle pour .NET. |
| **PDF : Windows.Data.Pdf** | Intégré à Windows. | PDFium / bibliothèques tierces. |
| **XLSX : lecteur minimal** (zip + XML) | Valeurs seulement : 100 lignes, aucune dépendance. | ClosedXML / ExcelDataReader : inutiles pour lire des valeurs. |

Seules dépendances NuGet : `Microsoft.WindowsAppSDK` et `Microsoft.Windows.SDK.BuildTools` (MIT). Le cœur n'en a aucune.

## Projets

```
src/RetoucheInventaire.Core    Logique métier, sans Windows : testable partout
  Imaging/    redressement (4 coins + inclinaison), opérations d'image
  Analysis/   filets → grille, préparation OCR, analyse page → tableaux, types de ligne
  Model/      modèle de données + actions d'édition (verrous, type de ligne)
  Export/     export .txt
  Import/     TXT, CSV, XLSX → page tableau
  Storage/    fichier projet .inventaire
src/RetoucheInventaire.App     WinUI 3 : l'écran, OCR Windows, PDF Windows, décodage d'images
tests/                         27 tests sur les pages fournies
```

## L'écran (unique)

- **Barre d'outils** (boutons de 64 px) : Importer · Recadrer · Verrouiller · Type de ligne · Rétablir · Exporter .txt · Enregistrer · Ouvrir · Nouveau.
- **Pages** l'une sous l'autre, zoom au pincement. Chaque case de chaque ligne est un champ posé à sa position exacte.
- **Barre d'état** : ce qui se passe, la ligne sélectionnée et son type, le texte d'origine d'une case modifiée.
- **Recadrage** (par-dessus l'écran, à la demande) : 4 poignées, Pivoter, Détection auto, Page entière, Valider.

Une seule page affichée à la fois (navigation ◀ 3 / 10 ▶, liste des pages), avec à sa gauche la colonne des numéros de lot, hors de la page.

Repères sur la page : badge à gauche de la ligne (**T** titre, **Σ** total, **E** en-tête, **×** non exportée), zones verrouillées en orange.

## Modèle de données

```
InventoryProject
└─ Page                 source (image ou données), recadrage (4 coins), inclinaison, quarts de tour, zones verrouillées
   ├─ Table             bords de colonnes et de rangées (pixels de page)
   │  └─ Row            entre deux filets horizontaux
   │     └─ PrintedLine type : Article | Titre | Total | EnTete | Ignoree
   │        └─ Field    une case de la ligne : Text, OriginalText, Ink (encre d'origine),
   │                    Box (zone d'édition, jamais hors de la case), alignement, retrait, taille de police, Locked
   └─ LooseLines        texte hors tableau (n° de page, signature, tampon) : verrouillé, non exporté
```

La géométrie est figée à l'analyse ; l'édition ne change que `Text`. C'est ce qui garantit que rien ne bouge (vérifié par un test).

## Flux import → édition → export texte

1. **Import** — photo/scan : orientation EXIF appliquée ; PDF : pages rendues une par une en image (une seule en mémoire), chaque page analysée est enregistrée aussitôt ; TXT/CSV/XLSX : tableau reconstruit directement (pas d'OCR).
2. **Redressement** — coins de la feuille trouvés automatiquement (zone claire sur fond sombre), perspective corrigée, puis inclinaison résiduelle mesurée et corrigée, en un seul rééchantillonnage. Si la page paraît illisible, les autres quarts de tour sont essayés.
3. **Grille** — détection des filets horizontaux et verticaux (tolère traits fins, ondulés, interrompus).
4. **OCR** — sur une copie préparée : filets effacés, seuil adaptatif (fonds tramés, ombres), agrandie ×2. Puis **seconde lecture** case par case des cases vides qui contiennent de l'encre (nombres isolés que l'OCR de page oublie).
5. **Analyse** — mots → cases → lignes imprimées ; types de ligne devinés (en-tête, titre, total, article).
6. **Édition** — un champ invisible par case. Toucher : il s'affiche. Modifier : il masque l'original dans sa case et montre le nouveau texte en noir, comme l'original, police réduite si besoin pour ne jamais sortir de la case. Entrée : case suivante de la colonne.
7. **Export .txt** — une ligne par ligne imprimée, tabulations entre colonnes, titres seuls (leurs valeurs descendent sur la première ligne d'article), totaux comme les autres lignes, nombres sans espace de milliers, ligne vide entre deux tableaux, UTF-8 avec BOM, fins de ligne Windows.
8. **Sauvegarde** — `.inventaire` = zip (`projet.json` + fichiers sources tels qu'importés). Enregistrement en continu : chaque modification est écrite une seconde après la frappe (et à la fermeture), dans le fichier projet choisi au premier « Enregistrer » et dans une copie de secours rechargée au lancement.

## Version iPad (dossier `web/`)

Application web installable (« Sur l'écran d'accueil »), sans étape de compilation, publiée sur GitHub Pages (branche `gh-pages`) à chaque envoi sur `main`.

- **Cœur** : portage fidèle du cœur C# en JavaScript (`web/core/`). Sur les 3 pages de test, le .txt produit est identique octet pour octet à celui de Windows (vérifié à chaque compilation).
- **Lecture du texte** : Tesseract.js (Apache-2.0), moteur et données françaises embarqués. **PDF** : pdf.js (Apache-2.0). **Zip / XLSX / .inventaire** : fflate (MIT).
- **Hors ligne** : un service worker met toute l'application en cache à la première ouverture (~15 Mo).
- **Stockage** : IndexedDB de l'iPad, enregistrement continu une seconde après chaque frappe. Format `.inventaire` identique à Windows (copie de sauvegarde échangeable).
- **Exports** : JPEG (photo d'origine, seules les cases modifiées changent) et .txt, remis via le menu Partager de l'iPad.
- **Mémoire (iPhone/iPad)** : une seule page à l'écran ; les images des 3 dernières pages vues sont gardées, les autres recalculées à la demande ; chaque canvas est vidé après usage (sur iOS la mémoire des images n'est rendue qu'ainsi — c'était la cause des pages blanches).

## Rangée qui s'agrandit (`Model/PageLayout.cs`, `web/core/layout.js`)

- Désignation à taille constante, coupée en lignes (`TextWrap`, même règle des deux côtés). Hauteur manquante → `PrintedLine.Extra`, bande insérée à `ExtraAt` (milieu de l'interligne sous le texte d'origine).
- `SetExtra` décale tout ce qui est sous la bande (cases, encre, filets `RowYs`, rangées, tableaux, zones verrouillées, autres bandes) et allonge la page ; revenir à 0 rend exactement la géométrie d'origine.
- `Stretch` produit l'image agrandie depuis l'image redressée d'origine (hauteur `BaseHeight`) : dans le tableau, pixels sombres de l'interligne (filets verticaux) prolongés, papier ailleurs. Identique à l'octet entre Windows et iPhone/iPad (vérifié avec la colonne des lots).
- Export JPEG d'une page agrandie : la page redressée (plus de recomposition dans la photo d'origine).

## Texte barré (`Model/Strikes.cs`, `Model/TextPlacement.cs`, `web/core/strikes.js`)

- `Field.Strikes` : morceaux [Start, Start+Length[ du texte avec la couleur du trait (rouge, noir). `Toggle` barre ou débarre la sélection ; `Adjust` fait suivre les traits quand le texte change (préfixe/suffixe communs).
- Position : texte non retouché → sur l'encre du scan (abscisses au prorata des largeurs mesurées) ; texte retapé → sur les lignes placées par `TextPlacement.Layout`. Trait à 0,28 taille au-dessus de la ligne de base, épaisseur 6 % de la taille.
- Écran : calque de traits au-dessus des champs. Export : traits dessinés, case recopiée dans la photo d'origine. Le .txt n'est pas touché.

## Numéros de lot (`Model/Lots.cs`, `web/core/lots.js`)

- Objet = première ligne d'article de chaque rangée (celle qui porte des valeurs si plusieurs). Titres, totaux, en-têtes, lignes ignorées : pas de numéro.
- `Lot` et `LotManual` sont enregistrés sur la ligne. Un numéro tapé est « manuel » ; `Resync` garde les manuels et numérote les autres à la suite, toutes pages confondues. Appelé après chaque saisie de lot, changement de type, ajout/retrait/recadrage de page, et quand une saisie fait apparaître ou disparaître un objet.
- Lot 0 → objet absent/barré : ne consomme pas de numéro ; le .txt le regroupe (avec sa suite) en fin d'inventaire.
- Poubelle → `NoLot` (la ligne n'est plus un objet) ; « + » → `ForceLot` (objet en plus, même sur une ligne de suite). `Resync` (bouton « Synchroniser ») garde les numéros tapés à la main.
- Export .txt : 1re colonne « Lot » sur toutes les lignes quand des lots existent (« Lot » sur l'en-tête, vide sur titres/totaux).
- Colonne tracée (`Export/LotColumn.cs`, `web/core/lotcolumn.js`) : pour chaque tableau à grille qui porte des lots, une colonne de largeur `ColumnWidth` (3 chiffres dans la taille du texte, limitée par la marge) collée à son filet gauche. Épaisseur et couleur des filets mesurées sur l'image (cœur des traits), fond de chaque case recopié depuis une partie sans texte de sa rangée. Calcul en entiers : même image à l'octet près sur les deux versions (vérifié à chaque compilation). Affichée à l'écran et à l'export ; numéros écrits par-dessus, centrés, en Times noir. La zone de la colonne est recopiée dans la photo d'origine comme une case modifiée. Réglage `LotFrame` du projet.
