using System.IO.Compression;
using System.Reflection;
using System.Text;
using System.Text.RegularExpressions;
using RetoucheInventaire.Core.Analysis;
using RetoucheInventaire.Core.Export;
using RetoucheInventaire.Core.Imaging;
using RetoucheInventaire.Core.Import;
using RetoucheInventaire.Core.Model;
using RetoucheInventaire.Core.Storage;

namespace RetoucheInventaire.Core.Tests;

/// <summary>Mini-framework : chaque méthode publique statique « T_… » est un test.</summary>
public static class Tests
{
    public static int RunAll()
    {
        var tests = typeof(Tests).GetMethods(BindingFlags.Public | BindingFlags.Static).Where(m => m.Name.StartsWith("T_")).ToList();
        int failed = 0;
        foreach (var t in tests)
        {
            var sw = System.Diagnostics.Stopwatch.StartNew();
            try
            {
                t.Invoke(null, null);
                Console.WriteLine($"  OK    {t.Name} ({sw.ElapsedMilliseconds} ms)");
            }
            catch (TargetInvocationException e)
            {
                failed++;
                Console.WriteLine($"  ÉCHEC {t.Name} : {e.InnerException?.Message}");
            }
        }
        Console.WriteLine(failed == 0 ? $"{tests.Count} tests, tous réussis." : $"{failed} échec(s) sur {tests.Count}.");
        return failed == 0 ? 0 : 1;
    }

    private static void Check(bool cond, string message)
    {
        if (!cond) throw new Exception(message);
    }

    private static void Equal<T>(T expected, T actual, string what)
    {
        if (!EqualityComparer<T>.Default.Equals(expected, actual)) throw new Exception($"{what} : attendu « {expected} », obtenu « {actual} »");
    }

    private static InventoryProject ThreePages()
    {
        var p = new InventoryProject();
        foreach (var n in new[] { "page1", "page2", "page5" }) p.Pages.Add(Fixtures.Analyze(n));
        return p;
    }

    private static string[] ExportLines(InventoryProject p) =>
        TxtExporter.Build(p).Text.Split("\r\n")[..^1];

    // ---------- Redressement ----------

    public static void T_Redressement_retire_le_cadre_noir_et_l_inclinaison()
    {
        var src = Pgm.Read(Path.Combine(Fixtures.Dir, "page5.pgm.gz"));
        var n = PageNormalizer.Normalize(src);
        Check(n.Crop.TopLeft.X > 28 && n.Crop.TopRight.X < src.Width - 28, "le cadre noir latéral n'est pas exclu");
        Check(n.SkewDegrees is < -0.1 and > -0.8, $"inclinaison inattendue {n.SkewDegrees}");
    }

    public static void T_Perspective_photo_simulee()
    {
        // On « photographie » la page 1 de travers sur un fond sombre, puis on la redresse.
        var page = Pgm.Read(Path.Combine(Fixtures.Dir, "page1.pgm.gz"));
        int W = 1500, H = 1900;
        var corners = new[] { new PointD(190, 140), new PointD(1330, 230), new PointD(1420, 1830), new PointD(120, 1760) };
        var pageCorners = new[] { new PointD(32, 0), new PointD(1137, 0), new PointD(1137, page.Height - 1), new PointD(32, page.Height - 1) };
        var map = ImageOps.Homography(corners, pageCorners);
        var photo = new byte[W * H];
        for (int y = 0; y < H; y++)
            for (int x = 0; x < W; x++)
            {
                var p = map(x, y);
                photo[y * W + x] = p.X >= 32 && p.X <= 1137 && p.Y >= 0 && p.Y < page.Height - 1
                    ? page[(int)p.X, (int)p.Y]
                    : (byte)(45 + (x * 7 + y * 13) % 20);
            }
        var n = PageNormalizer.Normalize(new Raster(W, H, 1, photo));
        var found = n.Crop.ToArray();
        for (int i = 0; i < 4; i++)
            Check(found[i].DistanceTo(corners[i]) < 15, $"coin {i} trouvé à {found[i]}, attendu {corners[i]}");

        var grids = RuleDetector.Detect(n.Image);
        Equal(1, grids.Count, "tableaux trouvés après redressement");
        Equal(4, grids[0].ColumnXs.Count, "bords de colonnes");
        Equal(29, grids[0].RowYs.Count, "bords de rangées");
    }

