# Retouche d'inventaire — étape 1

Application Windows pour tablette Surface, au doigt, **sans Internet** : on importe un inventaire imprimé (photo, PDF, TXT, CSV, XLSX), on corrige le texte **directement sur la page sans que rien ne bouge**, et on exporte un **fichier texte** que n'importe quel logiciel sait rouvrir.

- `ARCHITECTURE.md` — choix techniques, écran, modèle de données, flux import → édition → export.
- `LIMITES.md` — limites connues (photos floues, ombres, tableaux sans lignes…).
- `docs/export-exemple.txt` — le .txt obtenu avec tes 3 pages (OCR de test, voir plus bas).
- `docs/apercu-edition.png` — le principe d'édition sur la page (simulation).

## Utilisation

1. **Importer** une ou plusieurs photos, PDF, TXT, CSV ou XLSX. Les photos sont redressées automatiquement.
2. Si une photo est mal cadrée : toucher la page, puis **Recadrer** (4 poignées).
3. **Toucher une case** : elle s'ouvre avec ce qui a été lu, et l'original s'affiche en loupe juste au-dessus. Taper la correction. **Entrée** passe à la case suivante de la colonne. **Rétablir** remet le texte d'origine.
4. Vérifier les badges à gauche des lignes : **T** titre, **Σ** total, **E** en-tête, **×** non exportée. Pour corriger : toucher la ligne, puis **Type de ligne**.
5. **Verrouiller** : tracer un rectangle au doigt sur une signature ou un tampon (le texte hors tableau est déjà verrouillé). Toucher une zone orange la retire.
6. **Exporter JPEG** : pour chaque photo (ou page de PDF), une image « nom - modifié.jpg » identique à l'original, sauf les cases modifiées.
7. **Exporter .txt** : le texte en tableau (lignes, tabulations). Il est construit à partir de ce que la reconnaissance a lu : vérifier les lignes non corrigées.
8. **Enregistrer** une fois pour choisir le fichier projet : ensuite tout est enregistré en continu.

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

21 tests sur tes 3 pages, dont les cas demandés :
- le .txt est en UTF-8 avec BOM, fins de ligne Windows, tabulations, sans caractère parasite, et se relit comme un tableau ;
- modifier une ligne de titre (« SALLE LAVAGE : » → « SALLE DE BAIN : »), un chiffre (10 → 30) et un texte ne change la position ni la taille d'aucun champ ;
- titres seuls, valeurs rattachées à l'article, une ligne par ligne imprimée, totaux, nombres sans espace, numéro de page non exporté ;
- redressement d'une photo simulée prise de travers, sauvegarde/reprise, imports CSV/XLSX, verrouillage, tableau sans filets.

Hors Windows, l'OCR est simulé avec Tesseract : les mots reconnus sont enregistrés dans `tests/fixtures/*.words.tsv` (régénérables avec `tools/generer_fixtures.py`). Les erreurs de lecture visibles dans `docs/export-exemple.txt` (« souffiants », « S 500 »…) viennent de cet OCR de test, pas de la structure.
