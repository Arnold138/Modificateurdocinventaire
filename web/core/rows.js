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
import { firstText, applyLocks, isModified } from './model.js';
import { CUT, FILET, cutY, gapMid, lineTop } from './arrange.js';
import { baseBox, insertBand, removeBand, setExtra, toBase, insertsOf, cutBand, pieces } from './layout.js';
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
  if (!line || line.OutsideTable || (line.Kind !== 'Article' && line.Kind !== 'Titre') || (firstText(line).length === 0 && !page?.Added)) return false;
  return locate(page, line) !== null;
}

/** « Séparer ici » : la ligne n'est pas la première de sa rangée. */
/**
 * Le trait tracé au-dessus de la ligne i passe bien entre les lignes : toutes les cases d'avant au-dessus, toutes celles
 * d'après en dessous (une lecture parasite sur un fond tramé peut chevaucher la ligne d'au-dessus : pas de trait alors).
 */
function splitOk(at) {
  if (!at || at.i <= 0 || at.i >= at.row.Lines.length) return false;
  const { row, i } = at;
  const y = gapMid(row.Lines[i - 1], row.Lines[i]) + CUT;
  const cy = (l, f) => { const b = baseBox(l, f); return b.Y + b.Height / 2; };
  return row.Lines.every((l, k) => l.Cells.every((f) => (k < i ? cy(l, f) < y : cy(l, f) > y)));
}
export function canSplit(page, line) {
  const at = line && !line.OutsideTable ? locate(page, line) : null;
  return splitOk(at);
}

/** Rangée ajoutée par l'utilisateur qui contient cette ligne : { table, r, band } (null sinon). */
export function addedRowOf(page, line) {
  const at = locate(page, line);
  const band = at ? bandOf(page, at.row) : null;
  return band ? { ...at, band } : null;
}

/**
 * Rangées de pixels (page agrandie) [y0, y1[ → morceaux : rangées de l'image d'origine, et interlignes d'une rangée déjà
 * ajoutée (recopiés comme interlignes : sans eux, copier une ligne ajoutée donnait une rangée réduite à son filet).
 * Les bandes d'agrandissement d'un texte tapé ne sont pas recopiées (la copie grandit à son tour si son texte le demande).
 */