    public static void T_Rotation_quatre_quarts_de_tour_revient_a_l_original()
    {
        var r = new Raster(3, 2, 1, [1, 2, 3, 4, 5, 6]);
        var back = ImageOps.Rotate90(ImageOps.Rotate90(ImageOps.Rotate90(ImageOps.Rotate90(r, 1), 1), 1), 1);
        Check(back.Data.SequenceEqual(r.Data) && back.Width == 3, "rotation incohérente");
        var q = ImageOps.Rotate90(r, 1);
        Equal(2, q.Width, "largeur après quart de tour");
        Equal(4, q.Data[0], "pixel haut-gauche après quart de tour horaire");
    }

    // ---------- Structure des tableaux ----------

    public static void T_Grilles_des_trois_pages()
    {
        var p1 = Fixtures.Analyze("page1");
        Equal(1, p1.Tables.Count, "page 1 : tableaux");
        Equal(4, p1.Tables[0].ColumnXs.Count, "page 1 : bords de colonnes");
        Equal(28, p1.Tables[0].Rows.Count, "page 1 : rangées");

        var p2 = Fixtures.Analyze("page2");
        Equal(1, p2.Tables.Count, "page 2 : tableaux");
        Equal(4, p2.Tables[0].ColumnXs.Count, "page 2 : bords de colonnes");
        Equal(34, p2.Tables[0].Rows.Count, "page 2 : rangées");

        var p5 = Fixtures.Analyze("page5");
        Equal("6,3,4", string.Join(",", p5.Tables.Select(t => t.Rows.Count)), "page 5 : rangées par tableau");
    }

    public static void T_Types_de_ligne()
    {
        var p1 = Fixtures.Analyze("page1");
        var lines = p1.Tables[0].Rows.SelectMany(r => r.Lines).ToList();
        LineKind KindOf(string start) => lines.First(l => l.Cells[0].Text.StartsWith(start)).Kind;
        Equal(LineKind.EnTete, KindOf("I - MOBILIER"), "en-tête");
        Equal(LineKind.Titre, KindOf("DANS L'ENTREPOT"), "titre DANS L'ENTREPOT");
        Equal(LineKind.Titre, KindOf("SALLE LAVAGE"), "titre SALLE LAVAGE");
        Equal(LineKind.Article, KindOf("2 laves main"), "article");

        var p5 = Fixtures.Analyze("page5");
        var l5 = p5.Tables.SelectMany(t => t.Rows).SelectMany(r => r.Lines).ToList();
        Equal(LineKind.Article, l5.First(l => l.Cells[0].Text == "CARTONNAGE").Kind, "CARTONNAGE n'est pas un titre");
        Equal(LineKind.Total, l5.First(l => l.Cells[0].Text == "TOTAL STOCK").Kind, "total");
        Check(p5.LooseLines.Any(l => l.FirstText == "5/6" && l.Cells.All(c => c.Locked)), "le numéro de page doit être hors tableau et verrouillé");
    }

    public static void T_Champs_dans_leur_case()
    {
        foreach (var page in ThreePages().Pages)
            foreach (var t in page.Tables)
                for (int r = 0; r < t.Rows.Count; r++)
                    foreach (var f in t.Rows[r].Lines.SelectMany(l => l.Cells))
                    {
                        double l = t.ColumnXs[f.Column], rr = t.ColumnXs[f.Column + 1];
                        Check(f.Box.Left >= l && f.Box.Right <= rr, $"{page.Id} : champ « {f.Text} » déborde de sa colonne");
                        Check(f.Box.Top >= t.RowYs[r] && f.Box.Bottom <= t.RowYs[r + 1], $"{page.Id} : champ « {f.Text} » déborde de sa rangée");
                        Check(f.FontSize > 5 && f.FontSize < 40, $"{page.Id} : taille de police aberrante {f.FontSize:0.0} pour « {f.Text} »");
                    }
    }

