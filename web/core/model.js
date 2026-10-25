// Modèle de données, analyse de page, types de ligne et actions d'édition.
// Portage de PageAnalyzer.cs, LineClassifier.cs, PageEdits.cs et Project.cs.
// Les noms de propriétés sont ceux du JSON de la version Windows (projets compatibles).
import { Rect } from './geometry.js';
import { detectGrids, mergeThinRows } from './rules.js';

const PAD = 4;

export const newPage = (props = {}) => ({
  Id: Math.random().toString(16).slice(2, 10),
  Source: 'Image', SourceName: '', SourceEntry: null,
  Width: 0, Height: 0, QuarterTurns: 0, Crop: null, Skew: 0,
  Tables: [], LooseLines: [], LockZones: [],
  ...props,
});

export const newProject = (name = 'Inventaire') => ({ FormatVersion: 1, Name: name, LotFrame: true, Pages: [] });

const newField = (props) => ({
  Id: '', Column: 0, OriginalText: '', Text: '', Ink: Rect.empty(), Box: Rect.empty(),
  Align: 'Left', Indent: 0, FontSize: 12, Locked: false, Strikes: [], ...props,
});

export const isModified = (f) => f.Text !== f.OriginalText;
export const lineBounds = (line) => line.Cells.reduce((r, f) => Rect.union(r, f.Box), Rect.empty());
export const firstText = (line) => (line.Cells.find((c) => c.Text.trim().length > 0)?.Text.trim()) ?? '';

export function* allLines(page) {
  for (const t of page.Tables) for (const r of t.Rows) for (const l of r.Lines) yield l;
  for (const l of page.LooseLines) yield l;
}
export function* allFields(page) {
  for (const l of allLines(page)) for (const f of l.Cells) yield f;
}

// ---------- Analyse ----------

export function analyze(page, image, rawWords, grids = null) {
  page.Width = image.w;
  page.Height = image.h;
  page.Tables = [];
  page.LooseLines = [];
  const words = rawWords.map(clean).filter(Boolean);
  const used = new Array(words.length).fill(false);

  for (const grid of grids ?? detectGrids(image)) page.Tables.push(buildGridTable(grid, words, used));

  const rest = words.filter((_, i) => !used[i]);
  if (page.Tables.length === 0) {
    if (rest.length > 0) page.Tables.push(buildGridlessTable(rest, page.Width));
  } else {
    for (const row of buildGridlessTable(rest, page.Width).Rows)
      for (const line of row.Lines) { line.OutsideTable = true; line.Kind = 'Ignoree'; page.LooseLines.push(line); }
  }
  for (const t of page.Tables) classify(t);
  assignIds(page);
  applyLocks(page);
}

export function reclassify(page) { for (const t of page.Tables) classify(t); }

/**
 * Projet enregistré avec une version précédente : rangées sans texte lu (total sur fond gris) → cases vides modifiables ;
 * rangée de libellés sans désignation (« exploitation réalisation » d'un récapitulatif) → en-tête. Rien d'autre ne change.
 */
export function upgradePage(page) {
  let n = 0;
  for (const t of page.Tables) {
    if (t.HasGrid === false || !(t.ColumnXs?.length >= 2) || !(t.RowYs?.length >= 2)) continue;
    const sizes = t.Rows.flatMap((r) => r.Lines).map((l) => l.Cells[0]?.FontSize ?? 0).filter((v) => v > 0).sort((a, b) => a - b);
    const em = sizes.length > 0 ? sizes[Math.trunc(sizes.length / 2)] : 12;
    const header = mergeThinRowsOf(t);
    t.Rows.forEach((row, r) => {
      if (row.Lines.length > 0 || r + 1 >= t.RowYs.length) return;
      const rowTop = t.RowYs[r], rowBottom = t.RowYs[r + 1];
      const top = rowTop + PAD, bottom = Math.max(rowTop + PAD + 1, rowBottom - PAD);
      const line = { Kind: r === 0 && header ? 'EnTete' : 'Article', KindChosenByUser: false, OutsideTable: false, Cells: [], Lot: '', LotManual: false, NoLot: false, ForceLot: false, Extra: 0, ExtraAt: 0 };
      for (let c = 0; c + 1 < t.ColumnXs.length; c++) {
        const box = cellBox(t.ColumnXs[c], t.ColumnXs[c + 1], rowTop, rowBottom, top, bottom, Rect.empty());
        line.Cells.push(newField({ Id: `${page.Id}-v${n++}`, Column: c, Box: box, Align: c === 0 ? 'Left' : 'Right', Indent: 4, FontSize: Math.min(em, Math.max(6, box.Height / 1.05)) }));
      }
      row.Lines.push(line);
    });
    t.Rows.forEach((row, r) => {
      if (r === 0 || !isHeaderRow(row.Lines)) return;
      if (!row.Lines.every((l) => blank(l.Cells[0]?.Text ?? '') || isRomanHeading(l.Cells[0].Text))) return;
      for (const l of row.Lines) if (!l.KindChosenByUser && !l.OutsideTable) l.Kind = 'EnTete';
    });
  }
}