function partsOf(page, y0, y1) {
  const seq = pieces(page);
  const parts = [];
  let pos = 0, k = 0, gapOf = -1;
  for (let y = y0; y < y1; y++) {
    while (k < seq.length && y >= pos + seq[k].n) { pos += seq[k].n; k++; }
    if (k >= seq.length) break;
    const x = seq[k], last = parts[parts.length - 1];
    if (x.extra) continue;
    if (x.gap) {
      if (last?.Gap && gapOf === k) last.Height++;
      else { parts.push({ Src: x.s, Height: 1, Gap: true, Span: x.span, Cols: x.cols, X0: x.x0, X1: x.x1 }); gapOf = k; }
      continue;
    }
    const s = x.y0 + (y - pos);
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
  let top = i > 0 ? gapMid(row.Lines[i - 1], line) : cutY(t.RowYs[r]) + FILET;
  let bottom = i + 1 < row.Lines.length ? gapMid(line, row.Lines[i + 1]) : cutY(t.RowYs[r + 1]);
  // Lignes qui se chevauchent (lecture parasite sur un fond tramé) : la bande contient toujours toute la ligne, dans sa rangée.
  const boxes = line.Cells.map((f) => baseBox(line, f));
  top = Math.max(cutY(t.RowYs[r]) + FILET, Math.min(top, Math.floor(Math.min(...boxes.map((b) => b.Y)))));
  bottom = Math.min(cutY(t.RowYs[r + 1]), Math.max(bottom, Math.ceil(Math.max(...boxes.map((b) => Rect.bottom(b))))));
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
export function copyLine(page, line, blank = false, above = false, src = line, force = false) {
  const at = locate(page, line);
  if (!at || (!force && src === line && !canCopyLine(page, line)) || (above && at.r === 0)) return null;
  const { table: t, r } = at;
  // Modèle de la rangée (`src`, même tableau) : hauteur, filets et place des cases ; `line` : où la poser.
  const sat = src === line ? at : locate(page, src);
  if (!sat || sat.table !== t) return null;
  const { top, bottom } = lineBand(sat, src);
  const filetTop = cutY(t.RowYs[sat.r]);
  const body = partsOf(page, top, bottom);
  const cols = t.ColumnXs.map((x) => Math.floor(x + 0.5)), x0 = Math.floor(t.ColumnXs[0]) - 4, x1 = Math.ceil(t.ColumnXs[t.ColumnXs.length - 1]) + 4;
  // Rangée la plus nette pour prolonger les filets : la plus longue rangée d'image, sinon l'interligne d'une rangée ajoutée.
  const longest = body.filter((p) => !p.Gap).reduce((a, p) => (!a || p.Height > a.Height ? p : a), null) ?? body.find((p) => p.Gap) ?? null;
  const span = longest?.Gap ? longest.Span : longest?.Height;
  const bodyH = body.reduce((n, p) => n + p.Height, 0);
  const parts = [...partsOf(page, filetTop, filetTop + FILET),
    ...(blank && longest ? [{ Src: longest.Src, Height: bodyH, Gap: true, Span: span, Cols: cols, X0: x0, X1: x1 }] : body)];
  // Sous le texte, autant d'espace qu'au-dessus (la ligne copiée peut venir d'une rangée de plusieurs lignes) :
  // un interligne propre, filets verticaux prolongés depuis la rangée la plus nette de la ligne copiée.
  const inkTop = lineTop(src), inkBottom = inkBottomOf(src);
  const spaceAbove = inkTop - top, below = bodyH - (inkBottom - top);
  const pad = Math.min(40, Math.max(0, Math.ceil(spaceAbove - below)));
  if (pad > 0 && longest && src === line) parts.push({ Src: longest.Src, Height: pad, Gap: true, Span: span, Cols: cols, X0: x0, X1: x1 });
  const y = cutY(t.RowYs[above ? r : r + 1]);
  // Cases de la ligne, relevées avant que la bande ne décale la page.
  const cells = src.Cells.map((f) => ({ f, b: baseBox(src, f), ink: f.Ink }));
  const band = insertBand(page, y, parts, 'Copy');
  // Ligne recopiée : mêmes cases, posées dans la nouvelle rangée (même place par rapport à leur rangée).
  const dy = y + FILET - top;
  const copy = {
    Kind: blank ? 'Article' : src.Kind, KindChosenByUser: !blank && !!src.KindChosenByUser, OutsideTable: false,
    Lot: '', LotManual: false, LotSlot: false, NoLot: false, ForceLot: false, Extra: 0, ExtraAt: 0, Copy: true,
    Cells: cells.map(({ f, b, ink }) => ({
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
  if (!splitOk(at)) return false;
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

/** « Tracer un trait en dessous » : la ligne suivante de la rangée commence une nouvelle rangée. */
export function canSplitBelow(page, line) {
  const at = line && !line.OutsideTable ? locate(page, line) : null;
  return !!at && at.i + 1 < at.row.Lines.length && splitOk({ ...at, i: at.i + 1 });
}
export function splitBelow(page, line) {
  const at = locate(page, line);
  return !!at && at.i + 1 < at.row.Lines.length && splitAt(page, at.row.Lines[at.i + 1]);
}

/**
 * « Ajouter une ligne TOTAL » sous la rangée de la ligne : rangée complète (comme une ligne vide), en gras,
 * désignation « TOTAL », montants calculés selon `scope` ({ Mode: 'above' | 'page' | 'pages' | 'all', Pages: [Id], Auto }).
 */
export function addTotalLine(page, line, scope) {
  const t = copyLine(page, line, true, false);
  if (!t) return null;
  t.Kind = 'Total';
  t.KindChosenByUser = true;
  t.Cells[0].Text = 'TOTAL';
  t.TotalScope = { Mode: scope?.Mode ?? 'above', Pages: [...(scope?.Pages ?? [])], Auto: scope?.Auto !== false, Bold: !!scope?.Bold };
  return t;
}

/** Ligne d'appui pour une ligne TOTAL ajoutée depuis le menu de la page : la dernière ligne d'objet du tableau (ou du dernier). */
export function lastObjectLine(page, table = null) {
  const tables = page.Tables.filter((t) => gridTable(t) && (!table || t === table));
  for (let k = tables.length - 1; k >= 0; k--) {
    const t = tables[k];
    const order = rowOrder(t);
    // De préférence la dernière ligne qui porte un montant (pas une lecture parasite d'un fond grisé).
    for (const strict of [true, false])
      for (let i = order.length - 1; i >= 0; i--) {
        const row = t.Rows[order[i]];
        for (let j = row.Lines.length - 1; j >= 0; j--) {
          const l = row.Lines[j];
          if (!canCopyLine(page, l) || l.Deleted) continue;
          if (!strict || l.Cells.slice(1).some((c) => AMOUNT.test(c.Text))) return l;
        }
      }
  }
  return null;
}

const AMOUNT = /^\s*(?:m[ée]moire|[+-]?\d[\d\s.,]*)\s*€?\s*$/i;

// ---------- Réduire la rangée : enlever l'espace vide imprimé au-dessus ou en dessous du texte ----------

/** Écriture de la ligne : encre lue (texte retapé : sa case), null si la ligne est vide. */
function contentOf(l) {
  let r = null;
  for (const c of l.Cells) {
    if (!c.Text.trim()) continue;
    const b = !isModified(c) && !Rect.isEmpty(c.Ink) ? c.Ink : baseBox(l, c);
    r = r ? Rect.union(r, b) : b;
  }
  return r;
}

/** Haut et bas de l'écriture d'une rangée, et ses bornes utiles (sous le filet du haut, au-dessus de celui du bas). */
function rowSpan(t, r) {
  const rects = t.Rows[r].Lines.map(contentOf).filter(Boolean);
  if (!rects.length) return null;
  return {
    lines: rects.length,
    top: cutY(t.RowYs[r]) + FILET, bottom: cutY(t.RowYs[r + 1]),
    inkTop: Math.floor(Math.min(...rects.map((x) => x.Y))), inkBottom: Math.ceil(Math.max(...rects.map((x) => Rect.bottom(x)))),
  };
}

const median = (a) => { const s = [...a].sort((x, y) => x - y); return s[Math.trunc(s.length / 2)]; };

/** Marges habituelles des rangées d'une ligne du tableau (au-dessus et en dessous du texte) et hauteur du texte. */
function normalPads(t) {
  const top = [], bottom = [], height = [];
  t.Rows.forEach((row, r) => {
    if (r + 1 >= t.RowYs.length || row.Inserted || row.Lines.some((l) => (l.Extra ?? 0) > 0)) return;
    const s = rowSpan(t, r);
    if (!s || s.lines !== 1) return;
    top.push(s.inkTop - s.top); bottom.push(s.bottom - s.inkBottom); height.push(s.inkBottom - s.inkTop);
  });
  return top.length ? { top: median(top), bottom: median(bottom), height: median(height) } : null;
}

/** Rangées de pixels [y0, y0 + h[ toutes prises dans l'image d'origine, d'un seul tenant (pas dans une bande ajoutée). */
function contiguous(page, y0, h) {
  const a = toBase(page, y0), b = toBase(page, y0 + h - 1);
  return a !== null && b !== null && b - a === h - 1;
}

/**
 * Espace vide en trop dans la rangée de la ligne (au-dessus et en dessous du texte), comparé aux autres rangées du tableau :
 * [{ y0, h }] du bas vers le haut, ou null. Pas pour une ligne dupliquée ou vide ajoutée, une rangée agrandie ou qui contient une ligne supprimée
 * (une rangée séparée, oui : seul son filet a été ajouté).
 */
export function rowTrimOf(page, line) {
  // Page ajoutée : rangées vierges, rien d'imprimé à enlever (la hauteur se règle avec « Ligne plus / moins haute »).
  const at = line && !line.OutsideTable && !page.Added ? locate(page, line) : null;
  if (!at) return null;
  const { table: t, r, row } = at;
  if (bandOf(page, row)?.Kind === 'Copy' || row.Lines.some((l) => l.Deleted || (l.Extra ?? 0) > 0)) return null;
  // Texte tapé sur plusieurs lignes : la place lui revient, on ne réduit pas.
  if (row.Lines.some((l) => l.Cells.some((c) => isModified(c) && c.Text.includes('\n')))) return null;
  const pads = normalPads(t), s = rowSpan(t, r);
  if (!pads || !s) return null;
  const min = Math.max(4, Math.trunc(pads.height / 2));
  const cuts = [];
  // Espace en trop au-dessus et en dessous du texte : enlevé s'il dépasse, en tout, la moitié d'une ligne.
  const below = Math.max(0, s.bottom - s.inkBottom - pads.bottom), above = Math.max(0, s.inkTop - s.top - pads.top);
  if (below + above < min) return null;
  if (below > 0 && contiguous(page, s.bottom - below, below)) cuts.push({ y0: s.bottom - below, h: below });
  if (above > 0 && contiguous(page, s.top, above)) cuts.push({ y0: s.top, h: above });
  return cuts.length ? cuts : null;
}

/** « Réduire la rangée » : enlève l'espace vide en trop ; la rangée prend la hauteur des autres. Vrai si elle a changé. */
export function trimRow(page, line) {
  const cuts = rowTrimOf(page, line);
  if (!cuts) return false;
  const id = locate(page, line).row.Lines[0]?.Cells[0]?.Id ?? null; // la réduction suit sa rangée
  for (const c of cuts) cutBand(page, c.y0, c.h, id);
  rowBounds(locate(page, line).table);
  return true;
}

/** Bandes « Trim » de la rangée de la ligne (rangée réduite). */
export function trimsOf(page, line) {
  const at = line ? locate(page, line) : null;
  if (!at) return [];
  const ids = new Set(at.row.Lines.map((l) => l.Cells[0]?.Id));
  return insertsOf(page).filter((b) => b.Kind === 'Trim' && b.Line && ids.has(b.Line));
}

/** « Remettre la hauteur d'origine » : l'espace enlevé revient (les cases reprennent la place qu'il leur faut). */
export function untrimRow(page, line) {
  const bands = trimsOf(page, line).sort((a, b) => b.At - a.At);
  for (const b of bands) removeBand(page, b);
  if (bands.length) rowBounds(locate(page, line).table);
  return bands.length > 0;
}
