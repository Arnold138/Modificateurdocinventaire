# Retouche d'inventaire — étape 1

Application Windows pour tablette Surface, au doigt, **sans Internet** : on importe un inventaire imprimé (photo, PDF, TXT, CSV, XLSX), on corrige le texte **directement sur la page sans que rien ne bouge**, et on exporte un **fichier texte** que n'importe quel logiciel sait rouvrir.

- `ARCHITECTURE.md` — choix techniques, écran, modèle de données, flux import → édition → export.
- `LIMITES.md` — limites connues (photos floues, ombres, tableaux sans lignes…).
- `docs/export-exemple.txt` — le .txt obtenu avec tes 3 pages (OCR de test, voir plus bas).
- `docs/apercu-edition.png` — le principe d'édition sur la page (simulation).

## Utilisation

L'écran est le même sur la Surface, l'iPad et l'iPhone, et **tout est en haut** (rien en bas) : le projet, ↶, les pages (◀ 3 / 10 ▶) et le zoom (− ⟷ +) ; dessous les outils (Photo, Importer, Lots, Mise en page, Recadrer, Verrouiller, Exporter) ; quand une case est touchée, les outils de la case (Ligne, Barrer, Couleur, Type, Rétablir, Recalculer).

1. **Photo** (caméra de l'appareil) ou **Importer** une ou plusieurs photos, PDF, TXT, CSV, XLSX, un projet `.inventaire`, **un dossier entier** ou un **.zip** (pris dans l'ordre des noms : page 2 avant page 10). Les photos sont redressées automatiquement. Une barre de progression indique la page en cours, et chaque page est enregistrée dès qu'elle est lue.
2. **Changer de page** : ◀ ▶ en haut, ou toucher « 3 / 10 » pour la liste des pages. Une seule page est affichée à la fois.
3. Si une photo est mal cadrée : **Recadrer** (4 poignées) — agit sur la page affichée seulement. Les numéros de lot, les types de ligne et les corrections des lignes retrouvées sont gardés (les lignes ajoutées sont perdues). Un scan ou un PDF (le papier touche les bords de l'image) est gardé en page entière.
4. **Toucher une case** : elle s'ouvre avec ce qui a été lu, et l'original s'affiche en loupe juste au-dessus. Dans la désignation, le texte garde **la taille de l'imprimé** et passe à la ligne ; **Entrée** crée une nouvelle ligne dans la case ; la rangée s'agrandit et tout ce qui est en dessous descend. Dans les colonnes de valeurs, **Entrée** passe à la case suivante. Sur les totaux et en-têtes, le texte retapé est en gras et garde le fond gris tramé de la rangée.
5. **Barrer** et **Couleur** (en haut, case touchée) : sélectionner un morceau de texte (ou rien pour toute la case). **Barrer** trace un trait fin au milieu des lettres (le rond à côté : trait **rouge** ou **noir**) ; **Couleur** met le texte en **rouge**, **bleu** ou **vert** (**noir** = enlever). Sur l'écriture d'origine, l'encre prend la couleur ; sur le texte retapé, il est écrit en couleur. Toucher à nouveau enlève. Le .txt garde le texte tel quel.
6. **Type de ligne** (Article, Titre, Total, En-tête, Non exportée), **Rétablir** (texte d'origine). Badges à gauche : **T** titre, **Σ** total, **E** en-tête, **×** non exportée.
7. **Lots** :
   - Le bouton **Lots** demande d'abord **quelles pages** reçoivent des numéros (une case à cocher par page, et par tableau quand la page en a plusieurs) : les pages avec un tableau d'objets sont proposées, pas la page de garde, un formulaire, un récapitulatif ou une signature. **OK** crée la colonne sur ces pages seulement, **une case vide par objet** (pas sur les totaux, en-têtes ni le récapitulatif « I - … II - … TOTAL GENERAL »). On tape les numéros soi-même : **un numéro tapé ne change jamais les autres**. Pour changer de pages ensuite : **Lots › Pages et tableaux…** (les numéros déjà tapés restent).
   - **Synchroniser…** (au-dessus de la page, dans **Lots** ou **Mise en page**) : **cette page** ou **toutes les pages**. Remplit seulement les **cases vides** : les numéros déjà là (tapés ou remplis avant) **ne bougent pas** ; une case vide prend le numéro qui suit le lot précédent et décale d'un cran les lots qui la suivaient directement (6, 7, 8 → 7, 8, 9) ; un ordre fait à la main (30, 29, 6…) est gardé. Les numéros en double sont signalés. **Renuméroter tout** (Lots, avec confirmation) refait toute la numérotation dans l'ordre affiché. Une case qui manque sur un objet : clic droit › **Ajouter une case de lot**. Aussi dans **Lots** : Renuméroter tout, Vider les cases, Enlever la colonne, colonne tracée ou non.
   - La colonne est **tracée dans le tableau** (trait à gauche, filets prolongés, même fond que la rangée), seulement de l'en-tête au dernier objet : rien sur les totaux ni en dessous.
   - **0** (ou clic droit › **Mettre à la fin (lot 0)**) = objet absent : la 1re fois, l'app demande **« Mettre ce lot à la fin ? »** et où : **à la fin de sa partie**, après le dernier lot et **juste avant la ligne TOTAL** qui la ferme (ex. « TOTAL MOBILIER ET MATERIEL », même mal lue sur fond tramé ; même sur une autre page, dans une rangée refaite aux colonnes du tableau d'arrivée ; sans ligne TOTAL après lui : après le dernier lot du document), puis, si l'endroit ne convient pas, **Modifier** : sa **poignée orange ⋮⋮** le glisse où on veut (l'endroit est gardé, aussi dans le .txt), ou sur une **page « Lots de fin »** insérée **après la page choisie** (les pages suivantes sont renumérotées ; même mise en page que la vente ; une nouvelle page s'ajoute quand elle est pleine, elles disparaissent quand il n'y a plus de lot 0). Les lots 0 suivants y vont tout seuls (changer : **Lots › Lots de fin (lot 0)…**). Le message en bas dit **exactement où** la ligne est partie (« page 8 « Lots de fin », 2e ligne »). Si l'objet partage sa rangée avec un titre (« VESTIAIRE HOMME : 80 / 40 » puis « 2 vestiaires… »), il part seul avec les valeurs ; le titre reste à sa place. **Retaper un numéro (ou clic droit › Remettre à sa place) le remet à sa place.** Pareil sur le JPEG et dans le .txt ; ses montants sont retirés des totaux.
   - **Lots › Comment ça marche ?** : l'aide pas à pas.
   - **Modifier** (en haut) : les poignées **⋮⋮** apparaissent tout de suite à gauche de chaque ligne (la poubelle et le « + » se cachent pendant ce temps) ; glisser la rangée pour la monter ou la descendre dans son tableau (écran, JPEG, .txt). Dans un bloc de plusieurs lignes, chaque ligne a sa poignée (en pointillés sur iPad, blanche sur Windows) : la ligne glissée devient une rangée à part, posée où on la lâche.
   - **Poubelle** : ce n'est pas un objet, sa case disparaît (les autres numéros ne bougent pas). **+** : ajouter une case.
