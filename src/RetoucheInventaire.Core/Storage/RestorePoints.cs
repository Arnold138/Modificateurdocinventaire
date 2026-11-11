using System.IO.Compression;
using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using RetoucheInventaire.Core.Model;

namespace RetoucheInventaire.Core.Storage;

/// <summary>
/// Points de restauration : l'état complet du projet, écrit sur le disque avant chaque action (et à l'ouverture, à l'import,
/// avant un retour en arrière…). Ils survivent à la fermeture de l'application, à « Nouveau projet » et à une page retirée :
/// chaque point suffit à tout retrouver (les fichiers sources sont gardés une seule fois, à part).
/// <list type="bullet">
/// <item><c>points/AAAAMMJJ-HHMMSS-mmm-n.pt</c> : gzip ; 1re ligne = fiche (date, action, nom, pages, lots, état sain, sources),
/// la suite = projet.json.</item>
/// <item><c>sources/&lt;empreinte&gt;</c> : fichiers sources (photos, PDF rendus), une fois chacun.</item>
/// </list>
/// </summary>
public static class RestorePoints
{
    /// <summary>Fiche d'un point de restauration.</summary>
    public sealed class Info
    {
        public DateTime When { get; set; }
        /// <summary>Ce qui allait être fait (« avant : Numéro de lot »).</summary>
        public string Label { get; set; } = "";
        public string Name { get; set; } = "";
        public int Pages { get; set; }
        public int Lots { get; set; }
        public int Edits { get; set; }
        /// <summary>Mise en page contrôlée sans problème (<see cref="Integrity"/>) au moment du point.</summary>
        public bool Sound { get; set; } = true;
        /// <summary>Entrée d'archive → empreinte du fichier source.</summary>
        public Dictionary<string, string> Sources { get; set; } = [];
        [System.Text.Json.Serialization.JsonIgnore] public string File { get; set; } = "";
    }

    private static readonly object Gate = new();
    private static readonly ConditionalWeakTable<byte[], string> Hashes = new();
    private static readonly JsonSerializerOptions Line = new() { WriteIndented = false };
    private static int _counter;
    private static string? _lastHash;

    /// <summary>État du projet compressé (pile du retour en arrière : cent états tiennent en mémoire).</summary>
    public static byte[] Pack(string json)
    {
        using var ms = new MemoryStream();
        using (var gz = new GZipStream(ms, CompressionLevel.Fastest)) gz.Write(Encoding.UTF8.GetBytes(json));
        return ms.ToArray();
    }

    public static string Unpack(byte[] packed)
    {
        using var gz = new GZipStream(new MemoryStream(packed), CompressionMode.Decompress);
        using var r = new StreamReader(gz, Encoding.UTF8);
        return r.ReadToEnd();
    }

    private static string PointsDir(string dir) => Path.Combine(dir, "points");
    private static string SourcesDir(string dir) => Path.Combine(dir, "sources");

    private static string HashOf(byte[] bytes) => Hashes.GetValue(bytes, b => Convert.ToHexString(SHA256.HashData(b))[..32].ToLowerInvariant());

    /// <summary>Fiche du projet (nombre de pages, de lots, de cases modifiées) pour l'afficher dans la liste.</summary>
    public static Info Describe(InventoryProject project, string label, bool sound) => new()
    {
        When = DateTime.Now, Label = label, Name = project.Name, Pages = project.Pages.Count, Sound = sound,
        Lots = project.Pages.SelectMany(p => p.AllLines()).Count(l => l.Lot.Length > 0),
        Edits = project.Pages.SelectMany(p => p.AllFields()).Count(f => f.IsModified),
    };

