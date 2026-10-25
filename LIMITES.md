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
- **Scan, PDF** : quand le papier touche les quatre bords de l'image, la page entière est gardée (sinon une grande case blanche d'un tableau qui touche les bords pouvait être prise pour la feuille : page 6 de « BEENDHI prisé »). Une photo de feuille posée sur un fond clair qui remplit toute l'image est donc aussi gardée entière : « Recadrer » si besoin.
- **Recadrer** relit le texte : chaque ligne relue reprend le numéro de lot et les corrections de la ligne d'avant **de même texte lu**. Une ligne lue différemment après recadrage perd les siens (à retaper) ; les lignes ajoutées sont perdues.
- **Reflets, flash** : zone illisible, à ressaisir à la main.

## Tableaux

- **Tableaux sans lignes** (ou filets très pâles) : repli sur des colonnes devinées d'après les grands espaces. L'alignement d'une ligne à l'autre n'est pas garanti, et le texte hors tableau n'est plus verrouillé d'office (verrouiller signatures et tampons à la main).
- **Cases fusionnées, séparateurs partiels** : un séparateur de colonne qui ne court pas sur au moins la moitié du tableau est ignoré.
- **Types de ligne devinés** : titre = ligne en majuscules suivie d'un article (« 1 … », « +/- 20 … ») ou finissant par « : » ; total = commence par « TOTAL ». « CARTONNAGE » n'est donc pas un titre (correct sur tes pages), mais un autre document peut tromper la règle : corriger d'un toucher (« Type de ligne »), le choix n'est jamais écrasé.
- **Texte hors tableau** (n° de page, signatures, tampons) : ni modifiable ni exporté, par choix.
- **Signature ou tampon posé dans une case** : à verrouiller à la main.

## Rendu à l'écran

- Le texte modifié est écrit en **Times New Roman**, taille calée sur la largeur du texte d'origine. Si le document utilise une autre police, la différence se voit.
- Le texte modifié est en noir, comme l'original : pour retrouver une correction, toucher la case (la barre du haut affiche le texte d'origine) ou ouvrir la liste des pages (« 3 / 10 ») qui indique le nombre de cases modifiées par page.
- **Gras** : le texte retapé est en gras sur les lignes de total et d'en-tête (c'est le cas sur tes documents), normal ailleurs. Un document qui met en gras d'autres lignes ne sera pas reproduit à l'identique.
- **Fond gris tramé** (totaux, en-têtes) : sous le texte retapé, un morceau propre de la même rangée est répété ; très proche de l'original, pas identique au pixel près.
- **Une page à la fois** : passer d'une page à l'autre recalcule l'image redressée (environ une seconde par page) ; seules les dernières pages vues restent en mémoire, ce qui permet de longs inventaires sur iPhone.

## Export JPEG

- L'image exportée est la photo d'origine : seules les cases modifiées changent (fond couleur papier + nouveau texte en noir). Les photos très grandes sont réduites à 4000 px de côté.
- Une case modifiée est réécrite en entier avec le texte du champ : si la reconnaissance avait mal lu un mot de cette ligne, le corriger aussi avant de valider (la loupe montre l'original).

## Numéros de lot

- **Pages et tableaux** : seuls les tableaux cochés ont des lots ; « Synchroniser » ne numérote qu'eux. La proposition (tableaux d'objets) écarte les cadres à une colonne, les récapitulatifs (« I - … », « II - … ») et les tableaux sans objet ; elle se corrige d'un toucher. Le nom affiché d'un tableau est sa 1re ligne d'objet telle qu'elle a été lue.
- **Lignes illisibles** (bandes grisées des en-têtes et totaux lues comme du charabia) : pas d'objet sans montant lisible (chiffres ou « mémoire ») ni début d'objet (« 1 … », « +/- 20 … ») ni texte lisible. Une rangée d'en-tête ou de total n'a jamais d'objet. Une ligne réelle écartée à tort : « + ».

