// Mise en page des rangées déplacées : lots 0 envoyés après le dernier lot, rangées montées/descendues à la main.
// Rien n'est modifié dans le projet : la page affichée (et exportée) est recomposée à partir des pages d'origine.
//  - lot 0 : l'objet (avec ses lignes de suite et, s'il les reprend, les valeurs de son titre) quitte sa rangée,
//    ce qui suit remonte ; il est recollé juste après la rangée du dernier objet numéroté (même s'il est sur
//    une autre page), dans une rangée refaite aux colonnes du tableau d'arrivée ; ce qui suit descend.
//  - rangée déplacée à la main : l'ordre des rangées du tableau change (Table.RowOrder).
// Portage identique (entiers, pixel pour pixel) dans Arrangement.cs.
import { Rect } from './geometry.js';
import { allLines, looksLikeTotal } from './model.js';
import { rowOrder, hasLotCell, isZeroLot, lotsActive, objectLines, columnWidth, numberBox, tableLeft } from './lots.js';
import { baseBox } from './layout.js';
import { framedTables, framedRows, lotFrameEnabled, ruleLeftOf } from './lotcolumn.js';

export const CUT = 2;    // une rangée commence 2 px au-dessus du milieu de son filet du haut
export const FILET = 5;  // hauteur réservée au filet du haut
export const MARGIN = 3; // marge entre un filet vertical et le contenu recopié d'une case

const R = (v) => Math.floor(v + 0.5);
export const cutY = (y) => R(y) - CUT;
const blank = (s) => (s ?? '').trim().length === 0;
const hasValues = (l) => l.Cells.slice(1).some((c) => !blank(c.Text));
const gridTable = (t) => t.HasGrid !== false && t.RowYs?.length >= 2 && t.ColumnXs?.length >= 2;

/** Haut et bas de l'écriture d'une ligne (bande ajoutée comprise). */
export function lineTop(l) {
  const ink = l.Cells.filter((c) => !Rect.isEmpty(c.Ink));
  return ink.length ? Math.min(...ink.map((c) => c.Ink.Y)) : Math.min(...l.Cells.map((c) => baseBox(l, c).Y));
}
export function lineBottom(l) {
  const ink = l.Cells.filter((c) => !Rect.isEmpty(c.Ink));
  let b = ink.length ? Math.max(...ink.map((c) => Rect.bottom(c.Ink))) : Math.max(...l.Cells.map((c) => Rect.bottom(baseBox(l, c))));
  if ((l.Extra ?? 0) > 0) b = Math.max(b, l.ExtraAt + l.Extra);
  return b;
}
export const gapMid = (a, b) => Math.floor((lineBottom(a) + lineTop(b)) / 2);

/** Titre de la rangée dont les valeurs descendent sur `target` (comme dans le .txt). */
function carriedTitle(lines, target) {
  const taken = new Set();
  for (let i = 0; i < lines.length; i++) {
    if (lines[i].Kind !== 'Titre' || !hasValues(lines[i])) continue;
    const t = lines.slice(i + 1).find((x) => x.Kind !== 'Titre' && !hasValues(x) && !taken.has(x));
    if (!t) continue;
    taken.add(t);
    if (t === target) return lines[i];
  }
  return null;
}

const ITEM_START = /^\s*(\+\/-\s*)?\d{1,3}\s+\p{L}/u;

/**
 * Lignes qui quittent leur place, dans l'ordre du document :
 *  - kind 'zero' : objet au lot 0 (avec ses lignes de suite) — recollé après le dernier lot ou sur la page « lots de fin » ;
 *  - kind 'deleted' : ligne supprimée (« Supprimer cette ligne », avec ses lignes de suite) — retirée, la suite remonte.
 */
const sameMove = (a, b) => a.Page === b.Page && a.Where === b.Where && (a.Anchor ?? '') === (b.Anchor ?? '');
function movedUnits(project) {
  const units = [];
  const lots = lotsActive(project);
  // Tri par numéro de lot (Lots › Trier) : chaque objet numéroté peut partir à la place de son numéro.
  const sorting = lots && !!project.SortLots;
  const objects = sorting ? new Set([...objectLines(project)].map((o) => o.line)) : null;
  project.Pages.forEach((page, p) => page.Tables.forEach((table, ti) => {
    if (!gridTable(table)) return;
    for (const r of rowOrder(table)) {
      if (r + 1 >= table.RowYs.length) continue;
      const row = table.Rows[r];
      const lines = row.Lines.filter((l) => l.Kind !== 'Ignoree' && (l.Cells.some((c) => !blank(c.Text)) || l.Deleted));
      const found = [];
      lines.forEach((l, i) => {
        const deleted = !!l.Deleted;
        const zero = lots && hasLotCell(l) && isZeroLot(l);
        const move = !deleted && !zero && lots && hasLotCell(l) && !!l.MoveTo;
        // Case encore vide : elle reste à sa place (elle n'a pas de numéro à suivre).
        const sort = !deleted && !zero && !move && sorting && objects.has(l) && (l.Lot ?? '').trim().length > 0;
        if (!deleted && !zero && !move && !sort) return;
        if (found.some((u) => u.lines.includes(l))) return;
        const own = [l];
        for (let j = i + 1; j < lines.length && lines[j].Kind !== 'Titre' && !hasLotCell(lines[j]) && !lines[j].Deleted; j++) {
          // Ligne supprimée : sa suite (texte sans montant), pas un autre objet (« 1 coffre fort… »).
          if (deleted && (hasValues(lines[j]) || ITEM_START.test(lines[j].Cells[0]?.Text ?? ''))) break;
          own.push(lines[j]);
        }
        found.push({ kind: deleted ? 'deleted' : zero ? 'zero' : move ? 'move' : 'sort', object: l, lines: own, title: hasValues(l) ? null : carriedTitle(lines, l) });
      });
      if (found.length === 0) continue;
      const covered = new Set(found.flatMap((u) => u.lines));
      // Rangée entière : seulement si tous ses objets vont au même endroit (deux numéros différents dans une rangée,
      // ou deux destinations différentes : chacun part de son côté).
      const sameWay = (u) => u.kind === 'sort' ? compareKeys(lotKey(u.object.Lot), lotKey(found[0].object.Lot)) === 0
        : u.kind === 'move' ? sameMove(u.object.MoveTo, found[0].object.MoveTo) : true;
      const whole = lines.every((l) => covered.has(l)) && found.every((u) => !u.title) && found.every((u) => u.kind === found[0].kind && sameWay(u));
      const base = { p, page, ti, table, r };
      if (whole) {
        const top = cutY(table.RowYs[r]);
        units.push({ ...base, kind: found[0].kind, object: found[0].object, lines: row.Lines.slice(), title: null, whole: true, top, bodyTop: top + FILET, bottom: cutY(table.RowYs[r + 1]) });
        continue;
      }
      for (const u of found) {
        const first = lines.indexOf(u.lines[0]), last = lines.indexOf(u.lines[u.lines.length - 1]);
        const top = first > 0 ? gapMid(lines[first - 1], lines[first]) : cutY(table.RowYs[r]) + FILET;
        const bottom = last + 1 < lines.length ? gapMid(lines[last], lines[last + 1]) : cutY(table.RowYs[r + 1]);
        if (bottom <= top) continue;
        units.push({ ...base, ...u, whole: false, top, bodyTop: top, bottom });
      }
    }
  }));
  return units;
}