/**
 * Rangées fines d'une trame lue comme plusieurs filets (projet d'une version précédente) : réunies en une rangée (même règle
 * que la détection). En-tête du tableau relu en morceaux (texte illisible) : remplacé par des cases vides modifiables,
 * sauf s'il a été corrigé. Vrai si l'en-tête a été vidé.
 */
function mergeThinRowsOf(t) {
  if (t.RowOrder || t.Rows.some((r) => r.Inserted) || t.Rows.length + 1 !== t.RowYs.length) return false;
  const keep = mergeThinRows(t.RowYs);
  if (keep.length === t.RowYs.length) return false;
  const rows = [];
  let header = false;
  t.Rows.forEach((row, r) => {
    if (keep.includes(t.RowYs[r])) { rows.push({ ...row, Lines: [...row.Lines], n: 1 }); return; }
    const g = rows[rows.length - 1];
    g.Lines.push(...row.Lines);
    g.n++;
    g.Bounds = Rect.fromEdges(g.Bounds.X, g.Bounds.Y, Math.max(Rect.right(g.Bounds), Rect.right(row.Bounds)), Rect.bottom(row.Bounds));
  });
  if (rows[0]?.n > 1 && !rows[0].Lines.some((l) => l.KindChosenByUser || l.Cells.some(isModified))) { rows[0].Lines = []; header = true; }
  t.Rows = rows.map(({ n, ...row }) => row);
  t.RowYs = keep;
  return header;
}

export function assignIds(page) {
  let n = 0;
  for (const f of allFields(page)) f.Id = `${page.Id}-${n++}`;
}

function clean(w) {
  const t = w.Text.trim().replace(/^[|¦]+|[|¦]+$/g, '');
  if (t.length === 0 || [...t].every((c) => '|¦_[]{}'.includes(c))) return null;
  if (w.Box.Width <= 0 || w.Box.Height <= 0) return null;
  return { Text: t, Box: w.Box };
}

function indexOf(edges, v) {
  for (let i = 0; i < edges.length - 2; i++) if (v < edges[i + 1]) return i;
  return edges.length - 2;
}

