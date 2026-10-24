// Colonne « Lot » tracée dans la marge gauche, comme si elle avait été imprimée avec le tableau :
// trait vertical à gauche, filets horizontaux prolongés, chaque case au même fond que sa rangée.
// Portage de LotColumn.cs, en entiers : résultat identique, pixel pour pixel, à la version Windows.
import { Rect } from './geometry.js';
import { columnWidth, numberBox, hasLotCell } from './lots.js';

const R = (v) => Math.floor(v + 0.5);
const half = (n) => Math.trunc(n / 2);
const hasLot = (t) => t.Rows.some((r) => r.Lines.some(hasLotCell));
const intersects = (a, b) => a.X < b.X + b.Width && b.X < a.X + a.Width && a.Y < b.Y + b.Height && b.Y < a.Y + a.Height;

/** Le réglage « colonne tracée » du projet (vrai par défaut). */
export const lotFrameEnabled = (project) => project.LotFrame !== false;

/** Tableaux à grille qui portent au moins un numéro de lot. */
export const framedTables = (page) =>
  page.Tables.filter((t) => t.HasGrid !== false && t.ColumnXs?.length >= 2 && t.RowYs?.length >= 2 && hasLot(t));

/** Signature de ce qui est tracé (pour savoir s'il faut redessiner l'écran). */
export const frameSignature = (page, enabled) =>
  !enabled ? '' : framedTables(page).map((t) => page.Tables.indexOf(t)).join(',') + '/' + columnWidth(page);

export function frames(image, page) {
  const w = columnWidth(page);
  const out = [];
  for (const t of framedTables(page)) {
    const right = R(t.ColumnXs[0]), left = R(t.ColumnXs[0] - w);
    if (right - left < 8) continue;
    const m = measureRules(image, t);
    out.push({ table: t, left, right, top: R(t.RowYs[0]), bottom: R(t.RowYs[t.RowYs.length - 1]), vThick: m.vThick, hThick: m.hThick, color: m.color });
  }
  return out;
}

/** Zones de la page redressée qui changent (à recopier dans la photo d'origine). */
export function lotRegions(page, enabled) {
  const framed = enabled ? framedTables(page) : [];
  const w = columnWidth(page);
  const boxes = framed.map((t) => Rect.fromEdges(t.ColumnXs[0] - w - 8, t.RowYs[0] - 8, t.ColumnXs[0] - 1, t.RowYs[t.RowYs.length - 1] + 8));
  for (const t of page.Tables.filter((x) => !framed.includes(x)))
    for (const l of t.Rows.flatMap((r) => r.Lines).filter((x) => (x.Lot ?? '').length > 0)) boxes.push(numberBox(t, l, w));
  return boxes;
}

/** Copie de la page redressée avec les colonnes tracées (rien d'autre ne change). */
export function drawLotColumns(image, page) {
  const output = { w: image.w, h: image.h, ch: image.ch, data: new Uint8ClampedArray(image.data) };
  for (const f of frames(image, page)) {
    drawBackgrounds(image, output, f);
    const hv = half(f.vThick);
    fill(output, f.left - hv, f.top - half(f.hThick), f.vThick, f.bottom - f.top + f.hThick, f.color);
    for (const y of f.table.RowYs) fill(output, f.left - hv, R(y) - half(f.hThick), f.right - (f.left - hv), f.hThick, f.color);
  }
  return output;
}

/** Fond de chaque case : celui de la rangée (gris hachuré de l'en-tête et des totaux, papier ailleurs). */
function drawBackgrounds(source, output, f) {
  const t = f.table;
  const x0 = f.left - half(f.vThick) + f.vThick, x1 = f.right - half(f.vThick);
  const w = x1 - x0;
  if (w <= 0) return;
  for (let r = 0; r + 1 < t.RowYs.length; r++) {
    const y0 = R(t.RowYs[r]) - half(f.hThick) + f.hThick, y1 = R(t.RowYs[r + 1]) - half(f.hThick);
    if (y1 <= y0) continue;
    const ink = (r < t.Rows.length ? t.Rows[r].Lines : []).flatMap((l) => l.Cells).filter((c) => !Rect.isEmpty(c.Ink)).map((c) => c.Ink);
    const start = R(t.ColumnXs[0]) + f.vThick + 2, end = R(t.ColumnXs[1]) - f.vThick - 2 - w;
    for (let sx = start; sx <= end; sx += 2) {
      const patch = Rect.make(sx - 3, y0, w + 6, y1 - y0);
      if (ink.some((i) => intersects(i, patch))) continue;
      copy(source, output, sx, y0, x0, y0, w, y1 - y0);
      break;
    }
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
export function measureRules(img, t) {
  const vRuns = [], hRuns = [];
  let s0 = 0, s1 = 0, s2 = 0, sg = 0, n = 0;
  const ch = img.ch;
  const take = (x, y) => {
    const o = (y * img.w + x) * ch, d = img.data;
    s0 += d[o]; s1 += ch === 1 ? d[o] : d[o + 1]; s2 += ch === 1 ? d[o] : d[o + 2];
    sg += gray(img, x, y); n++;
  };

  const xr = R(t.ColumnXs[0]);
  const top = t.RowYs[0], bottom = t.RowYs[t.RowYs.length - 1];
  for (let k = 1; k <= 11; k++) {
    const y = R(top + ((bottom - top) * k) / 12);
    if (y < 0 || y >= img.h || t.RowYs.some((ry) => Math.abs(ry - y) < 6)) continue;
    const a = Math.max(0, xr - 6), b = Math.min(img.w - 1, xr + 6);
    if (a >= b) continue;
    const g = (i) => gray(img, i, y);
    const r = run(a, b, g);
    if (!r) continue;
    vRuns.push(r.len);
    take(darkest(r.from, r.to, g), y);
  }

  const x0 = t.ColumnXs[0], x1 = t.ColumnXs[1];
  for (const ryD of t.RowYs) {
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
