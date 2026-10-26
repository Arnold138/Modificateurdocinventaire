// Colonne « Lot » tracée dans la marge gauche, comme si elle avait été imprimée avec le tableau :
// trait vertical à gauche, filets horizontaux prolongés, chaque case au même fond que sa rangée.
// Portage de LotColumn.cs, en entiers : résultat identique, pixel pour pixel, à la version Windows.
import { Rect } from './geometry.js';
import { columnWidth, numberBox, hasLotCell, rowOrder, ownBox, lotSegment } from './lots.js';
import { looksLikeTotal } from './model.js';
import { toBase, baseToPage } from './layout.js';

const R = (v) => Math.floor(v + 0.5);
const half = (n) => Math.trunc(n / 2);
const hasLot = (t) => t.Rows.some((r) => r.Lines.some(hasLotCell));
const intersects = (a, b) => a.X < b.X + b.Width && b.X < a.X + a.Width && a.Y < b.Y + b.Height && b.Y < a.Y + a.Height;

/** Le réglage « colonne tracée » du projet (vrai par défaut). */
export const lotFrameEnabled = (project) => project.LotFrame !== false;

/** Tableaux à grille qui portent au moins un numéro de lot. */
export const framedTables = (page) =>
  page.Tables.filter((t) => t.HasGrid !== false && t.ColumnXs?.length >= 2 && t.RowYs?.length >= 2 && hasLot(t));

/**
 * Rangées où la colonne est tracée : de la 1re à la dernière rangée qui porte une case de lot (ordre d'affichage),
 * plus l'en-tête juste au-dessus. Rien sous le dernier objet (totaux, rangées vides).
 */
export function framedRows(table) {
  const order = rowOrder(table).filter((r) => r + 1 < table.RowYs.length);
  const has = order.map((r) => table.Rows[r].Lines.some(hasLotCell));
  const first = has.indexOf(true);
  if (first < 0) return [];
  let last = has.lastIndexOf(true);
  // Colonne prolongée à la main (« + » dans la colonne) sous le dernier objet, ex. jusqu'au total.
  for (let k = order.length - 1; k > last; k--) if (table.Rows[order[k]].LotFrame === true) { last = k; break; }
  let from = first;
  while (from > 0 && table.Rows[order[from - 1]].Lines.length > 0 && table.Rows[order[from - 1]].Lines.every((l) => l.Kind === 'EnTete')) from--;
  return order.slice(from, last + 1).filter((r, k) => has[from + k] || frameAuto(table.Rows[r]));
}

/**
 * Rangée sans case de lot entre deux objets : colonne tracée ou non. Par défaut, pas sur une ligne de total (la colonne
 * s'arrête au total) ; au choix sinon (« + » / corbeille dans la colonne : Row.LotFrame vrai / faux).
 */
// Page ajoutée (lignes AutoLot) : un titre écrit entre deux objets n'a pas la colonne (au choix avec « + »).
const frameAuto = (row) => (row.LotFrame === true ? true : row.LotFrame === false ? false
  : !row.Lines.some(looksLikeTotal) && !(row.Lines.length > 0 && row.Lines.every((l) => l.AutoLot)));

/**
 * Rangées écrites où l'on peut tracer ou retirer la colonne (sans case de lot, sous le 1er objet) : { rows, framed }. Les rangées
 * minces qui se suivent (bande de total découpée en plusieurs, < 30 px) n'ont qu'un bouton.
 */
export function frameToggles(table) {
  const order = rowOrder(table).filter((r) => r + 1 < table.RowYs.length);
  const first = order.findIndex((r) => table.Rows[r].Lines.some(hasLotCell));
  if (first < 0) return [];
  const framed = new Set(framedRows(table));
  const thin = (r) => table.RowYs[r + 1] - table.RowYs[r] < 30;
  const out = [];
  let prev = -1;
  for (let k = first + 1; k < order.length; k++) {
    const r = order[k];
    // Rangée vide (ligne ajoutée pas encore remplie) : pas de bouton, le « + » de la marge y met une case de lot.
    if (table.Rows[r].Lines.some(hasLotCell) || !table.Rows[r].Lines.some((l) => l.Cells.some((c) => c.Text.trim()))) { prev = -1; continue; }
    const g = out[out.length - 1];
    if (prev >= 0 && g && (thin(r) || thin(prev))) { g.rows.push(r); g.framed ||= framed.has(r); } else out.push({ rows: [r], framed: framed.has(r) });
    prev = r;
  }
  return out;
}

