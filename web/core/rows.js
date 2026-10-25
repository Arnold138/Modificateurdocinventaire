// Lignes ajoutées (menu « Ligne », clic droit) : de vraies rangées du tableau, insérées dans la page.
//  - « Dupliquer la ligne » : une rangée est ajoutée juste sous celle de la ligne, avec une copie de la ligne
//    (texte et valeurs, pixels recopiés : elle ressemble à l'imprimé) ; on corrige ce qui change (quantité, prix).
//    Ses montants comptent dans les totaux (ils ne sont pas dans le total imprimé). Case de lot vide.
//  - « Séparer ici » : la ligne touchée (et celles qui la suivent dans la rangée) devient une rangée à part :
//    un filet est tracé au-dessus d'elle (recopié du filet de la rangée), avec sa case de lot et ses cases de prix.
//  - Texte tapé sur plusieurs lignes dans la désignation : « Séparer ici » envoie la suite du texte dans une
//    rangée ajoutée juste en dessous (rangée vierge, filets du tableau, prix à taper).
// Tout ce qui est en dessous descend ; rien d'autre ne change (numéros de lot, autres cases, mise en page).
// « Supprimer la ligne ajoutée » / « Remettre dans la rangée du dessus » défont exactement l'opération.
// Portage identique dans Rows.cs.
import { Rect } from './geometry.js';
import { firstText, applyLocks } from './model.js';
import { CUT, FILET, cutY, gapMid, lineTop } from './arrange.js';
import { baseBox, insertBand, removeBand, setExtra, toBase, insertsOf } from './layout.js';
import { rowOrder } from './lots.js';
import { adjustStrikes } from './strikes.js';

const gridTable = (t) => t.HasGrid !== false && t.RowYs?.length >= 2 && t.ColumnXs?.length >= 2;

/** Tableau, rangée et rang de la ligne dans sa rangée (null hors tableau à filets). */
export function locate(page, line) {
  for (const t of page.Tables) {
    if (!gridTable(t)) continue;
    for (let r = 0; r < t.Rows.length && r + 1 < t.RowYs.length; r++) {
      const i = t.Rows[r].Lines.indexOf(line);
      if (i >= 0) return { table: t, r, i, row: t.Rows[r] };
    }
  }
  return null;
}

/** Bande insérée qui a créé cette rangée (null pour une rangée imprimée). */
export const bandOf = (page, row) => (row?.Inserted ? insertsOf(page).find((b) => b.Id === row.Inserted) ?? null : null);

/** Une ligne d'article (ou un titre) d'un tableau à filets peut être dupliquée ; pas un total ni un en-tête. */
export function canCopyLine(page, line) {
  if (!line || line.OutsideTable || (line.Kind !== 'Article' && line.Kind !== 'Titre') || firstText(line).length === 0) return false;
  return locate(page, line) !== null;
}

/** « Séparer ici » : la ligne n'est pas la première de sa rangée. */
export function canSplit(page, line) {
  const at = line && !line.OutsideTable ? locate(page, line) : null;
  return !!at && at.i > 0;
}

/** Rangée ajoutée par l'utilisateur qui contient cette ligne : { table, r, band } (null sinon). */
export function addedRowOf(page, line) {
  const at = locate(page, line);
  const band = at ? bandOf(page, at.row) : null;
  return band ? { ...at, band } : null;
}

/** Rangées de pixels (page agrandie) [y0, y1[ → morceaux de l'image d'origine. */
function partsOf(page, y0, y1) {
  const parts = [];
  for (let y = y0; y < y1; y++) {
    const s = toBase(page, y);
    if (s === null) continue;
    const last = parts[parts.length - 1];
    if (last && !last.Gap && last.Src + last.Height === s) last.Height++;
    else parts.push({ Src: s, Height: 1 });
  }
  return parts;
}

/** Bornes des rangées d'un tableau à filets, d'après ses filets. */
function rowBounds(t) {
  const l = t.ColumnXs[0], r = t.ColumnXs[t.ColumnXs.length - 1];
  t.Rows.forEach((row, i) => { if (i + 1 < t.RowYs.length) row.Bounds = Rect.fromEdges(l, t.RowYs[i], r, t.RowYs[i + 1]); });
}