    public static void T_Alignement_des_valeurs_a_droite()
    {
        var p1 = Fixtures.Analyze("page1");
        var values = p1.Tables[0].Rows.Skip(1).SelectMany(r => r.Lines).SelectMany(l => l.Cells).Where(c => c.Column > 0 && c.Text.Any(char.IsDigit)).ToList();
        Check(values.Count > 30, "trop peu de valeurs");
        Check(values.All(v => v.Align == TextAlign.Right), "les nombres doivent être alignés à droite");
    }

    public static void T_Seconde_lecture_remplit_les_cases_oubliees()
    {
        var p1 = Fixtures.Analyze("page1");
        var line = p1.Tables[0].Rows.SelectMany(r => r.Lines).First(l => l.Cells[0].Text.StartsWith("2 vestiaires"));
        Equal("50", line.Cells[1].Text, "exploitation des vestiaires");
        Equal("25", line.Cells[2].Text, "réalisation des vestiaires");
    }

    // ---------- Export texte ----------

    public static void T_Export_titres_seuls_et_valeurs_rattachees()
    {
        var lines = ExportLines(ThreePages());
        int i = Array.IndexOf(lines, "DANS L'ENTREPOT");
        Check(i >= 0, "titre DANS L'ENTREPOT absent ou accompagné de valeurs");
        Equal("2 laves main en inox + 1 sanibroyeur SPEED\t130\t70", lines[i + 1], "article sous le titre");
        int j = Array.IndexOf(lines, "SALLE LAVAGE :");
        Check(j >= 0, "titre SALLE LAVAGE absent");
        Equal("1 gazelle TULESCA inox\t200\t100", lines[j + 1], "article sous SALLE LAVAGE");
    }

    public static void T_Export_une_ligne_par_ligne_imprimee()
    {
        var lines = ExportLines(ThreePages());
        int i = Array.FindIndex(lines, l => l.StartsWith("1 compresseur"));
        Check(lines[i].EndsWith("\t2800\t1800"), "valeurs sur la 1re ligne de l'article");
        Equal("CD3+ +/- 14130 heures", lines[i + 1], "suite de l'article, texte seul");
    }

    public static void T_Export_totaux_nombres_et_separation_des_tableaux()
    {
        var lines = ExportLines(ThreePages());
        Check(lines.Contains("TOTAL STOCK\t44550\t9000"), "ligne de total");
        Check(lines.Contains("+/- 60 racks ADETECH de stockage L 320 cm H 480 cm +/- 549 places\t15000\t8000"), "nombre sans espace de milliers");
        foreach (var l in lines)
            foreach (var v in l.Split('\t').Skip(1))
                Check(!Regex.IsMatch(v, @"^\d{1,3}( \d{3})+$"), $"espace de milliers restant : « {v} »");
        int s = Array.IndexOf(lines, "II - STOCK\texploitation\tréalisation");
        Check(s > 0 && lines[s - 1] == "", "une ligne vide doit séparer deux tableaux");
        Check(!lines.Any(l => l.Contains("5/6") || l.Contains("2/6")), "le numéro de page ne doit pas être exporté");
    }

    public static void T_Fichier_texte_lisible_Bloc_notes_et_Excel()
    {
        var path = Path.Combine(Path.GetTempPath(), "test_export.txt");
        TxtExporter.Write(ThreePages(), path);
        var bytes = File.ReadAllBytes(path);
        Check(bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF, "BOM UTF-8 absent (Excel lirait mal les accents)");
        var text = new UTF8Encoding(false, true).GetString(bytes, 3, bytes.Length - 3); // lève une exception si UTF-8 invalide
        Check(!Regex.IsMatch(text, "(?<!\r)\n"), "fin de ligne Unix isolée (Bloc-notes ancien)");
        Check(text.EndsWith("\r\n"), "dernière ligne non terminée");
        foreach (var l in text.Split("\r\n"))
        {
            Check(l.Count(c => c == '\t') <= 2, $"trop de colonnes : « {l} »");
            Check(!l.Any(c => char.IsControl(c) && c != '\t'), "caractère de contrôle");
        }
        Check(text.Contains("réalisation") && text.Contains("€"), "accents ou symbole euro perdus");
        File.Delete(path);
    }

