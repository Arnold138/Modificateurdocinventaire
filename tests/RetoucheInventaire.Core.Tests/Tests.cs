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

    // ---------- Lots ----------

    public static void T_Lots_un_par_objet_continus_entre_les_pages()
    {
        var p = ThreePages();
        Lots.Number(p, 1);
        var objects = Lots.ObjectLines(p).ToList();
        Equal("1", objects[0].Line.Lot, "premier lot");
        Equal(objects.Count.ToString(), objects[^1].Line.Lot, "dernier lot = nombre d'objets");
        var lines = p.Pages.SelectMany(pg => pg.AllLines()).ToList();
        Check(lines.Where(l => l.Kind is LineKind.Titre or LineKind.Total or LineKind.EnTete).All(l => l.Lot == ""), "pas de lot sur titres, totaux, en-têtes");
        var cd3 = lines.First(l => l.Cells[0].Text.StartsWith("CD3+"));
        Equal("", cd3.Lot, "pas de lot sur la suite d'un objet");
        var laves = lines.First(l => l.Cells[0].Text.StartsWith("2 laves"));
        Equal("1", laves.Lot, "l'objet sous « DANS L'ENTREPOT » porte le lot 1");
        var page2First = Lots.ObjectLines(p).First(o => o.Page == p.Pages[1]).Line;
        var page1Last = Lots.ObjectLines(p).Last(o => o.Page == p.Pages[0]).Line;
        Equal(int.Parse(page1Last.Lot) + 1, int.Parse(page2First.Lot), "numérotation continue d'une page à l'autre");
    }

    public static void T_Lots_un_numero_tape_ne_change_rien_d_autre()
    {
        var p = ThreePages();
        Lots.Number(p, 1);
        var objects = Lots.ObjectLines(p).Select(o => o.Line).ToList();
        Lots.Set(p, objects[9], "81");
        Equal("9", objects[8].Lot, "les lots d'avant ne bougent pas");
        Equal("81", objects[9].Lot, "lot saisi");
        Equal("11", objects[10].Lot, "le suivant ne bouge pas tout seul");
        Lots.FillAutomatically(p);
        Equal("82", objects[10].Lot, "« Synchroniser » : la suite repart du numéro tapé");
        Equal((81 + objects.Count - 10).ToString(), objects[^1].Lot, "toute la suite est recalée");
        PageEdits.CycleKind(objects[11]); // Article → Titre
        Lots.Resync(p);
        Equal("", objects[11].Lot, "plus de lot sur un titre");
        Equal("84", objects[12].Lot, "les autres ne bougent pas");
        Lots.FillAutomatically(p);
        Equal("83", objects[12].Lot, "la suite reprend à la synchronisation");
        Lots.Clear(p);
        Check(!Lots.IsActive(p), "numérotation enlevée");
    }

    public static void T_Lots_poubelle_et_ajout()
    {
        var p = ThreePages();
        Lots.Number(p, 1);
        var objects = Lots.ObjectLines(p).Select(o => o.Line).ToList();
        Lots.Remove(p, objects[30]);
        Equal("", objects[30].Lot, "ligne retirée à la poubelle");
        Check(!objects[30].LotSlot, "case retirée");
        Equal("32", objects[31].Lot, "le suivant garde son numéro");
        Check(Lots.CanAdd(objects[30]), "« + » proposé");
        Lots.Add(p, objects[30]);
        Check(objects[30].LotSlot && objects[30].Lot == "", "case vide remise avec « + »");
        Lots.FillAutomatically(p);
        Equal("31", objects[30].Lot, "synchronisée");
        // « + » sur une ligne de suite : un objet de plus.
        var cd3 = p.Pages[0].AllLines().First(l => l.Cells[0].Text.StartsWith("CD3+"));
        var row = p.Pages[0].Tables[0].Rows.First(r => r.Lines.Contains(cd3));
        Check(!Lots.CanAdd(cd3, row), "pas de « + » sur une suite qui n'a pas l'air d'un objet");
        Lots.Add(p, cd3);
        Check(cd3.LotSlot, "case ajoutée sur une ligne de suite");
    }

    public static void T_Lots_ni_totaux_ni_en_tete_ni_recapitulatif()
    {
        var rows = new List<string[]>
        {
            new[] { "RECAPITULATIF", "", "" }, new[] { "", "exploitation", "réalisation" },
            new[] { "I - MOBILIER ET MATERIEL", "148 760", "47 405" }, new[] { "II - STOCK", "44 550", "9 000" },
            new[] { "TOTAL GENERAL", "193 310", "56 405" },
        };
        var p = new InventoryProject();
        p.Pages.Add(RetoucheInventaire.Core.Import.DataPageBuilder.Build("recap.csv", rows));
        var lines = p.Pages[0].Tables[0].Rows.SelectMany(r => r.Lines).ToList();
        Equal(LineKind.EnTete, lines[1].Kind, "en-tête du récapitulatif");
        Equal(LineKind.Total, lines[4].Kind, "total");
        Lots.CreateEmpty(p);
        Equal(0, Lots.ObjectLines(p).Count(), "aucun objet");
    }

    public static void T_Projet_ancien_rangee_de_total_vide_modifiable()
    {
        var page = Fixtures.Analyze("page5");
        page.Tables[0].Rows[5].Lines.Clear();
        PageAnalyzer.Upgrade(page);
        var lines = page.Tables[0].Rows[5].Lines;
        Equal(1, lines.Count, "une ligne ajoutée");
        Check(lines[0].Cells.Count == 3 && lines[0].Cells.All(c => c.Box.Width > 10 && c.Id.Length > 0), "cases modifiables");
        PageAnalyzer.Upgrade(page);
        Equal(1, page.Tables[0].Rows[5].Lines.Count, "pas en double");
    }

    public static void T_Rangee_sans_texte_lu_cases_modifiables()
    {
        var (image, words) = Fixtures.Load("page5");
        var page = new Page { Id = "page5", Source = PageSource.Image };
        PageAnalyzer.Analyze(page, image, words.Where(w => !(w.Box.CenterY > 530 && w.Box.CenterY < 556)).ToList());
        var row = page.Tables[0].Rows[5];
        Equal(1, row.Lines.Count, "une ligne vide");
        Equal(3, row.Lines[0].Cells.Count, "une case par colonne");
        Check(row.Lines[0].Cells.All(c => !c.Locked && c.Text == "" && c.Box.Width > 10), "cases modifiables");
        Check(row.Lines[0].Cells[0].FontSize > 10, "taille d'écriture du tableau");
    }

    public static void T_Lots_colonne_tracee_dans_la_marge()
    {
        var p = new InventoryProject();
        var page = Fixtures.Analyze("page5");
        p.Pages.Add(page);
        Lots.Number(p, 1);
        var image = Fixtures.Load("page5").Image;
        var frames = LotColumn.Frames(image, page);
        Equal(3, frames.Count, "une colonne par tableau");
        Equal("0,1,2,3,4", string.Join(",", LotColumn.FramedRows(page.Tables[0])), "en-tête et objets, pas TOTAL STOCK");
        var drawn = LotColumn.Draw(image, page);
        int changed = 0, outside = 0;
        for (int y = 0; y < image.Height; y++)
            for (int x = 0; x < image.Width; x++)
            {
                if (drawn.Data[y * image.Width + x] == image.Data[y * image.Width + x]) continue;
                changed++;
                if (!frames.Any(f => x >= f.Left - 8 && x < f.Right && f.Rows.Any(r => y >= f.Table.RowYs[r] - 8 && y <= f.Table.RowYs[r + 1] + 8))) outside++;
            }
        Check(changed > 1000, $"traits tracés : {changed} pixels");
        Equal(0, outside, "pixels modifiés hors de la colonne");
        int total = (int)Math.Round(page.Tables[0].RowYs[5] + 6), onTotal = 0;
        for (int x = frames[0].Left - 4; x < frames[0].Right; x++) if (drawn.Data[total * image.Width + x] != image.Data[total * image.Width + x]) onTotal++;
        Equal(0, onTotal, "rien dessiné sur la rangée TOTAL STOCK");
        foreach (var (line, box) in Lots.Labels(page))
            Check(box.Left >= 0 && box.Right <= page.Tables[0].ColumnXs[0] + 1, $"lot {line.Lot} hors de la colonne");
    }

    public static void T_Lots_zero_en_fin_de_txt()
    {
        var p = ThreePages();
        Lots.Number(p, 1);
        var objects = Lots.ObjectLines(p).Select(o => o.Line).ToList();
        Lots.Set(p, objects[3], "0");
        Lots.FillAutomatically(p);
        Equal("0", objects[3].Lot, "lot 0 gardé");
        Equal("4", objects[4].Lot, "le 0 ne prend pas de numéro");
        Equal((objects.Count - 1).ToString(), objects[^1].Lot, "la suite continue");
        var lines = ExportLines(p);
        Equal("0\t2 rolls en métal\t60\t30", lines[^1], "objet à 0 regroupé à la fin");
        Check(lines.Count(l => l.Contains("2 rolls en métal")) == 1, "retiré de sa place");
        // Un objet avec sa suite (« CD3+ … ») part en entier.
        var compresseur = p.Pages[0].AllLines().First(l => l.Cells[0].Text.StartsWith("1 compresseur"));
        Lots.Set(p, compresseur, "0");
        lines = ExportLines(p);
        Check(lines[^2].StartsWith("0\t1 compresseur") && lines[^1] == "\tCD3+ +/- 14130 heures", "objet et sa suite en fin d'inventaire");
    }

    public static void T_Rangee_qui_grandit_puis_revient()
    {
        var page = Fixtures.Analyze("page1");
        // Positions arrondies au millionième (les décalages successifs laissent des poussières de calcul).
        string Geometry() => System.Text.RegularExpressions.Regex.Replace(System.Text.Json.JsonSerializer.Serialize(page), @"-?\d+\.\d+",
            m => Math.Round(double.Parse(m.Value, System.Globalization.CultureInfo.InvariantCulture), 6).ToString(System.Globalization.CultureInfo.InvariantCulture));
        var before = Geometry();
        var lines = page.Tables[0].Rows.SelectMany(r => r.Lines).ToList();
        var line = lines.First(l => l.Cells[0].Text == "2 rolls en métal");
        var next = lines[lines.IndexOf(line) + 1];
        var above = lines[lines.IndexOf(line) - 1];
        double nextY = next.Cells[0].Box.Y, aboveY = above.Cells[0].Box.Y, h = line.Cells[0].Box.Height;
        int height = page.Height;
        Check(PageLayout.SetExtra(page, line, 40), "agrandie");
        Equal(nextY + 40, next.Cells[0].Box.Y, "la ligne suivante descend");
        Equal(aboveY, above.Cells[0].Box.Y, "la ligne du dessus ne bouge pas");
        Equal(h + 40, line.Cells[0].Box.Height, "la case grandit");
        Equal(height + 40, page.Height, "la page s'allonge");
        Check(line.ExtraAt > line.Cells[0].Box.Y && line.ExtraAt < nextY, "bande entre les deux lignes");
        PageLayout.SetExtra(page, line, 25);
        Equal(nextY + 25, next.Cells[0].Box.Y, "réduite");
        PageLayout.SetExtra(page, line, 0);
        Equal(before, Geometry(), "revenue exactement à l'origine");

        var image = Fixtures.Load("page1").Image;
        PageLayout.SetExtra(page, line, 30);
        var big = PageLayout.Stretch(image, page);
        Equal(image.Height + 30, big.Height, "image allongée");
        int at = line.ExtraAt;
        Check(image.Data.AsSpan(0, at * image.Width).SequenceEqual(big.Data.AsSpan(0, at * image.Width)), "haut inchangé");
        Check(image.Data.AsSpan(at * image.Width).SequenceEqual(big.Data.AsSpan((at + 30) * image.Width)), "bas décalé de 30 pixels, intact");
        Equal(2, PageLayout.ExtraFor(line, line.Cells[0], 1, 20) == 0 ? 2 : 0, "une seule ligne : rien à ajouter");
        Check(PageLayout.ExtraFor(line, line.Cells[0], 3, 20) > 30, "trois lignes : de la place en plus");
    }

    public static void T_Tri_naturel_des_noms()
    {
        var names = new List<string> { "page 10.jpg", "Page 2.jpg", "page 1.jpg", "annexe.pdf" };
        names.Sort(RetoucheInventaire.Core.Import.Names.NaturalCompare);
        Equal("annexe.pdf|page 1.jpg|Page 2.jpg|page 10.jpg", string.Join("|", names), "ordre");
    }

    public static void T_Texte_coupe_en_lignes()
    {
        var lines = TextWrap.Wrap("un chariot gris et blanc\nde 180 cm", 12, s => s.Length);
        Equal("un chariot|gris et|blanc|de 180 cm", string.Join("|", lines), "lignes");
    }

    public static void T_Texte_barre_suit_la_saisie()
    {
        var f = new Field { Text = "2 distributeurs de savon + 1 distributeur papier", OriginalText = "2 distributeurs de savon + 1 distributeur papier" };
        int a = f.Text.IndexOf("+ 1");
        Strikes.Toggle(f, a, f.Text.Length, Strikes.Red);
        Check(Strikes.IsStruck(f, a, f.Text.Length) && !Strikes.IsStruck(f, 0, a), "morceau barré");
        // Texte tapé au début : le trait suit.
        string old = f.Text; f.Text = "Lot : " + old; Strikes.Adjust(f, old, f.Text);
        Equal("+ 1 distributeur papier", f.Text.Substring(f.Strikes[0].Start, f.Strikes[0].Length), "trait décalé");
        // Mot effacé au milieu du morceau barré : le trait raccourcit.
        old = f.Text; f.Text = old.Replace("distributeur papier", "papier"); Strikes.Adjust(f, old, f.Text);
        Equal("+ 1 papier", f.Text.Substring(f.Strikes[0].Start, f.Strikes[0].Length), "trait raccourci");
        // Barrer à nouveau le même morceau : trait retiré.
        Strikes.Toggle(f, f.Strikes[0].Start, f.Strikes[0].Start + f.Strikes[0].Length, Strikes.Red);
        Equal(0, f.Strikes.Count, "trait retiré");
        // Le .txt garde le texte barré tel quel.
        Strikes.Toggle(f, 0, 0, Strikes.Black);
        Equal(1, f.Strikes.Count, "toute la case barrée sans sélection");
        Equal(Strikes.Black, f.Strikes[0].Color, "trait noir");
    }

    public static void T_Lots_colonne_vide_remplie_a_la_main()
    {
        var p = ThreePages();
        Lots.CreateEmpty(p);
        var objects = Lots.ObjectLines(p).Select(o => o.Line).ToList();
        Check(Lots.IsActive(p) && objects.All(l => l.Lot == "" && l.LotSlot), "toutes les cases vides");
        Equal(objects.Count, p.Pages.Sum(pg => Lots.Labels(pg).Count), "une case par objet à l'écran");
        Lots.Set(p, objects[0], "12");
        Lots.Set(p, objects[1], "0");
        Equal("", objects[2].Lot, "rien ne se remplit tout seul");
        var lines = ExportLines(p);
        Equal("Lot\tI - MOBILIER ET MATERIEL\texploitation\tréalisation", lines[0], "colonne Lot dans le .txt");
        Lots.FillAutomatically(p);
        Equal("12", objects[0].Lot, "numéro tapé gardé");
        Equal("0", objects[1].Lot, "lot 0 gardé");
        Equal("13", objects[2].Lot, "le reste rempli à la suite");
    }

    /// <summary>Scénario partagé avec la version iPad (commande export-arrange, test JS).</summary>
    public static InventoryProject ArrangeScenario()
    {
        var p = new InventoryProject();
        foreach (var n in new[] { "page1", "page5" }) p.Pages.Add(Fixtures.Analyze(n));
        Lots.CreateEmpty(p);
        Lots.FillAutomatically(p);
        PrintedLine Find(Page pg, string t) => pg.AllLines().First(l => l.Cells[0].Text == t);
        Lots.Set(p, Find(p.Pages[0], "2 rolls en métal"), "0");
        Lots.Set(p, Find(p.Pages[0], "1 gazelle TULESCA inox"), "0");
        Lots.Set(p, Find(p.Pages[1], "CARTONNAGE"), "0");
        p.Pages[1].Tables[2].RowOrder = [0, 2, 1, 3]; // « Appartient à Orange » monté au-dessus de VDM
        Find(p.Pages[1], "EN COURS DE FABRICATION").Cells[1].Text = "150";
        return p;
    }

    public static List<Raster> ComposeAll(InventoryProject p, Arrangement.Plan plan, Func<Page, Raster> imageOf)
    {
        var images = p.Pages.Select(imageOf).ToList();
        return p.Pages.Select((pg, k) => Arrangement.ComposePage(plan, k, images[k],
            plan.Pages[k].Pieces.Where(pc => pc.IsUnit).ToDictionary(pc => pc.Unit!, pc => (Arrangement.Crop?)Arrangement.UnitCrop(pc.Unit!, images[pc.Unit!.P])))).ToList();
    }

    public static void T_Lot_zero_passe_apres_le_dernier_lot()
    {
        var p = ArrangeScenario();
        var plan = Arrangement.Build(p);
        Equal(3, plan.Units.Count, "trois objets déplacés");
        Equal("1,2,3", $"{plan.Dest!.P},{plan.Dest.Ti},{plan.Dest.R}", "après « Appartenant à ULMA »");
        Check(plan.Units[1].Title != null && !plan.Units[1].Whole, "gazelle : objet seul + valeurs du titre");
        int cut0 = plan.Units.Where(u => u.P == 0).Sum(u => u.Bottom - u.Top);
        Equal(p.Pages[0].Height - cut0, plan.Pages[0].Height, "page 1 raccourcie");
        int add = plan.Units.Sum(u => u.Height) - plan.Units.Where(u => u.P == 1).Sum(u => u.Bottom - u.Top);
        Equal(p.Pages[1].Height + add, plan.Pages[1].Height, "page 5 allongée");
        var v0 = Arrangement.ViewOf(p, plan, 0);
        var v1 = Arrangement.ViewOf(p, plan, 1);
        Check(!v0.Lines.Any(l => l.Line.Cells[0].Text == "2 rolls en métal"), "retirée de la page 1");
        Equal("2 rolls en métal|1 gazelle TULESCA inox|CARTONNAGE|42057 € à l'achat HT",
            string.Join("|", v1.Lines.Where(l => l.Moved && !l.CarriedOnly).Select(l => l.Line.Cells[0].Text)), "recollées en fin, dans l'ordre");
        Equal(1, v0.Lines.First(l => l.Line.Cells[0].Text == "SALLE LAVAGE :").Cells.Count, "le titre reste, sans ses valeurs");
        var images = ComposeAll(p, plan, pg => LotColumn.Draw(Fixtures.Load(pg.Id).Image, pg));
        var src = LotColumn.Draw(Fixtures.Load("page1").Image, p.Pages[0]);
        var u = plan.Units[0];
        int y = u.Bottom + 40, w = src.Width;
        Check(src.Data.AsSpan(y * w, w).SequenceEqual(images[0].Data.AsSpan((y - (u.Bottom - u.Top)) * w, w)), "la suite remonte intacte");
        // Le numéro repris : tout revient.
        Lots.Set(p, p.Pages[0].AllLines().First(l => l.Cells[0].Text == "2 rolls en métal"), "4");
        Lots.Set(p, p.Pages[0].AllLines().First(l => l.Cells[0].Text == "1 gazelle TULESCA inox"), "28");
        Lots.Set(p, p.Pages[1].AllLines().First(l => l.Cells[0].Text == "CARTONNAGE"), "31");
        p.Pages[1].Tables[2].RowOrder = null;
        var back = Arrangement.Build(p);
        Equal(0, back.Units.Count, "plus rien à déplacer");
        Check(!Arrangement.PageArranged(p, back, 0) && !Arrangement.PageArranged(p, back, 1), "pages d'origine");
    }

    public static void T_Rangee_deplacee_a_la_main()
    {
        var p = new InventoryProject();
        p.Pages.Add(Fixtures.Analyze("page1"));
        Lots.Number(p, 1);
        var t = p.Pages[0].Tables[0];
        var order = Enumerable.Range(0, t.Rows.Count).ToList();
        order.RemoveAt(2); order.Insert(5, 2); // « aspirateur » descend sous « distributeurs »
        t.RowOrder = order;
        var l = ExportLines(p).ToList();
        Check(l.FindIndex(x => x.Contains("aspirateur")) > l.FindIndex(x => x.Contains("distributeurs")), ".txt dans le nouvel ordre");
        Lots.FillAutomatically(p);
        Equal("5", p.Pages[0].AllLines().First(x => x.Cells[0].Text.StartsWith("1 aspirateur")).Lot, "numéro selon l'ordre affiché");
        var img = Fixtures.Load("page1").Image;
        var plan = Arrangement.Build(p);
        var outImg = ComposeAll(p, plan, _ => img)[0];
        Equal(img.Height, outImg.Height, "même hauteur");
        int a0 = (int)Math.Floor(t.RowYs[2] + 0.5) - 2, a1 = (int)Math.Floor(t.RowYs[3] + 0.5) - 2, e0 = (int)Math.Floor(t.RowYs[6] + 0.5) - 2;
        var moved = plan.Pages[0].Pieces.First(pc => !pc.IsUnit && pc.Y0 == a0);
        Equal(e0 - (a1 - a0), moved.Dst, "rangée posée sous « distributeurs »");
        Check(img.Data.AsSpan(a0 * img.Width, (a1 - a0) * img.Width).SequenceEqual(outImg.Data.AsSpan(moved.Dst * img.Width, (a1 - a0) * img.Width)), "pixels intacts");
    }

    public static void T_Totaux_recalcules_et_saisie_a_la_main()
    {
        Equal(4455000L, Totals.ParseAmount("44 550"), "lecture");
        Equal(550000L, Totals.ParseAmount("S 500"), "lecture OCR");
        Equal(0L, Totals.ParseAmount("mémoire"), "mémoire");
        Equal("198 310", Totals.FormatAmount(19831000), "écriture");
        var p = new InventoryProject();
        p.Pages.Add(Fixtures.Analyze("page5"));
        p.Pages.Add(RetoucheInventaire.Core.Import.DataPageBuilder.Build("recap.csv", new List<string[]>
        {
            new[] { "", "exploitation", "réalisation" }, new[] { "II - STOCK", "44 550", "9 000" }, new[] { "TOTAL GENERAL", "44 550", "9 000" },
        }));
        PrintedLine Find(Page pg, string t) => pg.AllLines().First(l => l.Cells[0].Text == t);
        var total = Find(p.Pages[0], "TOTAL STOCK");
        var recap = Find(p.Pages[1], "II - STOCK");
        var general = Find(p.Pages[1], "TOTAL GENERAL");
        Equal(0, Totals.Recompute(p).Count, "rien ne change sans modification");
        Find(p.Pages[0], "CARTONNAGE").Cells[1].Text = "17 000";
        Totals.Recompute(p);
        Equal("45 550", total.Cells[1].Text, "total de la rubrique");
        Equal("45 550", recap.Cells[1].Text, "récapitulatif");
        Equal("45 550", general.Cells[1].Text, "total général");
        Lots.CreateEmpty(p);
        Lots.Set(p, Find(p.Pages[0], "PRODUITS FINIS"), "0");
        Totals.Recompute(p, Arrangement.Build(p));
        Equal("42 050", total.Cells[1].Text, "lot 0 retiré du total");
        Equal("8 000", total.Cells[2].Text, "autre colonne");
        total.Cells[1].Text = "50 000"; total.Cells[1].Manual = true;
        Totals.Recompute(p);
        Equal("50 000", total.Cells[1].Text, "tapé à la main : gardé");
        Equal("50 000", recap.Cells[1].Text, "le récapitulatif suit le total tapé");
        total.Cells[1].Manual = false;
        Totals.Recompute(p, Arrangement.Build(p));
        Equal("42 050", total.Cells[1].Text, "Recalculer");
    }

    public static void T_Couleur_du_texte()
    {
        var f = new Field { Text = "2 rolls en métal", OriginalText = "2 rolls en métal" };
        Strikes.SetColor(f, 2, 7, "red");
        Equal("-|red|-", string.Join("|", Strikes.ColorRuns(f).Select(r => r.Color ?? "-")), "morceaux");
        var old = f.Text; f.Text = "Lot " + old; Strikes.Adjust(f, old, f.Text);
        Equal("rolls", f.Text.Substring(f.Colors[0].Start, f.Colors[0].Length), "suit le texte");
        Strikes.SetColor(f, f.Colors[0].Start, f.Colors[0].Start + f.Colors[0].Length, "red");
        Equal(0, f.Colors.Count, "même couleur une 2e fois : retirée");
        Strikes.SetColor(f, 0, 0, "blue");
        Equal(f.Text.Length, f.Colors[0].Length, "sans sélection : toute la case");
    }

    public static void T_Nom_decompose_iphone_repare()
    {
        var nfd = "BEENDHI prisÃ©.pdf".Normalize(System.Text.NormalizationForm.FormD);
        Equal("BEENDHI prisé.pdf", RetoucheInventaire.Core.Import.Names.Repair(nfd), "nom iPhone réparé");
    }

    public static void T_Lots_dans_le_txt()
    {
        var p = ThreePages();
        Lots.Number(p, 1);
        var lines = ExportLines(p);
        Equal("Lot\tI - MOBILIER ET MATERIEL\texploitation\tréalisation", lines[0], "en-tête");
        Equal("\tDANS L'ENTREPOT", lines[1], "titre sans lot");
        Equal("1\t2 laves main en inox + 1 sanibroyeur SPEED\t130\t70", lines[2], "objet avec lot");
        Check(lines.Contains("\tCD3+ +/- 14130 heures"), "suite d'objet sans lot");
        Check(lines.Any(l => l.StartsWith("\tTOTAL STOCK\t44550")), "total sans lot");
    }

    public static void T_Nom_de_fichier_abime_repare()
    {
        Equal("BEENDHI prisé.pdf", RetoucheInventaire.Core.Import.Names.Repair("BEENDHI prisÃ©.pdf"), "réparation");
        Equal("Prix 50 €", RetoucheInventaire.Core.Import.Names.Repair("Prix 50 €"), "nom correct inchangé");
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

    // ---------- Lignes ajoutées (Dupliquer, Séparer ici) ----------

    private static string RoundJson(Page page) => Regex.Replace(System.Text.Json.JsonSerializer.Serialize(page), @"-?\d+\.\d+",
        m => Math.Round(double.Parse(m.Value, System.Globalization.CultureInfo.InvariantCulture), 6).ToString(System.Globalization.CultureInfo.InvariantCulture));

    private static PrintedLine FindLine(Page pg, string t) => pg.AllLines().First(l => l.Cells[0].Text == t);

    public static void T_Dupliquer_une_ligne()
    {
        var p = new InventoryProject();
        p.Pages.Add(Fixtures.Analyze("page1"));
        var page = p.Pages[0];
        var t = page.Tables[0];
        string before = RoundJson(page);
        var txt = ExportLines(p);
        var rolls = FindLine(page, "2 rolls en métal");
        var next = FindLine(page, "2 distributeurs de savon + 1 distributeur papier");
        double y = next.Cells[0].Box.Y;
        int n = t.Rows.Count, h = page.Height, baseH = PageLayout.BaseHeight(page);
        Check(Rows.CanCopy(page, rolls) && !Rows.CanCopy(page, FindLine(page, "I - MOBILIER ET MATERIEL")), "pas l'en-tête");
        var copy = Rows.Copy(page, rolls)!;
        int H = page.Height - h;
        Check(H > 20 && H < 80, $"hauteur de la rangée ({H})");
        Equal(n + 1, t.Rows.Count, "une rangée de plus");
        Check(t.Rows[5].Lines[0] == copy, "juste sous la ligne copiée");
        Equal("2 rolls en métal|60|30", string.Join("|", copy.Cells.Select(c => c.Text)), "copie du texte et des valeurs");
        Equal(y + H, next.Cells[0].Box.Y, "la suite descend");
        Equal(baseH, PageLayout.BaseHeight(page), "image d'origine inchangée");
        var l = ExportLines(p);
        int k = Array.IndexOf(l, "2 rolls en métal\t60\t30");
        Equal("2 rolls en métal\t60\t30", l[k + 1], "copie juste après dans le .txt");
        Equal(string.Join("\n", txt), string.Join("\n", l.Where((_, i) => i != k + 1)), "le reste du .txt identique");
        var img = Fixtures.Load("page1").Image;
        var big = PageLayout.Stretch(img, page);
        Equal(img.Height + H, big.Height, "image allongée");
        int yc = (int)Math.Round(t.RowYs[6]) - 2;
        Check(img.Data.AsSpan((yc - H) * img.Width).SequenceEqual(big.Data.AsSpan(yc * img.Width, (img.Height - yc + H) * img.Width)), "bas décalé intact");
        Lots.CreateEmpty(p);
        Check(copy.LotSlot && copy.Lot == "", "case de lot vide");
        Lots.FillAutomatically(p);
        Equal((int.Parse(rolls.Lot) + 1).ToString(), copy.Lot, "numéro suivant");
        Lots.Clear(p);
        Check(Rows.AddedRowOf(page, copy) != null && Rows.AddedRowOf(page, rolls) == null, "rangée ajoutée reconnue");
        Check(Rows.RemoveAdded(page, copy), "supprimée");
        Equal(before, RoundJson(page), "page revenue à l'origine");
    }

    public static void T_Separer_ici()
    {
        var p = new InventoryProject();
        p.Pages.Add(Fixtures.Analyze("page1"));
        var page = p.Pages[0];
        var t = page.Tables[0];
        string before = RoundJson(page);
        var txt = ExportLines(p);
        var comp = FindLine(page, "1 compresseur à vis ATLASCAPCO avec cuve 250L + 1 sécheur ATLASCAPCO");
        var cd3 = FindLine(page, "CD3+ +/- 14130 heures");
        Check(!Rows.CanSplit(page, comp) && Rows.CanSplit(page, cd3), "pas sur la 1re ligne de la rangée");
        int h = page.Height;
        double y = cd3.Cells[0].Box.Y;
        Check(Rows.Split(page, cd3), "séparée");
        Equal(5, page.Height - h, "un filet de plus");
        Equal(y + 5, cd3.Cells[0].Box.Y, "la ligne descend du filet");
        Check(t.Rows[15].Lines.Count == 1 && t.Rows[16].Lines[0] == cd3, "rangées");
        Equal(string.Join("\n", txt), string.Join("\n", ExportLines(p)), ".txt identique (sans lots)");
        Lots.CreateEmpty(p);
        Check(comp.LotSlot && cd3.LotSlot, "chaque rangée a sa case de lot");
        Lots.Clear(p);
        Check(Rows.RemoveAdded(page, cd3), "remise dans la rangée du dessus");
        Equal(before, RoundJson(page), "page revenue à l'origine");
    }

    public static void T_Separer_un_texte_tape_et_totaux_des_copies()
    {
        var p = new InventoryProject();
        p.Pages.Add(Fixtures.Analyze("page1"));
        var rolls = FindLine(p.Pages[0], "2 rolls en métal");
        rolls.Cells[0].Text = "2 rolls en métal\nLOT 6 : 1 table de travail";
        var typed = Rows.SplitTypedText(p.Pages[0], rolls, 1)!;
        Equal("2 rolls en métal|60|30", string.Join("|", rolls.Cells.Select(c => c.Text)), "texte du dessus");
        Equal("LOT 6 : 1 table de travail||", string.Join("|", typed.Cells.Select(c => c.Text)), "suite du texte, prix à taper");

        var q = new InventoryProject();
        q.Pages.Add(Fixtures.Analyze("page5"));
        var page = q.Pages[0];
        var total = FindLine(page, "TOTAL STOCK");
        var carton = FindLine(page, "CARTONNAGE");
        var t = page.Tables.First(x => x.Rows.Any(r => r.Lines.Contains(carton)));
        t.RowOrder = [0, 2, 1, 3, 4, 5];
        var copy = Rows.Copy(page, carton)!;
        Equal("0,2,3,1,4,5,6", string.Join(",", t.RowOrder), "ordre d'affichage : la copie suit sa ligne");
        Totals.Recompute(q);
        Equal("60 550", total.Cells[1].Text, "copie ajoutée au total");
        carton.Cells[1].Text = "8 000"; copy.Cells[1].Text = "8 000";
        Totals.Recompute(q);
        Equal("44 550", total.Cells[1].Text, "partagé en deux : total inchangé");
        Check(Rows.RemoveAdded(page, copy), "supprimée");
        Equal("0,2,1,3,4,5", string.Join(",", t.RowOrder), "ordre rétabli");
    }

    /// <summary>Scénario partagé avec l'iPad (commande export-rows) : copies, copie de copie, séparations, bandes.</summary>
    public static InventoryProject RowsScenario()
    {
        var p = new InventoryProject();
        foreach (var n in new[] { "page1", "page5" }) p.Pages.Add(Fixtures.Analyze(n));
        Page a = p.Pages[0], b = p.Pages[1];
        var rolls = Rows.Copy(a, FindLine(a, "2 rolls en métal"))!;
        Rows.Copy(a, rolls);
        Rows.Split(a, FindLine(a, "CD3+ +/- 14130 heures"));
        var carton = FindLine(b, "CARTONNAGE");
        var copy = Rows.Copy(b, carton)!;
        carton.Cells[1].Text = "8 000"; copy.Cells[1].Text = "8 000";
        copy.Cells[0].Text = "CARTONNAGE (2e lot)";
        PageLayout.SetExtra(b, copy, 37);
        Rows.Copy(b, copy);
        Rows.Split(b, FindLine(b, "42057 € à l'achat HT"));
        PageLayout.SetExtra(a, FindLine(a, "2 souffiants STANLEY"), 25);
        var vest = FindLine(a, "2 vestiaires avec 1 porte en métal");
        vest.Cells[0].Text = "2 vestiaires avec 1 porte en métal\nLOT 6 : 1 table de travail";
        Rows.SplitTypedText(a, vest, 1)!.Cells[1].Text = "20";
        Rows.AddBlank(a, FindLine(a, "1 diable à plateau à roulettes"), above: true)!.Cells[0].Text = "Ajoutée au-dessus";
        var dans = FindLine(a, "DANS L'ENTREPOT");
        Rows.SplitBelow(a, dans);
        Rows.TrimRow(a, dans);
        Rows.TrimRow(a, FindLine(a, "2 laves main en inox + 1 sanibroyeur SPEED"));
        Rows.Copy(a, dans);
        Lots.CreateEmpty(p);
        Lots.FillAutomatically(p);
        Totals.Recompute(p, Arrangement.Build(p));
        return p;
    }

    public static void T_Lignes_ajoutees_scenario_complet()
    {
        var p = RowsScenario();
        foreach (var page in p.Pages)
        {
            Equal(page.Height, PageLayout.Stretch(Fixtures.Load(page.Id).Image, page).Height, $"hauteur {page.Id}");
            foreach (var t in page.Tables.Where(t => t.HasGrid)) Equal(t.Rows.Count + 1, t.RowYs.Count, "filets");
        }
        Equal("52 550", FindLine(p.Pages[1], "TOTAL STOCK").Cells[1].Text, "total");
        // Enregistré puis relu : tout est gardé.
        var path = Path.Combine(Path.GetTempPath(), "lignes-ajoutees.inventaire");
        ProjectStore.Save(path, p, new Dictionary<string, byte[]>());
        var back = ProjectStore.Load(path).Project;
        Equal(RoundJson(p.Pages[1]), RoundJson(back.Pages[1]), "relu à l'identique");
    }

    public static void T_Ligne_vide_au_dessus_en_dessous()
    {
        var p = new InventoryProject();
        p.Pages.Add(Fixtures.Analyze("page5"));
        var page = p.Pages[0];
        string before = RoundJson(page);
        var produits = FindLine(page, "PRODUITS FINIS");
        var total = FindLine(page, "TOTAL STOCK");
        var t = page.Tables.First(x => x.Rows.Any(r => r.Lines.Contains(produits)));
        var below = Rows.AddBlank(page, produits)!;
        int rb = t.Rows.FindIndex(r => r.Lines.Contains(below));
        Check(t.Rows[rb - 1].Lines.Contains(produits) && t.Rows[rb + 1].Lines.Contains(total), "entre PRODUITS FINIS et TOTAL STOCK");
        Check(below.Cells.All(c => c.Text == "" && c.OriginalText == ""), "cases vides");
        Lots.CreateEmpty(p);
        Check(below.LotSlot && !total.LotSlot, "case de lot vide sur la ligne vide, pas sur le total");
        var above = Rows.AddBlank(page, produits, above: true)!;
        Check(t.Rows[t.Rows.FindIndex(r => r.Lines.Contains(above)) + 1].Lines.Contains(produits), "juste au-dessus");
        Lots.FillAutomatically(p);
        Equal(int.Parse(produits.Lot) - 1, int.Parse(above.Lot), "numérotée avant");
        Equal(int.Parse(produits.Lot) + 1, int.Parse(below.Lot), "numérotée après");
        Check(Rows.AddBlank(page, FindLine(page, "II - STOCK")) is null, "pas sur l'en-tête");
        Equal(page.Height, PageLayout.Stretch(Fixtures.Load("page5").Image, page).Height, "image");
        Lots.Clear(p);
        Rows.RemoveAdded(page, above);
        Rows.RemoveAdded(page, below);
        Equal(before, RoundJson(page), "page revenue à l'origine");
    }

    public static void T_Lots_page_par_page()
    {
        var p = ThreePages();
        var choices = p.Pages.Select(Lots.LotTables).ToList();
        Check(choices[0].Any(x => x.Objects) && choices[2].Any(x => x.Objects), "pages d'objets proposées");
        var chosen = choices[0].Select(x => x.Table).ToHashSet();
        Lots.SetTables(p, chosen);
        Lots.CreateEmpty(p);
        Lots.FillAutomatically(p);
        Equal("0", string.Join(",", Lots.ObjectLines(p).Select(o => p.Pages.IndexOf(o.Page)).Distinct()), "seulement la page 1");
        Check(p.Pages[1].AllLines().All(l => !l.LotSlot && l.Lot.Length == 0), "page 2 sans case");
        string first = Lots.ObjectLines(p).First().Line.Lot;
        chosen.Add(choices[2][0].Table);
        Lots.SetTables(p, chosen);
        Equal(first, Lots.ObjectLines(p).First().Line.Lot, "numéros gardés");
        Check(Lots.ObjectLines(p).Any(o => o.Page == p.Pages[2]) && Lots.ObjectLines(p).All(o => o.Page != p.Pages[2] || o.Table == choices[2][0].Table), "un seul tableau de la page 3");
        Lots.SetTables(p, new HashSet<Table>());
        Check(!Lots.IsActive(p), "plus aucun lot");
    }

    public static void T_Recadrer_garde_lots_et_corrections()
    {
        var p = new InventoryProject();
        p.Pages.Add(Fixtures.Analyze("page1"));
        Lots.CreateEmpty(p);
        Lots.FillAutomatically(p);
        var old = p.Pages[0];
        var rolls = FindLine(old, "2 rolls en métal");
        Lots.Set(p, rolls, "42");
        rolls.Cells[1].Text = "70";
        var fresh = Fixtures.Analyze("page1");
        int kept = PageEdits.CarryOver(old, fresh);
        Check(kept > 25, $"lignes retrouvées ({kept})");
        p.Pages[0] = fresh;
        Lots.Resync(p);
        Equal("42", FindLine(fresh, "2 rolls en métal").Lot, "numéro gardé");
        Equal("70", FindLine(fresh, "2 rolls en métal").Cells[1].Text, "correction gardée");
        Equal(FindLine(old, "1 table pliante").Lot, FindLine(fresh, "1 table pliante").Lot, "les autres numéros aussi");
    }

    public static void T_Cadrage_scan_qui_touche_les_bords()
    {
        int w = 800, h = 1100;
        var data = Enumerable.Repeat((byte)250, w * h).ToArray();
        void Line(int x0, int y0, int x1, int y1) { for (int y = y0; y <= y1; y++) for (int x = x0; x <= x1; x++) data[y * w + x] = 20; }
        for (int y = 60; y < h; y += 40) Line(40, y, w - 1, y + 1);
        foreach (int x in new[] { 40, 600, 700, w - 3 }) Line(x, 60, x + 1, h - 1);
        var q = PageDetector.Detect(new Raster(w, h, 1, data));
        Equal($"0,0 {w - 1},{h - 1}", $"{q.TopLeft.X},{q.TopLeft.Y} {q.BottomRight.X},{q.BottomRight.Y}", "page entière");
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

    public static void T_Supprimer_une_ligne_existante()
    {
        var p = new InventoryProject();
        foreach (var n in new[] { "page1", "page5" }) p.Pages.Add(Fixtures.Analyze(n));
        Lots.CreateEmpty(p);
        Lots.FillAutomatically(p);
        var a = p.Pages[0];
        var rolls = FindLine(a, "2 rolls en métal");
        var next = FindLine(a, "2 distributeurs de savon + 1 distributeur papier");
        var before = ExportLines(p);
        string lotNext = next.Lot;
        int h = Arrangement.Build(p).Pages[0].Height;
        rolls.Deleted = true;
        var plan = Arrangement.Build(p);
        var l = ExportLines(p);
        Equal(before.Length - 1, l.Length, "une ligne de moins dans le .txt");
        Check(!l.Any(x => x.Contains("2 rolls en métal")), "absente du .txt");
        Equal(lotNext, next.Lot, "les autres numéros ne bougent pas");
        Check(plan.Pages[0].Height < h, "la page raccourcit");
        Check(!Arrangement.ViewOf(p, plan, 0).Lines.Any(e => e.Line == rolls), "plus affichée");
        Lots.Resync(p);
        Check(!rolls.LotSlot && rolls.Lot == "4", "plus de case de lot (numéro gardé)");
        var comp = FindLine(a, "1 compresseur à vis ATLASCAPCO avec cuve 250L + 1 sécheur ATLASCAPCO");
        comp.Deleted = true;
        Check(!ExportLines(p).Any(x => x.Contains("CD3+")), "la suite part avec");
        var b = p.Pages[1];
        var total = FindLine(b, "TOTAL STOCK");
        FindLine(b, "CARTONNAGE").Deleted = true;
        Totals.Recompute(p, Arrangement.Build(p));
        Equal("28 550", total.Cells[1].Text, "montant retiré du total");
        rolls.Deleted = false; comp.Deleted = false; FindLine(b, "CARTONNAGE").Deleted = false;
        Lots.Resync(p);
        Totals.Recompute(p, Arrangement.Build(p));
        Equal(string.Join("\n", before), string.Join("\n", ExportLines(p)), "remise");
        Equal("44 550", total.Cells[1].Text, "total revenu");
    }

    public static void T_Lots_de_fin_sur_une_page_dediee()
    {
        var p = new InventoryProject();
        foreach (var n in new[] { "page1", "page5" }) p.Pages.Add(Fixtures.Analyze(n));
        Lots.CreateEmpty(p);
        Lots.FillAutomatically(p);
        p.EndLots = new EndLotsChoice { Mode = "page", AfterId = "page1" };
        Lots.Set(p, FindLine(p.Pages[0], "2 rolls en métal"), "0");
        Check(Arrangement.SyncEndPages(p), "page créée");
        Equal("page1,END,page5", string.Join(",", p.Pages.Select(x => x.EndLots ? "END" : x.Id)), "entre la page 1 et la page 5");
        var plan = Arrangement.Build(p);
        Equal(1, plan.Moving.Count, "un lot 0");
        Equal(0, plan.Pages[1].Template, "modèle : la page 1");
        Check(Arrangement.ViewOf(p, plan, 1).Lines.Any(e => e.Line.Cells[0].Text == "2 rolls en métal" && e.Moved), "affiché sur la page dédiée");
        Check(!Arrangement.ViewOf(p, plan, 0).Lines.Any(e => e.Line.Cells[0].Text == "2 rolls en métal"), "parti de la page 1");
        var l = ExportLines(p).ToList();
        int k = l.FindIndex(x => x.StartsWith("0\t2 rolls"));
        Check(k > l.FindIndex(x => x.Contains("gazelle")) && k < l.FindIndex(x => x.Contains("Liste détaillée")), ".txt : entre les pages");
        foreach (var o in Lots.ObjectLines(p).ToList()) Lots.Set(p, o.Line, "0");
        Arrangement.SyncEndPages(p);
        Check(p.Pages.Count(x => x.EndLots) >= 2, "plusieurs pages de fin");
        Equal("page1", p.Pages[0].Id, "page 1 en tête");
        Equal("page5", p.Pages[^1].Id, "page 5 à la fin");
        foreach (var o in Lots.ObjectLines(p).ToList()) Lots.Set(p, o.Line, "7");
        Arrangement.SyncEndPages(p);
        Equal(0, p.Pages.Count(x => x.EndLots), "pages retirées");
    }

    public static void T_Ligne_TOTAL_ajoutee()
    {
        var p = new InventoryProject();
        foreach (var n in new[] { "page1", "page5" }) p.Pages.Add(Fixtures.Analyze(n));
        var b = p.Pages[1];
        var t = Rows.AddTotalLine(b, FindLine(b, "PRODUITS FINIS"), new TotalScope { Mode = "above" })!;
        Totals.Recompute(p);
        Equal("TOTAL|44 550|9 000", string.Join("|", t.Cells.Select(c => c.Text)), "lignes au-dessus");
        Equal("44 550", FindLine(b, "TOTAL STOCK").Cells[1].Text, "le total imprimé ne change pas");
        FindLine(b, "CARTONNAGE").Cells[1].Text = "6 000";
        Totals.Recompute(p);
        Equal("34 550", t.Cells[1].Text, "recalculé tout seul");
        Equal("34 550", FindLine(b, "TOTAL STOCK").Cells[1].Text, "le total imprimé suit aussi");
        t.TotalScope = new TotalScope { Mode = "pages", Pages = ["page1"] };
        Totals.Recompute(p);
        long sum1 = p.Pages[0].Tables.SelectMany(x => x.Rows).SelectMany(r => r.Lines).Where(x => x.Kind is LineKind.Article or LineKind.Titre)
            .Sum(x => x.Cells.Count > 1 ? Totals.ParseAmount(x.Cells[1].Text) ?? 0 : 0);
        Equal(Totals.FormatAmount(sum1), t.Cells[1].Text, "page 1 seulement");
        t.TotalScope.Auto = false; t.Cells[1].Text = "";
        Totals.Recompute(p);
        Equal("", t.Cells[1].Text, "à taper soi-même");
        Check(ExportLines(p).Any(x => Regex.IsMatch(x, @"^TOTAL\t\t\d+$")), "dans le .txt");
        var t0 = p.Pages[0].Tables[0];
        Check(t0.Rows[^1].Lines.Contains(Rows.LastObjectLine(p.Pages[0])!), "appui : la dernière rangée à montants");
    }

    public static void T_Tracer_un_trait_en_dessous()
    {
        var p = new InventoryProject();
        p.Pages.Add(Fixtures.Analyze("page1"));
        var a = p.Pages[0];
        var comp = FindLine(a, "1 compresseur à vis ATLASCAPCO avec cuve 250L + 1 sécheur ATLASCAPCO");
        Check(Rows.CanSplitBelow(a, comp) && !Rows.CanSplitBelow(a, FindLine(a, "2 rolls en métal")), "seulement s'il y a une ligne dessous");
        int n = a.Tables[0].Rows.Count;
        Check(Rows.SplitBelow(a, comp), "trait tracé");
        Equal(n + 1, a.Tables[0].Rows.Count, "deux rangées");
    }

    public static void T_Reduire_la_rangee()
    {
        var p = new InventoryProject();
        p.Pages.Add(Fixtures.Analyze("page1"));
        var a = p.Pages[0];
        var t = a.Tables[0];
        var dans = FindLine(a, "DANS L'ENTREPOT");
        var laves = FindLine(a, "2 laves main en inox + 1 sanibroyeur SPEED");
        Check(Rows.RowTrimOf(a, FindLine(a, "2 rolls en métal")) is null, "rangée normale : rien à réduire");
        Check(Rows.Split(a, laves), "séparée");
        int r = t.Rows.FindIndex(x => x.Lines.Contains(dans));
        double h0 = t.RowYs[r + 1] - t.RowYs[r];
        int H = a.Height;
        Check(Rows.TrimRow(a, dans), "réduite");
        double h1 = t.RowYs[r + 1] - t.RowYs[r];
        Check(h1 < h0, $"hauteur {h0} → {h1}");
        Equal(H - (int)Math.Round(h0 - h1), a.Height, "la page raccourcit d'autant");
        Check(Rows.RowTrimOf(a, dans) is null, "plus rien à réduire");
        Equal(a.Height, PageLayout.Stretch(Fixtures.Load("page1").Image, a).Height, "image");
        Check(Rows.UntrimRow(a, dans), "hauteur d'origine");
        Equal(H, a.Height, "page revenue");
    }

    public static InventoryProject EndScenario()
    {
        var p = new InventoryProject();
        foreach (var n in new[] { "page1", "page5" }) p.Pages.Add(Fixtures.Analyze(n));
        Lots.CreateEmpty(p);
        Lots.FillAutomatically(p);
        p.EndLots = new EndLotsChoice { Mode = "page", AfterId = "page1" };
        Lots.Set(p, FindLine(p.Pages[0], "2 rolls en métal"), "0");
        Lots.Set(p, FindLine(p.Pages[0], "1 gazelle TULESCA inox"), "0");
        Lots.Set(p, FindLine(p.Pages[1], "CARTONNAGE"), "0");
        FindLine(p.Pages[0], "1 compresseur à vis ATLASCAPCO avec cuve 250L + 1 sécheur ATLASCAPCO").Deleted = true;
        Rows.AddTotalLine(p.Pages[1], FindLine(p.Pages[1], "PRODUITS FINIS"), new TotalScope { Mode = "above" });
        Arrangement.SyncEndPages(p);
        return p;
    }

    public static List<Raster> ComposeEnd(InventoryProject p)
    {
        var plan = Arrangement.Build(p);
        Totals.Recompute(p, plan);
        var imgs = p.Pages.Select(pg => pg.EndLots ? null : LotColumn.Draw(PageLayout.Stretch(Fixtures.Load(pg.Id).Image, pg), pg)).ToList();
        return p.Pages.Select((pg, k) => Arrangement.ComposePage(plan, k, imgs[plan.Pages[k].Template ?? k]!,
            plan.Pages[k].Pieces.Where(pc => pc.IsUnit).ToDictionary(pc => pc.Unit!, pc => (Arrangement.Crop?)Arrangement.UnitCrop(pc.Unit!, imgs[pc.Unit!.P]!)))).ToList();
    }

    public static void T_Scenario_lots_de_fin_supprimee_total()
    {
        var p = EndScenario();
        Equal("page1,END,page5", string.Join(",", p.Pages.Select(x => x.EndLots ? "END" : x.Id)), "pages");
        var plan = Arrangement.Build(p);
        Equal(3, plan.Moving.Count, "trois lots 0 sur la page de fin");
        Equal(p.Pages[0].Height, ComposeEnd(p)[1].Height, "page de fin à la hauteur du modèle");
    }
}
