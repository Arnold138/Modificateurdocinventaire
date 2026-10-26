// Page ajoutée (« Ajouter une page ») : page vierge sur le modèle d'une page de l'inventaire, pour les objets inventoriés
// à la main. Mêmes tableaux, mêmes rangées, mêmes filets et mêmes en-têtes que le modèle ; tout le reste est vide.
// L'image vient de la même source que le modèle (même cadrage), « nettoyée » : dans les tableaux d'objets, papier propre et
// filets retracés (recopiés de l'imprimé, voir blankBase) ; en-têtes gardés tels qu'imprimés. On peut ensuite ajouter ou
// retirer des colonnes (addColumn / removeColumn), des lignes (menu « Ligne », comme ailleurs) et des lignes plus hautes (Tall).
// Portage identique dans AddedPage.cs (même image au pixel près sur Windows et sur iPhone/iPad).
import { Rect } from './geometry.js';
import { allLines, assignIds, firstText } from './model.js';
import { setExtra, removeBand, insertsOf, cleanRow, paperOf, baseHeight } from './layout.js';
import { removeAddedRow } from './rows.js';

const R = (v) => Math.floor(v + 0.5);
const DARK = 150;
/** Plus petite largeur d'une colonne (pixels de page) : en dessous, on ne la coupe plus en deux. */
export const MIN_COLUMN = 36;

const gridTable = (t) => t.HasGrid !== false && t.RowYs?.length >= 2 && t.ColumnXs?.length >= 2;
/**
 * Tableau vidé sur la page ajoutée : tableau à filets d'au moins trois colonnes dont une large (la désignation) : objets,
 * récapitulatifs ; pas un cadre de titre sur fond tramé (lu comme une suite de colonnes étroites).
 */
function blankable(t) {
  if (!gridTable(t) || t.ColumnXs.length < 4) return false;
  let widest = 0;
  for (let c = 0; c + 1 < t.ColumnXs.length; c++) widest = Math.max(widest, t.ColumnXs[c + 1] - t.ColumnXs[c]);
  return widest * 100 >= 35 * (t.ColumnXs[t.ColumnXs.length - 1] - t.ColumnXs[0]);
}

/** Taille d'écriture habituelle du tableau (médiane). */
function emOf(t) {
  const sizes = t.Rows.flatMap((r) => r.Lines).flatMap((l) => l.Cells).map((f) => f.FontSize ?? 0).filter((v) => v > 0).sort((a, b) => a - b);
  return sizes.length ? sizes[Math.trunc(sizes.length / 2)] : 12;
}
/** Hauteur sous laquelle une rangée ne peut pas contenir une ligne de texte (bande d'un fond tramé lue comme des filets). */
const thinOf = (t) => Math.max(10, emOf(t) * 1.1);

/** Ligne vierge qui occupe toute la rangée r (une case par colonne), comme une rangée sans texte lu. */
function freshLine(t, r, em) {
  const top = t.RowYs[r], bottom = t.RowYs[r + 1], cells = [];
  for (let c = 0; c + 1 < t.ColumnXs.length; c++) {
    const box = Rect.fromEdges(t.ColumnXs[c] + 4, top + 4, t.ColumnXs[c + 1] - 4, Math.max(top + 5, bottom - 4));
    cells.push({ Id: '', Column: c, OriginalText: '', Text: '', Ink: Rect.empty(), Box: box, Align: c === 0 ? 'Left' : 'Right', Indent: 4,
      FontSize: Math.min(em, Math.max(6, box.Height / 1.05)), Locked: false, Strikes: [], Colors: [], Manual: false });
  }
  return { Kind: 'Article', KindChosenByUser: false, OutsideTable: false, Cells: cells, Lot: '', LotManual: false, LotSlot: false, NoLot: false, ForceLot: false, Extra: 0, ExtraAt: 0, Copy: false, Deleted: false, TotalScope: null, EndAt: null, Tall: 0 };
}
const rowHeight = (t, r) => t.RowYs[r + 1] - t.RowYs[r];
/** Numéro de page imprimé hors tableau (« 3 », « Page 3 », « 3/9 », « - 3 - ») : effacé sur la page ajoutée. */
const PAGE_NUMBER = /^\s*(?:page\s*)?[-–]?\s*\d{1,3}\s*(?:[\/sur]+\s*\d{1,3})?\s*[-–]?\s*$/i;