8. **Ligne** (en haut quand une case est touchée, ou **clic droit** / **appui long** sur la case — sur PC, en tête du menu Couper / Copier / Coller) :
   - **Ajouter des lignes… (1, 5, 10, 15)** : plusieurs lignes vides d'un coup, sous (ou sur) la ligne touchée, même hauteur et mêmes traits que les lignes du tableau ; la page garde sa taille (le blanc du bas est pris) et, si elle est pleine, la suite part sur une page blanche juste après. La case de lot apparaît quand on écrit dans la ligne (sur une page ajoutée : seulement avec le « + » à gauche). Possible sur **toute ligne** du tableau (même vide, un titre ou un total), et **au-dessus de la 1re ligne** (sauf au-dessus de l'en-tête).
   - **Ajouter une ligne vide en dessous** / **au-dessus** : une rangée complète, comme les autres (traits du tableau, case de lot vide, désignation et prix vides), juste sous (ou sur) la rangée touchée ; tout ce qui suit descend. Taper la désignation et les prix ; le numéro à la main ou **Synchroniser**.
   - **Dupliquer la ligne** : une copie de la ligne (texte et prix, telle qu'imprimée) est ajoutée **juste en dessous**, dans une vraie rangée du tableau ; tout ce qui suit descend. On corrige ce qui change (« 2 doseuses » → « 1 doseuse », le prix de chacune). Sa case de lot est vide : taper le numéro, ou **Synchroniser**. Ses montants s'ajoutent aux totaux.
   - **Grande case avec plusieurs objets** (ex. « Appartient à PRINT O7 » … « 1 copieur KONICA ») : **Ajouter une case de lot ici** (clic droit sur la ligne, ou « + » à gauche) met une case **à la hauteur de cette ligne** ; on tape le numéro, **aucun autre ne bouge**. Ça marche aussi dans un tableau qui n'avait pas de lots (seule cette ligne reçoit une case). Ou **Couper ici** : un trait au-dessus de la ligne, elle devient une rangée à part.
   - **Colonne des lots et totaux** : la colonne s'arrête au dernier objet ; une rangée de **total** entre deux objets n'est pas tracée. Clic droit sur une ligne sans case : **Ne pas tracer / Tracer la colonne des lots ici**. ↶ annule.
   - **Document déjà numéroté** (ex. PDF sorti du logiciel puis réimporté) : à la création des cases (bouton **Lots** ou **Synchroniser…**), les numéros imprimés dans la marge sont **lus et gardés** ; un numéro mal lu laisse sa case vide, **Synchroniser** la remplit à la suite. Le bouton **Synchroniser…** est toujours à côté de « Retirer… », même avant d'avoir créé les lots.
   - **Trier par numéro de lot** (Lots) : les lignes s'affichent du plus petit numéro au plus grand sur tout le document (les « 1 » ensemble, puis les « 2 »…, les codes en lettres comme VGG ou « ? » groupés à la fin), en déplaçant le moins de lignes possible ; le .txt suit le même ordre. « Remettre dans l'ordre du document » revient en arrière. Les totaux imprimés de chaque partie ne changent pas.
   - **Déplacer des lots** (Lots, ou clic droit sur une ligne) : cocher un ou plusieurs lots (recherche par numéro ou mot), choisir la page et l'endroit (**début**, **fin**, **après un lot**) ; ils s'y affichent, le reste ne bouge pas (marche aussi avec le tri). **Remettre à leur place** annule.
   - **Lecture corrigée** : « I trémie » (1 lu comme I ou l) devient « 1 trémie », « +1- » devient « +/- » ; la désignation d'un article retapée reste à gauche.
   - **Un seul « + »** (dans la marge) pour ajouter une case de lot : le curseur est mis dans la case, on tape le numéro. **Cliquer dans la colonne des lots vide**, à la hauteur d'une ligne, fait pareil.
   - **Export** : le texte retapé est écrit comme l'imprimé de la page (même hauteur de lettres, même épaisseur de trait, même noir d'encre, mesurés sur le scan).
   - **Lignes et pages ajoutées** : le **« + » bleu à gauche** met une case de lot sur la ligne, **même vide** (on tape la désignation après). Sur une page ajoutée, **aucune case ne vient toute seule** : on peut n'écrire qu'un titre, sans case ni colonne.
   - **Case de lot (clic droit sur la case ou sur la ligne)** : **Numéro de lot en haut / au milieu / en bas** (grande case) et **Retirer la case de lot**. « Ajouter une case de lot ici » sur une ligne d'une grande case : **petite case fermée à la hauteur de cette ligne seulement**.
   - **Cadres toujours fermés** : une rangée déplacée ou un lot 0 parti ne laisse pas de trait qui dépasse ; la colonne garde son cadre complet. Un **tableau ouvert en bas** (il continue page suivante) est **fermé par un trait** sous sa dernière ligne.
   - **Couper ici (trait au-dessus de cette ligne)** / **Séparer en dessous (tracer un trait)** : pour couper un gros bloc, un trait est tracé au-dessus (ou en dessous) de la ligne touchée ; chaque morceau devient une rangée à part, avec sa case de lot et ses cases de prix (en dessous : possible aussi sur la 1re ligne du bloc). Si la désignation a été tapée sur plusieurs lignes (Entrée), placer le curseur sur la ligne voulue : **Séparer ici** envoie la suite du texte dans sa propre ligne, juste en dessous.
   - **Rangée trop haute** (espace vide imprimé, comme au-dessus de « DANS L'ENTREPOT ») : quand on sépare, ajoute ou duplique, la rangée est réduite à la hauteur des autres ; elle ne grandit que si le texte tapé le demande. Au clic droit : **Réduire la rangée (enlever l'espace vide)** / **Remettre la hauteur d'origine**.
   - **Supprimer cette ligne** (après « Êtes-vous sûr ? ») : objet retiré de la vente ; la ligne (et sa suite) disparaît de la page, du .txt et des totaux, ce qui suit remonte ; les autres numéros de lot ne changent pas. Pour la retrouver : ↶ ou **Retirer… › Remettre les lignes supprimées**.
   - Sur une ligne ajoutée : **Supprimer cette ligne ajoutée** ; sur une ligne séparée : **Remettre dans la rangée du dessus**. ↶ annule aussi.
   - Rien d'autre ne bouge : les autres cases, numéros de lot et la mise en page restent tels quels. Pareil sur le JPEG et dans le .txt.