/** Lignes supprimées (et leur suite) : hors du .txt, des totaux et des lots. */
export const deletedLines = (project) => new Set(movedUnits(project).filter((u) => u.kind === 'deleted').flatMap((u) => u.lines));

/**
 * Clé de tri d'un numéro de lot : les nombres du plus petit au plus grand (les « 1 » ensemble, puis les « 2 »…), puis
 * les codes en lettres groupés (« ? », « VGG »…), puis les cases vides.
 */
/**
 * Où va un lot déplacé à la main (Line.MoveTo = { Page, Where: 'start' | 'end' | 'after', Anchor }) : au début de la page
 * (avant sa 1re rangée d'objet), à la fin (après sa dernière rangée d'objet) ou juste après la rangée d'un lot choisi.
 * null si l'endroit n'existe plus (la ligne reste alors à sa place).
 */
function moveDest(project, u, rows) {
  const m = u.object.MoveTo;
  const pt = project.Pages.findIndex((pg) => pg.Id === m.Page);
  if (pt < 0) return null;
  const here = rows.filter((x) => x.p === pt);
  if (here.length === 0) return null;
  const objRows = here.filter((x) => x.table.Rows[x.r].Lines.some((l) => hasLotCell(l) && !l.Deleted));
  const at = (x, before = false) => ({ p: x.p, ti: x.ti, r: x.r, table: x.table, page: x.page, before });
  if (m.Where === 'after') {
    const x = here.find((y) => y.table.Rows[y.r].Lines.some((l) => l.Cells[0]?.Id === m.Anchor));
    if (x) return at(x);
  }
  if (m.Where === 'order') {
    // À sa place dans l'ordre des numéros de cette page : après la dernière rangée qui a un numéro plus petit ou égal.
    const k = lotKey(u.object.Lot);
    let last = null;
    for (const x of objRows) if (x.table.Rows[x.r].Lines.some((l) => l !== u.object && hasLotCell(l) && !l.MoveTo && (l.Lot ?? '').trim() && compareKeys(lotKey(l.Lot), k) <= 0)) last = x;
    if (last) return at(last);
    return objRows.length ? at(objRows[0], true) : at(here[0], true);
  }
  if (m.Where === 'start') return objRows.length ? at(objRows[0], true) : at(here[0], true);
  return objRows.length ? at(objRows[objRows.length - 1]) : at(here[here.length - 1]);
}

/**
 * Tri par numéro de lot, en bougeant le moins de lignes possible : la plus longue suite déjà dans l'ordre reste en
 * place ; chaque autre objet est recollé juste après la rangée du dernier objet resté en place qui a un numéro plus petit
 * ou égal (avant le 1er : après la rangée qui précède). Renvoie [{ u, d }] dans l'ordre des numéros.
 */
function sortPlaces(units, rows) {
  const keys = units.map((u) => lotKey(u.object.Lot));
  const tails = [], prev = new Array(units.length).fill(-1);
  for (let i = 0; i < units.length; i++) {
    let lo = 0, hi = tails.length;
    while (lo < hi) { const mid = (lo + hi) >> 1; if (compareKeys(keys[tails[mid]], keys[i]) <= 0) lo = mid + 1; else hi = mid; }
    prev[i] = lo > 0 ? tails[lo - 1] : -1;
    tails[lo] = i;
  }
  const stay = new Set();
  for (let i = tails.length ? tails[tails.length - 1] : -1; i >= 0; i = prev[i]) stay.add(i);
  // Une rangée avec deux numéros différents (« EN COURS… » lot 129 et « valorisé… » lot 130) : on ne peut poser une ligne
  // qu'avant ou après la rangée entière, donc seul le 1er numéro y reste ; les plus grands repartent juste après elle.
  const rowKey = new Map();
  for (let i = 0; i < units.length; i++) {
    if (!stay.has(i)) continue;
    const id = `${units[i].p}/${units[i].ti}/${units[i].r}`;
    if (!rowKey.has(id)) rowKey.set(id, keys[i]);
    else if (compareKeys(rowKey.get(id), keys[i]) !== 0) stay.delete(i);
  }
  const rowIndex = (u) => rows.findIndex((x) => x.p === u.p && x.ti === u.ti && x.r === u.r);
  // Avant le 1er objet resté en place : la rangée qui le précède ; s'il n'y en a pas (1re rangée du document), il bouge aussi.
  let first = -1;
  for (let i = 0; i < units.length; i++) {
    if (!stay.has(i)) continue;
    if (rowIndex(units[i]) > 0) { first = i; break; }
    stay.delete(i);
  }
  const order = units.map((u, i) => i).sort((a, b) => compareKeys(keys[a], keys[b]) || a - b);
  const out = [];
  for (const i of order) {
    if (stay.has(i)) continue;
    let anchor = -1;
    for (const j of stay) if (compareKeys(keys[j], keys[i]) <= 0 && j > anchor) anchor = j;
    let x;
    if (anchor >= 0) x = units[anchor];
    else if (first >= 0) { const row = rows[rowIndex(units[first]) - 1]; x = row; }
    else continue; // aucun repère : la ligne reste où elle est
    out.push({ u: units[i], d: { p: x.p, ti: x.ti, r: x.r, table: x.table, page: x.page } });
  }
  return out;
}

