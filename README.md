# Retouche d'inventaire — étape 1

Application Windows pour tablette Surface, au doigt, **sans Internet** : on importe un inventaire imprimé (photo, PDF, TXT, CSV, XLSX), on corrige le texte **directement sur la page sans que rien ne bouge**, et on exporte un **fichier texte** que n'importe quel logiciel sait rouvrir.

- `ARCHITECTURE.md` — choix techniques, écran, modèle de données, flux import → édition → export.
- `LIMITES.md` — limites connues (photos floues, ombres, tableaux sans lignes…).
- `docs/export-exemple.txt` — le .txt obtenu avec tes 3 pages (OCR de test, voir plus bas).
- `docs/apercu-edition.png` — le principe d'édition sur la page (simulation).

## Utilisation

L'écran est le même sur la Surface, l'iPad et l'iPhone, et **tout est en haut** (rien en bas) : le projet, ↶, les pages (◀ 3 / 10 ▶) et le zoom (− ⟷ +) ; dessous les outils (Photo, Importer, Lots, Modifier, Recadrer, Verrouiller, Exporter) ; quand une case est touchée, les outils de la case (Ligne, Barrer, Couleur, Type, Rétablir, Recalculer).

1. **Photo** (caméra de l'appareil) ou **Importer** une ou plusieurs photos, PDF, TXT, CSV, XLSX, un projet `.inventaire`, **un dossier entier** ou un **.zip** (pris dans l'ordre des noms : page 2 avant page 10). Les photos sont redressées automatiquement. Une barre de progression indique la page en cours, et chaque page est enregistrée dès qu'elle est lue.
2. **Changer de page** : ◀ ▶ en haut, ou toucher « 3 / 10 » pour la liste des pages. Une seule page est affichée à la fois.
3. Si une photo est mal cadrée : **Recadrer** (4 poignées) — agit sur la page affichée.
4. **Toucher une case** : elle s'ouvre avec ce qui a été lu, et l'original s'affiche en loupe juste au-dessus. Dans la désignation, le texte garde **la taille de l'imprimé** et passe à la ligne ; **Entrée** crée une nouvelle ligne dans la case ; la rangée s'agrandit et tout ce qui est en dessous descend. Dans les colonnes de valeurs, **Entrée** passe à la case suivante. Sur les totaux et en-têtes, le texte retapé est en gras et garde le fond gris tramé de la rangée.
5. **Barrer** et **Couleur** (en haut, case touchée) : sélectionner un morceau de texte (ou rien pour toute la case). **Barrer** trace un trait fin au milieu des lettres (le rond à côté : trait **rouge** ou **noir**) ; **Couleur** met le texte en **rouge**, **bleu** ou **vert** (**noir** = enlever). Sur l'écriture d'origine, l'encre prend la couleur ; sur le texte retapé, il est écrit en couleur. Toucher à nouveau enlève. Le .txt garde le texte tel quel.
6. **Type de ligne** (Article, Titre, Total, En-tête, Non exportée), **Rétablir** (texte d'origine). Badges à gauche : **T** titre, **Σ** total, **E** en-tête, **×** non exportée.
7. **Lots** :
   - Le bouton **Lots** crée directement la colonne, **une case vide par objet** (pas sur les totaux, en-têtes ni le récapitulatif « I - … II - … TOTAL GENERAL »). On tape les numéros soi-même : **un numéro tapé ne change jamais les autres**.
   - **Synchroniser automatiquement** (au-dessus de la page, dans **Lots** ou **Modifier**) : le logiciel remplit toutes les cases d'un coup, dans l'ordre affiché ; les numéros déjà tapés restent et la suite repart d'eux. Aussi dans **Lots** : Renuméroter tout, Vider les cases, Enlever la colonne, colonne tracée ou non.
   - La colonne est **tracée dans le tableau** (trait à gauche, filets prolongés, même fond que la rangée), seulement de l'en-tête au dernier objet : rien sur les totaux ni en dessous.
   - **0** = objet absent : sa ligne **quitte sa place tout de suite** (ce qui suit remonte) et passe **juste après le dernier lot numéroté** (après le 120 : la ligne « 0 » arrive en 121e position), même si c'est sur une autre page, dans une rangée refaite aux colonnes du tableau d'arrivée. Si l'objet partage sa rangée avec un titre (« VESTIAIRE HOMME : 80 / 40 » puis « 2 vestiaires… »), il part seul avec les valeurs ; le titre reste à sa place. **Retaper un numéro le remet à sa place.** Pareil sur le JPEG et dans le .txt ; ses montants sont retirés des totaux.
   - **⋮⋮** à gauche d'une rangée : la glisser pour la monter ou la descendre dans son tableau (écran, JPEG, .txt).
   - **Poubelle** : ce n'est pas un objet, sa case disparaît (les autres numéros ne bougent pas). **+** : ajouter une case.
8. **Ligne** (en haut quand une case est touchée, ou **clic droit** / **appui long** sur la case — sur PC, en tête du menu Couper / Copier / Coller) :
   - **Dupliquer la ligne** : une copie de la ligne (texte et prix, telle qu'imprimée) est ajoutée **juste en dessous**, dans une vraie rangée du tableau ; tout ce qui suit descend. On corrige ce qui change (« 2 doseuses » → « 1 doseuse », le prix de chacune). Sa case de lot est vide : taper le numéro, ou **Synchroniser**. Ses montants s'ajoutent aux totaux.
   - **Séparer ici** : la ligne touchée (et celles qui la suivent dans sa rangée) devient une rangée à part, avec un trait au-dessus, sa case de lot et ses cases de prix. Si la désignation a été tapée sur plusieurs lignes (Entrée), placer le curseur sur la ligne voulue : la suite du texte part dans sa propre ligne, juste en dessous.
   - Sur une ligne ajoutée : **Supprimer cette ligne ajoutée** ; sur une ligne séparée : **Remettre dans la rangée du dessus**. ↶ annule aussi.
   - Rien d'autre ne bouge : les autres cases, numéros de lot et la mise en page restent tels quels. Pareil sur le JPEG et dans le .txt.
9. **Totaux** : les totaux (TOTAL STOCK…) se recalculent tout seuls quand une valeur change ou qu'un lot passe à 0 (total imprimé + écarts), ainsi que le récapitulatif (« II - STOCK ») et le TOTAL GENERAL. On peut aussi les taper à la main : ils ne bougent plus, l'écart avec le calcul s'affiche en haut, et **Recalculer** les remet en automatique. Une rangée de total que la lecture n'avait pas lue (fond gris) a quand même ses cases à toucher.
10. **Modifier** : renommer le document, liste des pages, synchroniser les lots, retirer la page affichée.
11. **Verrouiller** : tracer un rectangle au doigt sur une signature ou un tampon (le texte hors tableau est déjà verrouillé). Toucher une zone orange la retire.
12. **Exporter** → **images JPEG** (toutes, ou seulement la page affichée) : « nom - modifié.jpg », identique à l'original sauf les cases modifiées et les numéros de lot. Une page dont des rangées ont bougé (lot 0, rangée déplacée, rangée agrandie, ligne ajoutée ou séparée) est exportée redressée. → **Fichier texte (.txt)**.
13. **↶** : annule la dernière action, jusqu'à 30 fois. Sur PC, aussi Ctrl+Z en dehors d'une case.
14. **Orthographe** : dans la désignation, le correcteur de l'appareil souligne les fautes (sur iPhone/iPad, aussi « Outils d'écriture » en sélectionnant du texte). Rien n'est corrigé automatiquement.
15. Tout est enregistré en continu. Sur la Surface, menu du projet (en haut à gauche) → **Enregistrer sous…** une fois pour choisir le fichier projet.

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

Au premier **Enregistrer**, choisir où ranger le projet (`.inventaire`). Ensuite **chaque modification est enregistrée automatiquement** dans ce fichier, une seconde après la frappe et à la fermeture ; l'heure du dernier enregistrement s'affiche en haut à droite. Avant ce premier choix, le travail est déjà gardé dans une copie de secours, rechargée au lancement suivant.

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

42 tests sur tes 3 pages (et 29 côté iPad, dont la comparaison octet pour octet et pixel pour pixel avec Windows), dont les cas demandés :
- le .txt est en UTF-8 avec BOM, fins de ligne Windows, tabulations, sans caractère parasite, et se relit comme un tableau ;
- modifier une ligne de titre (« SALLE LAVAGE : » → « SALLE DE BAIN : »), un chiffre (10 → 30) et un texte ne change la position ni la taille d'aucun champ ;
- titres seuls, valeurs rattachées à l'article, une ligne par ligne imprimée, totaux, nombres sans espace, numéro de page non exporté ;
- numéros de lot : un par objet, un numéro tapé ne change rien d'autre, « Synchroniser » remplit tout, poubelle et « + », présents dans le .txt ; colonne tracée seulement sur les objets, identique pixel pour pixel entre Windows et iPhone/iPad ;
- lot 0 : la ligne quitte sa page et passe après le dernier lot (avec les valeurs de son titre), puis revient ; rangée déplacée à la main ; totaux et récapitulatif recalculés, saisie à la main, « Recalculer » ; couleur du texte ;
- redressement d'une photo simulée prise de travers, sauvegarde/reprise, imports CSV/XLSX, verrouillage, tableau sans filets.

Hors Windows, l'OCR est simulé avec Tesseract : les mots reconnus sont enregistrés dans `tests/fixtures/*.words.tsv` (régénérables avec `tools/generer_fixtures.py`). Les erreurs de lecture visibles dans `docs/export-exemple.txt` (« souffiants », « S 500 »…) viennent de cet OCR de test, pas de la structure.
