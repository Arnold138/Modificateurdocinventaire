// Numéros de lot : un par objet, continus d'une page à l'autre ; poubelle et « + » par ligne. Portage de Lots.cs.
import { Rect } from './geometry.js';
import { allLines, lineBounds, firstText } from './model.js';

const blank = (s) => s.trim().length === 0;

/** L'objet de la rangée selon la règle : la ligne d'article qui porte les valeurs, sinon la première. */
function defaultPick(row) {
  const articles = row.Lines.filter((l) => l.Kind === 'Article' && !l.OutsideTable);
  if (articles.length === 0) return null;
  const line = articles.find((l) => l.Cells.slice(1).some((c) => !blank(c.Text))) ?? articles[0];
  return line.Cells.every((c) => blank(c.Text)) ? null : line;
}

const isObject = (line, pick) => !line.NoLot && !line.OutsideTable && (line === pick || (!!line.ForceLot && firstText(line).length > 0));

/** Lignes d'objet dans l'ordre du document. */
export function* objectLines(project) {
  for (const page of project.Pages)
    for (const table of page.Tables)
      for (const row of table.Rows) {
        const pick = defaultPick(row);
        for (const line of row.Lines) if (isObject(line, pick)) yield { page, table, line };
      }
}

/** Ligne sans numéro à laquelle on peut en ajouter un (« + »). */
export const canAddLot = (line) =>
  !hasLotCell(line) && !line.OutsideTable && line.Kind !== 'EnTete' && line.Kind !== 'Ignoree' && firstText(line).length > 0;

const everyLine = (project) => project.Pages.flatMap((p) => [...allLines(p)]);

/** Lot 0 : objet absent ou barré (regroupé à la fin du .txt). */
export const isZeroLot = (line) => /^[+-]?\d+$/.test((line.Lot ?? '').trim()) && parseInt(line.Lot, 10) === 0;

/** Case de lot à afficher sur cette ligne (numérotée ou vide). */
export const hasLotCell = (l) => (l.Lot ?? '').length > 0 || !!l.LotSlot;

/** « Colonne vide » : une case par objet, toutes vides ; rien ne se remplit tout seul. */
export function createEmptyLots(project) {
  clearLots(project);
  project.LotsByHand = true;
  for (const { line } of [...objectLines(project)]) line.LotSlot = true;
}

/** « Synchroniser automatiquement » : le logiciel remplit toutes les cases (numéros tapés gardés comme repères). */
export function fillLotsAutomatically(project) {
  project.LotsByHand = false;
  resyncLots(project);
}

export const lotsActive = (project) => everyLine(project).some(hasLotCell);

export function numberLots(project, start = 1) {
  project.LotsByHand = false;
  for (const l of everyLine(project)) { l.Lot = ''; l.LotManual = false; }
  const first = objectLines(project).next().value;
  if (!first) return;
  first.line.Lot = String(start);
  first.line.LotManual = true;
  resyncLots(project);
}

export function clearLots(project) {
  for (const l of everyLine(project)) { l.Lot = ''; l.LotManual = false; l.LotSlot = false; }
  project.LotsByHand = false;
}

/** Numéro tapé à la main : la suite se recale automatiquement. */
export function setLot(project, line, value) {
  line.Lot = value.trim();
  line.LotManual = line.Lot.length > 0;
  resyncLots(project);
}

/** Poubelle : cette ligne n'est pas un objet ; les numéros suivants se décalent. */
export function removeLot(project, line) {
  line.NoLot = true;
  line.ForceLot = false;
  line.Lot = '';
  line.LotManual = false;
  resyncLots(project);
}

/** « + » : cette ligne est un objet ; elle prend le numéro qui suit le précédent. */
export function addLot(project, line) {
  line.NoLot = false;
  line.ForceLot = true;
  line.Lot = '';
  line.LotManual = false;
  if (!lotsActive(project)) line.LotSlot = true;
  resyncLots(project);
}

