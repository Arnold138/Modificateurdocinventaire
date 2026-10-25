using System.Text.Json.Serialization;

namespace RetoucheInventaire.Core.Model;

/// <summary>Rôle d'une ligne imprimée, utilisé par l'export texte.</summary>
public enum LineKind
{
    /// <summary>Ligne normale : texte et valeurs séparés par des tabulations.</summary>
    Article,
    /// <summary>Ligne de titre (« DANS L'ENTREPOT ») : exportée avec son texte seul.</summary>
    Titre,
    /// <summary>Ligne de total : exportée comme un article (calcul automatique en étape 2).</summary>
    Total,
    /// <summary>En-tête de tableau (« exploitation », « réalisation »).</summary>
    EnTete,
    /// <summary>Non exportée (numéro de page, texte hors tableau…).</summary>
    Ignoree,
}

public enum TextAlign { Left, Center, Right }

public enum PageSource
{
    /// <summary>Photo, scan ou page de PDF : image + OCR.</summary>
    Image,
    /// <summary>TXT, CSV ou XLSX : tableau reconstitué, pas d'image.</summary>
    Data,
}

/// <summary>Un champ modifiable : le texte d'une ligne imprimée dans une case.</summary>
public sealed class Field
{
    public string Id { get; set; } = "";
    public int Column { get; set; }
    public string OriginalText { get; set; } = "";
    public string Text { get; set; } = "";

    /// <summary>Encre d'origine (zone recouverte quand le texte est modifié).</summary>
    public RectD Ink { get; set; }

    /// <summary>Zone d'édition : ne dépasse jamais la case.</summary>
    public RectD Box { get; set; }

    public TextAlign Align { get; set; }

    /// <summary>Retrait entre le bord de la zone et le texte, côté alignement.</summary>
    public double Indent { get; set; }

    /// <summary>Taille de police estimée d'après l'original (pixels de page).</summary>
    public double FontSize { get; set; }

    public bool Locked { get; set; }

    /// <summary>Morceaux de texte barrés (trait rouge ou noir), voir <see cref="Strikes"/>.</summary>
    public List<Strike> Strikes { get; set; } = [];

    /// <summary>Morceaux de texte en couleur (« red », « blue », « green »), voir <see cref="Strikes.SetColor"/>.</summary>
    public List<Strike> Colors { get; set; } = [];

    /// <summary>Montant de total tapé à la main : il ne se recalcule plus tout seul (« Recalculer » le remet en automatique).</summary>
    public bool Manual { get; set; }

    [JsonIgnore] public bool IsModified => !string.Equals(Text, OriginalText, StringComparison.Ordinal);
}

/// <summary>Une ligne imprimée : une cellule par colonne du tableau.</summary>
public sealed class PrintedLine
{
    public LineKind Kind { get; set; }
    public bool KindChosenByUser { get; set; }
    /// <summary>Texte hors de tout tableau : verrouillé et non exporté.</summary>
    public bool OutsideTable { get; set; }
    public List<Field> Cells { get; set; } = [];

    /// <summary>Numéro de lot (vide si aucun). Uniquement sur les lignes d'objet.</summary>
    public string Lot { get; set; } = "";
    /// <summary>Numéro saisi à la main : la numérotation automatique repart de lui.</summary>
    public bool LotManual { get; set; }
    /// <summary>Case de lot affichée sur cette ligne (objet), même vide : la colonne existe.</summary>
    public bool LotSlot { get; set; }
    /// <summary>Numéro retiré par l'utilisateur (poubelle) : cette ligne n'est pas un objet.</summary>
    public bool NoLot { get; set; }
    /// <summary>Numéro ajouté par l'utilisateur (« + ») sur une ligne que la règle n'avait pas retenue.</summary>
    public bool ForceLot { get; set; }

    /// <summary>
    /// Hauteur ajoutée sous cette ligne (texte saisi sur plusieurs lignes) : tout ce qui est en dessous descend d'autant.
    /// Les positions de la page en tiennent déjà compte ; voir <see cref="PageLayout"/>.
    /// </summary>
    public int Extra { get; set; }
    /// <summary>Ordonnée (page agrandie) où la bande ajoutée commence : un interligne propre sous le texte d'origine.</summary>
    public int ExtraAt { get; set; }

