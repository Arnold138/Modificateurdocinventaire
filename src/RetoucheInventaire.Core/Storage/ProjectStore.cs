using System.IO.Compression;
using System.Text.Json;
using System.Text.Json.Serialization;
using RetoucheInventaire.Core.Model;

namespace RetoucheInventaire.Core.Storage;

/// <summary>
/// Sauvegarde locale d'un projet : un seul fichier .inventaire (zip) contenant
/// projet.json et les images sources telles qu'importées. Rien ne quitte l'appareil.
/// </summary>
public static class ProjectStore
{
    public const string Extension = ".inventaire";
    private const string JsonEntry = "projet.json";

    public static readonly JsonSerializerOptions Json = new()
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() },
    };

    public static void Save(string path, InventoryProject project, IReadOnlyDictionary<string, byte[]> sources) =>
        Save(path, JsonSerializer.Serialize(project, Json), project.Pages.Select(p => p.SourceEntry), sources);

    /// <summary>
    /// Même fichier, à partir du projet déjà sérialisé (<paramref name="json"/>) : l'application relève l'état sur l'écran, puis
    /// écrit le fichier en arrière-plan (un gros projet fait plusieurs dizaines de Mo : l'écran ne doit pas attendre).
    /// </summary>
    public static void Save(string path, string json, IEnumerable<string?> entries, IReadOnlyDictionary<string, byte[]> sources)
    {
        var dir = Path.GetDirectoryName(Path.GetFullPath(path));
        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
        var tmp = path + ".tmp";
        using (var fs = File.Create(tmp))
        using (var zip = new ZipArchive(fs, ZipArchiveMode.Create))
        {
            var je = zip.CreateEntry(JsonEntry, CompressionLevel.Fastest);
            using (var s = new StreamWriter(je.Open(), new System.Text.UTF8Encoding(false))) s.Write(json);
            var written = new HashSet<string>();
            foreach (var entry in entries)
            {
                if (entry is null || !sources.TryGetValue(entry, out var bytes) || !written.Add(entry)) continue;
                var e = zip.CreateEntry(entry, CompressionLevel.NoCompression);
                using var s = e.Open();
                s.Write(bytes);
            }
        }
        File.Move(tmp, path, overwrite: true); // écriture atomique : pas de fichier à moitié écrit
    }

    public static (InventoryProject Project, Dictionary<string, byte[]> Sources) Load(string path)
    {
        using var zip = ZipFile.OpenRead(path);
        var entry = zip.GetEntry(JsonEntry) ?? throw new InvalidDataException("Fichier projet invalide (projet.json absent).");
        InventoryProject project;
        using (var s = entry.Open())
            project = JsonSerializer.Deserialize<InventoryProject>(s, Json) ?? throw new InvalidDataException("Projet vide.");

        var sources = new Dictionary<string, byte[]>();
        foreach (var e in zip.Entries.Where(e => e.FullName != JsonEntry))
        {
            using var s = e.Open();
            using var ms = new MemoryStream();
            s.CopyTo(ms);
            sources[e.FullName] = ms.ToArray();
        }
        return (project, sources);
    }
}
