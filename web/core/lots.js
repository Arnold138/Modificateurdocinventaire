// Numéros de lot : un par objet, continus d'une page à l'autre ; poubelle et « + » par ligne. Portage de Lots.cs.
import { Rect } from './geometry.js';
import { allLines, lineBounds, firstText, isRomanHeading, looksLikeTotal } from './model.js';

const blank = (s) => s.trim().length === 0;

/** Ordre d'affichage des rangées d'un tableau (rangées déplacées à la main), sinon l'ordre imprimé. */
export function rowOrder(table) {
  const n = table.Rows.length, o = table.RowOrder;
  if (!Array.isArray(o) || o.length !== n) return [...Array(n).keys()];
  const seen = new Set(o);
  return seen.size === n && o.every((r) => Number.isInteger(r) && r >= 0 && r < n) ? [...o] : [...Array(n).keys()];
}
export const orderedRows = (table) => rowOrder(table).map((r) => table.Rows[r]);

/** L'objet de la rangée selon la règle : la ligne d'article qui porte les valeurs, sinon la première (pas les récapitulatifs « II - STOCK »). */
function defaultPick(row) {
  // Rangée d'en-tête ou de total (souvent sur fond grisé, où la lecture invente des lignes) : jamais d'objet.
  if (row.Lines.some((l) => l.Kind === 'EnTete' || looksLikeTotal(l))) return null;
  const articles = row.Lines.filter((l) => l.Kind === 'Article' && !l.OutsideTable && !isRomanHeading(firstText(l)));
  if (articles.length === 0) return null;
  const line = articles.find((l) => l.Cells.slice(1).some((c) => !blank(c.Text))) ?? articles[0];
  if (line.Cells.every((c) => blank(c.Text))) return null;
  // Lecture parasite (fond grisé, traits lus comme du texte) : ni montant lisible, ni début d'objet (« 1 … », « +/- 20 … »).
  if (!row.Lines.some((l) => l.Cells.slice(1).some((c) => AMOUNT_LIKE.test(c.Text))) && !ITEM_START_PICK.test(firstText(line)) && !plainText(firstText(line))) return null;
  return line;
}