9. **Totaux** : les totaux (TOTAL STOCK…) se recalculent tout seuls quand une valeur change ou qu'un lot passe à 0 (total imprimé + écarts), ainsi que le récapitulatif (« II - STOCK ») et le TOTAL GENERAL. On peut aussi les taper à la main : ils ne bougent plus, l'écart avec le calcul s'affiche en haut, et **Recalculer** les remet en automatique. Une rangée de total que la lecture n'avait pas lue (fond gris) a quand même ses cases à toucher.
   - Bouton **Totaux** au-dessus de la page : **Recalculer les totaux de la page**, ou **Ajouter une ligne TOTAL** : une rangée du tableau (même mise en page), sous la ligne touchée ou à la fin d'un tableau, qui additionne au choix **les lignes au-dessus**, **toute la page**, **les pages cochées** ou **tout le document** ; calculée automatiquement (elle suit chaque modification) ou à taper soi-même ; **même écriture que les autres lignes**, ou **en gras** (case à cocher, aussi au clic droit). Toucher « TOTAL » pour la renommer.
10. **Ajouter une page** (au-dessus de la page, à côté de Totaux) : pour les objets inventoriés **à la main**, sur le modèle de la page affichée (mêmes colonnes, mêmes traits, mêmes en-têtes, ex. « exploitation / réalisation »). Deux choix : **Page blanche** (l'en-tête du tableau et une ligne : on ajoute les lignes qu'il faut) ou **Page remplie de lignes vides** (tous les tableaux et toutes les rangées du modèle, vides). Le numéro de page et ce qui est sous le dernier tableau sont effacés. On choisit **où la placer** : à la fin (avant « Lots de fin »), au début, ou après n'importe quelle page. Sur la page ajoutée :
   - **Ajouter des lignes** (bouton sous le tableau, ou « + » d'une ligne) : **1, 5, 10, 15** ou un nombre au choix, en dessous ou au-dessus ; chaque ligne a **les mêmes colonnes, la même hauteur et les mêmes traits** que les lignes du tableau (la rangée d'une ligne la plus courante). La page garde sa taille ; quand elle est pleine, **la suite part sur une page blanche** ajoutée juste après ;
   - toucher une case pour écrire ; une ligne écrite devient un objet (case de lot, **Synchroniser**), elle part dans le .txt et dans les totaux comme les autres ;
   - **« + » à droite de chaque ligne** : ligne vide au-dessus / en dessous, dupliquer, case de lot, **Ligne plus haute** / **moins haute**, supprimer… (le même menu que **Ligne**) ;
   - **« + » au-dessus d'une colonne** : la coupe en deux (une case vide de plus sur chaque ligne) ; **« − » sous un trait** : réunit les deux colonnes (textes mis ensemble). Le bord du tableau reste ;
   - on peut prendre une page ajoutée comme modèle d'une autre ; **Retirer…** la retire ; ↶ annule.