/** Signature de ce qui est tracé (pour savoir s'il faut redessiner l'écran). */
export const frameSignature = (page, enabled) =>
  !enabled ? '' : framedTables(page).map((t) => page.Tables.indexOf(t) + ':' + framedRows(t).join('.')).join(',') + '/' + columnWidth(page);

export function frames(image, page) {
  const w = columnWidth(page);
  const out = [];
  for (const t of framedTables(page)) {
    const right = R(t.ColumnXs[0]), left = R(t.ColumnXs[0] - w);
    if (right - left < 8) continue;
    const rows = framedRows(t);
    if (rows.length === 0) continue;
    // Traits mesurés une fois, sur l'imprimé (gardés dans le projet) : la colonne garde le même style quoi qu'on ajoute.
    if (!t.Rules) { const m = measureRules(image, t, page); t.Rules = { V: m.vThick, H: m.hThick, Color: m.color }; }
    out.push({ table: t, left, right, rows, vThick: t.Rules.V, hThick: t.Rules.H, color: t.Rules.Color });
  }
  return out;
}

/**
 * Tableau ouvert en bas (il continue page suivante sur l'imprimé, ou une ligne a été ajoutée dessous) : un trait est
 * tracé sous sa dernière rangée, comme les autres filets. Rien ne change si le trait est déjà là.
 */
export function closeTables(image, page) {
  let out = null;
  for (const t of page.Tables) {
    if (t.HasGrid === false || !(t.ColumnXs?.length >= 2) || !(t.RowYs?.length >= 2)) continue;
    const y = R(t.RowYs[t.RowYs.length - 1]);
    const x0 = R(t.ColumnXs[0]), x1 = R(t.ColumnXs[t.ColumnXs.length - 1]);
    t.Closed = false;
    if (x1 - x0 < 40 || y < 4 || y + 4 >= image.h) continue;
    let dark = 0, n = 0;
    for (let x = x0 + 4; x < x1 - 4; x += 3) {
      n++;
      for (let yy = y - 4; yy <= y + 4; yy++) if (gray(image, x, yy) < 150) { dark++; break; }
    }
    if (n === 0 || dark * 2 >= n) continue;
    const m = t.Rules ? { vThick: t.Rules.V, hThick: t.Rules.H, color: t.Rules.Color } : measureRules(image, t, page);
    out ??= { w: image.w, h: image.h, ch: image.ch, data: new Uint8ClampedArray(image.data) };
    fill(out, x0 - half(m.vThick), y - half(m.hThick), x1 - x0 + m.vThick, m.hThick, m.color);
    t.Closed = true; // pour l'export : cette bande change (recopiée dans la photo d'origine)
  }
  return out ?? image;
}

/** Zones de la page redressée qui changent (à recopier dans la photo d'origine). */
export function lotRegions(page, enabled) {
  const framed = enabled ? framedTables(page) : [];
  const w = columnWidth(page);
  const boxes = [];
  for (const t of framed) {
    const rows = framedRows(t);
    if (rows.length === 0) continue;
    const top = Math.min(...rows.map((r) => t.RowYs[r])), bottom = Math.max(...rows.map((r) => t.RowYs[r + 1]));
    boxes.push(Rect.fromEdges(t.ColumnXs[0] - w - 8, top - 8, t.ColumnXs[0] - 1, bottom + 8));
    // Traits d'une case de lot d'une seule ligne : sur toute la largeur du tableau.
    for (const r of rows) {
      const row = t.Rows[r];
      for (const l of row?.Lines ?? []) {
        if (!ownBox(row, l)) continue;
        const s = lotSegment(t, r, l);
        for (const y of [s.top, s.bottom]) boxes.push(Rect.fromEdges(t.ColumnXs[0] - 1, y - 6, t.ColumnXs[t.ColumnXs.length - 1] + 6, y + 6));
      }
    }
  }
  for (const t of page.Tables.filter((x) => x.Closed)) {
    const y = t.RowYs[t.RowYs.length - 1];
    boxes.push(Rect.fromEdges(t.ColumnXs[0] - 8, y - 8, t.ColumnXs[t.ColumnXs.length - 1] + 8, y + 8));
  }
  for (const t of page.Tables.filter((x) => !framed.includes(x)))
    for (const l of t.Rows.flatMap((r) => r.Lines).filter((x) => (x.Lot ?? '').length > 0)) boxes.push(numberBox(t, l, w));
  return boxes;
}

