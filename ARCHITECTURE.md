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
tests/                         51 tests sur les pages fournies (+ 40 côté iPad, dont la comparaison avec Windows)
```

## L'écran (unique)

Tout est en haut, rien en bas (Surface, iPad, iPhone) :

- **Barre du haut** : projet (nouveau, ouvrir, enregistrer, renommer) · ↶ · ◀ 3 / 10 ▶ · zoom − ⟷ + · enregistrement.
- **Outils** (gros boutons) : Photo · Importer · Lots · Modifier (renommer, pages, synchroniser, retirer la page) · Recadrer · Verrouiller · Exporter.
- **Case touchée** : texte d'origine ou montant calculé · Ligne (dupliquer, séparer ici ; aussi au clic droit / appui long sur la case) · Barrer · couleur du trait · Couleur du texte · Type de ligne · Rétablir · Recalculer (totaux).
- **Ligne d'état** fine et progression.
- **La page**, une seule à la fois, zoom au pincement ; à sa gauche, hors de la page : poignées ⋮⋮, badges (**T**, **Σ**, **E**, **×**), poubelle / « + » des lots.
- **Recadrage** (par-dessus l'écran, à la demande) : 4 poignées, Pivoter, Détection auto, Page entière, Valider.

## Modèle de données

```
InventoryProject
└─ Page                 source (image ou données), recadrage (4 coins), inclinaison, quarts de tour, zones verrouillées
   │                    Inserts : bandes insérées pour les rangées ajoutées (Id, Kind Copy | Split, At, Height, Parts)
   ├─ Table             bords de colonnes et de rangées (pixels de page)
   │  │                 RowOrder : ordre d'affichage des rangées (rangées déplacées à la main)
   │  └─ Row            entre deux filets horizontaux ; Inserted : bande de la rangée ajoutée (null : imprimée)
   │     └─ PrintedLine type : Article | Titre | Total | EnTete | Ignoree ; Copy : ligne dupliquée
   │        └─ Field    une case de la ligne : Text, OriginalText, Ink (encre d'origine),
   │                    Box (zone d'édition, jamais hors de la case), alignement, retrait, taille de police, Locked,
   │                    Strikes (traits), Colors (couleurs), Manual (total tapé à la main)
   └─ LooseLines        texte hors tableau (n° de page, signature, tampon) : verrouillé, non exporté
```

La géométrie est figée à l'analyse ; l'édition ne change que `Text`. C'est ce qui garantit que rien ne bouge (vérifié par un test). Seules exceptions, explicites : la rangée qui s'agrandit et les rangées ajoutées, qui décalent d'un bloc tout ce qui est en dessous (et reviennent exactement à l'origine quand on les défait, vérifié par des tests).

## Flux import → édition → export texte

1. **Import** — photo/scan : orientation EXIF appliquée ; PDF : pages rendues une par une en image (une seule en mémoire), chaque page analysée est enregistrée aussitôt ; TXT/CSV/XLSX : tableau reconstruit directement (pas d'OCR).
2. **Redressement** — coins de la feuille trouvés automatiquement (zone claire sur fond sombre), perspective corrigée, puis inclinaison résiduelle mesurée et corrigée, en un seul rééchantillonnage. Si la page paraît illisible, les autres quarts de tour sont essayés.
3. **Grille** — détection des filets horizontaux et verticaux (tolère traits fins, ondulés, interrompus). Une suite de rangées plus fines que 70 % de la rangée médiane (trame d'un en-tête ou d'un total lue comme des filets serrés) ne fait qu'une rangée (`MergeThinRows`, appliqué aussi à l'ouverture des projets existants : `Upgrade` / `upgradePage`).
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

## Lots page par page, tableau par tableau (`Model/Lots.cs`, `web/core/lots.js`)

- `Table.LotsOff` : tableau sans lots. `ObjectLines` saute ces tableaux : cases, colonne tracée, « Synchroniser », .txt suivent sans autre changement. `SetTables` applique le choix (fenêtre « Lots : quelles pages ? ») puis `Resync`.
- Proposition : `TableHasObjects` (≥ 2 colonnes de montants, pas un récapitulatif, au moins un objet). Objet d'une rangée (`PickOf`) : la règle (jamais dans une rangée d'en-tête ou de total ; pas une ligne illisible sans montant), sinon la 1re ligne d'une rangée ajoutée ou séparée (même vide).

## Cadrage et recadrage (`Imaging/PageDetector.cs`, `web/core/imaging.js`, `PageEdits.CarryOver`)

- Le papier touche les quatre bords (≥ 90 % clair à 2 px du bord de l'image réduite) → page entière (scan, PDF).
- Recadrer : nouvelle page relue, puis `CarryOver` reprend, ligne par ligne (même texte lu, dans l'ordre), lot, case, type choisi, corrections, traits, couleurs, totaux tapés, et `LotsOff` des tableaux.

## Rangées ajoutées : « Ligne vide », « Dupliquer la ligne », « Séparer ici » (`Model/Rows.cs`, `web/core/rows.js`)

- Une rangée ajoutée est une **vraie rangée** du tableau (`Rows`, `RowYs`, `RowOrder`) : lots, lots 0, poignées ⋮⋮, totaux, .txt et JPEG fonctionnent sans cas particulier.
- **Dupliquer** : nouvelle rangée juste sous celle de la ligne (copie de la ligne, `Copy = true`, case de lot vide). Bande insérée à `CutY` du filet suivant : filet du haut de la rangée + pixels de la ligne (de l'interligne au-dessus à celui d'en dessous) + sous le texte autant d'espace qu'au-dessus (morceau `Gap`). Les montants d'une ligne copiée ne sont pas dans le total imprimé : ils comptent en entier (`Totals`).
- **Séparer ici** : bande de 5 px (le filet du haut de la rangée, recopié) insérée au milieu de l'interligne au-dessus de la ligne ; la ligne et les suivantes forment la nouvelle rangée.
- **Ligne vide** (au-dessus ou en dessous) et **texte tapé sur plusieurs lignes** (« Séparer ici » au curseur) → rangée ajoutée **vierge** (filets seuls, `Gap`), cases vides (`Copy(blank, above)`).
- **Réduire la rangée** (`RowTrimOf`, `TrimRow`, `UntrimRow`) : bande `Trim` (hauteur négative, `Skip` rangées de l'image d'origine sautées, `Line` = Id de la 1re case de la rangée) ; `Pieces` saute ces rangées. Marges de référence : médianes des rangées d'une ligne du tableau. Faite automatiquement par l'écran avant/après séparer, ajouter, dupliquer, ligne TOTAL, déplacer une ligne.
- **Déplacer une ligne d'un bloc** (écran) : `Split` / `SplitBelow` pour l'isoler, `TrimRow`, puis `RowOrder` comme une rangée.
- `PageLayout.ShiftBelow` décale tout ce qui est sous la bande (le même code sert à la rangée qui s'agrandit) ; `RemoveAdded` défait exactement (copie supprimée ou rangée remise dans celle du dessus).
- Image : `PageLayout.Pieces` décrit la page agrandie de haut en bas (rangées de l'image d'origine, bandes insérées, bandes d'interligne, y compris une bande d'interligne à l'intérieur d'une rangée ajoutée) ; `Stretch` la dessine. Les morceaux recopiés sont des rangées entières de l'image d'origine (`Src`, coordonnées d'origine : copier une copie fonctionne) ; un morceau `Gap` prolonge les filets verticaux de la rangée la plus nette de la ligne copiée (`CleanRow`, choisie sur l'image). Entiers : même image à l'octet près sur les deux versions (vérifié à chaque compilation, avec le .txt).
- Écran : bouton « Ligne » de la case ; clic droit / appui long sur une case (Windows : en tête du menu Couper / Copier / Coller ; iPad : appui long sur une case pas encore ouverte). Au clic droit, la case réellement appuyée est retenue (la barre de la case qui apparaît décale la page sous le pointeur).

## Texte barré (`Model/Strikes.cs`, `Model/TextPlacement.cs`, `web/core/strikes.js`)

- `Field.Strikes` : morceaux [Start, Start+Length[ du texte avec la couleur du trait (rouge, noir). `Toggle` barre ou débarre la sélection ; `Adjust` fait suivre les traits quand le texte change (préfixe/suffixe communs).
- Position : texte non retouché → sur l'encre du scan (abscisses au prorata des largeurs mesurées) ; texte retapé → sur les lignes placées par `TextPlacement.Layout`. Trait à 0,28 taille au-dessus de la ligne de base, épaisseur 6 % de la taille.
- Écran : calque de traits au-dessus des champs. Export : traits dessinés, case recopiée dans la photo d'origine. Le .txt n'est pas touché.

## Numéros de lot (`Model/Lots.cs`, `web/core/lots.js`)

- Objet = première ligne d'article de chaque rangée (celle qui porte des valeurs si plusieurs). Titres, totaux, en-têtes, récapitulatif (« II - STOCK »), lignes ignorées : pas de numéro. Ordre = ordre d'affichage (`RowOrder`).
- `Lot`, `LotManual`, `LotSlot` (case affichée) sur la ligne. `CreateEmpty` (bouton Lots) : une case vide par objet. `Set` ne change que la case tapée ; `Resync` ne fait que placer les cases. `FillAutomatically` (« Synchroniser », toutes les pages ou les pages données) ne remplit que les cases vides : numéro du lot précédent + 1, et décale d'un cran la suite contiguë qui commençait à ce numéro ; rien d'autre ne change ; `Number` renumérote tout (sauf les 0).
- Lot 0 → objet absent : ne consomme pas de numéro ; voir « Mise en page » ci-dessous ; le .txt le place après la dernière ligne d'objet numéroté.
- Poubelle → `NoLot` (la ligne n'est plus un objet) ; « + » → `ForceLot` (objet en plus, même sur une ligne de suite).
- Export .txt : 1re colonne « Lot » sur toutes les lignes quand des lots existent (« Lot » sur l'en-tête, vide sur titres/totaux).
- Colonne tracée (`Export/LotColumn.cs`, `web/core/lotcolumn.js`) : pour chaque tableau à grille qui porte des lots, une colonne de largeur `ColumnWidth` (3 chiffres dans la taille du texte, limitée par la marge) collée à son filet gauche, sur les rangées `FramedRows` (de l'en-tête au dernier objet). Chaque rangée porte ses traits (elle peut être déplacée avec eux). Épaisseur et couleur des filets mesurées sur l'image (cœur des traits), fond de chaque case recopié depuis le morceau le plus clair de sa rangée. Calcul en entiers : même image à l'octet près sur les deux versions (vérifié à chaque compilation). Affichée à l'écran et à l'export ; numéros écrits par-dessus, centrés, en Times noir. La zone de la colonne est recopiée dans la photo d'origine comme une case modifiée. Réglage `LotFrame` du projet.

## Mise en page : lots 0 et rangées déplacées (`Export/Arrangement.cs`, `web/core/arrange.js`)

- Rien n'est modifié dans le projet : chaque page affichée est recomposée (`Build` → plan de morceaux). Une page = suite de morceaux recopiés de l'image d'origine (`[Y0, Y1[` → `Dst`) et de rangées refaites.
- Rangées : bandes `[R(RowYs[r]) − 2, R(RowYs[r+1]) − 2[` (filet du haut compris) recopiées dans l'ordre `RowOrder`.
- `MovedUnits` : ce qui quitte sa place, `zero` (lot 0) ou `deleted` (ligne `Deleted` et sa suite sans montant, jusqu'au prochain objet ; `DeletedLines` les retire aussi du .txt, des totaux et des lots).
- Lots de fin (`EndLots.Mode = "page"`) : pages `Page.EndLots` (sans image propre) composées sur la page modèle (`EndTemplate` : dernière page à lots avant elles) : papier jusqu'au 1er filet, lots 0 en rangées du tableau modèle, filet du bas, papier jusqu'à la hauteur du modèle (`PagePlan.Template`, morceaux `Paper`). Remplies jusqu'à la hauteur du tableau modèle ; `SyncEndPages` crée/retire les pages (après `AfterId`) à chaque mise en page.
- Destination de chaque lot 0 (`UnitDestination`) : `PrintedLine.EndAt` (endroit choisi à la poignée : après la rangée de cette case) ; sinon après le dernier objet numéroté avant la ligne TOTAL qui suit (`LooksLikeTotal` : Kind Total ou « TOTAL » mal lu) ; sinon après le dernier lot. Le .txt place les lignes au même endroit.
- Lot 0 : l'objet, ses lignes de suite et, s'il les reprend (comme dans le .txt), les valeurs de son titre. Rangée entière si elle ne contient que lui ; sinon bande entre les interlignes. Retiré de sa page, recollé après la rangée du dernier objet numéroté : filet du haut et fond (interligne le plus propre, filets verticaux prolongés) pris dans cette rangée, contenu de chaque case recopié et recalé sur sa colonne (décalage selon l'alignement), case du lot comprise ; valeurs du titre posées à la hauteur de l'objet et effacées (papier) sous le titre.
- `ViewOf` : où afficher chaque case (décalage par case, découpe à sa colonne), cases de lot, rangées à l'écran (poignées). L'écran pose les champs à ces places ; l'export dessine les corrections sur chaque page d'origine puis recompose.
- Calcul en entiers : pages recomposées identiques à l'octet près sur Windows et iPhone/iPad (vérifié à chaque compilation, avec le .txt et les totaux).

## Export PDF (`Export/PdfWriter.cs`, `web/core/pdf.js`)

- Une page PDF par page exportée : le JPEG de l'export JPEG mis tel quel (DCTDecode), largeur A4 (595 pt), hauteur au prorata. Sans bibliothèque ; même fichier à l'octet près sur Windows et iPad (vérifié à chaque compilation).

## Totaux (`Model/Totals.cs`, `web/core/totals.js`)

- Case calculée : montants d'une ligne Total ou d'une ligne de récapitulatif. `Field.Manual` : tapée à la main, n'est plus recalculée.
- Total = montant imprimé + Σ (valeur actuelle − valeur lue) des lignes de sa rubrique (de l'en-tête ou du total précédent jusqu'à lui, toutes pages) ; lots 0 (et valeurs de titre qu'ils emportent) retirés. Montant imprimé illisible : somme des valeurs.
- Ligne TOTAL ajoutée (`PrintedLine.TotalScope` : `above`, `page`, `pages` + Ids, `all` ; `Auto`) : somme des articles et titres de la portée, recalculée à chaque modification ; ignorée par les autres totaux.
- Récapitulatif : relié à la rubrique de même nom (sans numéro romain ni accents) ; suit l'écart de son total (ou de ses lignes). TOTAL GENERAL : total des lignes de récapitulatif au-dessus de lui.
- « 15 000 » ↔ 1500000 centimes ; « mémoire » = 0 ; « S/O/l » lus comme 5/0/1 dans un montant.

## Couleur du texte (`Model/Strikes.cs`, `web/core/strikes.js`)

- `Field.Colors` : morceaux [Start, Start+Length[ en rouge, bleu ou vert ; suivent la saisie comme les traits.
- Texte non retouché : l'encre du morceau prend la couleur sur l'image (chaque canal garde le plus clair de l'encre et de la couleur). Texte retapé : écrit morceau par morceau en couleur (écran : texte par-dessus le champ quand on n'y tape pas ; export : Win2D / canvas).

## Garde-fou et banc d'essai

- Windows : chaque action (lignes, lots, lots 0, déplacements, totaux, retirer, synchroniser, cases de lot ajoutées/retirées, rangées déplacées) passe par `GuardAsync` : instantané JSON au début ; en cas d'erreur, `RestoreAsync` remet le projet, l'historique est coupé à son niveau d'avant, `_busy` est relâché, l'erreur est notée dans `erreurs.log`. `App.UnhandledException` rattrape le reste (l'app ne se ferme pas). iPad : `run()` revient à l'état d'avant si l'action avait déjà mémorisé un retour.
- `web/tests/stress.mjs` génère et applique des manipulations reproductibles et contrôle la mise en page après chaque pas ; `tests/.../Stress.cs` rejoue la même liste (`ops.json`) avec le cœur C# ; `stress-compare.mjs` exige le même .txt et les mêmes pages au pixel près.
- Correctifs trouvés par le banc : `partsOf` copie aussi les morceaux ajoutés (rangée ajoutée copiée depuis une rangée ajoutée), garde `splitOk` (pas de trait à travers des lignes qui se chevauchent), `cleanGap` limité à la rangée, `lineBand` qui contient toute la ligne, mesure des traits de la colonne des lots en coordonnées d'origine (`baseToPage`) puis gardée dans `Table.Rules`, pas de réduction automatique d'une rangée avec texte tapé sur plusieurs lignes.