function buildGridTable(grid, words, used) {
  const table = { Bounds: grid.Bounds, HasGrid: true, ColumnXs: [...grid.ColumnXs], RowYs: [...grid.RowYs], Rows: [] };
  const nCols = grid.ColumnXs.length - 1;
  const area = Rect.inflate(grid.Bounds, 2, 2);
  const rowWords = Array.from({ length: grid.RowYs.length - 1 }, () => []);
  words.forEach((w, i) => {
    if (used[i] || !Rect.contains(area, Rect.cx(w.Box), Rect.cy(w.Box))) return;
    rowWords[indexOf(grid.RowYs, Rect.cy(w.Box))].push({ W: w, Col: indexOf(grid.ColumnXs, Rect.cx(w.Box)) });
    used[i] = true;
  });

  const bandOf = (l) => l.reduce((a, t) => Rect.union(a, t.W.Box), Rect.empty());
  for (let r = 0; r < rowWords.length; r++) {
    const rowTop = grid.RowYs[r], rowBottom = grid.RowYs[r + 1];
    const row = { Bounds: Rect.fromEdges(grid.ColumnXs[0], rowTop, grid.ColumnXs[grid.ColumnXs.length - 1], rowBottom), Lines: [] };
    const lines = groupIntoLines(rowWords[r], (t) => t.W.Box);
    // Rangée où l'OCR n'a rien lu (fond gris tramé d'un total…) : des cases vides, modifiables et relues une par une.
    if (lines.length === 0) {
      const line = { Kind: 'Article', KindChosenByUser: false, OutsideTable: false, Cells: [], Lot: '', LotManual: false, NoLot: false, ForceLot: false, Extra: 0, ExtraAt: 0 };
      const top = rowTop + PAD, bottom = Math.max(rowTop + PAD + 1, rowBottom - PAD);
      for (let c = 0; c < nCols; c++)
        line.Cells.push(newField({ Column: c, Box: cellBox(grid.ColumnXs[c], grid.ColumnXs[c + 1], rowTop, rowBottom, top, bottom, Rect.empty()), FontSize: 0 }));
      row.Lines.push(line);
      table.Rows.push(row);
      continue;
    }
    for (let li = 0; li < lines.length; li++) {
      const lineWords = lines[li];
      const band = bandOf(lineWords);
      const h = band.Height;
      let top = Math.max(rowTop + PAD, band.Y - 0.35 * h);
      let bottom = Math.min(rowBottom - PAD, Rect.bottom(band) + 0.35 * h);
      if (li > 0) top = Math.max(top, (Rect.bottom(bandOf(lines[li - 1])) + band.Y) / 2);
      if (li < lines.length - 1) bottom = Math.min(bottom, (Rect.bottom(band) + bandOf(lines[li + 1]).Y) / 2);
      const em = estimateFontSize(lineWords.map((t) => t.W));
      const line = { Kind: 'Article', KindChosenByUser: false, OutsideTable: false, Cells: [], Lot: '', LotManual: false, NoLot: false, ForceLot: false, Extra: 0, ExtraAt: 0 };
      for (let c = 0; c < nCols; c++) {
        const cellWords = lineWords.filter((t) => t.Col === c).map((t) => t.W);
        cellWords.sort((a, b) => a.Box.X - b.Box.X);
        const text = cellWords.map((w) => w.Text).join(' ');
        const ink = cellWords.reduce((a, w) => Rect.union(a, w.Box), Rect.empty());
        line.Cells.push(newField({
          Column: c, OriginalText: text, Text: text, Ink: ink,
          Box: cellBox(grid.ColumnXs[c], grid.ColumnXs[c + 1], rowTop, rowBottom, top, bottom, ink),
          FontSize: Math.min(em, Math.max(6, (bottom - top) / 1.05)),
        }));
      }
      row.Lines.push(line);
    }
    table.Rows.push(row);
  }
  computeAlignment(table, nCols);
  // Cases des rangées vides : taille d'écriture du tableau (médiane des autres lignes).
  const sizes = table.Rows.flatMap((r) => r.Lines).map((l) => l.Cells[0]?.FontSize ?? 0).filter((s) => s > 0).sort((a, b) => a - b);
  const em = sizes.length > 0 ? sizes[Math.trunc(sizes.length / 2)] : 12;
  for (const f of table.Rows.flatMap((r) => r.Lines).flatMap((l) => l.Cells))
    if (f.FontSize === 0) f.FontSize = Math.min(em, Math.max(6, f.Box.Height / 1.05));
  return table;
}

function cellBox(colLeft, colRight, rowTop, rowBottom, top, bottom, ink) {
  const minGap = 1.5;
  let l = colLeft + PAD, r = colRight - PAD;
  if (!Rect.isEmpty(ink)) {
    l = Math.max(colLeft + minGap, Math.min(l, ink.X - 2));
    r = Math.min(colRight - minGap, Math.max(r, Rect.right(ink) + 2));
    top = Math.max(rowTop + minGap, Math.min(top, ink.Y - 1));
    bottom = Math.min(rowBottom - minGap, Math.max(bottom, Rect.bottom(ink) + 1));
  }
  return Rect.fromEdges(l, top, r, bottom);
}