export function lotKey(lot) {
  const t = (lot ?? '').trim();
  if (t.length === 0) return [2, 0, ''];
  const m = /^(\d+)/.exec(t);
  if (m) return [0, parseInt(m[1], 10), t.slice(m[1].length).toUpperCase()];
  return [1, 0, t.toUpperCase()];
}
export function compareKeys(a, b) {
  return a[0] - b[0] || a[1] - b[1] || (a[2] < b[2] ? -1 : a[2] > b[2] ? 1 : 0);
}

/** Page qui sert de modèle aux pages « lots de fin » : la dernière page à lots avant elles (sinon celle du dernier lot). */
function endTemplate(project) {
  const first = project.Pages.findIndex((pg) => pg.EndLots);
  const objects = [...objectLines(project)];
  let best = null;
  for (const o of objects) {
    const p = project.Pages.indexOf(o.page);
    if (!gridTable(o.table) || (first >= 0 && p > first)) continue;
    best = o;
  }
  best ??= objects.filter((o) => gridTable(o.table)).pop() ?? null;
  if (!best) return null;
  const p = project.Pages.indexOf(best.page);
  // Rangée de référence (filet, fond, filets verticaux) : la 1re rangée d'objet du tableau.
  const r = objects.find((o) => o.table === best.table)?.row ?? best.row;
  return { p, ti: best.page.Tables.indexOf(best.table), r, table: best.table, page: best.page };
}

/** Rangée du dernier objet numéroté (pas au lot 0) : c'est là que les lots 0 sont recollés. */
function destination(project) {
  let last = null;
  for (const o of objectLines(project)) if (!isZeroLot(o.line)) last = o;
  if (!last || !gridTable(last.table)) return null;
  const p = project.Pages.indexOf(last.page);
  return { p, ti: last.page.Tables.indexOf(last.table), r: last.row, table: last.table, page: last.page };
}

/** Rangées des tableaux à filets, dans l'ordre affiché, toutes pages (pas les pages « lots de fin »). */
function rowsInOrder(project) {
  const out = [];
  project.Pages.forEach((page, p) => {
    if (page.EndLots) return;
    page.Tables.map((t, ti) => ({ t, ti })).filter((x) => gridTable(x.t)).sort((a, b) => a.t.RowYs[0] - b.t.RowYs[0])
      .forEach(({ t, ti }) => { for (const r of rowOrder(t)) if (r + 1 < t.RowYs.length) out.push({ p, ti, r, table: t, page }); });
  });
  return out;
}

const destOf = (x) => ({ p: x.p, ti: x.ti, r: x.r, table: x.table, page: x.page });

/**
 * Où recoller ce lot 0 : l'endroit choisi à la main (poignée ⋮⋮ en mode Modifier, `EndAt` : après la rangée de cette ligne) ;
 * sinon à la fin de sa partie : après le dernier objet numéroté avant la ligne TOTAL qui la ferme (« TOTAL MOBILIER ET
 * MATERIEL ») ; sans ligne TOTAL après lui, après le dernier lot du document.
 */
function unitDestination(project, u, rows, numbered, fallback) {
  const at = u.object.EndAt;
  if (at) {
    const x = rows.find((x) => x.page.Id === at.Page && x.table.Rows[x.r].Lines.some((l) => l.Cells[0]?.Id === at.Line));
    if (x) return destOf(x);
  }
  const k = rows.findIndex((x) => x.p === u.p && x.ti === u.ti && x.r === u.r);
  let last = null;
  for (let i = Math.max(0, k); i < rows.length; i++) {
    const lines = rows[i].table.Rows[rows[i].r].Lines;
    if (lines.some((l) => !l.Deleted && looksLikeTotal(l))) return destOf(last ?? rows[Math.max(k, i - 1)]);
    if (i > k && lines.some((l) => numbered.has(l))) last = rows[i];
  }
  return fallback;
}

/** Décalage horizontal d'une colonne vers le tableau d'arrivée (selon l'alignement de la colonne). */
function columnShift(src, dst, c, align) {
  const a = src.ColumnXs, b = dst.ColumnXs;
  if (a.length !== b.length) return R(b[0] - a[0]);
  if (align === 'Right') return R(b[c + 1] - a[c + 1]);
  if (align === 'Center') return R((b[c] + b[c + 1] - a[c] - a[c + 1]) / 2);
  return R(b[c] - a[c]);
}

/**
 * Plan de toutes les pages : morceaux recopiés (dans l'ordre) et rangées des lots 0.
 * Chaque page : { height, pieces: [{ kind: 'copy', y0, y1, dst } | { kind: 'unit', unit, dst, h } | { kind: 'paper', dst, h }] }.
 * Pages « lots de fin » (Page.EndLots) : composées depuis la page modèle (template) : marge, lots 0, filet du bas.
 * endNeeded : nombre de pages « lots de fin » nécessaires (mode page dédiée).
 */