11. **Mise en page** : renommer le document, liste des pages, synchroniser les lots, retirer la page affichée ; cases à cocher **Loupe « Original »** (l'agrandissement au-dessus de la case touchée) et, sur iPad, **Zoom automatique de l'iPad** quand on touche une case. Réglages gardés sur l'appareil. Au-dessus de la page, **Retirer…** : cette page, des **pages au choix** (cases à cocher) ou **tout le dossier** ; et remettre les lignes supprimées.
12. **Verrouiller** : tracer un rectangle au doigt sur une signature ou un tampon (le texte hors tableau est déjà verrouillé). Toucher une zone orange la retire.
13. **Exporter** → **images JPEG** (toutes, ou seulement la page affichée) : « nom - modifié.jpg », identique à l'original sauf les cases modifiées et les numéros de lot. Une page dont des rangées ont bougé (lot 0, rangée déplacée, rangée agrandie, ligne ajoutée ou séparée) est exportée redressée. → **Document PDF** (tout le document, ou la page affichée) : une page PDF par page, telle qu'à l'écran (même rendu que le JPEG, totaux recalculés juste avant, pages « Lots de fin » comprises ; pages TXT/CSV/XLSX non comprises). → **Fichier texte (.txt)**.
14. **↶** : annule la dernière action, jusqu'à 30 fois. Sur PC, aussi Ctrl+Z en dehors d'une case.
15. **Orthographe** : dans la désignation, le correcteur de l'appareil souligne les fautes (sur iPhone/iPad, aussi « Outils d'écriture » en sélectionnant du texte). Rien n'est corrigé automatiquement.
16. Tout est enregistré en continu. Sur la Surface, menu du projet (en haut à gauche) → **Enregistrer sous…** une fois pour choisir le fichier projet.

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