    public static void T_Export_relu_comme_tableau()
    {
        // Ce que fera le destinataire : rouvrir le .txt comme un tableau à tabulations.
        var text = TxtExporter.Build(ThreePages()).Text;
        var rows = DelimitedReader.Parse(text, ".txt");
        var total = rows.First(r => r[0] == "TOTAL STOCK");
        Equal("44550", total[1], "total relu");
        Equal("9000", total[2], "total relu");
    }

    public static void T_Avertissement_formule_Excel()
    {
        var page = DataPageBuilder.Build("t.csv", [["- 2 chaises", "10"], ["+/- 8 palettes", "80"], ["=SOMME", "1"]]);
        var w = TxtExporter.Build(new InventoryProject { Pages = [page] }).Warnings;
        Equal(2, w.Count, "avertissements (« - 2 chaises » et « =SOMME », pas « +/- »)");
    }

    // ---------- Édition ----------

    public static void T_Modifier_titre_chiffre_et_texte_ne_deplace_rien()
    {
        var project = ThreePages();
        var before = project.AllFields().Select(f => (f.Id, f.Box, f.Ink, f.FontSize, f.Align, f.Indent)).ToList();
        var lines = project.Pages[0].Tables[0].Rows.SelectMany(r => r.Lines).ToList();

        var title = lines.First(l => l.Cells[0].Text == "SALLE LAVAGE :").Cells[0];
        title.Text = "SALLE DE BAIN :";
        var number = lines.First(l => l.Cells[0].Text == "1 table pliante").Cells[1];
        number.Text = "30";
        var text = lines.First(l => l.Cells[0].Text.StartsWith("2 rolls")).Cells[0];
        text.Text = "3 rolls en métal galvanisé";

        var after = project.AllFields().Select(f => (f.Id, f.Box, f.Ink, f.FontSize, f.Align, f.Indent)).ToList();
        Check(before.SequenceEqual(after), "la géométrie d'un champ a changé");
        Equal(3, project.AllFields().Count(f => f.IsModified), "champs modifiés");

        var exported = ExportLines(project);
        Check(exported.Contains("SALLE DE BAIN :"), "titre modifié exporté seul");
        Check(exported.Contains("1 table pliante\t30\tmémoire"), "chiffre modifié exporté");
        Check(exported.Contains("3 rolls en métal galvanisé\t60\t30"), "texte modifié exporté");
    }

    public static void T_Zone_verrouillee()
    {
        var page = Fixtures.Analyze("page1");
        var f = page.Tables[0].Rows.SelectMany(r => r.Lines).First(l => l.Cells[0].Text == "1 table pliante").Cells[0];
        f.Text = "changé";
        PageEdits.AddLockZone(page, f.Box.Inflate(5, 5));
        Check(f.Locked, "le champ doit être verrouillé");
        Equal("1 table pliante", f.Text, "un champ verrouillé reprend son texte d'origine");
        Check(PageEdits.RemoveLockZoneAt(page, f.Box.CenterX, f.Box.CenterY) && !f.Locked, "déverrouillage");
    }

    public static void T_Type_de_ligne_choisi_par_l_utilisateur_n_est_pas_ecrase()
    {
        var page = Fixtures.Analyze("page5");
        var line = page.Tables[0].Rows.SelectMany(r => r.Lines).First(l => l.Cells[0].Text == "CARTONNAGE");
        Equal(LineKind.Titre, PageEdits.CycleKind(line), "Article → Titre");
        PageAnalyzer.Reclassify(page);
        Equal(LineKind.Titre, line.Kind, "choix conservé");
        var exported = ExportLines(new InventoryProject { Pages = [page] });
        int i = Array.IndexOf(exported, "CARTONNAGE");
        Equal("42057 € à l'achat HT\t16000\t5000", exported[i + 1], "valeurs descendues sous le titre");
    }

    // ---------- Export image ----------