export function planArrangement(project) {
  const all = movedUnits(project);
  const zeros = all.filter((u) => u.kind === 'zero'), deleted = all.filter((u) => u.kind === 'deleted');
  const slots = all.filter((u) => u.kind === 'sort');
  const endMode = project.EndLots?.Mode === 'page';
  const template = endMode && zeros.length ? endTemplate(project) : null;
  const endIdx = project.Pages.map((pg, k) => (pg.EndLots ? k : -1)).filter((k) => k >= 0);
  let dest = null, endNeeded = 0;
  const groups = [];
  let moving = [];
  const lots = lotsActive(project);
  const prepare = (u, d) => {
    const align = (c) => u.object.Cells[c]?.Align ?? (c === 0 ? 'Left' : 'Right');
    u.dest = d;
    u.shift = u.table.ColumnXs.slice(0, -1).map((_, c) => columnShift(u.table, d.table, c, align(c)));
    u.lotShift = R(tableLeft(d.table)) - R(tableLeft(u.table));
    u.srcLotWidth = lots ? R(columnWidth(u.page)) : 0;
    u.dstLotWidth = lots ? R(columnWidth(d.page)) : 0;
    u.height = FILET + (u.bottom - u.bodyTop);
  };
  if (template) {
    // Pages dédiées : les lots 0 à la suite, une nouvelle page quand la hauteur du tableau modèle est atteinte.
    const T = template.table, avail = cutY(T.RowYs[T.RowYs.length - 1]) - cutY(T.RowYs[0]);
    let used = 0;
    for (const u of zeros) {
      const h = FILET + (u.bottom - u.bodyTop);
      if (groups.length === 0 || (used + h > avail && used > 0)) { groups.push([]); used = 0; }
      groups[groups.length - 1].push(u);
      used += h;
    }
    endNeeded = groups.length;
    groups.forEach((g, i) => {
      if (i >= endIdx.length) return; // page pas encore créée : ces lots restent à leur place
      const d = { ...template, p: endIdx[i], ti: -1, r: -1, template };
      for (const u of g) { prepare(u, { ...template, p: endIdx[i] }); u.endPage = endIdx[i]; moving.push(u); }
      if (i === 0) dest = d;
    });
  } else if (zeros.length) {
    const fallback = destination(project);
    if (fallback) {
      const rows = rowsInOrder(project);
      const zeroLines = new Set(zeros.flatMap((u) => u.lines));
      const numbered = new Set([...objectLines(project)].map((o) => o.line).filter((l) => !isZeroLot(l) && !l.Deleted && !zeroLines.has(l)));
      moving = zeros;
      for (const u of moving) prepare(u, unitDestination(project, u, rows, numbered, fallback));
      dest = moving[0].dest;
    }
  }
  const sortMoves = [];
  const ordered = rowsInOrder(project);
  for (const { u, d } of sortPlaces(slots, ordered)) { prepare(u, d); sortMoves.push(u); }
  // Lots déplacés à la main (Lots › Déplacer des lots) : vers la page et l'endroit choisis.
  const handMoves = [];
  for (const u of all.filter((x) => x.kind === 'move')) { const d = moveDest(project, u, ordered); if (d) { prepare(u, d); handMoves.push(u); } }
  moving = [...sortMoves, ...handMoves, ...moving];
  const cutting = [...moving, ...deleted];
  const pages = project.Pages.map((page, p) => {
    const pieces = [];
    let dst = 0;
    const copy = (y0, y1) => {
      if (y1 <= y0) return;
      const last = pieces[pieces.length - 1];
      if (last?.kind === 'copy' && last.y1 === y0) { last.y1 = y1; dst += y1 - y0; return; }
      pieces.push({ kind: 'copy', y0, y1, dst });
      dst += y1 - y0;
    };
    if (page.EndLots) {
      // Page « lots de fin » : marge du haut (papier), lots 0 en rangées du tableau modèle, filet du bas, papier.
      const T = template?.table;
      const mine = moving.filter((u) => u.endPage === p);
      if (!T) return { height: Math.max(1, page.Height), pieces: [{ kind: 'paper', dst: 0, h: Math.max(1, page.Height) }], template: -1 };
      const top = cutY(T.RowYs[0]), bottom = cutY(T.RowYs[T.RowYs.length - 1]);
      pieces.push({ kind: 'paper', dst: 0, h: top });
      dst = top;
      for (const u of mine) { pieces.push({ kind: 'unit', unit: u, dst, h: u.height }); dst += u.height; }
      copy(bottom, bottom + FILET);
      const H = Math.max(template.page.Height, dst);
      if (H > dst) { pieces.push({ kind: 'paper', dst, h: H - dst }); dst = H; }
      return { height: dst, pieces, template: template.p };
    }
    let cursor = 0;
    const tables = page.Tables.map((t, ti) => ({ t, ti })).filter((x) => gridTable(x.t)).sort((a, b) => a.t.RowYs[0] - b.t.RowYs[0]);
    for (const { t, ti } of tables) {
      const n = t.RowYs.length - 1;
      const A = Math.max(cursor, cutY(t.RowYs[0])), B = cutY(t.RowYs[n]);
      if (B <= A) continue;
      copy(cursor, A);
      for (const r of rowOrder(t)) {
        if (r >= n) continue;
        const s0 = Math.max(A, cutY(t.RowYs[r])), s1 = Math.min(B, cutY(t.RowYs[r + 1]));
        const cuts = cutting.filter((u) => u.p === p && u.ti === ti && u.r === r).sort((a, b) => a.top - b.top);
        if (!template)
          for (const u of moving) if (u.dest.p === p && u.dest.ti === ti && u.dest.r === r && u.dest.before) { pieces.push({ kind: 'unit', unit: u, dst, h: u.height }); dst += u.height; }
        let y = s0;
        for (const u of cuts) { copy(y, Math.max(y, u.top)); y = Math.max(y, u.bottom); }
        copy(y, s1);
        if (!template)
          for (const u of moving) if (u.dest.p === p && u.dest.ti === ti && u.dest.r === r && !u.dest.before) { pieces.push({ kind: 'unit', unit: u, dst, h: u.height }); dst += u.height; }
      }
      cursor = B;
    }
    copy(cursor, page.Height);
    return { height: Math.max(1, dst), pieces };
  });
  // Valeurs d'un titre parties avec son objet (ou supprimées avec lui) : effacées de leur place.
  const erase = cutting.filter((u) => u.title).flatMap((u) =>
    u.title.Cells.filter((c) => c.Column > 0 && !blank(c.Text)).map((c) => ({ p: u.p, rect: cellRect(u.table, c) })));
  return { pages, units: cutting, moving, dest, erase, endNeeded };
}

/** Partie d'une case (zone d'édition) à l'intérieur de sa colonne, en pixels entiers. */
function cellRect(table, f) {
  const c = f.Column;
  const x0 = Math.max(R(table.ColumnXs[c]) + MARGIN, Math.floor(f.Box.X)), x1 = Math.min(R(table.ColumnXs[c + 1]) - MARGIN, Math.ceil(Rect.right(f.Box)));
  return { x0, y0: Math.floor(f.Box.Y), x1, y1: Math.ceil(Rect.bottom(f.Box)) };
}