const isPageNumber = (l) => {
  const t = l.Cells.map((c) => c.Text).join(' ').replace(/[^\p{L}\p{N}\/ -]/gu, ' ').trim();
  return t.length > 0 && PAGE_NUMBER.test(t);
};

/** La page peut servir de modèle : une page image (pas « Lots de fin ») avec au moins un tableau d'objets. */
export const canBeModel = (page) => !!page && page.Source === 'Image' && !page.EndLots && page.Tables.some(blankable);

/**
 * Rangées d'en-tête au début du tableau (gardées telles qu'imprimées) : celles dont toutes les lignes écrites sont des en-têtes,
 * et les bandes fines d'un en-tête sur fond tramé (lu comme plusieurs filets).
 */
function headRows(t) {
  const thin = thinOf(t);
  let h = 0;
  for (; h < t.Rows.length && h + 1 < t.RowYs.length; h++) {
    const written = t.Rows[h].Lines.filter((l) => firstText(l).length > 0 || l.Kind === 'EnTete');
    const head = written.length > 0 && written.every((l) => l.Kind === 'EnTete');
    if (!head && rowHeight(t, h) >= thin) break;
  }
  return Math.min(h, t.Rows.length - 1);
}

/** Retire le filet k (entre les rangées k−1 et k) : les deux rangées n'en font qu'une. */
function dropRule(t, k) {
  t.Rows[k - 1].Lines.push(...t.Rows[k].Lines);
  t.Rows.splice(k, 1);
  t.RowYs.splice(k, 1);
}

/**
 * Corps du tableau : les bandes fines (total sur fond tramé lu comme plusieurs filets) sont réunies en une rangée ; une rangée
 * réunie, ou dont les lignes sont des lectures parasites (plus petites qu'une demi-écriture), reçoit une ligne vierge qui l'occupe.
 */
function mergeThinBody(t, head) {
  const thin = thinOf(t), em = emOf(t);
  for (let guard = 0; guard < 500; guard++) {
    let r = head;
    while (r < t.Rows.length && !(rowHeight(t, r) < thin)) r++;
    if (r >= t.Rows.length || t.Rows.length - head <= 1) break;
    const keep = r + 1 < t.Rows.length ? r : r - 1;
    dropRule(t, keep + 1);
    t.Rows[keep].merged = true;
  }
  t.Rows.forEach((row, r) => {
    if (r < head) return;
    const real = row.Lines.filter((l) => l.Cells.length > 0 && l.Cells[0].Box.Height >= em * 0.5);
    row.Lines = row.merged || real.length === 0 ? [freshLine(t, r, em)] : real;
    delete row.merged;
  });
}

/** Filet vertical (abscisse x) bien présent sur l'image entre y0 et y1 (au moins la moitié de la hauteur) ; faux pour une colonne lue en trop. */
function ruleThere(img, x, y0, y1) {
  x = R(x); y0 = Math.max(0, R(y0)); y1 = Math.min(img.h, R(y1));
  if (y1 <= y0) return true;
  let n = 0;
  for (let y = y0; y < y1; y++) {
    let dark = false;
    for (let d = -2; d <= 2 && !dark; d++) if (x + d >= 0 && x + d < img.w && gray(img, x + d, y) < DARK) dark = true;
    if (dark) n++;
  }
  return n * 2 >= y1 - y0;
}