function computeAlignment(table, nCols) {
  const fields = table.Rows.flatMap((r) => r.Lines).flatMap((l) => l.Cells);
  for (let c = 0; c < nCols; c++) {
    const col = fields.filter((f) => f.Column === c);
    for (const f of col.filter((f) => !Rect.isEmpty(f.Ink))) {
      const left = Math.max(0, f.Ink.X - f.Box.X), right = Math.max(0, Rect.right(f.Box) - Rect.right(f.Ink));
      if (Math.abs(left - right) < 0.15 * f.Box.Width) { f.Align = 'Center'; f.Indent = 0; }
      else if (left < right) { f.Align = 'Left'; f.Indent = left; }
      else { f.Align = 'Right'; f.Indent = right; }
    }
    const filled = col.filter((f) => !Rect.isEmpty(f.Ink) && f.Align !== 'Center');
    let align;
    if (filled.length === 0) align = c === 0 ? 'Left' : 'Right';
    else {
      const counts = new Map();
      for (const f of filled) counts.set(f.Align, (counts.get(f.Align) ?? 0) + 1);
      let best = -1;
      for (const [k, n] of counts) if (n > best) { best = n; align = k; }
    }
    const indents = filled.filter((f) => f.Align === align).map((f) => f.Indent).sort((a, b) => a - b);
    const indent = indents.length === 0 ? 2 : indents[Math.trunc(indents.length / 2)];
    for (const f of col.filter((f) => Rect.isEmpty(f.Ink))) { f.Align = align; f.Indent = indent; }
  }
}

function buildGridlessTable(words, pageWidth) {
  const table = { Bounds: Rect.empty(), HasGrid: false, ColumnXs: [], RowYs: [], Rows: [] };
  const lines = groupIntoLines(words, (w) => w.Box);
  const bands = lines.map((l) => l.reduce((a, w) => Rect.union(a, w.Box), Rect.empty()));
  for (let li = 0; li < lines.length; li++) {
    const band = bands[li], h = band.Height;
    let top = band.Y - 0.35 * h, bottom = Rect.bottom(band) + 0.35 * h;
    if (li > 0) top = Math.max(top, (Rect.bottom(bands[li - 1]) + band.Y) / 2);
    if (li < lines.length - 1) bottom = Math.min(bottom, (Rect.bottom(band) + bands[li + 1].Y) / 2);
    const sorted = [...lines[li]].sort((a, b) => a.Box.X - b.Box.X);
    const charW = sorted.reduce((s, w) => s + w.Box.Width, 0) / Math.max(1, sorted.reduce((s, w) => s + w.Text.length, 0));
    const splitGap = Math.max(3 * charW, 1.2 * h);
    const segments = [[sorted[0]]];
    for (let i = 1; i < sorted.length; i++) {
      if (sorted[i].Box.X - Rect.right(sorted[i - 1].Box) > splitGap) segments.push([]);
      segments[segments.length - 1].push(sorted[i]);
    }
    const em = estimateFontSize(sorted);
    const line = { Kind: 'Article', KindChosenByUser: false, OutsideTable: false, Cells: [], Lot: '', LotManual: false, NoLot: false, ForceLot: false, Extra: 0, ExtraAt: 0 };
    segments.forEach((seg, s) => {
      const ink = seg.reduce((a, w) => Rect.union(a, w.Box), Rect.empty());
      const right = s < segments.length - 1 ? segments[s + 1][0].Box.X - 6 : Math.max(Rect.right(ink), pageWidth * 0.98);
      const text = seg.map((w) => w.Text).join(' ');
      line.Cells.push(newField({
        Column: s, OriginalText: text, Text: text, Ink: ink,
        Box: Rect.fromEdges(ink.X - 2, top, right, bottom), Align: 'Left', Indent: 2,
        FontSize: Math.min(em, Math.max(6, bottom - top)),
      }));
    });
    table.Rows.push({ Bounds: lineBounds(line), Lines: [line] });
  }
  table.Bounds = table.Rows.reduce((a, r) => Rect.union(a, r.Bounds), Rect.empty());
  return table;
}