    /// <summary>
    /// Écrit un point de restauration (rien si le projet est identique au point précédent). <paramref name="json"/> : le projet
    /// sérialisé (<see cref="ProjectStore.Json"/>). Renvoie le fichier écrit, ou null.
    /// </summary>
    public static string? Save(string dir, string json, Info info, IReadOnlyDictionary<string, byte[]> sources, IEnumerable<string?> usedEntries)
    {
        lock (Gate)
        {
            string hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(json)));
            if (hash == _lastHash) return null;
            Directory.CreateDirectory(PointsDir(dir));
            Directory.CreateDirectory(SourcesDir(dir));
            foreach (var entry in usedEntries.Where(e => e != null).Distinct())
            {
                if (!sources.TryGetValue(entry!, out var bytes)) continue;
                string h = HashOf(bytes);
                info.Sources[entry!] = h;
                string target = Path.Combine(SourcesDir(dir), h);
                if (File.Exists(target)) continue;
                File.WriteAllBytes(target + ".tmp", bytes);
                File.Move(target + ".tmp", target, overwrite: true);
            }
            string name = $"{info.When:yyyyMMdd-HHmmss-fff}-{_counter++ % 1000:000}.pt";
            string path = Path.Combine(PointsDir(dir), name), tmp = path + ".tmp";
            using (var fs = File.Create(tmp))
            using (var gz = new GZipStream(fs, CompressionLevel.Fastest))
            using (var w = new StreamWriter(gz, new UTF8Encoding(false)))
            {
                w.Write(JsonSerializer.Serialize(info, Line));
                w.Write('\n');
                w.Write(json);
            }
            File.Move(tmp, path, overwrite: true);
            _lastHash = hash;
            return path;
        }
    }

    /// <summary>Points de restauration, du plus récent au plus ancien.</summary>
    public static List<Info> List(string dir)
    {
        var list = new List<Info>();
        if (!Directory.Exists(PointsDir(dir))) return list;
        foreach (var file in Directory.EnumerateFiles(PointsDir(dir), "*.pt"))
        {
            try
            {
                using var fs = File.OpenRead(file);
                using var gz = new GZipStream(fs, CompressionMode.Decompress);
                using var r = new StreamReader(gz, Encoding.UTF8);
                if (r.ReadLine() is { } first && JsonSerializer.Deserialize<Info>(first, Line) is { } info) { info.File = file; list.Add(info); }
            }
            catch { /* point illisible (écriture interrompue) : ignoré */ }
        }
        return [.. list.OrderByDescending(i => i.When).ThenByDescending(i => i.File, StringComparer.Ordinal)];
    }

    /// <summary>Relit un point : le projet et ses fichiers sources.</summary>
    public static (InventoryProject Project, Dictionary<string, byte[]> Sources) Load(string dir, Info info)
    {
        string json;
        using (var fs = File.OpenRead(info.File))
        using (var gz = new GZipStream(fs, CompressionMode.Decompress))
        using (var r = new StreamReader(gz, Encoding.UTF8))
        {
            r.ReadLine();
            json = r.ReadToEnd();
        }
        var project = JsonSerializer.Deserialize<InventoryProject>(json, ProjectStore.Json) ?? throw new InvalidDataException("Point de restauration vide.");
        var sources = new Dictionary<string, byte[]>();
        foreach (var (entry, hash) in info.Sources)
        {
            string file = Path.Combine(SourcesDir(dir), hash);
            if (File.Exists(file)) sources[entry] = File.ReadAllBytes(file);
        }
        return (project, sources);
    }

    /// <summary>
    /// Ménage : les <paramref name="recent"/> derniers points sont tous gardés ; avant, un point toutes les dix minutes ;
    /// plus rien après <paramref name="days"/> jours (sauf les <paramref name="recent"/> derniers). Les fichiers sources que plus
    /// aucun point n'utilise sont retirés.
    /// </summary>
    public static void Prune(string dir, int recent = 400, int days = 60)
    {
        lock (Gate)
        {
            var all = List(dir);
            var keep = new List<Info>();
            DateTime? slot = null;
            for (int i = 0; i < all.Count; i++)
            {
                var p = all[i];
                if (i < recent) { keep.Add(p); continue; }
                if ((DateTime.Now - p.When).TotalDays > days) { TryDelete(p.File); continue; }
                var s = new DateTime(p.When.Year, p.When.Month, p.When.Day, p.When.Hour, p.When.Minute / 10 * 10, 0);
                if (slot == s) { TryDelete(p.File); continue; }
                slot = s;
                keep.Add(p);
            }
            if (!Directory.Exists(SourcesDir(dir))) return;
            var used = keep.SelectMany(p => p.Sources.Values).ToHashSet();
            foreach (var f in Directory.EnumerateFiles(SourcesDir(dir)))
                if (!used.Contains(Path.GetFileName(f)) && (DateTime.Now - File.GetLastWriteTime(f)).TotalMinutes > 10) TryDelete(f);
        }
    }

    private static void TryDelete(string file)
    {
        try { File.Delete(file); } catch { /* sans importance */ }
    }
}