/** Montant (« 25 000 », « 1 500,50 », « mémoire ») : au moins un chiffre, rien d'autre que des chiffres et séparateurs. */
const AMOUNT_LIKE = /^\s*(?:m[ée]moire|[+-]?\d[\d\s  .,]*)\s*€?\s*$/i;
const ITEM_START_PICK = /^\s*(\+\/-\s*)?\d{1,3}\s+\p{L}/u;
/** Texte lisible : au moins deux mots de trois lettres ou plus, et plus de lettres que de signes. */
function plainText(t) {
  const words = t.split(/\s+/).filter((w) => /^\p{L}{3,}[\p{L}'’.,:-]*$/u.test(w));
  const letters = [...t].filter((c) => /\p{L}/u.test(c)).length, signs = [...t].filter((c) => !/[\p{L}\p{Nd}\s]/u.test(c)).length;
  return words.length >= 2 && letters > 3 * signs;
}

// ForceLot : « + » sur une ligne écrite ; LotHere : « + » sur une ligne encore vide (la case est là avant le texte) ;
// AutoLot : ligne d'une page ajoutée, objet dès qu'on y écrit.
const isObject = (line, pick) => !line.NoLot && !line.OutsideTable && !line.Deleted
  && (line === pick || !!line.LotHere || ((!!line.ForceLot || !!line.AutoLot) && firstText(line).length > 0));

/** Objet d'une rangée : la règle, ou la 1re ligne d'une rangée ajoutée ou séparée (même vide : « Ajouter une ligne vide »). */
const pickOf = (row) => defaultPick(row)
  ?? (row.Inserted ? row.Lines.find((l) => l.Kind === 'Article' && !l.NoLot && (l.Copy || firstText(l).length > 0)) ?? null : null);

/** Lignes d'objet dans l'ordre d'affichage (pages, tableaux, rangées déplacées à la main), sur les tableaux choisis. */
export function* objectLines(project) {
  for (const page of project.Pages)
    for (const table of page.Tables) {
      if (table.LotsOff) continue;
      for (const r of rowOrder(table)) {
        const row = table.Rows[r];
        const pick = pickOf(row);
        for (const line of row.Lines) if (isObject(line, pick)) yield { page, table, row: r, line };
      }
    }
}

// ---------- Lots page par page, tableau par tableau ----------

/** « II - STOCK », « [- MOBILIER » (lecture abîmée) : rubrique numérotée d'un récapitulatif. */
const RECAP_LIKE = /^\W{0,2}[IVXLliJj1|!{\[]{1,5}\s*[-–.]\s+\S/;

/**
 * Le tableau est un tableau d'objets (proposé pour les lots) : au moins deux colonnes de montants et des objets selon la
 * règle ; pas un cadre de texte (en-tête de l'étude, signature), ni un récapitulatif (« I - MOBILIER… 148 760 »).
 */
export function tableHasObjects(table) {
  if (table.HasGrid === false || !(table.RowYs?.length >= 2) || !(table.ColumnXs?.length >= 4)) return false;
  // Une colonne large (la désignation) : pas un cadre de titre sur fond tramé, lu comme une suite de colonnes étroites.
  if (!itemTable(table)) return false;
  const withValues = table.Rows.flatMap((r) => r.Lines).filter((l) => l.Kind !== 'EnTete' && l.Cells.slice(1).some((c) => !blank(c.Text)));
  if (withValues.length > 0 && withValues.filter((l) => RECAP_LIKE.test(firstText(l))).length * 2 >= withValues.length) return false;
  return table.Rows.some((row) => { const pick = pickOf(row); return row.Lines.some((l) => isObject(l, pick)); });
}

/** Tableaux d'une page qui peuvent recevoir des lots (avec leur nom : 1re ligne d'en-tête ou de texte). */
export function lotTables(page) {
  return page.Tables.map((t, i) => ({ table: t, index: i }))
    .filter(({ table: t }) => t.Rows.some((r) => r.Lines.some((l) => firstText(l).length > 0)))
    .map(({ table, index }) => {
      // Nom : la 1re ligne d'objet (bien lue), sinon l'en-tête ou la 1re ligne de texte.
      const lines = table.Rows.flatMap((r) => r.Lines);
      const object = table.Rows.map((r) => pickOf(r)).find((l) => l && firstText(l).length > 0);
      const head = object ?? lines.find((l) => l.Kind === 'EnTete' && firstText(l).length > 0) ?? lines.find((l) => firstText(l).length > 0);
      return { table, index, name: head ? firstText(head) : `Tableau ${index + 1}`, objects: tableHasObjects(table) };
    });
}

/** Choix proposé : les tableaux qui contiennent des objets (déjà choisis si les lots existent). */
export const defaultLotChoice = (project, table) => (lotsActive(project) ? !table.LotsOff : tableHasObjects(table));

/** Applique le choix (tableaux cochés) : les autres n'ont plus de case ni de numéro ; rien d'autre ne change. */
export function setLotTables(project, chosen) {
  for (const page of project.Pages) for (const t of page.Tables) t.LotsOff = !chosen.has(t);
  resyncLots(project);
}

const ITEM_START = /^\s*(\+\/-\s*)?\d{1,3}\s+\p{L}/u;

/**
 * Ligne sans numéro à laquelle on peut en ajouter un (« + ») : article ou titre (pas un total, un en-tête, un récapitulatif),
 * qui commence sa rangée ou ressemble à un objet (« 1 gerbeur… », « +/- 20 palettes… »).
 */
export function canAddLot(line, row = null) {
  if (hasLotCell(line) || line.OutsideTable || (line.Kind !== 'Article' && line.Kind !== 'Titre')) return false;
  const t = firstText(line);
  // Ligne encore vide d'une rangée ajoutée ou d'une page ajoutée : « + » pour y mettre une case avant d'écrire.
  if (t.length === 0) return line.Kind === 'Article' && (!!row?.Inserted || !!line.AutoLot);
  if (isRomanHeading(t)) return false;
  return !row || row.Lines.find((l) => firstText(l).length > 0) === line || ITEM_START.test(t);
}

const everyLine = (project) => project.Pages.flatMap((p) => [...allLines(p)]);

/** Lot 0 : objet absent ou barré, déplacé après le dernier lot. */
export const isZeroLot = (line) => /^[+-]?\d+$/.test((line.Lot ?? '').trim()) && parseInt(line.Lot, 10) === 0;

/** Case de lot à afficher sur cette ligne (numérotée ou vide). */
export const hasLotCell = (l) => (l.Lot ?? '').length > 0 || !!l.LotSlot;

export const lotsActive = (project) => everyLine(project).some(hasLotCell);

/** Bouton « Lots » : une case vide par objet ; on tape les numéros soi-même, rien ne se remplit tout seul. */
export function createEmptyLots(project) {
  clearLots(project);
  project.LotsByHand = true;
  for (const { line } of [...objectLines(project)]) line.LotSlot = true;
}

/**
 * « Synchroniser » : remplit les cases vides, sans toucher aux numéros déjà là (tapés ou remplis avant), dans l'ordre affiché.
 * Une case vide prend le numéro qui suit le lot précédent (1 au début). Si les lots suivants se suivaient à partir de ce numéro
 * (6, 7, 8…), ils sont décalés d'un cran (7, 8, 9…) pour éviter un doublon ; un ordre fait à la main (30, 29, 6…) n'est pas touché.
 * `pages` : seulement les cases vides de ces pages (les numéros des autres pages servent de repère).
 */
export function fillLotsAutomatically(project, pages = null) {
  resyncLots(project);
  const list = [...objectLines(project)];
  const num = (l) => (/^[+-]?\d+$/.test((l.Lot ?? '').trim()) ? parseInt(l.Lot, 10) : NaN);
  let prev = 0;
  for (let i = 0; i < list.length; i++) {
    const { line, page } = list[i];
    if ((line.Lot ?? '').length > 0) { const v = num(line); if (!Number.isNaN(v) && v !== 0) prev = v; continue; }
    if (pages && !pages.includes(page)) continue;
    const v = prev + 1;
    line.Lot = String(v);
    line.LotManual = false;
    // Décaler la suite : les lots qui se suivaient à partir de v prennent un cran de plus (les 0 et les cases vides sont passés).
    let k = v;
    for (let j = i + 1; j < list.length; j++) {
      const o = list[j].line;
      if ((o.Lot ?? '').length === 0 || isZeroLot(o)) continue;
      if (num(o) !== k) break;
      o.Lot = String(k + 1);
      k++;
    }
    prev = v;
  }
}

/** Renumérote tout à partir de `start` (les numéros tapés sont oubliés, sauf les 0). */
export function numberLots(project, start = 1) {
  resyncLots(project);
  if (!lotsActive(project)) for (const { line } of [...objectLines(project)]) line.LotSlot = true;
  for (const l of everyLine(project)) if (!isZeroLot(l)) { l.Lot = ''; l.LotManual = false; }
  const first = [...objectLines(project)].find((o) => !isZeroLot(o.line));
  if (!first) return;
  first.line.Lot = String(start);
  first.line.LotManual = true;
  fillLotsAutomatically(project);
}

/** Vide toutes les cases (la colonne reste). */
export function emptyLots(project) {
  for (const l of everyLine(project)) { l.Lot = ''; l.LotManual = false; }
  resyncLots(project);
}

export function clearLots(project) {
  for (const l of everyLine(project)) { l.Lot = ''; l.LotManual = false; l.LotSlot = false; }
  project.LotsByHand = false;
}

/** Numéro tapé à la main : seule cette case change (les autres ne bougent jamais toutes seules). */
export function setLot(project, line, value) {
  line.Lot = value.trim();
  if (!isZeroLot(line)) delete line.EndAt; // remis dans la vente : l'endroit choisi pour son lot 0 est oublié
  line.LotManual = line.Lot.length > 0;
  resyncLots(project);
}

/** Poubelle : cette ligne n'est pas un objet ; sa case disparaît (les autres numéros ne bougent pas). */
export function removeLot(project, line) {
  line.NoLot = true;
  line.ForceLot = false;
  line.LotHere = false;
  line.Lot = '';
  line.LotManual = false;
  resyncLots(project);
}

/** « + » : cette ligne est un objet ; une case vide apparaît. */
export function addLot(project, line) {
  line.NoLot = false;
  line.ForceLot = true;
  if (firstText(line).length === 0) line.LotHere = true; // ligne encore vide : la case reste en attendant le texte
  line.Lot = '';
  line.LotManual = false;
  if (!lotsActive(project)) line.LotSlot = true;
  resyncLots(project);
}

/** Tableau d'objets (au moins trois colonnes dont une large, la désignation) : on peut y ajouter une case même si ses lots sont éteints. */
export function itemTable(t) {
  if (!t || t.HasGrid === false || !(t.RowYs?.length >= 2) || !(t.ColumnXs?.length >= 4)) return false;
  let widest = 0;
  for (let c = 0; c + 1 < t.ColumnXs.length; c++) widest = Math.max(widest, t.ColumnXs[c + 1] - t.ColumnXs[c]);
  return widest * 100 >= 35 * (t.ColumnXs[t.ColumnXs.length - 1] - t.ColumnXs[0]);
}

/**
 * « + » sur une ligne (ex. un objet dans une grande case) : une case vide apparaît à sa hauteur, rien d'autre ne bouge.
 * Dans un tableau qui n'avait pas de lots, le tableau les reçoit, mais seule cette ligne a une case (les autres objets
 * gardent « pas de case » : on les ajoute un par un avec « + », ou avec Lots › Pages et tableaux).
 */
export function addLotHere(project, table, line) {
  if (table?.LotsOff) {
    table.LotsOff = false;
    for (const o of objectLines(project)) if (o.table === table && o.line !== line && !hasLotCell(o.line)) o.line.NoLot = true;
  }
  addLot(project, line);
}

/** Met les cases en place (une par objet) sans toucher à aucun numéro. */
export function resyncLots(project) {
  if (!lotsActive(project)) return;
  const objects = new Set([...objectLines(project)].map((o) => o.line));
  for (const l of everyLine(project)) {
    l.LotSlot = objects.has(l);
    if (!l.LotSlot && !l.Deleted) { l.Lot = ''; l.LotManual = false; } // ligne supprimée : son numéro est gardé (si on la remet)
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