/** Colonne du bord (première ou dernière) qui n'existe pas sur l'imprimé : retirée (ses cases avec). */
function dropOuterColumn(t, first) {
  const k = first ? 0 : t.ColumnXs.length - 1;
  t.ColumnXs.splice(k, 1);
  const gone = first ? 0 : t.ColumnXs.length - 1;
  for (const row of t.Rows)
    for (const l of row.Lines) {
      l.Cells = l.Cells.filter((f) => f.Column !== gone);
      if (first) for (const f of l.Cells) f.Column--;
    }
}

function rowBounds(t) {
  const l = t.ColumnXs[0], r = t.ColumnXs[t.ColumnXs.length - 1];
  t.Rows.forEach((row, i) => { if (i + 1 < t.RowYs.length) row.Bounds = Rect.fromEdges(l, t.RowYs[i], r, t.RowYs[i + 1]); });
}

const clearField = (f) => {
  f.Text = ''; f.OriginalText = ''; f.Ink = Rect.empty(); f.Strikes = []; f.Colors = []; f.Manual = false; f.Locked = false;
};
const clearLine = (l, force) => {
  for (const f of l.Cells) clearField(f);
  Object.assign(l, { Kind: 'Article', KindChosenByUser: false, Lot: '', LotManual: false, LotSlot: false, NoLot: false, ForceLot: force, Extra: 0, ExtraAt: 0, Copy: false, Deleted: false, TotalScope: null, EndAt: null, Tall: 0 });
};

/**
 * Crée la page vierge sur le modèle de `model` et l'insère à la position `at` (indice dans project.Pages).
 * Renvoie la nouvelle page (null si le modèle ne convient pas).
 */