    public static void T_Export_image_ne_touche_que_les_cases_modifiees()
    {
        var page = Fixtures.Analyze("page5");
        var (flat, _) = Fixtures.Load("page5");
        var source = Fixtures.Source("page5");
        var field = page.Tables[0].Rows.SelectMany(r => r.Lines).First(l => l.Cells[0].Text == "CARTONNAGE").Cells[1];
        field.Text = "17000";

        // Simule le rendu de l'application : la case modifiée peinte en noir sur la page redressée.
        var edited = flat.ToBgra();
        var data = (byte[])edited.Data.Clone();
        for (int y = (int)field.Box.Top + 1; y < (int)field.Box.Bottom; y++)
            for (int x = (int)field.Box.Left + 1; x < (int)field.Box.Right; x++)
                for (int c = 0; c < 3; c++) data[(y * edited.Width + x) * 4 + c] = 0;
        var result = ImageExporter.ComposeIntoSource(source, page, new Raster(edited.Width, edited.Height, 4, data));

        Equal(source.Width, result.Width, "largeur de la photo conservée");
        Equal(source.Height, result.Height, "hauteur de la photo conservée");
        var src = source.ToBgra();
        int changed = 0, darkInside = 0, inside = 0;
        for (int i = 0; i < src.Data.Length; i += 4)
            if (src.Data[i] != result.Data[i]) changed++;
        // Le centre de la case, reporté dans la photo, doit être noir.
        double cxp = field.Box.CenterX + page.Crop!.TopLeft.X, cyp = field.Box.CenterY + page.Crop.TopLeft.Y;
        for (int dy = -3; dy <= 3; dy++)
            for (int dx = -10; dx <= 10; dx++)
            {
                inside++;
                if (result.Data[(((int)cyp + dy) * src.Width + (int)cxp + dx) * 4] < 60) darkInside++;
            }
        Check(darkInside > inside * 0.9, $"la case modifiée n'est pas au bon endroit ({darkInside}/{inside})");
        double boxArea = field.Box.Width * field.Box.Height;
        Check(changed < boxArea * 1.2, $"trop de pixels modifiés hors de la case : {changed} pour {boxArea:0}");
        Check(result.Data[(5 * src.Width + 5) * 4] == src.Data[(5 * src.Width + 5) * 4], "le bord de la photo a changé");
    }

    public static void T_Couleur_du_papier()
    {
        var (flat, _) = Fixtures.Load("page1");
        var page = Fixtures.Analyze("page1");
        var f = page.Tables[0].Rows[3].Lines[0].Cells[0];
        var (r, g, b) = ImageExporter.PaperColor(flat, f.Box);
        Check(r > 200 && g > 200 && b > 200, $"papier trop sombre : {r},{g},{b}");
    }

    // ---------- Sauvegarde ----------

    public static void T_Sauvegarde_et_reprise()
    {
        var project = ThreePages();
        project.Pages[0].SourceEntry = "sources/page1.jpg";
        project.Pages[0].Tables[0].Rows[2].Lines[0].Cells[0].Text = "modifié";
        PageEdits.AddLockZone(project.Pages[1], new RectD(10, 10, 100, 50));
        var img = File.ReadAllBytes(Path.Combine(Fixtures.Dir, "page1.jpg"));
        var path = Path.Combine(Path.GetTempPath(), "test" + ProjectStore.Extension);
        ProjectStore.Save(path, project, new Dictionary<string, byte[]> { ["sources/page1.jpg"] = img });

        var (loaded, sources) = ProjectStore.Load(path);
        Equal(TxtExporter.Build(project).Text, TxtExporter.Build(loaded).Text, "export identique après reprise");
        Check(sources["sources/page1.jpg"].SequenceEqual(img), "image source intacte");
        Equal(1, loaded.Pages[1].LockZones.Count, "zones verrouillées");
        Check(loaded.AllFields().Select(f => f.Box).SequenceEqual(project.AllFields().Select(f => f.Box)), "positions conservées");
        Equal("modifié", loaded.Pages[0].Tables[0].Rows[2].Lines[0].Cells[0].Text, "texte modifié conservé");
        File.Delete(path);
    }

    // ---------- Import TXT / CSV / XLSX ----------