60 tests sur tes pages (et 52 côté iPad, dont la comparaison octet pour octet et pixel pour pixel avec Windows), dont les cas demandés :
- le .txt est en UTF-8 avec BOM, fins de ligne Windows, tabulations, sans caractère parasite, et se relit comme un tableau ;
- modifier une ligne de titre (« SALLE LAVAGE : » → « SALLE DE BAIN : »), un chiffre (10 → 30) et un texte ne change la position ni la taille d'aucun champ ;
- titres seuls, valeurs rattachées à l'article, une ligne par ligne imprimée, totaux, nombres sans espace, numéro de page non exporté ;
- numéros de lot : un par objet, un numéro tapé ne change rien d'autre, « Synchroniser » remplit tout, poubelle et « + », présents dans le .txt ; colonne tracée seulement sur les objets, identique pixel pour pixel entre Windows et iPhone/iPad ;
- lot 0 : la ligne quitte sa page et passe après le dernier lot (avec les valeurs de son titre), puis revient ; page « Lots de fin » insérée où on veut, nouvelle page quand elle est pleine, retirée sans lot 0 ; ligne supprimée (page, .txt, totaux, lots inchangés) ; ligne TOTAL ajoutée (au-dessus, page, pages au choix, tout, à la main) ; trait tracé sous une ligne ; rangée déplacée à la main ; totaux et récapitulatif recalculés, saisie à la main, « Recalculer » ; couleur du texte ;
- redressement d'une photo simulée prise de travers, sauvegarde/reprise, imports CSV/XLSX, verrouillage, tableau sans filets.

**Banc d'essai** (`web/tests/stress.mjs` + `stress-replay` côté Windows) : des centaines de manipulations au hasard mais reproductibles (graine), comme à l'écran — lignes vides, dupliquées, séparées, texte tapé sur plusieurs lignes, mots ajoutés et enlevés, montants, lots, lots 0, synchroniser, rangées et lignes déplacées, lignes TOTAL. Après **chaque** pas : image d'origine intacte, rangées dans l'ordre, chaque case dans sa rangée, colonne des lots continue, chaque objet a sa case, aucun mot tapé perdu, numéros inchangés hors actions de lot. Windows rejoue la même liste : le .txt et chaque page doivent être identiques au pixel près. La CI en fait 3 × 400 sur les pages de test.

```
RetoucheInventaire.Core.Tests stress-fixtures /tmp/banc
node web/tests/stress.mjs /tmp/banc/project.json /tmp/banc /tmp/ipad 400 1
RetoucheInventaire.Core.Tests stress-replay /tmp/ipad/ops.json /tmp/banc/project.json /tmp/banc /tmp/win
node web/tests/stress-compare.mjs /tmp/ipad /tmp/win
```

Hors Windows, l'OCR est simulé avec Tesseract : les mots reconnus sont enregistrés dans `tests/fixtures/*.words.tsv` (régénérables avec `tools/generer_fixtures.py`). Les erreurs de lecture visibles dans `docs/export-exemple.txt` (« souffiants », « S 500 »…) viennent de cet OCR de test, pas de la structure.