export function addBlankPage(project, model, at, img = null, id = null) {
  if (!canBeModel(model)) return null;
  const page = JSON.parse(JSON.stringify(model));
  // 1. Page d'origine, sans ce qui a été ajouté ou retiré à la main (rangées ajoutées, agrandies, réduites, déplacées).
  for (const l of [...allLines(page)]) setExtra(page, l, 0);
  for (let guard = 0; guard < 1000; guard++) {
    const t = page.Tables.find((x) => x.Rows.some((r) => r.Inserted));
    if (!t) break;
    const row = [...t.Rows].reverse().find((r) => r.Inserted);
    if (!row.Lines.length || !removeAddedRow(page, row.Lines[0])) row.Inserted = null;
  }
  for (const b of [...insertsOf(page)].sort((a, c) => c.At - a.At)) removeBand(page, b);
  page.Inserts = [];
  page.Height = baseHeight(model);
  page.LockZones = [];
  for (const t of page.Tables) { t.RowOrder = null; if (gridTable(t)) rowBounds(t); }
  // 2. Tableaux d'objets vidés (en-têtes gardés), numéros de page effacés.
  const info = model.Added ? JSON.parse(JSON.stringify(model.Added)) : { Tables: [], Erase: [] };
  page.Id = id ?? Math.random().toString(16).slice(2, 10);
  page.Tables.forEach((t, ti) => {
    // Modèle déjà ajouté : mêmes repères (image d'origine) ; sinon, les filets et colonnes imprimés.
    let ti_ = model.Added ? info.Tables[ti] ?? null : null;
    if (!model.Added && blankable(t)) {
      const head = headRows(t), orig = [...t.ColumnXs];
      mergeThinBody(t, head);
      // Colonne du bord lue en trop (pas de filet sur l'imprimé) : retirée.
      if (img) {
        const y0 = t.RowYs[head], y1 = t.RowYs[t.RowYs.length - 1];
        while (t.ColumnXs.length > 3 && !ruleThere(img, t.ColumnXs[t.ColumnXs.length - 1], y0, y1)) dropOuterColumn(t, false);
        while (t.ColumnXs.length > 3 && !ruleThere(img, t.ColumnXs[0], y0, y1)) dropOuterColumn(t, true);
      }
      ti_ = { Ys: [...t.RowYs], Head: head, Orig: orig };
      rowBounds(t);
    }
    info.Tables[ti] = ti_;
    if (!ti_) return;
    t.Rows.forEach((row, r) => {
      if (r >= ti_.Head) row.Lines.forEach((l, i) => clearLine(l, i === 0));
      // En-tête : texte mal lu (fond tramé) vidé et verrouillé (l'imprimé reste) ; les vrais en-têtes restent modifiables.
      else for (const l of row.Lines) if (l.Kind !== 'EnTete' && !l.KindChosenByUser) for (const f of l.Cells) { f.Text = ''; f.OriginalText = ''; f.Locked = true; }
    });
  });
  info.Tables.length = page.Tables.length;
  // Sous le dernier tableau vidé (total général, pied de page) : page vide.
  if (info.Tail == null) {
    const bottoms = info.Tables.filter(Boolean).map((x) => x.Ys[x.Ys.length - 1]);
    info.Tail = bottoms.length ? Math.max(...bottoms) : null;
  }
  if (info.Tail != null) {
    const below = (l) => { const b = l.Cells.reduce((u, c) => Rect.union(u, c.Box), Rect.empty()); return !Rect.isEmpty(b) && b.Y + b.Height / 2 > info.Tail + 4; };
    for (const l of page.LooseLines) if (below(l)) for (const f of l.Cells) clearField(f);
    page.Tables.forEach((t, ti) => { if (!info.Tables[ti]) for (const l of t.Rows.flatMap((r) => r.Lines)) if (below(l)) for (const f of l.Cells) clearField(f); });
  }
  const erase = (l) => {
    const ink = l.Cells.reduce((u, c) => Rect.union(u, c.Ink), Rect.empty());
    let z = Rect.isEmpty(ink) ? Rect.empty() : Rect.inflate(ink, 3, 3);
    // Jamais sur un tableau : la zone s'arrête au-dessus de son trait du haut (et d'un double trait).
    for (const t of page.Tables) {
      if (!gridTable(t) || Rect.isEmpty(z)) continue;
      const l0 = t.ColumnXs[0], r0 = t.ColumnXs[t.ColumnXs.length - 1], y0 = t.RowYs[0], y1 = t.RowYs[t.RowYs.length - 1];
      if (z.X >= r0 || z.X + z.Width <= l0 || z.Y >= y1 || z.Y + z.Height <= y0 - 12) continue;
      z = Rect.fromEdges(z.X, z.Y, z.X + z.Width, Math.min(z.Y + z.Height, y0 - 12));
    }
    if (!Rect.isEmpty(z)) info.Erase.push(z);
    for (const f of l.Cells) clearField(f);
  };
  for (const l of page.LooseLines) if (isPageNumber(l)) erase(l);
  for (const t of page.Tables) if (!gridTable(t)) for (const l of t.Rows.flatMap((r) => r.Lines)) if (isPageNumber(l)) erase(l);
  info.From = model.Id;
  info.Next = 0;
  page.Added = info;
  page.EndLots = false;
  const k = project.Pages.indexOf(model);
  page.SourceName = `Page ajoutée (modèle : page ${k + 1})`;
  assignIds(page);
  project.Pages.splice(Math.max(0, Math.min(project.Pages.length, at)), 0, page);
  return page;
}

/** Tableau vidé de la page ajoutée (on peut y ajouter ou retirer des colonnes). */
export const addedTable = (page, t) => !!page?.Added?.Tables?.[page.Tables.indexOf(t)];

const nextId = (page) => `${page.Id}-a${page.Added.Next++}`;

/** Case d'en-tête dont l'imprimé ne correspond plus (colonne coupée ou réunie) : son texte devient tapé. */
function typeHeader(f) {
  if (f.Text === f.OriginalText) f.OriginalText = '';
  f.Ink = Rect.empty();
}

/** La colonne c peut être coupée en deux (assez large). */
export const canAddColumn = (page, t, c) => addedTable(page, t) && c >= 0 && c + 1 < t.ColumnXs.length && t.ColumnXs[c + 1] - t.ColumnXs[c] >= 2 * MIN_COLUMN;
/** Le filet intérieur k (entre les colonnes k−1 et k) peut être retiré : il reste au moins deux colonnes. */
export const canRemoveColumn = (page, t, k) => addedTable(page, t) && k >= 1 && k + 1 < t.ColumnXs.length && t.ColumnXs.length > 3;

