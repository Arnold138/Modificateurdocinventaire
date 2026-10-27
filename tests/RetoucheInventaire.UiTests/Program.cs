// Essai de l'interface Windows (ce que fait le client à la souris), sur un vrai poste Windows (CI) :
//   RetoucheInventaire.UiTests <RetoucheInventaire.exe> <essai.inventaire> <dossier des captures>
// 1. le projet s'ouvre (lot VGG déplacé en haut de la page 1 : cadré) ;
// 2. clic droit dans la grande case de lot, au-dessus du numéro : le menu « Numéro de lot … » s'ouvre ;
// 3. « Numéro de lot au milieu » ; 4. le numéro glissé à la souris dans sa case ;
// 5. Modifier : poignée ↕ d'un espace vide tirée vers le haut ;
// 6. le fichier enregistré contient bien la position du numéro et l'espace réglé.
using System.Diagnostics;
using System.Drawing;
using FlaUI.Core;
using FlaUI.Core.AutomationElements;
using FlaUI.Core.Capturing;
using FlaUI.Core.Input;
using FlaUI.UIA3;
using Application = FlaUI.Core.Application;
using RetoucheInventaire.Core.Model;
using RetoucheInventaire.Core.Storage;

string exe = args[0], file = Path.GetFullPath(args[1]), outDir = args[2];
Directory.CreateDirectory(outDir);
int failures = 0;
void Check(bool ok, string what) { Console.WriteLine((ok ? "OK    " : "ÉCHEC ") + what); if (!ok) failures++; }
int shot = 0;
void Shot(string name) { try { Capture.Screen().ToFile(Path.Combine(outDir, $"{++shot:00}-{name}.png")); } catch (Exception e) { Console.WriteLine("capture impossible : " + e.Message); } }