function groupIntoLines(items, box) {
  const lines = [];
  const ordered = items.map((it, i) => [it, i]).sort((a, b) => Rect.cy(box(a[0])) - Rect.cy(box(b[0])) || a[1] - b[1]).map((p) => p[0]);
  for (const it of ordered) {
    const b = box(it);
    let best = -1, bestOverlap = 0;
    for (let i = 0; i < lines.length; i++) {
      const ov = Rect.vOverlap(lines[i].band, b);
      if (ov > bestOverlap) { bestOverlap = ov; best = i; }
    }
    if (best >= 0 && bestOverlap >= 0.4 * Math.min(lines[best].band.Height, b.Height)) {
      lines[best].band = Rect.union(lines[best].band, b);
      lines[best].items.push(it);
    } else lines.push({ band: b, items: [it] });
  }
  return lines.map((l, i) => [l, i]).sort((a, b) => a[0].band.Y - b[0].band.Y || a[1] - b[1]).map((p) => p[0].items);
}

export function estimateFontSize(words) {
  const ems = words.map((w) => w.Box.Height / heightRatio(w.Text)).sort((a, b) => a - b);
  return ems.length === 0 ? 12 : ems[Math.trunc(ems.length / 2)];
}

function heightRatio(t) {
  const desc = [...'gjpqyQJ,;()[]{}/@çµ'].some((c) => t.includes(c));
  const asc = [...t].some((ch) => /\p{Lu}|\p{Nd}/u.test(ch) || "bdfhklt'\"àâäéèêëîïôöûùüÿ!?%&#€$+".includes(ch));
  if (desc && asc) return 0.93;
  if (desc) return 0.72;
  if (asc) return 0.70;
  return 0.47;
}

// ---------- Types de ligne ----------

const ITEM_START = /^\s*(\+\/-\s*)?\d{1,3}\s+\p{L}/u;
const blank = (s) => s.trim().length === 0;

/** « II - STOCK », « IV - MATERIEL … » : rubrique numérotée en chiffres romains (en-tête ou ligne de récapitulatif). */
export const ROMAN_HEADING = /^\s*[IVX]{1,5}\s*[-–.]\s*\S/;
export const isRomanHeading = (s) => ROMAN_HEADING.test(s ?? '');

export function classify(table) {
  table.Rows.forEach((row, r) => {
    const lines = row.Lines;
    // En-tête : 1re rangée, ou rangée de libellés (« exploitation », « réalisation ») sans désignation (récapitulatif).
    const header = isHeaderRow(lines) && (r === 0 || lines.every((l) => blank(l.Cells[0]?.Text ?? '') || isRomanHeading(l.Cells[0].Text)));
    lines.forEach((line, i) => {
      if (line.KindChosenByUser || line.OutsideTable) return;
      if (header) { line.Kind = 'EnTete'; return; }
      const first = line.Cells.length > 0 ? line.Cells[0].Text.trim() : '';
      const valuesEmpty = line.Cells.slice(1).every((c) => blank(c.Text));
      const singleSegment = line.Cells.filter((c) => !blank(c.Text)).length === 1;
      if (startsWithTotal(first)) line.Kind = 'Total';
      else if (isUpperTitle(first) && (
        (i + 1 < lines.length && (ITEM_START.test(lines[i + 1].Cells[0].Text) || first.endsWith(':')))
        || (lines.length === 1 && valuesEmpty && (table.HasGrid || singleSegment))))
        line.Kind = 'Titre';
      else line.Kind = 'Article';
    });
  });
}

function isHeaderRow(lines) {
  const values = lines.flatMap((l) => l.Cells.slice(1)).map((c) => c.Text.trim()).filter((t) => t.length > 0);
  return values.length > 0 && values.every((v) => !/\p{Nd}/u.test(v)) && values.some((v) => v.toLowerCase() !== 'mémoire');
}

/**
 * Ligne de total, même mal lue sur un fond tramé (« TOTAT. MOBILIER », « TOTAE MATERIEL… ») : pas un objet (pas de numéro
 * de lot) et fin de sa partie (un lot 0 est recollé juste avant elle). Le type de la ligne n'est pas changé.
 */
export const looksLikeTotal = (line) => line?.Kind === 'Total' || /^[^A-Za-z0-9_]{0,3}T[O0Q][TI1l][A4][LTEI1|!]/i.test(firstText(line));