/** Coupe la colonne c en deux (filet tracé au milieu) : une case vide apparaît sur chaque ligne. Vrai si fait. */
export function addColumn(page, t, c) {
  if (!canAddColumn(page, t, c)) return false;
  const a = t.ColumnXs[c], b = t.ColumnXs[c + 1], x = R((a + b) / 2);
  t.ColumnXs.splice(c + 1, 0, x);
  const head = page.Added.Tables[page.Tables.indexOf(t)].Head;
  t.Rows.forEach((row, r) => {
    for (const l of row.Lines) {
      const f = l.Cells.find((q) => q.Column === c);
      for (const q of l.Cells) if (q.Column > c) q.Column++;
      if (!f) continue;
      const insetL = Math.max(1.5, f.Box.X - a), insetR = Math.max(1.5, b - Rect.right(f.Box));
      const left = Rect.fromEdges(f.Box.X, f.Box.Y, x - insetR, Rect.bottom(f.Box));
      const box = Rect.fromEdges(x + insetL, f.Box.Y, b - insetR, Rect.bottom(f.Box));
      f.Box = left;
      if (r < head) typeHeader(f); else f.Ink = Rect.empty();
      const nf = { Id: nextId(page), Column: c + 1, OriginalText: '', Text: '', Ink: Rect.empty(), Box: box, Align: c === 0 ? 'Left' : f.Align, Indent: f.Indent, FontSize: f.FontSize, Locked: false, Strikes: [], Colors: [], Manual: false };
      l.Cells.splice(l.Cells.indexOf(f) + 1, 0, nf);
    }
  });
  rowBounds(t);
  return true;
}

/** Retire le filet intérieur k : les colonnes k−1 et k n'en font plus qu'une (textes réunis). Vrai si fait. */
export function removeColumn(page, t, k) {
  if (!canRemoveColumn(page, t, k)) return false;
  t.ColumnXs.splice(k, 1);
  const head = page.Added.Tables[page.Tables.indexOf(t)].Head;
  t.Rows.forEach((row, r) => {
    for (const l of row.Lines) {
      const f1 = l.Cells.find((q) => q.Column === k - 1), f2 = l.Cells.find((q) => q.Column === k);
      if (f1 && f2) {
        const text = [f1.Text.trim(), f2.Text.trim()].filter(Boolean).join(' ');
        if (r < head) { typeHeader(f1); f1.Text = text; } else { f1.Text = text; f1.Ink = Rect.empty(); }
        f1.Box = Rect.fromEdges(f1.Box.X, f1.Box.Y, Rect.right(f2.Box), Math.max(Rect.bottom(f1.Box), Rect.bottom(f2.Box)));
        f1.Strikes = []; f1.Colors = [];
        l.Cells.splice(l.Cells.indexOf(f2), 1);
      } else if (f2) { f2.Column = k - 1; }
      for (const q of l.Cells) if (q.Column > k) q.Column--;
    }
  });
  rowBounds(t);
  return true;
}

// ---------- Image ----------

function gray(img, x, y) {
  const o = (y * img.w + x) * img.ch, d = img.data;
  return img.ch === 1 ? d[o] : (d[o] * 77 + d[o + 1] * 150 + d[o + 2] * 29) >> 8;
}