    /// <summary>
    /// Ligne dupliquée (« Dupliquer la ligne ») : ses montants ne sont pas dans le total imprimé, ils y sont ajoutés en entier.
    /// Voir <see cref="Rows"/>.
    /// </summary>
    public bool Copy { get; set; }

    /// <summary>Ligne supprimée (« Supprimer cette ligne ») : retirée de la page, du .txt, des totaux et des lots (son numéro est gardé).</summary>
    public bool Deleted { get; set; }

    /// <summary>Ligne TOTAL ajoutée : ce qu'elle additionne (voir <see cref="Totals"/>).</summary>
    public TotalScope? TotalScope { get; set; }
    /// <summary>Lot 0 placé à la main (poignée, mode Modifier) : recollé après la rangée de cette ligne.</summary>
    public EndAnchor? EndAt { get; set; }

    [JsonIgnore] public RectD Bounds => Cells.Aggregate(RectD.Empty, (r, f) => r.Union(f.Box));
    [JsonIgnore] public string FirstText => Cells.FirstOrDefault(c => !string.IsNullOrWhiteSpace(c.Text))?.Text.Trim() ?? "";
}

/// <summary>
/// Ligne TOTAL ajoutée : somme des montants actuels, colonne par colonne, des lignes au-dessus (« above », depuis l'en-tête ou le total
/// précédent), de sa page (« page »), des pages choisies (« pages ») ou de tout le document (« all ») ; Auto = false : à taper.
/// </summary>
/// <summary>Endroit choisi pour un lot 0 : page (Id) et case (Id de la 1re case d'une ligne de la rangée d'arrivée).</summary>
public sealed class EndAnchor
{
    public string Page { get; set; } = "";
    public string Line { get; set; } = "";
}

public sealed class TotalScope
{
    public string Mode { get; set; } = "above";
    public List<string> Pages { get; set; } = [];
    public bool Auto { get; set; } = true;
    /// <summary>Écrite en gras (sinon comme les autres lignes).</summary>
    public bool Bold { get; set; }
}

/// <summary>Où vont les lots 0 : « last » (après le dernier lot numéroté) ou « page » (pages « lots de fin » après la page AfterId).</summary>
public sealed class EndLotsChoice
{
    public string Mode { get; set; } = "last";
    public string? AfterId { get; set; }
}

/// <summary>Une rangée du tableau (entre deux filets horizontaux).</summary>
public sealed class Row
{
    public RectD Bounds { get; set; }
    public List<PrintedLine> Lines { get; set; } = [];
    /// <summary>Rangée ajoutée par l'utilisateur : identifiant de sa bande (<see cref="Page.Inserts"/>), null pour une rangée imprimée.</summary>
    public string? Inserted { get; set; }
}

/// <summary>
/// Morceau d'une bande insérée : rangées de pixels recopiées de l'image d'origine (page redressée) ou, avec <see cref="Gap"/>,
/// interligne propre : filets verticaux (entre X0 et X1) de la rangée la plus nette parmi [Src, Src + Span[, papier ailleurs.
/// </summary>
public sealed class BandPart
{
    public int Src { get; set; }
    public int Height { get; set; }
    public bool Gap { get; set; }
    public int Span { get; set; }
    /// <summary>Abscisses (arrondies) des filets verticaux du tableau.</summary>
    public List<int> Cols { get; set; } = [];
    public int X0 { get; set; }
    public int X1 { get; set; }
}