/** Ordonnée sur la page affichée d'une ordonnée de la page d'origine (null si ce morceau est parti ailleurs). */
export function mapY(planPage, y) {
  for (const pc of planPage.pieces) if (pc.kind === 'copy' && y >= pc.y0 && y < pc.y1) return pc.dst + y - pc.y0;
  const last = planPage.pieces.filter((pc) => pc.kind === 'copy').pop();
  if (last && y >= last.y1) return last.dst + (last.y1 - last.y0) + (y - last.y1);
  return null;
}

/** Inverse de mapY pour la page elle-même (zones verrouillées tracées à l'écran). */
export function unmapY(planPage, y) {
  for (const pc of planPage.pieces) {
    const h = pc.kind === 'copy' ? pc.y1 - pc.y0 : pc.h;
    if (y >= pc.dst && y < pc.dst + h) return pc.kind === 'copy' ? pc.y0 + y - pc.dst : null;
  }
  return y;
}

// ---------- Placement des cases sur la page affichée ----------

const unitBodyDy = (u, pc) => pc.dst + FILET - u.bodyTop;
const centerY = (line, f) => Math.floor(Rect.cy(baseBox(line, f)));

/**
 * Ce qu'affiche la page k : lignes (avec le décalage de chaque case), tableaux (rangées à l'écran) et hauteur.
 * lines : [{ line, page, table, cells: [{ f, dx, dy, box, ink }], dy, lotBox, moved, unit }]
 */
export function viewOf(project, plan, k) {
  const page = project.Pages[k], pp = plan.pages[k];
  const out = { page, height: pp.height, width: page.Width, lines: [], tables: [] };
  const movedLines = new Set(plan.units.flatMap((u) => u.lines));
  const carried = new Map();
  for (const u of plan.units) if (u.title) for (const c of u.title.Cells) if (c.Column > 0 && !blank(c.Text)) carried.set(c, u);
  const lots = lotsActive(project);
  const w = columnWidth(page);

  // Lignes restées sur leur page.
  for (const t of page.Tables)
    for (const r of rowOrder(t))
      for (const line of t.Rows[r].Lines) {
        if (movedLines.has(line)) continue;
        const anchor = centerY(line, line.Cells[0]);
        const my = gridTable(t) ? mapY(pp, anchor) : anchor;
        if (my === null) continue;
        const dy = my - anchor;
        const cells = line.Cells.filter((f) => !carried.has(f)).map((f) => place(f, 0, dy, null));
        out.lines.push({ line, page, table: t, cells, dy, moved: false, lotBox: lots && hasLotCell(line) ? shiftRect(numberBox(t, line, w), 0, dy) : null });
      }
  // Lots 0 recollés ici.
  for (const pc of pp.pieces) {
    if (pc.kind !== 'unit') continue;
    const u = pc.unit, dt = u.dest.table;
    const dy = unitBodyDy(u, pc);
    const top = pc.dst + FILET, bottom = pc.dst + pc.h;
    const clipCol = (c) => Rect.fromEdges(dt.ColumnXs[Math.min(c, dt.ColumnXs.length - 2)] + 1.5, top, dt.ColumnXs[Math.min(c + 1, dt.ColumnXs.length - 1)] - 1.5, bottom);
    for (const line of u.lines) {
      const cells = line.Cells.map((f) => place(f, u.shift[f.Column] ?? 0, dy, clipCol(f.Column)));
      let lotBox = null;
      if (lots && hasLotCell(line)) {
        const b = shiftRect(numberBox(u.table, line, u.srcLotWidth || w), 0, dy);
        const right = tableLeft(dt), left = Math.max(0, right - (u.dstLotWidth || w));
        lotBox = Rect.make(left + 2, b.Y, Math.max(4, right - left - 4), b.Height);
      }
      out.lines.push({ line, page: u.page, table: u.table, cells, dy, moved: true, unit: u, lotBox });
    }
    if (u.title) {
      const dyc = dy + centerY(u.object, u.object.Cells[0]) - centerY(u.title, u.title.Cells.find((c) => c.Column > 0 && !blank(c.Text)));
      const cells = u.title.Cells.filter((c) => carried.get(c) === u).map((f) => place(f, u.shift[f.Column] ?? 0, dyc, clipCol(f.Column)));
      out.lines.push({ line: u.title, page: u.page, table: u.table, cells, dy: dyc, moved: true, unit: u, lotBox: null, carriedOnly: true });
    }
  }
  // Tableaux tels qu'à l'écran : rangées dans l'ordre affiché (pour les poignées et le quadrillage des pages sans image).
  page.Tables.forEach((t, ti) => {
    if (!gridTable(t)) { out.tables.push({ table: t, ColumnXs: t.ColumnXs, RowYs: t.RowYs, strips: [] }); return; }
    const strips = [];
    for (const r of rowOrder(t)) {
      if (r + 1 >= t.RowYs.length) continue;
      const whole = plan.units.some((u) => u.p === k && u.ti === ti && u.r === r && u.whole);
      if (whole) continue;
      const a = mapY(pp, cutY(t.RowYs[r]) + CUT);
      if (a === null) continue;
      strips.push({ r, top: a, bottom: a + (cutY(t.RowYs[r + 1]) - cutY(t.RowYs[r])) - cutHeight(plan, k, ti, r) });
    }
    const ys = [];
    const units = page.EndLots ? [] : pp.pieces.filter((pc) => pc.kind === 'unit' && pc.unit.dest.p === k && pc.unit.dest.ti === ti);
    for (const s of strips) ys.push(s.top);
    for (const pc of units) ys.push(pc.dst + CUT);
    const end = mapY(pp, R(t.RowYs[t.RowYs.length - 1]));
    if (end !== null) ys.push(end);
    ys.sort((a, b) => a - b);
    // Lots 0 recollés dans ce tableau : déplaçables avec une poignée (mode Modifier).
    out.tables.push({ table: t, ColumnXs: t.ColumnXs, RowYs: ys, strips, units: units.filter((pc) => pc.unit.kind !== 'deleted').map((pc) => ({ unit: pc.unit, top: pc.dst + CUT, bottom: pc.dst + pc.h })) });
  });
  return out;
}