/** Colonne la plus nette pour recopier les filets horizontaux : le plus de pixels sombres près des filets, le moins ailleurs. */
function cleanCol(img, x0, x1, y0, y1, ys, cols) {
  const near = new Uint8Array(img.h), skip = new Uint8Array(img.w);
  for (const y of ys) for (let v = Math.max(0, y - 3); v <= Math.min(img.h - 1, y + 3); v++) near[v] = 1;
  for (const c of cols) for (let v = Math.max(0, c - 5); v <= Math.min(img.w - 1, c + 5); v++) skip[v] = 1;
  y0 = Math.max(0, y0); y1 = Math.min(img.h, y1);
  let best = Math.max(0, Math.min(img.w - 1, x0)), bestScore = -Infinity;
  for (let x = Math.max(0, x0); x < Math.min(img.w, x1); x++) {
    if (skip[x]) continue;
    let score = 0;
    for (let y = y0; y < y1; y++) if (gray(img, x, y) < DARK) score += near[y] ? 1 : -1;
    if (score > bestScore) { bestScore = score; best = x; }
  }
  return best;
}

/** Pixels sombres d'un filet (décalage −3…+3 → valeurs), mesurés en travers du filet. */
function profile(img, x, y, vertical) {
  const out = [];
  for (let d = -3; d <= 3; d++) {
    const xx = vertical ? x + d : x, yy = vertical ? y : y + d;
    if (xx < 0 || yy < 0 || xx >= img.w || yy >= img.h || gray(img, xx, yy) >= DARK) continue;
    const o = (yy * img.w + xx) * img.ch;
    out.push([d, Array.from(img.data.subarray(o, o + img.ch))]);
  }
  return out;
}

/** Profils vides (filet coupé à cet endroit) : celui du plus proche qui ne l'est pas, sinon un filet gris de 2 px. */
function fillProfiles(list, ch) {
  const ok = list.map((p, i) => (p.length ? i : -1)).filter((i) => i >= 0);
  return list.map((p, i) => {
    if (p.length) return p;
    if (!ok.length) return [[0, new Array(ch).fill(70)], [1, new Array(ch).fill(70)]];
    let best = ok[0];
    for (const j of ok) if (Math.abs(j - i) < Math.abs(best - i)) best = j;
    return list[best];
  });
}

const minD = (p) => p.reduce((m, [d]) => Math.min(m, d), 0);
const maxD = (p) => p.reduce((m, [d]) => Math.max(m, d), 0);

/**
 * Image de base de la page ajoutée : l'image du modèle (même source, même cadrage), avec, dans chaque tableau vidé, papier
 * propre et filets retracés aux positions actuelles (colonnes ajoutées ou retirées comprises), recopiés de l'imprimé ;
 * en-têtes gardés, sauf les cases dont la colonne a changé (texte tapé). Numéros de page effacés.
 */