export function resyncLots(project) {
  if (!lotsActive(project)) return;
  const objects = new Set([...objectLines(project)].map((o) => o.line));
  for (const l of everyLine(project)) {
    l.LotSlot = objects.has(l);
    if (!l.LotSlot) { l.Lot = ''; l.LotManual = false; }
  }
  if (project.LotsByHand) return; // remplis à la main : on ne touche à aucun numéro
  let next = 1;
  for (const { line } of objectLines(project)) {
    const v = /^[+-]?\d+$/.test(line.Lot ?? '') ? parseInt(line.Lot, 10) : NaN;
    if (line.LotManual && !Number.isNaN(v)) { if (v !== 0) next = v + 1; } // 0 : absent ou barré, pas de numéro
    else if (line.LotManual && (line.Lot ?? '').length > 0) { /* « 12 bis » : gardé tel quel */ }
    else { line.Lot = String(next); line.LotManual = false; next++; }
  }
}

// ---------- Emplacement : une colonne ajoutée à gauche de chaque tableau, dans la marge ----------

/** Bord gauche du tableau (son filet vertical gauche s'il a une grille). */
export const tableLeft = (table) => (table.HasGrid !== false && table.ColumnXs?.length > 0 ? table.ColumnXs[0] : table.Bounds.X);

/** Largeur de la colonne des lots : 3 chiffres (plus si besoin) dans la taille du texte imprimé, sans dépasser la marge. */
export function columnWidth(page) {
  const sizes = page.Tables.flatMap((t) => t.Rows).flatMap((r) => r.Lines)
    .map((l) => l.Cells[0]?.FontSize ?? 0).filter((s) => s > 0).sort((a, b) => a - b);
  const fs = sizes.length > 0 ? sizes[Math.trunc(sizes.length / 2)] : 16;
  const digits = Math.max(3, ...[...allLines(page)].map((l) => (l.Lot ?? '').length));
  const desired = Math.floor(fs * (0.55 * digits + 0.9) + 0.5);
  const lefts = page.Tables.filter((t) => t.Rows.some((r) => r.Lines.some(hasLotCell))).map(tableLeft);
  const avail = lefts.length > 0 ? Math.min(...lefts) - 6 : desired;
  return Math.max(8, Math.min(desired, avail));
}

/** Case du numéro de lot : dans la colonne, à la hauteur de la ligne d'objet. */
export function numberBox(table, line, width) {
  const lb = lineBounds(line);
  const b = { ...lb, Height: lb.Height - (line.Extra ?? 0) }; // à la hauteur de la 1re ligne, même si la case a grandi
  const right = tableLeft(table), left = Math.max(0, right - width);
  const h = Math.max(8, b.Height);
  return Rect.make(left + 2, Rect.cy(b) - h / 2, Math.max(4, right - left - 4), h);
}

export function lotLabels(page) {
  const w = columnWidth(page);
  return page.Tables.flatMap((t) => t.Rows.flatMap((r) => r.Lines).filter(hasLotCell).map((line) => ({ line, table: t, box: numberBox(t, line, w) })));
}

/** Tri « naturel » des noms : « page 2 » avant « page 10 ». */
export const naturalCompare = (a, b) => a.localeCompare(b, 'fr', { numeric: true, sensitivity: 'base' });

/** « prisÃ© » → « prisé » : octets UTF-8 relus comme du Latin-1. */
export function repairName(name) {
  name = name.normalize('NFC'); // iPhone/Mac : noms décomposés (« A » + tilde)
  if (!/[ÃÂ]/.test(name)) return name;
  try {
    const bytes = Uint8Array.from([...name].map((c) => { const k = c.charCodeAt(0); if (k > 255) throw new Error(); return k; }));
    return new TextDecoder('utf-8', { fatal: true }).decode(bytes);
  } catch { return name; }
}