export const startsWithTotal = (s) =>
  s.normalize('NFD').replace(/\p{Mn}/gu, '').trimStart().toUpperCase().startsWith('TOTAL');

export function isUpperTitle(s) {
  const letters = [...s].filter((c) => /\p{L}/u.test(c));
  return letters.length >= 4 && letters.filter((c) => /\p{Lu}/u.test(c)).length >= 0.85 * letters.length;
}

// ---------- Édition (ne touche jamais à la géométrie) ----------

export function applyLocks(page) {
  for (const line of allLines(page))
    for (const f of line.Cells) {
      const inZone = page.LockZones.some((z) => Rect.contains(z, Rect.cx(f.Box), Rect.cy(f.Box)));
      f.Locked = line.OutsideTable || inZone;
      if (f.Locked && isModified(f)) f.Text = f.OriginalText;
    }
}

export function addLockZone(page, zone) {
  if (zone.Width < 8 || zone.Height < 8) return false;
  page.LockZones.push(zone);
  applyLocks(page);
  return true;
}

export function removeLockZoneAt(page, x, y) {
  for (let i = page.LockZones.length - 1; i >= 0; i--)
    if (Rect.contains(page.LockZones[i], x, y)) { page.LockZones.splice(i, 1); applyLocks(page); return true; }
  return false;
}

const CYCLE = ['Article', 'Titre', 'Total', 'EnTete', 'Ignoree'];
export function cycleKind(line) {
  line.Kind = CYCLE[(CYCLE.indexOf(line.Kind) + 1) % CYCLE.length];
  line.KindChosenByUser = true;
  return line.Kind;
}

export const kindLabel = (k) => ({ Article: 'Article', Titre: 'Titre', Total: 'Total', EnTete: 'En-tête' })[k] ?? 'Non exportée';

export function findLine(project, field) {
  for (const p of project.Pages) for (const l of allLines(p)) if (l.Cells.includes(field)) return { page: p, line: l };
  return null;
}

// ---------- Recadrage : ce qui a été fait sur la page est gardé ----------

const keyOf = (s) => (s ?? '').normalize('NFC').toLowerCase().replace(/\s+/g, ' ').trim();

/**
 * Après un recadrage (le texte est relu) : chaque ligne relue reprend, depuis la ligne d'avant de même texte (dans l'ordre),
 * son numéro de lot, sa case de lot, son type choisi à la main, et les corrections de ses cases (texte, traits, couleurs,
 * total tapé). Choix des tableaux pour les lots repris dans l'ordre des tableaux. Renvoie le nombre de lignes retrouvées.
 */
export function carryOver(old, fresh) {
  const pool = new Map();
  for (const l of allLines(old)) {
    const k = keyOf(l.Cells[0]?.OriginalText);
    if (!k) continue;
    if (!pool.has(k)) pool.set(k, []);
    pool.get(k).push(l);
  }
  let found = 0;
  for (const l of allLines(fresh)) {
    const k = keyOf(l.Cells[0]?.OriginalText);
    const o = k ? pool.get(k)?.shift() : null;
    if (!o) continue;
    found++;
    for (const p of ['Lot', 'LotManual', 'LotSlot', 'NoLot', 'ForceLot']) if (o[p] !== undefined) l[p] = o[p];
    if (o.KindChosenByUser) { l.Kind = o.Kind; l.KindChosenByUser = true; }
    for (const c of l.Cells) {
      const oc = o.Cells.find((x) => x.Column === c.Column);
      if (!oc || keyOf(oc.OriginalText) !== keyOf(c.OriginalText) || c.Locked) continue;
      if (oc.Text !== oc.OriginalText) c.Text = oc.Text;
      c.Strikes = (oc.Strikes ?? []).map((x) => ({ ...x }));
      if (oc.Colors?.length) c.Colors = oc.Colors.map((x) => ({ ...x }));
      if (oc.Manual) c.Manual = true;
    }
  }
  old.Tables.forEach((t, i) => { if (t.LotsOff && fresh.Tables[i]) fresh.Tables[i].LotsOff = true; });
  return found;
}
