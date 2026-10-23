# Limites connues — étape 1

## Non vérifié ici (à faire en premier sur la Surface)

- **Compilation et affichage de l'interface WinUI 3** : écrits sans poste Windows. Le cœur est compilé et testé ; l'écran ne l'est pas encore. Le workflow GitHub Actions fourni compile l'exe sur un Windows de GitHub.
- **Qualité de Windows.Media.Ocr** : les tests utilisent Tesseract pour simuler l'OCR. Sur la tablette, les erreurs de lecture seront différentes (meilleures ou pires).
- **Ouverture dans Excel** : contrôlée par les tests (BOM UTF-8, tabulations, CRLF, nombres), pas dans Excel lui-même.

## Photos

- **Floues, bougées, basse résolution** : l'OCR se trompe (1 ↔ l, 5 ↔ S, 0 ↔ O) ou oublie des mots. Parade : toucher une case montre ce qui a été lu ; « Rétablir » remet le texte reconnu. Reprendre la photo si trop de cases sont fausses.
- **Ombres, éclairage inégal** : le seuil adaptatif les atténue. Une ombre qui coupe un filet peut faire perdre une rangée (deux rangées fusionnées).
- **Feuille courbée, pliée, pas à plat** : la perspective corrige un plan, pas une courbure ; des filets courbes peuvent ne pas être détectés.
- **Feuille claire sur fond clair** : les coins ne sont pas trouvés → « Recadrer » et placer les 4 poignées à la main.
- **Reflets, flash** : zone illisible, à ressaisir à la main.

## Tableaux

- **Tableaux sans lignes** (ou filets très pâles) : repli sur des colonnes devinées d'après les grands espaces. L'alignement d'une ligne à l'autre n'est pas garanti, et le texte hors tableau n'est plus verrouillé d'office (verrouiller signatures et tampons à la main).
- **Cases fusionnées, séparateurs partiels** : un séparateur de colonne qui ne court pas sur au moins la moitié du tableau est ignoré.
- **Types de ligne devinés** : titre = ligne en majuscules suivie d'un article (« 1 … », « +/- 20 … ») ou finissant par « : » ; total = commence par « TOTAL ». « CARTONNAGE » n'est donc pas un titre (correct sur tes pages), mais un autre document peut tromper la règle : corriger d'un toucher (« Type de ligne »), le choix n'est jamais écrasé.
- **Texte hors tableau** (n° de page, signatures, tampons) : ni modifiable ni exporté, par choix.
- **Signature ou tampon posé dans une case** : à verrouiller à la main.

## Rendu à l'écran

- Le texte modifié est écrit en **Times New Roman**, taille calée sur la largeur du texte d'origine. Si le document utilise une autre police, la différence se voit.
- Le texte modifié est en noir, comme l'original : pour retrouver une correction, toucher la case (la barre d'état affiche le texte d'origine).

## Export texte

- Une case qui commence par `=`, `-`, `+` ou `@` peut être lue comme une formule par Excel : l'export affiche un avertissement. Les « +/- » de tes pages ne forment pas une formule valide, Excel devrait les garder en texte — à confirmer au test.
- Double-clic sur le .txt : Windows l'ouvre dans le Bloc-notes. Dans Excel (Fichier › Ouvrir), l'assistant d'importation peut s'afficher : les choix proposés (délimité, tabulation, UTF-8) conviennent tels quels.
- Les espaces de milliers ne sont retirés que dans les colonnes de valeurs (pas dans les désignations, pour ne pas transformer « 1 100 litres »).

## Imports

- **XLSX** : valeurs seulement (pas de formules ni de mise en forme) ; une date apparaît comme un nombre.
- **PDF protégé par mot de passe** : refusé.
- **Photos très grandes** : lues à 4000 px maximum, pages redressées à 2400 px maximum.
- **OCR français** : doit être présent dans Windows (c'est le cas sur un Windows en français ; sinon voir le README).
