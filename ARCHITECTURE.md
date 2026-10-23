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
tests/                         21 tests sur les pages fournies
```

## L'écran (unique)

- **Barre d'outils** (boutons de 64 px) : Importer · Recadrer · Verrouiller · Type de ligne · Rétablir · Exporter .txt · Enregistrer · Ouvrir · Nouveau.
- **Pages** l'une sous l'autre, zoom au pincement. Chaque case de chaque ligne est un champ posé à sa position exacte.
- **Barre d'état** : ce qui se passe, la ligne sélectionnée et son type, le texte d'origine d'une case modifiée.
- **Recadrage** (par-dessus l'écran, à la demande) : 4 poignées, Pivoter, Détection auto, Page entière, Valider.

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

1. **Import** — photo/scan : orientation EXIF appliquée ; PDF : chaque page rendue en image ; TXT/CSV/XLSX : tableau reconstruit directement (pas d'OCR).
2. **Redressement** — coins de la feuille trouvés automatiquement (zone claire sur fond sombre), perspective corrigée, puis inclinaison résiduelle mesurée et corrigée, en un seul rééchantillonnage. Si la page paraît illisible, les autres quarts de tour sont essayés.
3. **Grille** — détection des filets horizontaux et verticaux (tolère traits fins, ondulés, interrompus).
4. **OCR** — sur une copie préparée : filets effacés, seuil adaptatif (fonds tramés, ombres), agrandie ×2. Puis **seconde lecture** case par case des cases vides qui contiennent de l'encre (nombres isolés que l'OCR de page oublie).
5. **Analyse** — mots → cases → lignes imprimées ; types de ligne devinés (en-tête, titre, total, article).
6. **Édition** — un champ invisible par case. Toucher : il s'affiche. Modifier : il masque l'original dans sa case et montre le nouveau texte en noir, comme l'original, police réduite si besoin pour ne jamais sortir de la case. Entrée : case suivante de la colonne.
7. **Export .txt** — une ligne par ligne imprimée, tabulations entre colonnes, titres seuls (leurs valeurs descendent sur la première ligne d'article), totaux comme les autres lignes, nombres sans espace de milliers, ligne vide entre deux tableaux, UTF-8 avec BOM, fins de ligne Windows.
8. **Sauvegarde** — `.inventaire` = zip (`projet.json` + fichiers sources tels qu'importés). Enregistrement en continu : chaque modification est écrite une seconde après la frappe (et à la fermeture), dans le fichier projet choisi au premier « Enregistrer » et dans une copie de secours rechargée au lancement.