/** Ordre d'affichage : la rangée `index` (nouvelle) se place juste après (ou avant) la rangée `anchor` (indice d'avant). */
function orderInsert(t, index, anchor, before = false) {
  if (!Array.isArray(t.RowOrder)) return;
  const shift = (x) => (x >= index ? x + 1 : x);
  const o = rowOrder({ ...t, Rows: t.Rows.slice(0, -1) }).map(shift);
  const k = o.indexOf(shift(anchor));
  o.splice(before ? k : k + 1, 0, index);
  t.RowOrder = o;
}

function orderRemove(t, index) {
  if (!Array.isArray(t.RowOrder)) return;
  t.RowOrder = rowOrder({ ...t, Rows: [...t.Rows, null] }).filter((x) => x !== index).map((x) => (x > index ? x - 1 : x));
}

/** Zone verticale [haut, bas[ de la ligne dans sa rangée (de l'interligne au-dessus à celui d'en dessous). */
function lineBand(at, line) {
  const { table: t, r, i, row } = at;
  const top = i > 0 ? gapMid(row.Lines[i - 1], line) : cutY(t.RowYs[r]) + FILET;
  const bottom = i + 1 < row.Lines.length ? gapMid(line, row.Lines[i + 1]) : cutY(t.RowYs[r + 1]);
  return { top, bottom: Math.max(top + 1, bottom) };
}

/** Bas de l'écriture d'origine de la ligne (sans sa bande d'interligne). */
function inkBottomOf(l) {
  const ink = l.Cells.filter((c) => !Rect.isEmpty(c.Ink));
  return ink.length ? Math.max(...ink.map((c) => Rect.bottom(c.Ink))) : Math.max(...l.Cells.map((c) => Rect.bottom(baseBox(l, c))));
}

/**
 * « Dupliquer la ligne » : rangée ajoutée juste sous la rangée de la ligne, avec une copie de la ligne.
 * `blank` : rangée vierge de même structure (filets du tableau, case de lot vide, désignation et prix vides).
 * `above` : la rangée est posée juste au-dessus de celle de la ligne (sinon juste en dessous).
 * Renvoie la nouvelle ligne (la hauteur de son texte sur plusieurs lignes est à recalculer par l'écran).
 */
export function copyLine(page, line, blank = false, above = false) {
  const at = locate(page, line);
  if (!at || !canCopyLine(page, line) || (above && at.r === 0)) return null;
  const { table: t, r } = at;
  const { top, bottom } = lineBand(at, line);
  const filetTop = cutY(t.RowYs[r]);
  const body = partsOf(page, top, bottom);
  const cols = t.ColumnXs.map((x) => Math.floor(x + 0.5)), x0 = Math.floor(t.ColumnXs[0]) - 4, x1 = Math.ceil(t.ColumnXs[t.ColumnXs.length - 1]) + 4;
  const longest = body.filter((p) => !p.Gap).reduce((a, p) => (!a || p.Height > a.Height ? p : a), null);
  const bodyH = body.reduce((n, p) => n + p.Height, 0);
  const parts = [...partsOf(page, filetTop, filetTop + FILET),
    ...(blank && longest ? [{ Src: longest.Src, Height: bodyH, Gap: true, Span: longest.Height, Cols: cols, X0: x0, X1: x1 }] : body)];
  // Sous le texte, autant d'espace qu'au-dessus (la ligne copiée peut venir d'une rangée de plusieurs lignes) :
  // un interligne propre, filets verticaux prolongés depuis la rangée la plus nette de la ligne copiée.
  const inkTop = lineTop(line), inkBottom = inkBottomOf(line);
  const spaceAbove = inkTop - top, below = bodyH - (inkBottom - top);
  const pad = Math.min(40, Math.max(0, Math.ceil(spaceAbove - below)));
  if (pad > 0 && longest) parts.push({ Src: longest.Src, Height: pad, Gap: true, Span: longest.Height, Cols: cols, X0: x0, X1: x1 });
  const y = cutY(t.RowYs[above ? r : r + 1]);
  // Cases de la ligne, relevées avant que la bande ne décale la page.
  const src = line.Cells.map((f) => ({ f, b: baseBox(line, f), ink: f.Ink }));
  const band = insertBand(page, y, parts, 'Copy');
  // Ligne recopiée : mêmes cases, posées dans la nouvelle rangée (même place par rapport à leur rangée).
  const dy = y + FILET - top;
  const copy = {
    Kind: blank ? 'Article' : line.Kind, KindChosenByUser: !blank && !!line.KindChosenByUser, OutsideTable: false,
    Lot: '', LotManual: false, LotSlot: false, NoLot: false, ForceLot: false, Extra: 0, ExtraAt: 0, Copy: true,
    Cells: src.map(({ f, b, ink }) => ({
      ...f, Id: `${page.Id}-${band.Id}-${f.Column}`, Locked: false,
      Box: { ...b, Y: b.Y + dy },
      Ink: Rect.isEmpty(ink) || blank ? Rect.empty() : { ...ink, Y: ink.Y + dy },
      ...(blank ? { OriginalText: '', Text: '', Manual: false } : {}),
      Strikes: blank ? [] : (f.Strikes ?? []).map((s) => ({ ...s })), Colors: blank ? [] : (f.Colors ?? []).map((s) => ({ ...s })),
    })),
  };
  const index = above ? r : r + 1;
  t.RowYs.splice(index, 0, y + CUT);
  t.Rows.splice(index, 0, { Bounds: Rect.empty(), Lines: [copy], Inserted: band.Id });
  orderInsert(t, index, r, above);
  rowBounds(t);
  applyLocks(page);
  return copy;
}