- **Un objet = la ligne d'article qui porte les valeurs** (ou la première ligne de la rangée) : les lignes de suite (« CD3+ +/- 14130 heures ») n'ont pas de numéro. Si une rangée imprimée contient en réalité deux objets, l'un des deux n'aura pas de numéro : corriger le type de ligne ou taper le numéro voulu.
- **La colonne tient dans la marge gauche** : le tableau n'est jamais déplacé. Marge étroite (photo recadrée au ras de la feuille, tableau près du bord) → colonne étroite et numéros réduits. Sous 8 pixels de marge, pas de colonne tracée.
- **Où est la colonne** : de l'en-tête au dernier objet de chaque tableau ; rien sur les totaux, le récapitulatif (« I - … », « II - … », TOTAL GENERAL) ni sous le dernier objet. Une ligne de récapitulatif est reconnue à son numéro romain suivi d'un tiret.
- **Fond des cases** : recopié depuis le morceau le plus clair de la même rangée (jamais du texte, même si la lecture ne l'a pas trouvé). Le hachuré est très proche de l'original mais pas identique au pixel près.
- **Traits** : tracés droits, de la couleur moyenne des filets du scan ; sur une photo de travers, ils suivent la même perspective que le reste (tracés sur la page redressée puis remis dans la photo).
- **Tableaux sans filets** : pas de colonne tracée, numéros seuls.
- **Rien ne se renumérote tout seul** : un numéro tapé, une case retirée ou ajoutée, une rangée déplacée ne changent aucun autre numéro. « Synchroniser automatiquement » (ou « Renuméroter tout ») le fait d'un coup, dans l'ordre affiché. Un numéro non numérique (« 12 bis ») est gardé tel quel.
- **Lot 0** : l'objet part **juste après la rangée du dernier lot numéroté** (pas forcément sur la dernière page : avant un récapitulatif ou des totaux qui suivent). Les rangées arrivées sont refaites aux colonnes du tableau d'arrivée (fond et filets de ce tableau, contenu de chaque case recopié et recalé sur sa colonne) : si les deux tableaux n'ont pas le même nombre de colonnes, tout est simplement décalé sur le bord gauche. Un objet sous un titre dans la même rangée part seul, avec les valeurs du titre ; le titre reste, sans valeurs. Les numéros des autres lots ne changent pas (« Synchroniser » pour renuméroter). Une page qui perd ou reçoit des rangées est exportée redressée (page droite), comme une page agrandie.
- **Afficher la page où arrivent les lots 0** demande de lire un morceau des pages d'où ils viennent : la première fois, un peu plus lent (gardé ensuite en mémoire).
- **Déplacer une rangée (⋮⋮)** : seulement dans son tableau (pas d'une page à l'autre). L'en-tête ne se déplace pas.
- **Fin de partie** : un lot 0 va avant la 1re ligne TOTAL qui le suit (reconnue même mal lue : « TOTAT », « TOTAE »…). Si la ligne TOTAL n'a pas du tout été lue, il va après le dernier lot du document : le replacer avec sa poignée (Modifier). La poignée orange le déplace dans le tableau affiché (pas d'une page à l'autre).
- **Tableau sans filets** : les lots 0 n'y bougent pas (seul le .txt les place après le dernier lot).
- **Page « Lots de fin »** : composée sur la dernière page à lots qui la précède (marge du haut, rangées refaites à son tableau, filet du bas, même hauteur) ; l'en-tête du tableau n'y est pas recopié. Elle se remplit jusqu'à la hauteur du tableau modèle, puis une autre page s'ajoute juste après. Créée et retirée automatiquement : on ne la recadre pas, on n'y verrouille rien, on n'y retire rien (retaper un numéro y suffit). Dans le .txt, ses lignes sont à sa place dans l'ordre des pages.
- **Supprimer cette ligne** emporte ses lignes de suite (texte sans montant) jusqu'au prochain objet ; une ligne supprimée seule dans sa rangée retire toute la rangée. Les numéros des autres lots ne changent pas (« Synchroniser » pour renuméroter). ↶ ou **Retirer… › Remettre les lignes supprimées** (page affichée) la remet.
- **Retour en arrière** : 30 actions, pendant la session (pas après avoir fermé l'app). Le texte tapé dans une case compte pour une seule action jusqu'à ce qu'on change de case.
- **Orthographe** : correcteur de l'appareil (iPhone/iPad, Windows), en français si le clavier est en français. Il n'est actif que dans la désignation.

## Lignes ajoutées (Ligne vide, Dupliquer, Séparer ici)

- **Ligne vide** : même hauteur que la ligne touchée (une ligne de texte), filets du tableau prolongés depuis cette rangée ; sa case de lot existe même vide. Pas au-dessus de l'en-tête.

- **Dupliquer** ajoute la copie **sous toute la rangée** de la ligne : si la rangée imprimée contient plusieurs lignes, la copie vient après la dernière. Seule la ligne touchée est copiée (pas ses lignes de suite).
- La copie reprend les **pixels** de la ligne (de l'interligne au-dessus à celui d'en dessous) : si la lecture avait mal découpé deux lignes collées, un bout de la ligne voisine peut apparaître en haut ou en bas de la copie. Retaper la case le masque.
- La **case de lot** de la copie est vide : la remplir à la main ou « Synchroniser » (les autres numéros ne changent pas tout seuls).
- Les **montants de la copie s'ajoutent** aux totaux (ils n'étaient pas dans le total imprimé) : en partageant un objet en deux (« 2 doseuses » → 1 + 1), corriger aussi le prix de l'original.
- **Séparer au-dessus** trace le filet au milieu de l'interligne au-dessus de la ligne, recopié du filet du haut de la rangée ; impossible sur la 1re ligne d'une rangée (rien à séparer) : utiliser **Séparer en dessous** (trait sous la ligne), impossible sur la dernière.
- **Synchroniser** ne renumérote plus rien : après une suite tapée à la main qui descend (30, 29…), la case vide suivante prend 30 (le numéro qui suit le lot précédent) même s'il existe déjà ; le message signale les doublons. « Renuméroter tout » refait tout dans l'ordre affiché.
- **Export PDF** : chaque page est l'image JPEG de la page (comme l'export JPEG), en largeur A4 ; le texte n'est pas sélectionnable dans le PDF (c'est une image, comme le scan d'origine).
- **Réduire la rangée** : l'espace en trop est mesuré par rapport aux marges habituelles des rangées d'une ligne du même tableau (au-dessus et en dessous du texte) ; il est enlevé s'il dépasse en tout une demi-ligne. Pas sur une ligne dupliquée ou une ligne vide ajoutée (déjà à la bonne hauteur), une rangée agrandie par du texte tapé, ni une rangée qui contient une ligne supprimée.
- **Déplacer une ligne d'un bloc** : elle devient une rangée à part (trait tracé au-dessus et, si des lignes la suivent, en dessous) ; ses lignes de suite ne partent pas avec elle (les déplacer aussi, ou déplacer le bloc entier par la poignée de sa 1re ligne).
- **En-tête sur fond tramé** : la trame peut être lue comme plusieurs filets serrés ; ils sont réunis en une rangée (à l'import et à l'ouverture d'un projet existant). Si l'en-tête avait été lu en morceaux, ses cases deviennent vides et modifiables (l'imprimé reste affiché) ; une case déjà corrigée n'est pas touchée.
- **Ligne TOTAL ajoutée** : additionne les articles et titres (pas les totaux, en-têtes, récapitulatifs, lignes supprimées ni autres lignes TOTAL ajoutées) ; « lignes au-dessus » = du début du tableau (ou du total précédent) jusqu'à elle, sur sa page. Elle ne compte pas dans les totaux imprimés.
- Le .txt garde une ligne par ligne : séparer ne change rien au .txt (sauf les numéros de lot) ; un titre avec valeurs séparé de son article garde ses valeurs sur sa propre ligne.
- Tableaux sans filets : ni copie ni séparation (il n'y a pas de rangées à tracer).
- **Recadrer** une page relit le texte : ses lignes ajoutées sont perdues (comme les agrandissements).
- Une page avec des lignes ajoutées est exportée **redressée** (comme une page agrandie).

## Texte sur plusieurs lignes

- Seule la **désignation** (1re colonne) passe à la ligne ; une valeur trop longue est encore réduite pour tenir sur une ligne.
- La rangée s'agrandit en insérant une bande sous la ligne modifiée : les filets verticaux du tableau y sont prolongés, le reste est mis à la couleur du papier. Une page agrandie ne se superpose plus à la photo d'origine : son JPEG est la page **redressée** (droite, recadrée), plus longue de la hauteur ajoutée.
- L'écran (champ de saisie) et l'export coupent les lignes avec la même règle ; un mot peut exceptionnellement passer à la ligne à un endroit légèrement différent.
- Recadrer une page relit le texte : les agrandissements de cette page sont perdus.

## Totaux

- **Calcul** : total = montant imprimé + écarts de sa rubrique (valeurs changées, lots 0 retirés). Une erreur de lecture sur une ligne qu'on ne touche pas ne fausse donc pas le total. Mais si l'on corrige une valeur que la lecture avait mal lue (« S 500 » au lieu de « 5 500 »), l'écart compté est celui entre les deux lectures : « S » est lu comme 5, « O » comme 0, « l » comme 1 pour limiter ce cas.
- **Rubrique d'un total** : de l'en-tête (ou du total précédent) jusqu'à lui, même sur plusieurs pages. « mémoire » compte pour 0.
- **Récapitulatif** (« II - STOCK  44 550 ») : relié à sa rubrique par le nom (« STOCK »), pas par le numéro romain (ils diffèrent parfois entre le récapitulatif et les pages). Si le nom a été mal lu, la ligne ne suit pas : la corriger, ou taper le montant.
- Un total tapé à la main ne bouge plus (le montant calculé s'affiche en haut) ; « Recalculer » le remet en automatique. « Rétablir » sur un total remet le montant imprimé et le garde.
- Le montant écrit utilise l'espace des milliers (« 198 310 ») ; le .txt l'écrit sans espace.

## Couleur du texte

- Sur l'écriture d'origine, l'encre du morceau choisi prend la couleur (le papier reste tel quel) : la position des lettres est estimée comme pour le trait (à une demi-lettre près).
- Pendant qu'on tape dans une case, son texte s'affiche en noir ; les couleurs réapparaissent en quittant la case.
- Le .txt ne garde pas les couleurs.

## Texte barré

- Sur un texte non retouché, le trait est posé sur l'écriture du scan : la position des lettres est estimée d'après la largeur de l'encre (police Times). Sur une police très différente, le trait peut commencer ou finir à une demi-lettre près.
- Le .txt ne peut pas montrer un texte barré : il garde le texte tel quel (choix demandé). Pour sortir un objet de l'inventaire, mettre son lot à 0.

## Export texte

- Le .txt reprend ce que la reconnaissance a lu : les lignes non touchées peuvent contenir ses erreurs (« march » pour « marches »).

- Une case qui commence par `=`, `-`, `+` ou `@` peut être lue comme une formule par Excel : l'export affiche un avertissement. Les « +/- » de tes pages ne forment pas une formule valide, Excel devrait les garder en texte — à confirmer au test.
- Double-clic sur le .txt : Windows l'ouvre dans le Bloc-notes. Dans Excel (Fichier › Ouvrir), l'assistant d'importation peut s'afficher : les choix proposés (délimité, tabulation, UTF-8) conviennent tels quels.
- Les espaces de milliers ne sont retirés que dans les colonnes de valeurs (pas dans les désignations, pour ne pas transformer « 1 100 litres »).

## Imports

- **XLSX** : valeurs seulement (pas de formules ni de mise en forme) ; une date apparaît comme un nombre.
- **PDF protégé par mot de passe** : refusé.
- **PDF de scanner sur iPhone/iPad** : les images JPEG 2000 et JBIG2 sont décodées par l'app (décodeurs livrés avec elle). Si une page reste malgré tout blanche, l'app le signale : la retirer et importer une photo de la page.
- **Dossier sur iPhone/iPad** : « Un dossier entier » dépend de la version d'iOS ; sinon, compresser le dossier dans Fichiers (appui long → Compresser) et importer le .zip, ou sélectionner toutes les photos du dossier.
- **Photos très grandes** : lues à 4000 px maximum, pages redressées à 2400 px maximum.
- **OCR français** : doit être présent dans Windows (c'est le cas sur un Windows en français ; sinon voir le README).