var trace = Path.Combine(outDir, "trace.txt");
var psi = new ProcessStartInfo(exe, $"\"{file}\"") { UseShellExecute = false };
psi.Environment["RI_TRACE"] = trace;
var app = Application.Launch(psi);
// Glisser comme une main : vrais mouvements de souris de Windows (SendInput), appui, petits pas, capture au milieu, lâcher.
void Drag(Point from, Point to, string name)
{
    Input.Move(from); Thread.Sleep(300);
    Input.Button(down: true); Thread.Sleep(300);
    for (int k = 1; k <= 20; k++) { Input.Move(new Point(from.X + (to.X - from.X) * k / 20, from.Y + (to.Y - from.Y) * k / 20)); Thread.Sleep(40); }
    Thread.Sleep(300);
    Shot(name + "-pendant");
    Input.Button(down: false);
    Thread.Sleep(2500);
}
using var automation = new UIA3Automation();
try
{
    var window = app.GetMainWindow(automation, TimeSpan.FromSeconds(90));
    Check(window != null, "fenêtre ouverte");
    if (window is null) return 1;
    window.SetForeground();

    AutomationElement[] Cases()
    {
        for (int k = 0; k < 120; k++)
        {
            var found = window.FindAllDescendants(cf => cf.ByName("Case du lot"));
            if (found.Length > 0) return found;
            Thread.Sleep(500);
        }
        return [];
    }
    AutomationElement? Find(string name, int seconds = 10)
    {
        for (int k = 0; k < seconds * 4; k++)
        {
            var e = window.FindFirstDescendant(cf => cf.ByName(name)) ?? automation.GetDesktop().FindFirstDescendant(cf => cf.ByName(name));
            if (e != null) return e;
            Thread.Sleep(250);
        }
        return null;
    }
    string Status() => window.FindFirstDescendant(cf => cf.ByAutomationId("StatusText"))?.Name ?? "";

    var cases = Cases();
    Check(cases.Length > 0, $"cases de lot affichées ({cases.Length})");
    Thread.Sleep(2000);
    Shot("ouvert");
    if (cases.Length == 0) return 1;

    // La plus grande case visible (DANS L'ENTREPOT / lot 1) et son numéro.
    var big = cases.OrderByDescending(c => c.BoundingRectangle.Height).First();
    string id = big.AutomationId["case-".Length..];
    var r = big.BoundingRectangle;
    Console.WriteLine($"grande case {id} : {r}");
    var number = window.FindFirstDescendant(cf => cf.ByAutomationId("lot-" + id));
    Check(number != null, "numéro de la grande case trouvé");

    // 2. Clic droit tout en haut de la case (là où il n'y a pas le numéro).
    Mouse.Click(new Point(r.X + r.Width / 2, r.Y + 6), MouseButton.Right);
    Thread.Sleep(1200);
    var middle = Find("Numéro de lot au milieu");
    Shot("clic-droit-dans-la-case");
    Check(middle != null, "clic droit au-dessus du numéro : menu « Numéro de lot au milieu »");
    // 3. Au milieu.
    if (middle != null) { middle.Click(); Thread.Sleep(2500); }
    Shot("numero-au-milieu");
    Console.WriteLine("statut : " + Status());
    Check(Status().Contains("Numéro de lot déplacé"), "numéro mis au milieu de sa case");

    // 4. Le numéro glissé à la souris tout en haut de sa case.
    cases = Cases();
    big = cases.FirstOrDefault(c => c.AutomationId == "case-" + id) ?? big;
    r = big.BoundingRectangle;
    number = window.FindFirstDescendant(cf => cf.ByAutomationId("lot-" + id));
    if (number != null)
    {
        var n = number.BoundingRectangle;
        Drag(new Point(n.X + n.Width / 2, n.Y + n.Height / 2), new Point(n.X + n.Width / 2, r.Y + n.Height), "numero");
    }
    Shot("numero-glisse");
    Console.WriteLine("statut : " + Status());
    Check(Status().Contains("Numéro 1 placé") || Status().Contains(" placé dans sa case. Clic droit"), "numéro glissé à la souris");

    // 5. Modifier : poignées ↕, celle du grand espace vide de la case tirée vers le haut.
    var toggle = window.FindFirstDescendant(cf => cf.ByAutomationId("MoveToggle"));
    toggle?.Click();
    Thread.Sleep(2000);
    var handles = window.FindAllDescendants(cf => cf.ByName("Espace vide"));
    Shot("modifier-poignees");
    Check(handles.Length > 0, $"poignées ↕ des espaces vides ({handles.Length})");
    var inBig = handles.Where(h => h.BoundingRectangle.Y > r.Y - 5 && h.BoundingRectangle.Bottom < r.Bottom + 5).OrderBy(h => h.BoundingRectangle.Y).FirstOrDefault() ?? handles.FirstOrDefault();
    if (inBig != null)
    {
        var h = inBig.BoundingRectangle;
        Console.WriteLine($"poignée ↕ : {h}");
        Drag(new Point(h.X + h.Width / 2, h.Y + h.Height / 2), new Point(h.X + h.Width / 2, h.Y + h.Height / 2 - 40), "espace");
    }
    Shot("espace-reduit");
    Console.WriteLine("statut : " + Status());
    Check(Status().Contains("Espace vide réglé"), "espace vide réduit avec la poignée ↕");
    // Contrôle : la poignée ⋮⋮ d'une rangée (déjà utilisée par le client) se glisse aussi avec le robot.
    var grip = window.FindAllDescendants(cf => cf.ByName("Déplacer la rangée")).Skip(3).FirstOrDefault();
    if (grip != null)
    {
        var g = grip.BoundingRectangle;
        Drag(new Point(g.X + g.Width / 2, g.Y + g.Height / 2), new Point(g.X + g.Width / 2, g.Y + g.Height / 2 + 70), "rangee");
        Console.WriteLine("statut (contrôle ⋮⋮) : " + Status());
    }
    toggle?.Click();
    Thread.Sleep(1500);
    Shot("fin");

    // 6. Le fichier (enregistrement continu) contient la position du numéro et l'espace réglé.
    Thread.Sleep(2500);
    var copy = Path.Combine(outDir, "resultat.inventaire");
    File.Copy(file, copy, true);
    var (project, _) = ProjectStore.Load(copy);
    var line = project.Pages.SelectMany(p => p.AllLines()).FirstOrDefault(l => l.Cells.FirstOrDefault()?.Id == id);
    Check(line?.LotPos != null, $"fichier : numéro glissé enregistré (LotPos = {line?.LotPos})");
    Check(project.Pages.Any(p => p.Inserts.Any(b => b.Gap != null)), "fichier : espace vide réglé enregistré");
}
catch (Exception e)
{
    Console.WriteLine("ÉCHEC exception : " + e);
    Shot("exception");
    failures++;
}
finally
{
    try { app.Close(); Thread.Sleep(1500); if (!app.HasExited) app.Kill(); } catch { /* déjà fermée */ }
}
if (File.Exists(trace)) Console.WriteLine("--- trace de l'application ---\n" + File.ReadAllText(trace));
Console.WriteLine(failures == 0 ? "Interface Windows : tout est bon." : $"Interface Windows : {failures} échec(s).");
return failures == 0 ? 0 : 1;

/// <summary>Souris de Windows (SendInput) : mouvements avec le bouton enfoncé, reçus par l'application comme une vraie main.</summary>
static class Input
{
    [System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Sequential)]
    private struct MOUSEINPUT { public int dx, dy; public uint mouseData, dwFlags, time; public IntPtr dwExtraInfo; }
    [System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Explicit)]
    private struct INPUT { [System.Runtime.InteropServices.FieldOffset(0)] public uint type; [System.Runtime.InteropServices.FieldOffset(8)] public MOUSEINPUT mi; }
    [System.Runtime.InteropServices.DllImport("user32.dll")] private static extern uint SendInput(uint n, INPUT[] inputs, int size);
    [System.Runtime.InteropServices.DllImport("user32.dll")] private static extern int GetSystemMetrics(int index);
    private const uint MOVE = 0x0001, LEFTDOWN = 0x0002, LEFTUP = 0x0004, ABSOLUTE = 0x8000, VIRTUALDESK = 0x4000;

    private static void Send(uint flags, int dx = 0, int dy = 0)
    {
        var input = new INPUT { type = 0, mi = new MOUSEINPUT { dx = dx, dy = dy, dwFlags = flags } };
        SendInput(1, [input], System.Runtime.InteropServices.Marshal.SizeOf<INPUT>());
    }

    public static void Move(Point p)
    {
        int w = Math.Max(1, GetSystemMetrics(0) - 1), h = Math.Max(1, GetSystemMetrics(1) - 1);
        Send(MOVE | ABSOLUTE, (int)Math.Round(p.X * 65535.0 / w), (int)Math.Round(p.Y * 65535.0 / h));
    }

    public static void Button(bool down) => Send(down ? LEFTDOWN : LEFTUP);
}
