// Numéros de lot : un par objet (ligne d'article), continus d'une page à l'autre. Portage de Lots.cs.
import { Rect } from './geometry.js';
import { allLines, lineBounds } from './model.js';

const blank = (s) => s.trim().length === 0;

/** Lignes d'objet dans l'ordre du document : la première ligne d'article de chaque rangée. */
export function* objectLines(project) {
  for (const page of project.Pages)
    for (const table of page.Tables)
      for (const row of table.Rows) {
        const articles = row.Lines.filter((l) => l.Kind === 'Article' && !l.OutsideTable);
        if (articles.length === 0) continue;
        const line = articles.find((l) => l.Cells.slice(1).some((c) => !blank(c.Text))) ?? articles[0];
        if (line.Cells.every((c) => blank(c.Text))) continue;
        yield { page, table, line };
      }
}

const everyLine = (project) => project.Pages.flatMap((p) => [...allLines(p)]);

export const lotsActive = (project) => everyLine(project).some((l) => (l.Lot ?? '').length > 0);

export function numberLots(project, start = 1) {
  for (const l of everyLine(project)) { l.Lot = ''; l.LotManual = false; }
  const first = objectLines(project).next().value;
  if (!first) return;
  first.line.Lot = String(start);
  first.line.LotManual = true;
  resyncLots(project);
}

export function clearLots(project) {
  for (const l of everyLine(project)) { l.Lot = ''; l.LotManual = false; }
}

/** Numéro tapé à la main : la suite se recale automatiquement. */
export function setLot(project, line, value) {
  line.Lot = value.trim();
  line.LotManual = line.Lot.length > 0;
  resyncLots(project);
}

export function resyncLots(project) {
  if (!lotsActive(project)) return;
  const objects = new Set([...objectLines(project)].map((o) => o.line));
  for (const l of everyLine(project)) if (!objects.has(l)) { l.Lot = ''; l.LotManual = false; }
  let next = 1;
  for (const { line } of objectLines(project)) {
    const v = /^[+-]?\d+$/.test(line.Lot ?? '') ? parseInt(line.Lot, 10) : NaN;
    if (line.LotManual && !Number.isNaN(v)) next = v + 1;
    else if (line.LotManual && (line.Lot ?? '').length > 0) { /* « 12 bis » : gardé tel quel */ }
    else { line.Lot = String(next); line.LotManual = false; next++; }
  }
}

/** Emplacement du numéro dans la marge gauche de la page, à la hauteur de la ligne. */
export function lotBox(table, line) {
  const b = lineBounds(line);
  const w = Math.min(70, Math.max(14, table.Bounds.X - 6));
  const x = Math.max(1, table.Bounds.X - 4 - w);
  const h = Math.max(8, b.Height);
  return Rect.make(x, Rect.cy(b) - h / 2, w, h);
}

export const lotLabels = (page) =>
  page.Tables.flatMap((t) => t.Rows.flatMap((r) => r.Lines).filter((l) => (l.Lot ?? '').length > 0).map((line) => ({ line, table: t, box: lotBox(t, line) })));

/** « prisÃ© » → « prisé » : octets UTF-8 relus comme du Latin-1. */
export function repairName(name) {
  if (!/[ÃÂ]/.test(name)) return name;
  try {
    const bytes = Uint8Array.from([...name].map((c) => { const k = c.charCodeAt(0); if (k > 255) throw new Error(); return k; }));
    return new TextDecoder('utf-8', { fatal: true }).decode(bytes);
  } catch { return name; }
}
