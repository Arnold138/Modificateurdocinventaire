# Retouche d'inventaire — étape 1

Application Windows pour tablette Surface, au doigt, **sans Internet** : on importe un inventaire imprimé (photo, PDF, TXT, CSV, XLSX), on corrige le texte **directement sur la page sans que rien ne bouge**, et on exporte un **fichier texte** que n'importe quel logiciel sait rouvrir.

- `ARCHITECTURE.md` — choix techniques, écran, modèle de données, flux import → édition → export.
- `LIMITES.md` — limites connues (photos floues, ombres, tableaux sans lignes…).
- `docs/export-exemple.txt` — le .txt obtenu avec tes 3 pages (OCR de test, voir plus bas).
- `docs/apercu-edition.png` — le principe d'édition sur la page (simulation).

## Utilisation

L'écran est le même sur la Surface et sur iPhone/iPad : le projet et les pages en haut, la page au milieu, les outils en bas.

1. **Photo** (caméra de l'appareil) ou **Importer** une ou plusieurs photos, PDF, TXT, CSV, XLSX ou un projet `.inventaire`. Les photos sont redressées automatiquement. Une barre de progression indique la page en cours (« Page 3 sur 10 »), et chaque page est enregistrée dès qu'elle est lue.
2. **Changer de page** : flèches ◀ ▶ en haut, ou toucher « 3 / 10 » pour la liste des pages. Une seule page est affichée à la fois.
3. Si une photo est mal cadrée : **Recadrer** (4 poignées) — agit sur la page affichée.
4. **Toucher une case** : elle s'ouvre avec ce qui a été lu, et l'original s'affiche en loupe juste au-dessus. Taper la correction. **Entrée** passe à la case suivante de la colonne. La barre qui apparaît en bas montre le texte d'origine, le **type de ligne** (Article, Titre, Total, En-tête, Non exportée) et **Rétablir**.
5. Badges à gauche des lignes : **T** titre, **Σ** total, **E** en-tête, **×** non exportée.
6. **Lots** : **Numéroter** donne un numéro à chaque objet (1, 2, 3… à partir du nombre choisi), à la suite d'une page à l'autre ; les titres, totaux et en-têtes n'en ont pas.
   - Les numéros sont écrits **sur la page**, dans une **colonne ajoutée à gauche du tableau** et tracée comme lui : trait à gauche, filets prolongés, même fond que chaque rangée (gris hachuré des en-têtes et totaux). Rien d'autre ne bouge. Option « Tracer la colonne dans le tableau » dans le menu Lots (sinon, numéros seuls).
   - Toucher un numéro pour le changer (80 → 81) : les suivants se recalent pendant la frappe (82, 83…).
   - **Poubelle** à gauche d'une ligne : ce n'est pas un objet, son numéro disparaît et les suivants se décalent. **+** : ajouter un numéro sur une ligne oubliée.
   - **Synchroniser les lots** (au-dessus de la page, ou menu Lots) : recalcule tout ; les numéros tapés à la main sont gardés. **Enlever les numéros** les retire tous.
   - Ils sortent dans le .txt (1re colonne « Lot ») et sur le JPEG (la colonne tracée, identique à l'écran).
7. **Verrouiller** : tracer un rectangle au doigt sur une signature ou un tampon (le texte hors tableau est déjà verrouillé). Toucher une zone orange la retire.
8. **Exporter** → **images JPEG** (toutes, ou seulement la page affichée) : pour chaque photo ou page de PDF, « nom - modifié.jpg » identique à l'original, sauf les cases modifiées et les numéros de lot. → **Fichier texte (.txt)** : le texte en tableau (lignes, tabulations), construit à partir de ce que la reconnaissance a lu : vérifier les lignes non corrigées.
9. Tout est enregistré en continu. Sur la Surface, menu du projet (en haut à gauche) → **Enregistrer sous…** une fois pour choisir le fichier projet.

## Version iPad (application web, hors ligne)

Même logique que la version Windows (le .txt produit est identique), dans Safari :

1. Sur l'iPad, ouvrir **Safari** à l'adresse https://arnold138.github.io/Modificateurdocinventaire/ (avec Internet, une seule fois).
2. Bouton **Partager** (carré avec une flèche) → **Sur l'écran d'accueil** → **Ajouter**.
3. Lancer l'app depuis l'icône **« Inventaire »**. Elle fonctionne ensuite **sans Internet** ; tout reste sur l'iPad.

Fonctionne aussi sur iPhone (même adresse, même installation). Particularités : exports via le menu **Partager** (Enregistrer dans Fichiers, Enregistrer l'image, Mail, AirDrop), projets enregistrés en continu dans l'appareil (bouton du projet en haut à gauche : ouvrir, renommer, supprimer, copie de sauvegarde `.inventaire` ouvrable aussi sur la Surface). Lecture du texte : Tesseract (plus lent et un peu moins précis que l'OCR de Windows).

Code : dossier `web/` (aucune étape de compilation). Tests : `node web/tests/test.mjs`.

## Installer sur la Surface

1. Télécharger le zip qui correspond à la Surface (Paramètres › Système › Informations › *Type de système*) :
   - x64 : https://github.com/Arnold138/Modificateurdocinventaire/releases/latest/download/RetoucheInventaire-win-x64.zip
   - ARM64 : https://github.com/Arnold138/Modificateurdocinventaire/releases/latest/download/RetoucheInventaire-win-arm64.zip
2. Clic droit sur le zip → **Propriétés** → cocher **Débloquer** → OK, puis clic droit → **Extraire tout**.
3. Dans le dossier extrait, double-cliquer **Installer.cmd**. L'icône **« Retouche d'inventaire »** apparaît sur le Bureau et dans le menu Démarrer.
4. Si Windows affiche « Windows a protégé votre ordinateur » : **Informations complémentaires** → **Exécuter quand même** (l'application n'est pas signée).

Attention : le bouton vert « Code › Download ZIP » donne le code source, pas l'application.

Ensuite, plus besoin d'Internet. Pour une nouvelle version : refaire les étapes 1 à 3 (les projets ne sont pas touchés).

## Enregistrement

Au premier **Enregistrer**, choisir où ranger le projet (`.inventaire`). Ensuite **chaque modification est enregistrée automatiquement** dans ce fichier, une seconde après la frappe et à la fermeture ; l'heure du dernier enregistrement s'affiche en bas à droite. Avant ce premier choix, le travail est déjà gardé dans une copie de secours, rechargée au lancement suivant.

## Compiler

**Sans Visual Studio** : pousser ce dossier dans un dépôt GitHub. Le workflow `.github/workflows/build.yml` lance les tests puis compile l'exe sur un Windows de GitHub (onglet *Actions* → *Artifacts*) :
- `RetoucheInventaire-win-x64` pour une Surface Intel/AMD,
- `RetoucheInventaire-win-arm64` pour une Surface Snapdragon.

**Avec Visual Studio 2022/2026** (charge de travail « Développement d'applications WinUI ») et le SDK .NET 10 : ouvrir `RetoucheInventaire.sln`, choisir la plateforme x64 ou ARM64, lancer `RetoucheInventaire.App`.

**En ligne de commande** (Windows) :

```
dotnet publish src/RetoucheInventaire.App -c Release -r win-x64 -p:Platform=x64 -o publish
```

Copier le dossier `publish` sur la tablette et lancer `RetoucheInventaire.exe`. Rien à installer : .NET et le Windows App SDK sont inclus.

## Reconnaissance du texte en français

L'OCR de Windows doit avoir le français. C'est le cas sur un Windows en français ; l'application le signale au démarrage sinon. Pour vérifier ou l'ajouter (PowerShell en administrateur, connexion nécessaire une seule fois) :

```
Get-WindowsCapability -Online | Where-Object Name -like 'Language.OCR*'
Add-WindowsCapability -Online -Name "Language.OCR~~~fr-FR~0.0.1.0"
```

## Tests

```
dotnet run --project tests/RetoucheInventaire.Core.Tests -c Release
```

29 tests sur tes 3 pages, dont les cas demandés :
- le .txt est en UTF-8 avec BOM, fins de ligne Windows, tabulations, sans caractère parasite, et se relit comme un tableau ;
- modifier une ligne de titre (« SALLE LAVAGE : » → « SALLE DE BAIN : »), un chiffre (10 → 30) et un texte ne change la position ni la taille d'aucun champ ;
- titres seuls, valeurs rattachées à l'article, une ligne par ligne imprimée, totaux, nombres sans espace, numéro de page non exporté ;
- numéros de lot : un par objet, continus d'une page à l'autre, recalés après une saisie, poubelle et « + », présents dans le .txt ; colonne tracée uniquement dans la marge, identique pixel pour pixel entre Windows et iPhone/iPad ;
- redressement d'une photo simulée prise de travers, sauvegarde/reprise, imports CSV/XLSX, verrouillage, tableau sans filets.

Hors Windows, l'OCR est simulé avec Tesseract : les mots reconnus sont enregistrés dans `tests/fixtures/*.words.tsv` (régénérables avec `tools/generer_fixtures.py`). Les erreurs de lecture visibles dans `docs/export-exemple.txt` (« souffiants », « S 500 »…) viennent de cet OCR de test, pas de la structure.