function cutHeight(plan, k, ti, r) {
  return plan.units.filter((u) => u.p === k && u.ti === ti && u.r === r && !u.whole).reduce((s, u) => s + u.bottom - u.top, 0);
}

const shiftRect = (b, dx, dy) => ({ ...b, X: b.X + dx, Y: b.Y + dy });

function place(f, dx, dy, clip) {
  let box = shiftRect(f.Box, dx, dy);
  if (clip) {
    const x0 = Math.max(box.X, clip.X), y0 = Math.max(box.Y, clip.Y);
    const x1 = Math.min(Rect.right(box), Rect.right(clip)), y1 = Math.min(Rect.bottom(box), Rect.bottom(clip));
    if (x1 > x0 && y1 > y0) box = Rect.fromEdges(x0, y0, x1, y1);
  }
  return { f, dx, dy, box, ink: Rect.isEmpty(f.Ink) ? f.Ink : shiftRect(f.Ink, dx, dy) };
}

/** La page k change-t-elle de forme (rangées parties, arrivées ou déplacées) ? */
export function pageArranged(project, plan, k) {
  const pp = plan.pages[k];
  return pp.pieces.length !== 1 || pp.pieces[0].kind !== 'copy' || pp.pieces[0].y0 !== 0 || pp.height !== project.Pages[k].Height
    || plan.erase.some((e) => e.p === k);
}

/** Pages dont l'image est nécessaire pour composer la page k (elle-même ou son modèle, et celles des lots 0 recollés). */
export const sourcesOf = (plan, k) => [...new Set([plan.pages[k].template ?? k, ...plan.pages[k].pieces.filter((pc) => pc.kind === 'unit').map((pc) => pc.unit.p)])];

// ---------- Pages « lots de fin » ----------

let endCounter = 0;
/**
 * Mode page dédiée : crée ou retire les pages « lots de fin » pour qu'il y en ait juste assez (une de plus quand la
 * précédente est pleine, aucune s'il n'y a plus de lot 0). La 1re est placée après la page choisie (EndLots.AfterId).
 * Renvoie vrai si des pages ont été ajoutées ou retirées.
 */
export function syncEndPages(project) {
  let changed = false;
  for (let guard = 0; guard < 50; guard++) {
    const plan = planArrangement(project);
    const ends = project.Pages.filter((pg) => pg.EndLots);
    const need = project.EndLots?.Mode === 'page' ? plan.endNeeded : 0;
    if (ends.length === need) {
      // Même taille que la page modèle.
      const t = endTemplate(project);
      if (t) for (const pg of ends) if (pg.Width !== t.page.Width || pg.Height !== t.page.Height) { pg.Width = t.page.Width; pg.Height = t.page.Height; changed = true; }
      return changed;
    }
    changed = true;
    if (ends.length > need) { project.Pages.splice(project.Pages.lastIndexOf(ends[ends.length - 1]), 1); continue; }
    const tpl = ends.length ? ends[0] : null;
    let at;
    if (ends.length) at = project.Pages.indexOf(ends[ends.length - 1]) + 1;
    else {
      const after = project.Pages.findIndex((pg) => pg.Id === project.EndLots.AfterId);
      at = after >= 0 ? after + 1 : project.Pages.length;
    }
    const model = tpl ?? project.Pages.find((pg) => !pg.EndLots && pg.Tables.length) ?? project.Pages[0];
    project.Pages.splice(at, 0, {
      Id: `fin-${Date.now().toString(36)}-${endCounter++}`, EndLots: true, Source: 'Image', SourceName: 'Lots de fin', SourceEntry: null,
      Width: model?.Width ?? 1000, Height: model?.Height ?? 1400, QuarterTurns: 0, Crop: null, Skew: 0, Tables: [], LooseLines: [], LockZones: [],
    });
  }
  return changed;
}

/** Morceau d'image utile d'un lot 0 (de quoi recomposer sa rangée), pris sur l'image de sa page. */
export function unitCrop(unit, image) {
  let y0 = unit.bodyTop;
  if (unit.title) for (const c of unit.title.Cells) if (c.Column > 0 && !blank(c.Text)) y0 = Math.min(y0, cellRect(unit.table, c).y0);
  y0 = Math.max(0, y0);
  const y1 = Math.min(image.h, unit.bottom);
  const stride = image.w * image.ch;
  return { w: image.w, h: Math.max(0, y1 - y0), ch: image.ch, oy: y0, data: image.data.slice(y0 * stride, Math.max(y0, y1) * stride) };
}

// ---------- Pixels ----------

const lum = (img, x, y) => {
  const o = ((y - (img.oy ?? 0)) * img.w + x) * img.ch, d = img.data;
  return img.ch === 1 ? d[o] : (d[o] * 77 + d[o + 1] * 150 + d[o + 2] * 29) >> 8;
};
function put(dst, dx, dy, src, sx, sy) {
  const o = (dy * dst.w + dx) * dst.ch, i = ((sy - (src.oy ?? 0)) * src.w + sx) * src.ch;
  if (src.ch === dst.ch) { for (let c = 0; c < dst.ch; c++) dst.data[o + c] = src.data[i + c]; return; }
  if (dst.ch === 1) { dst.data[o] = lum(src, sx, sy); return; }
  const g = src.data[i];
  dst.data[o] = g; dst.data[o + 1] = g; dst.data[o + 2] = g; dst.data[o + 3] = 255;
}
function paint(dst, x, y, color) {
  const o = (y * dst.w + x) * dst.ch;
  for (let c = 0; c < dst.ch; c++) dst.data[o + c] = color[Math.min(c, color.length - 1)];
}