/// <summary>
/// Bande insérée dans la page pour une rangée ajoutée (<see cref="Rows"/>) : filet du haut et, pour une ligne dupliquée,
/// les pixels de la ligne recopiés ; tout ce qui est en dessous descend de <see cref="Height"/>.
/// </summary>
public sealed class InsertedBand
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N")[..8];
    /// <summary>« Copy » (ligne dupliquée), « Split » (« Séparer ici » : filet seul) ou « Trim » (« Réduire la rangée »).</summary>
    public string Kind { get; set; } = "Copy";
    /// <summary>Ordonnée (page agrandie) où la bande commence.</summary>
    public int At { get; set; }
    public int Height { get; set; }
    /// <summary>Bande « Trim » (« Réduire la rangée ») : rangées de l'image d'origine sautées (Height = −Skip).</summary>
    public int Skip { get; set; }
    /// <summary>Bande « Trim » : case (Id) de la 1re ligne de la rangée réduite (la réduction suit sa rangée).</summary>
    public string? Line { get; set; }
    public List<BandPart> Parts { get; set; } = [];
}

/// <summary>Traits du tableau mesurés sur l'imprimé (épaisseurs, couleur 3 octets + gris) pour tracer la colonne des lots.</summary>
public sealed class RuleStyle
{
    public int V { get; set; }
    public int H { get; set; }
    public List<int> Color { get; set; } = [];
}

public sealed class Table
{
    public RectD Bounds { get; set; }
    /// <summary>Faux si le tableau n'a pas de filets (colonnes devinées par les espaces).</summary>
    public bool HasGrid { get; set; } = true;
    public List<double> ColumnXs { get; set; } = [];
    public List<double> RowYs { get; set; } = [];
    /// <summary>Traits mesurés une fois sur l'imprimé (colonne des lots) ; null : pas encore mesurés.</summary>
    public RuleStyle? Rules { get; set; }
    public List<Row> Rows { get; set; } = [];
    /// <summary>Ordre d'affichage des rangées (rangées déplacées à la main), null : l'ordre imprimé.</summary>
    public List<int>? RowOrder { get; set; }
    /// <summary>Pas de numéros de lot dans ce tableau (lots activés page par page, tableau par tableau).</summary>
    public bool LotsOff { get; set; }
}

public sealed class Page
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N")[..8];
    public PageSource Source { get; set; }
    /// <summary>Nom du fichier importé (affichage).</summary>
    public string SourceName { get; set; } = "";
    /// <summary>Entrée de l'archive projet contenant l'image source (null pour les pages Data).</summary>
    public string? SourceEntry { get; set; }

    /// <summary>Taille de la page redressée, en pixels.</summary>
    public int Width { get; set; }
    public int Height { get; set; }

    /// <summary>Quarts de tour horaires appliqués à la source avant recadrage.</summary>
    public int QuarterTurns { get; set; }
    public Quad? Crop { get; set; }
    public double Skew { get; set; }

    public List<Table> Tables { get; set; } = [];
    public List<PrintedLine> LooseLines { get; set; } = [];
    public List<RectD> LockZones { get; set; } = [];
    /// <summary>Bandes insérées (rangées ajoutées), voir <see cref="Rows"/>.</summary>
    public List<InsertedBand> Inserts { get; set; } = [];
    /// <summary>Page « lots de fin » (créée automatiquement) : composée depuis une page modèle, voir <see cref="Export.Arrangement"/>.</summary>
    public bool EndLots { get; set; }

    public IEnumerable<PrintedLine> AllLines() =>
        Tables.SelectMany(t => t.Rows).SelectMany(r => r.Lines).Concat(LooseLines);

    public IEnumerable<Field> AllFields() => AllLines().SelectMany(l => l.Cells);
}

public sealed class InventoryProject
{
    public int FormatVersion { get; set; } = 1;
    public string Name { get; set; } = "Inventaire";
    /// <summary>Numéros de lot dans une colonne tracée comme le tableau (traits prolongés), sinon numéros seuls.</summary>
    public bool LotFrame { get; set; } = true;
    /// <summary>Lots remplis à la main : rien ne se remplit tout seul (sauf « Synchroniser automatiquement »).</summary>
    public bool LotsByHand { get; set; }
    public List<Page> Pages { get; set; } = [];
    /// <summary>Où vont les lots 0 (null : pas encore choisi, on le demande).</summary>
    public EndLotsChoice? EndLots { get; set; }

    public IEnumerable<Field> AllFields() => Pages.SelectMany(p => p.AllFields());
}