/** Copie de la page redressée avec les colonnes tracées (rien d'autre ne change). */
export function drawLotColumns(image, page) {
  const output = { w: image.w, h: image.h, ch: image.ch, data: new Uint8ClampedArray(image.data) };
  for (const f of frames(image, page)) {
    drawBackgrounds(image, output, f);
    const hv = half(f.vThick), hh = half(f.hThick);
    // Chaque rangée porte ses traits (gauche, haut, bas) : elle peut être déplacée avec eux.
    for (const r of f.rows) {
      const y0 = R(f.table.RowYs[r]), y1 = R(f.table.RowYs[r + 1]);
      fill(output, f.left - hv, y0 - hh, f.vThick, y1 - y0 + f.hThick, f.color);
      // Trait entre le numéro et la désignation (trait gauche du tableau) : partout où l'image ne l'a pas (rangée
      // ajoutée, trait imprimé qui s'arrête ou décalé), il est tracé, ligne de pixels par ligne de pixels.
      for (let y = Math.max(0, y0 - hh); y < Math.min(output.h, y1 - hh + f.hThick); y++) {
        let dark = false;
        for (let x = Math.max(0, f.right - 8); x <= Math.min(output.w - 1, f.right + 3) && !dark; x++) dark = gray(output, x, y) < 150;
        if (!dark) fill(output, f.right - f.vThick, y, f.vThick, 1, f.color); // dans la colonne, contre le tableau
      }
      fill(output, f.left - hv, y0 - hh, f.right - (f.left - hv), f.hThick, f.color);
      fill(output, f.left - hv, y1 - hh, f.right - (f.left - hv), f.hThick, f.color);
      // Case de lot d'une seule ligne dans une grande case : fermée en haut et en bas, à sa hauteur, et la ligne est
      // délimitée sur toute la largeur du tableau (comme une rangée à part).
      const row = f.table.Rows[r];
      const tableRight = rightRuleOf(image, f.table, y0, y1) + hv;
      for (const l of row?.Lines ?? []) {
        if (!ownBox(row, l)) continue;
        const s = lotSegment(f.table, r, l);
        for (const y of [R(s.top), R(s.bottom)]) if (y - y0 > 3 && y1 - y > 3) fill(output, f.left - hv, y - hh, tableRight - (f.left - hv), f.hThick, f.color);
      }
    }
  }
  return output;
}

/**
 * Trait gauche du tableau dans la rangée [y0, y1[ : abscisse de son premier pixel sombre près de `right` (il peut
 * être un peu décalé : page légèrement de travers), mesurée sur trois hauteurs ; null s'il n'y est pas (rangée ajoutée).
 */
/** Bord droit du tableau dans la rangée [y0, y1[ : le dernier trait vertical vraiment imprimé (une colonne lue en trop n'en a pas). */
function rightRuleOf(img, t, y0, y1) {
  const ys = [y0 + Math.trunc((y1 - y0) / 3), y0 + Math.trunc((2 * (y1 - y0)) / 3)].filter((y) => y >= 0 && y < img.h);
  for (let c = t.ColumnXs.length - 1; c > 0; c--) {
    const x = R(t.ColumnXs[c]);
    if (ys.length > 0 && ys.every((y) => { for (let xx = Math.max(0, x - 3); xx <= Math.min(img.w - 1, x + 3); xx++) if (gray(img, xx, y) < 150) return true; return false; })) return x;
  }
  return R(t.ColumnXs[t.ColumnXs.length - 1]);
}