/** Couleur du papier sur une rangée de pixels : moyenne de la moitié la plus claire (comme PageLayout). */
function paperRow(img, y) {
  const w = img.w, ch = img.ch, d = img.data, base = (y - (img.oy ?? 0)) * w;
  const l = [];
  for (let x = 0; x < w; x++) { const o = (base + x) * ch; l.push([ch === 1 ? d[o] : d[o] + d[o + 1] + d[o + 2], x]); }
  l.sort((a, b) => (b[0] !== a[0] ? b[0] - a[0] : a[1] - b[1]));
  const n = Math.max(1, Math.trunc(w / 2));
  const sum = new Array(ch).fill(0);
  for (let i = 0; i < n; i++) { const o = (base + l[i][1]) * ch; for (let c = 0; c < ch; c++) sum[c] += d[o + c]; }
  return sum.map((v) => Math.trunc(v / n));
}

/** Couleur du papier autour d'un rectangle : moyenne de la moitié la plus claire de son pourtour. */
function paperAround(img, r) {
  const s = [];
  const x0 = Math.max(0, r.x0), x1 = Math.min(img.w - 1, r.x1 - 1), y0 = Math.max(0, r.y0), y1 = Math.min(img.h - 1, r.y1 - 1);
  const add = (x, y) => { const o = (y * img.w + x) * img.ch; s.push([lum(img, x, y), o]); };
  for (let x = x0; x <= x1; x++) { add(x, y0); add(x, y1); }
  for (let y = y0; y <= y1; y++) { add(x0, y); add(x1, y); }
  if (s.length === 0) return new Array(img.ch).fill(255);
  s.sort((a, b) => (b[0] !== a[0] ? b[0] - a[0] : a[1] - b[1]));
  const n = Math.max(1, Math.trunc(s.length / 2));
  const sum = new Array(img.ch).fill(0);
  for (let i = 0; i < n; i++) for (let c = 0; c < img.ch; c++) sum[c] += img.data[s[i][1] + c];
  return sum.map((v) => Math.trunc(v / n));
}

/**
 * Image de la page k telle qu'affichée : `images[k]` est l'image de la page (redressée, colonne des lots tracée,
 * corrections dessinées pour l'export) ; `crops` donne, pour chaque lot 0 recollé ici, son morceau d'image (unitCrop).
 */
export function composePage(project, plan, k, image, crops) {
  const pp = plan.pages[k];
  const w = image.w, ch = image.ch;
  const out = { w, h: pp.height, ch, data: new Uint8ClampedArray(w * pp.height * ch) };
  const stride = w * ch;
  const paper = paperRow(image, Math.min(image.h - 1, 2));
  for (const pc of pp.pieces) {
    if (pc.kind === 'paper') {
      for (let y = pc.dst; y < Math.min(out.h, pc.dst + pc.h); y++) for (let x = 0; x < w; x++) paint(out, x, y, paper);
      continue;
    }
    if (pc.kind === 'copy') {
      const a = Math.min(Math.max(pc.y0, 0), image.h), b = Math.min(Math.max(pc.y1, a), image.h);
      out.data.set(image.data.subarray(a * stride, b * stride), pc.dst * stride);
      const white = new Array(ch).fill(255);
      for (let y = pc.dst + (b - a); y < pc.dst + (pc.y1 - pc.y0); y++) for (let x = 0; x < w; x++) paint(out, x, y, white);
    } else synthesize(out, pc, image, crops.get(pc.unit));
  }
  // Valeurs de titre parties avec leur objet : papier à la place.
  for (const e of plan.erase) {
    if (e.p !== k) continue;
    const paper = paperAround(image, e.rect);
    for (let y = e.rect.y0; y < e.rect.y1; y++) {
      const my = mapY(pp, y);
      if (my === null || my < 0 || my >= out.h) continue;
      for (let x = Math.max(0, e.rect.x0); x < Math.min(w, e.rect.x1); x++) paint(out, x, my, paper);
    }
  }
  if (!project.Pages[k].EndLots) closeLotColumns(project, project.Pages[k], pp, image, out, paper);
  return out;
}

/**
 * Colonne des lots après les déplacements (rangée déplacée, lot 0 parti) : chaque rangée tracée garde son cadre complet
 * (haut, bas, gauche) là où elle arrive, et le trait laissé par une rangée partie disparaît du haut des autres rangées.
 */
function closeLotColumns(project, page, pp, image, out, paper) {
  if (!lotFrameEnabled(project)) return;
  const w = columnWidth(page);
  const units = pp.pieces.filter((pc) => pc.kind === 'unit');
  for (const t of framedTables(page)) {
    if (!t.Rules) continue;
    const right = R(t.ColumnXs[0]), left = R(t.ColumnXs[0] - w);
    if (right - left < 8) continue;
    const vT = t.Rules.V, hT = t.Rules.H, hv = Math.trunc(vT / 2), hh = Math.trunc(hT / 2);
    const color = t.Rules.Color;
    // Rangée r sur la page affichée : [début, fin[ de sa bande (null si elle est partie entière).
    const strip = (r) => {
      const a = cutY(t.RowYs[r]), b = cutY(t.RowYs[r + 1]);
      let s = null, e = null;
      for (const pc of pp.pieces) {
        if (pc.kind !== 'copy') continue;
        const y0 = Math.max(a, pc.y0), y1 = Math.min(b, pc.y1);
        if (y1 <= y0) continue;
        const d0 = pc.dst + y0 - pc.y0;
        if (s === null || d0 < s) s = d0;
        if (e === null || d0 + y1 - y0 > e) e = d0 + y1 - y0;
      }
      return s === null ? null : { s, e };
    };
    const framed = new Set(framedRows(t));
    const bands = [];
    for (const r of framed) { const st = strip(r); if (st) bands.push({ y0: st.s + CUT - hh, y1: st.e + CUT - hh + hT, st }); }
    // Ligne déplacée ou rangée avec sa case de lot : cadrée à son arrivée, même entre des rangées sans cadre (haut de page…).
    for (const pc of units) {
      if (pc.unit.dest.table !== t || !pc.unit.lines.some(hasLotCell)) continue;
      const st = { s: pc.dst, e: pc.dst + pc.h };
      bands.push({ y0: st.s + CUT - hh, y1: st.e + CUT - hh + hT, st });
    }
    const inside = (y) => bands.some((b) => y >= b.y0 && y < b.y1) || units.some((u) => y >= u.dst && y < u.dst + u.h);
    // Haut des rangées sans cadre : le trait d'une rangée tracée qui n'est plus là est effacé (encre seulement).
    for (const r of rowOrder(t)) {
      if (r + 1 >= t.RowYs.length || framed.has(r)) continue;
      const st = strip(r);
      if (!st) continue;
      const rule = ruleLeftOf(image, right, R(t.RowYs[r]) + hh + 1, R(t.RowYs[r + 1]) - hh);
      const xEnd = Math.min(right - vT, rule === null ? right : rule);
      for (let y = st.s; y < Math.min(st.e, st.s + CUT - hh + hT + 1, out.h); y++) {
        if (y < 0 || inside(y)) continue;
        for (let x = Math.max(0, left - hv - 1); x < xEnd; x++) if (lum(out, x, y) < 160) paint(out, x, y, paper);
      }
    }
    // Cadre de chaque rangée tracée, à sa place d'arrivée.
    for (const { st } of bands) {
      const top = st.s + CUT, bottom = st.e + CUT;
      fillRect(out, left - hv, top - hh, vT, bottom - top + hT, color);
      fillRect(out, left - hv, top - hh, right - (left - hv), hT, color);
      fillRect(out, left - hv, bottom - hh, right - (left - hv), hT, color);
    }
  }
}