export function blankBase(img, page) {
  const A = page.Added;
  if (!A || !img) return img;
  const w = img.w, h = img.h, ch = img.ch;
  const data = new Uint8ClampedArray(img.data);
  const set = (x, y, v) => { if (x < 0 || y < 0 || x >= w || y >= h) return; const o = (y * w + x) * ch; for (let c = 0; c < ch; c++) data[o + c] = v[c]; };
  const fill = (x0, y0, x1, y1, v) => {
    x0 = Math.max(0, x0); y0 = Math.max(0, y0); x1 = Math.min(w, x1); y1 = Math.min(h, y1);
    for (let y = y0; y < y1; y++) for (let x = x0; x < x1; x++) { const o = (y * w + x) * ch; for (let c = 0; c < ch; c++) data[o + c] = v[c]; }
  };
  (A.Tables ?? []).forEach((info, ti) => {
    const t = page.Tables[ti];
    if (!info || !t || !(t.ColumnXs?.length >= 2)) return;
    const cols = t.ColumnXs.map(R), orig = info.Orig.map(R), ys = info.Ys.map(R);
    const n = ys.length - 1, head = Math.min(info.Head, n);
    if (n < 1) return;
    const top = ys[head], bottom = ys[n];
    const yRef = cleanRow(img, Math.min(bottom, top + 4), Math.max(1, bottom - top - 8), orig);
    const paper = paperOf(img, yRef);
    const xRef = cleanCol(img, orig[0] + 5, orig[orig.length - 1] - 5, top - 4, bottom + 4, ys.slice(head), orig);
    const vOrig = fillProfiles(orig.map((x) => profile(img, x, yRef, true)), ch);
    const hp = fillProfiles(ys.map((y) => profile(img, xRef, y, false)), ch);
    const interior = vOrig.map((p, i) => i).filter((i) => i > 0 && i < orig.length - 1);
    const vOf = (x) => {
      const i = orig.indexOf(x);
      if (i >= 0) return { p: vOrig[i] };
      const pool = interior.length ? interior : vOrig.map((p, j) => j);
      let best = pool[0];
      for (const j of pool) if (Math.abs(orig[j] - x) < Math.abs(orig[best] - x)) best = j;
      return { p: vOrig[best] };
    };
    const vp = cols.map(vOf);
    const left = Math.min(cols[0], orig[0]) - 4, right = Math.max(cols[cols.length - 1], orig[orig.length - 1]) + 5;
    // 1. Corps du tableau : papier propre (sous un en-tête, son trait du bas est gardé tel qu'imprimé).
    if (head < n) fill(left, head > 0 ? top + 4 : top - 3, right, bottom + 4, paper);
    // Trait retiré : son bout sous l'en-tête aussi.
    if (head > 0 && head < n) for (const x of orig) if (cols.indexOf(x) < 0) fill(x - 3, top + maxD(hp[head]) + 1, x + 4, top + 4, paper);
    // 2. En-tête : les cases dont la colonne a changé sont vidées (leur texte est tapé par-dessus).
    for (let c = 0; c + 1 < cols.length && head > 0; c++) {
      const i = orig.indexOf(cols[c]);
      if (i >= 0 && orig[i + 1] === cols[c + 1]) continue;
      const x0 = cols[c] + maxD(vp[c].p) + 1, x1 = cols[c + 1] + minD(vp[c + 1].p);
      const mid = (cols[c] + cols[c + 1]) >> 1;
      for (let r = 0; r < head; r++) {
        let t0 = 1, t1 = 1;
        for (let d = 0; d <= 3; d++) if (mid < w && ys[r] + d < h && ys[r] + d >= 0 && gray(img, mid, ys[r] + d) < DARK) t0 = d + 1;
        for (let d = 0; d <= 3; d++) if (mid < w && ys[r + 1] - d >= 0 && ys[r + 1] - d < h && gray(img, mid, ys[r + 1] - d) < DARK) t1 = d + 1;
        fill(x0, ys[r] + t0, x1, ys[r + 1] - t1 + 1, paper);
      }
    }
    // 3. Filets horizontaux du corps, d'un bord à l'autre du tableau.
    const xa = cols[0] + minD(vp[0].p), xb = cols[cols.length - 1] + maxD(vp[cols.length - 1].p);
    for (let r = head > 0 ? head + 1 : head; r <= n && head < n; r++)
      for (let x = xa; x <= xb; x++) for (const [d, v] of hp[r]) set(x, ys[r] + d, v);
    // 4. Filets verticaux : dans le corps, et dans l'en-tête pour une colonne ajoutée.
    cols.forEach((x, c) => {
      const isNew = orig.indexOf(x) < 0;
      const ya = isNew || head === 0 ? ys[0] + minD(hp[0]) : top + minD(hp[head]);
      const yb = bottom + maxD(hp[n]);
      if (!isNew && head >= n) return;
      for (let y = ya; y <= yb; y++) for (const [d, v] of vp[c].p) set(x + d, y, v);
    });
  });
  if (A.Tail != null) {
    const y0 = Math.max(0, R(A.Tail) + 5);
    if (y0 < h) fill(0, y0, w, h, paperOf(img, Math.max(0, Math.min(h - 1, R(A.Tail) - 6))));
  }
  for (const z of A.Erase ?? [])
    fill(Math.floor(z.X), Math.floor(z.Y), Math.ceil(z.X + z.Width), Math.ceil(z.Y + z.Height), paperOf(img, Math.max(0, Math.min(h - 1, Math.floor(z.Y) - 2))));
  return { w, h, ch, data };
}