    public static void T_Import_CSV_point_virgule_guillemets_et_Windows1252()
    {
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
        var csv = "Désignation;exploitation;réalisation\r\nDANS L'ENTREPOT;;\r\n\"2 laves main; inox\";130;70\r\nTOTAL;15 000;8 000\r\n";
        var rows = DelimitedReader.Parse(DelimitedReader.DecodeText(Encoding.GetEncoding(1252).GetBytes(csv)), ".csv");
        Equal(4, rows.Count, "lignes CSV");
        Equal("2 laves main; inox", rows[2][0], "guillemets");
        var page = DataPageBuilder.Build("stock.csv", rows);
        var lines = ExportLines(new InventoryProject { Pages = [page] });
        Equal("Désignation\texploitation\tréalisation|DANS L'ENTREPOT|2 laves main; inox\t130\t70|TOTAL\t15000\t8000",
              string.Join("|", lines), "export d'un CSV");
    }

    public static void T_Import_XLSX()
    {
        using var ms = new MemoryStream();
        using (var zip = new ZipArchive(ms, ZipArchiveMode.Create, leaveOpen: true))
        {
            void Add(string name, string xml)
            {
                using var w = new StreamWriter(zip.CreateEntry(name).Open(), new UTF8Encoding(false));
                w.Write(xml);
            }
            const string ns = "http://schemas.openxmlformats.org/spreadsheetml/2006/main";
            Add("xl/workbook.xml", $"<workbook xmlns=\"{ns}\" xmlns:r=\"http://schemas.openxmlformats.org/officeDocument/2006/relationships\"><sheets><sheet name=\"Stock\" sheetId=\"1\" r:id=\"rId1\"/></sheets></workbook>");
            Add("xl/_rels/workbook.xml.rels", "<Relationships xmlns=\"http://schemas.openxmlformats.org/package/2006/relationships\"><Relationship Id=\"rId1\" Target=\"worksheets/sheet1.xml\" Type=\"x\"/></Relationships>");
            Add("xl/sharedStrings.xml", $"<sst xmlns=\"{ns}\"><si><t>CARTONNAGE</t></si><si><r><t>mémo</t></r><r><t>ire</t></r></si></sst>");
            Add("xl/worksheets/sheet1.xml", $"<worksheet xmlns=\"{ns}\"><sheetData><row r=\"1\"><c r=\"A1\" t=\"s\"><v>0</v></c><c r=\"B1\"><v>16000</v></c><c r=\"C1\" t=\"s\"><v>1</v></c></row><row r=\"3\"><c r=\"A3\" t=\"inlineStr\"><is><t>Prix</t></is></c><c r=\"C3\"><v>2.5</v></c></row></sheetData></worksheet>");
        }
        ms.Position = 0;
        var sheets = XlsxReader.Read(ms);
        Equal(1, sheets.Count, "feuilles");
        var rows = sheets[0].Rows;
        Equal("CARTONNAGE|16000|mémoire", string.Join("|", rows[0]), "ligne 1");
        Equal("", string.Join("", rows[1]), "ligne vide conservée");
        Equal("Prix||2,5", string.Join("|", rows[2]), "décimal à la française");
    }

    // ---------- Repli sans filets ----------

    public static void T_Tableau_sans_filets()
    {
        var blank = Raster.CreateWhite(1000, 400, 1);
        var words = new List<OcrWord>
        {
            new("1", new RectD(50, 100, 8, 14)), new("chaise", new RectD(62, 100, 50, 14)),
            new("10", new RectD(700, 100, 16, 14)), new("5", new RectD(850, 100, 8, 14)),
            new("SALLE", new RectD(50, 150, 50, 14)), new("BAIN", new RectD(106, 150, 40, 14)),
        };
        var page = new Page { Id = "x" };
        PageAnalyzer.Analyze(page, blank, words);
        Equal(1, page.Tables.Count, "tableau de repli");
        Check(!page.Tables[0].HasGrid, "repli sans grille");
        var lines = ExportLines(new InventoryProject { Pages = [page] });
        Equal("1 chaise\t10\t5|SALLE BAIN", string.Join("|", lines), "colonnes devinées par les espaces");
        Check(page.AllFields().All(f => !f.Locked), "sans tableau, rien n'est verrouillé d'office");
    }
}