export function ruleLeftOf(img, right, y0, y1) {
  let found = null;
  for (const y of [y0 + 2, (y0 + y1) >> 1, y1 - 3]) {
    if (y < y0 || y >= y1 || y < 0 || y >= img.h) continue;
    for (let x = Math.max(0, right - 8); x <= Math.min(img.w - 1, right + 3); x++)
      if (gray(img, x, y) < 150) { found = found === null ? x : Math.min(found, x); break; }
  }
  return found;
}

/**
 * Fond de chaque case : celui de la rangée (gris hachuré de l'en-tête, papier ailleurs), recopié depuis
 * le morceau le plus clair de la 1re colonne (jamais du texte, même si l'OCR ne l'a pas lu). Le fond s'arrête avant
 * le trait gauche du tableau, là où il est sur l'image (il reste visible).
 */
function drawBackgrounds(source, output, f) {
  const t = f.table;
  const x0 = f.left - half(f.vThick) + f.vThick, full = f.right - half(f.vThick);
  if (full - x0 <= 0) return;
  for (const r of f.rows) {
    const y0 = R(t.RowYs[r]) - half(f.hThick) + f.hThick, y1 = R(t.RowYs[r + 1]) - half(f.hThick);
    if (y1 <= y0) continue;
    const rule = ruleLeftOf(source, f.right, y0, y1);
    const x1 = rule === null ? full : Math.min(full, rule);
    const w = x1 - x0;
    if (w <= 0) continue;
    const ink = (r < t.Rows.length ? t.Rows[r].Lines : []).flatMap((l) => l.Cells).filter((c) => !Rect.isEmpty(c.Ink)).map((c) => c.Ink);
    const start = R(t.ColumnXs[0]) + f.vThick + 2, end = R(t.ColumnXs[1]) - f.vThick - 2 - w;
    let best = -1, bestSum = -1;
    for (let sx = start; sx <= end; sx += 2) {
      const patch = Rect.make(sx - 3, y0, w + 6, y1 - y0);
      if (ink.some((i) => intersects(i, patch))) continue;
      let sum = 0;
      for (let y = y0; y < y1; y += 2)
        for (let x = sx; x < sx + w; x += 2) if (x >= 0 && x < source.w && y >= 0 && y < source.h) sum += gray(source, x, y);
      if (sum > bestSum) { bestSum = sum; best = sx; }
    }
    if (best >= 0) copy(source, output, best, y0, x0, y0, w, y1 - y0);
  }
}

function copy(src, out, sx, sy, dx, dy, w, h) {
  const ch = src.ch;
  for (let y = 0; y < h; y++) {
    const ys = sy + y, yd = dy + y;
    if (ys < 0 || ys >= src.h || yd < 0 || yd >= src.h) continue;
    for (let x = 0; x < w; x++) {
      const xs = sx + x, xd = dx + x;
      if (xs < 0 || xs >= src.w || xd < 0 || xd >= src.w) continue;
      const a = (ys * src.w + xs) * ch, b = (yd * src.w + xd) * ch;
      for (let c = 0; c < ch; c++) out.data[b + c] = src.data[a + c];
    }
  }
}

function fill(img, x, y, w, h, color) {
  const ch = img.ch;
  for (let yy = Math.max(0, y); yy < Math.min(img.h, y + h); yy++)
    for (let xx = Math.max(0, x); xx < Math.min(img.w, x + w); xx++) {
      const o = (yy * img.w + xx) * ch;
      if (ch === 1) img.data[o] = color[3];
      else { img.data[o] = color[0]; img.data[o + 1] = color[1]; img.data[o + 2] = color[2]; img.data[o + 3] = 255; }
    }
}

const gray = (img, x, y) => {
  const o = (y * img.w + x) * img.ch, d = img.data;
  return img.ch === 1 ? d[o] : (d[o] * 77 + d[o + 1] * 150 + d[o + 2] * 29) >> 8;
};