function fillRect(img, x, y, w, h, color) {
  for (let yy = Math.max(0, y); yy < Math.min(img.h, y + h); yy++)
    for (let xx = Math.max(0, x); xx < Math.min(img.w, x + w); xx++) {
      const o = (yy * img.w + xx) * img.ch;
      if (img.ch === 1) img.data[o] = color[3];
      else { img.data[o] = color[0]; img.data[o + 1] = color[1]; img.data[o + 2] = color[2]; img.data[o + 3] = 255; }
    }
}

/** Rangée refaite pour un lot 0 : filet et fond du tableau d'arrivée, contenu des cases recopié colonne par colonne. */
function synthesize(out, pc, image, crop) {
  const u = pc.unit, d = u.dest, dt = d.table, st = u.table;
  const w = out.w;
  const fs = cutY(dt.RowYs[d.r]), re = cutY(dt.RowYs[d.r + 1]);
  // Filet du haut : celui de la rangée d'arrivée.
  for (let k = 0; k < FILET; k++) {
    const sy = Math.min(image.h - 1, Math.max(0, fs + k));
    for (let x = 0; x < w; x++) put(out, x, pc.dst + k, image, x, sy);
  }
  // Fond : l'interligne le plus propre de la rangée d'arrivée (filets verticaux prolongés, papier ailleurs).
  const x0 = Math.max(0, R(dt.ColumnXs[0]) - (u.dstLotWidth > 0 ? u.dstLotWidth + 8 : 4)), x1 = Math.min(w, R(dt.ColumnXs[dt.ColumnXs.length - 1]) + 4);
  let yc = Math.min(image.h - 1, Math.max(0, fs + FILET)), best = -1;
  for (let y = fs + FILET; y < re; y++) {
    if (y < 0 || y >= image.h) continue;
    let s = 0;
    for (let x = x0; x < x1; x++) s += lum(image, x, y);
    if (s > best) { best = s; yc = y; }
  }
  const paper = paperRow(image, yc);
  const bodyH = pc.h - FILET;
  for (let k = 0; k < bodyH; k++) {
    const y = pc.dst + FILET + k;
    for (let x = 0; x < w; x++) {
      if (x >= x0 && x < x1 && lum(image, x, yc) < 150) put(out, x, y, image, x, yc);
      else paint(out, x, y, paper);
    }
  }
  if (!crop) return;
  const dy = pc.dst + FILET - u.bodyTop;
  const blit = (sx0, sx1, sy0, sy1, dx, ddy, cx0, cx1) => {
    for (let y = Math.max(sy0, crop.oy); y < Math.min(sy1, crop.oy + crop.h); y++) {
      const ty = y + ddy;
      if (ty < pc.dst + FILET || ty >= pc.dst + pc.h) continue;
      for (let x = Math.max(0, sx0); x < Math.min(crop.w, sx1); x++) {
        const tx = x + dx;
        if (tx < cx0 || tx >= cx1 || tx < 0 || tx >= w) continue;
        put(out, tx, ty, crop, x, y);
      }
    }
  };
  // Contenu de chaque colonne, recalé sur la colonne d'arrivée.
  const nd = dt.ColumnXs.length - 1;
  for (let c = 0; c < st.ColumnXs.length - 1; c++) {
    const dc = Math.min(c, nd - 1);
    blit(R(st.ColumnXs[c]) + MARGIN, R(st.ColumnXs[c + 1]) - MARGIN, u.bodyTop, u.bottom, u.shift[c], dy,
      R(dt.ColumnXs[dc]) + MARGIN, R(dt.ColumnXs[dc + 1]) - MARGIN);
  }
  // Case du numéro de lot.
  if (u.srcLotWidth > 0 && u.dstLotWidth > 0) {
    const sl = R(tableLeft(st)), dl = R(tableLeft(dt));
    blit(sl - u.srcLotWidth + MARGIN, sl - MARGIN, u.bodyTop, u.bottom, u.lotShift, dy, dl - u.dstLotWidth + MARGIN, dl - MARGIN);
  }
  // Valeurs du titre reprises par l'objet : à la hauteur de l'objet.
  if (u.title) {
    const ref = u.title.Cells.find((c) => c.Column > 0 && !blank(c.Text));
    const dyc = dy + centerY(u.object, u.object.Cells[0]) - centerY(u.title, ref);
    for (const f of u.title.Cells) {
      if (f.Column === 0 || blank(f.Text)) continue;
      const r = cellRect(st, f), dc = Math.min(f.Column, nd - 1);
      blit(r.x0, r.x1, r.y0, r.y1, u.shift[f.Column] ?? 0, dyc, R(dt.ColumnXs[dc]) + MARGIN, R(dt.ColumnXs[dc + 1]) - MARGIN);
    }
  }
}

/** Lignes effacées de leur page parce qu'elles sont recollées ailleurs (pour l'export .txt et les totaux). */
export const movedLinesOf = (plan) => new Set(plan.units.flatMap((u) => u.lines));

export { allLines };