/** « Ajouter une ligne vide » au-dessus ou en dessous de la rangée de la ligne. */
export const addBlankLine = (page, line, above = false) => copyLine(page, line, true, above);

/** « Séparer ici » : la ligne et ce qui la suit dans sa rangée forment une rangée à part (filet tracé au-dessus). */
export function splitAt(page, line) {
  const at = locate(page, line);
  if (!at || at.i === 0) return false;
  const { table: t, r, i, row } = at;
  const y = gapMid(row.Lines[i - 1], line);
  const filetTop = cutY(t.RowYs[r]);
  const band = insertBand(page, y, partsOf(page, filetTop, filetTop + FILET), 'Split');
  const moved = row.Lines.splice(i);
  t.RowYs.splice(r + 1, 0, y + CUT);
  t.Rows.splice(r + 1, 0, { Bounds: Rect.empty(), Lines: moved, Inserted: band.Id });
  orderInsert(t, r + 1, r);
  rowBounds(t);
  applyLocks(page);
  return true;
}

/**
 * Désignation tapée sur plusieurs lignes : la suite du texte (à partir de la ligne `from`) part dans une rangée
 * ajoutée juste en dessous (rangée vierge de même hauteur, valeurs vides). Renvoie la nouvelle ligne.
 */
export function splitTypedText(page, line, from) {
  const f = line.Cells[0];
  const parts = f.Text.replace(/\r\n?/g, '\n').split('\n');
  if (from <= 0 || from >= parts.length) return null;
  const head = parts.slice(0, from).join('\n').replace(/\s+$/, ''), rest = parts.slice(from).join('\n').replace(/^\s+|\s+$/g, '');
  if (rest.length === 0 || head.length === 0) return null;
  const copy = copyLine(page, line, true);
  if (!copy) return null;
  copy.Cells[0].Text = rest;
  adjustStrikes(f, f.Text, head);
  f.Text = head;
  return copy;
}

/** Défait une rangée ajoutée : copie supprimée, ou rangée séparée remise dans celle du dessus. */
export function removeAddedRow(page, line) {
  const hit = addedRowOf(page, line);
  if (!hit) return false;
  const { table: t, r, band, row } = hit;
  if (band.Kind === 'Split') {
    if (r === 0) return false;
    t.Rows[r - 1].Lines.push(...row.Lines);
  } else for (const l of row.Lines) setExtra(page, l, 0);
  t.Rows.splice(r, 1);
  t.RowYs.splice(r, 1);
  orderRemove(t, r);
  removeBand(page, band);
  rowBounds(t);
  return true;
}