/** Épaisseur et couleur des filets, mesurées sur l'image (cœur des traits). Couleur : 3 octets + gris. */
/**
 * `page` : les traits sont mesurés seulement sur les rangées imprimées (pas dans une rangée ajoutée ni une bande
 * d'agrandissement, où le trait recopié est plus fin) : l'épaisseur ne change pas quand on ajoute des lignes.
 */
export function measureRules(img, t, page = null) {
  const vRuns = [], hRuns = [];
  let s0 = 0, s1 = 0, s2 = 0, sg = 0, n = 0;
  const ch = img.ch;
  const take = (x, y) => {
    const o = (y * img.w + x) * ch, d = img.data;
    s0 += d[o]; s1 += ch === 1 ? d[o] : d[o + 1]; s2 += ch === 1 ? d[o] : d[o + 2];
    sg += gray(img, x, y); n++;
  };

  const xr = R(t.ColumnXs[0]);
  let top = t.RowYs[0], bottom = t.RowYs[t.RowYs.length - 1];
  // Mesures aux mêmes endroits de l'image imprimée, quoi qu'on ajoute (positions prises sur la page d'origine).
  const map = page && (page.Inserts?.length || page.Tables.some((u) => u.Rows.some((r) => r.Lines.some((l) => (l.Extra ?? 0) > 0)))) ? baseToPage(page) : null;
  if (map) {
    top += (toBase(page, R(top)) ?? R(top)) - R(top);
    bottom += (toBase(page, R(bottom)) ?? R(bottom)) - R(bottom);
  }
  for (let k = 1; k <= 11; k++) {
    const yb = R(top + ((bottom - top) * k) / 12);
    const y = map ? map.get(yb) : yb;
    if (y === undefined || y < 0 || y >= img.h || t.RowYs.some((ry) => Math.abs(ry - y) < 6)) continue;
    const a = Math.max(0, xr - 6), b = Math.min(img.w - 1, xr + 6);
    if (a >= b) continue;
    const g = (i) => gray(img, i, y);
    const r = run(a, b, g);
    if (!r) continue;
    vRuns.push(r.len);
    take(darkest(r.from, r.to, g), y);
  }

  const x0 = t.ColumnXs[0], x1 = t.ColumnXs[1];
  for (const [i, ryD] of t.RowYs.entries()) {
    if (page && t.Rows[i]?.Inserted) continue; // filet recopié d'une rangée ajoutée : pas mesuré
    const ry = R(ryD);
    for (let k = 1; k <= 5; k++) {
      const x = R(x0 + ((x1 - x0) * k) / 6);
      if (x < 0 || x >= img.w || t.ColumnXs.some((cx) => Math.abs(cx - x) < 6)) continue;
      const a = Math.max(0, ry - 6), b = Math.min(img.h - 1, ry + 6);
      if (a >= b) continue;
      const g = (i) => gray(img, x, i);
      const r = run(a, b, g);
      if (!r) continue;
      hRuns.push(r.len);
      take(x, darkest(r.from, r.to, g));
    }
  }

  const median = (v, fallback) => {
    if (v.length === 0) return fallback;
    v.sort((a, b) => a - b);
    return Math.min(8, Math.max(1, v[Math.trunc(v.length / 2)]));
  };
  const vThick = median(vRuns, 2), hThick = median(hRuns, vThick);
  const color = n === 0 ? [40, 40, 40, 40] : [Math.trunc(s0 / n), Math.trunc(s1 / n), Math.trunc(s2 / n), Math.trunc(sg / n)];
  return { vThick, hThick, color };
}

function darkest(from, to, g) {
  let at = from;
  for (let i = from; i <= to; i++) if (g(i) < g(at)) at = i;
  return at;
}

/** Trait sombre le plus marqué entre a et b (null si aucun trait net). */
function run(a, b, g) {
  let min = 256, at = a, max = 0;
  for (let i = a; i <= b; i++) {
    const v = g(i);
    if (v < min) { min = v; at = i; }
    if (v > max) max = v;
  }
  if (min > 170 || max - min < 60) return null;
  const thr = Math.trunc((min + max) / 2);
  let from = at, to = at;
  while (from > a && g(from - 1) < thr) from--;
  while (to < b && g(to + 1) < thr) to++;
  return { from, to, len: to - from + 1 };
}
