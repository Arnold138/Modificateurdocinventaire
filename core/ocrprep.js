// Préparation des images envoyées à la reconnaissance du texte. Portage de OcrPreparation.cs.
import { Rect, pt, roundHalfEven } from './geometry.js';
import { toGray, otsu, resize, raster, white } from './imaging.js';

export const TARGET_WIDTH = 2200;
const clamp = (v, a, b) => (v < a ? a : v > b ? b : v);

/** Filets effacés, agrandissement, seuil adaptatif. */
export function prepare(page, grids) {
  const gray = toGray(page);
  const W = gray.w, H = gray.h;
  const data = new Uint8ClampedArray(gray.data);
  const thr = clamp(otsu(gray) + 30, 140, 200);
  for (const g of grids) {
    const top = Math.trunc(g.Bounds.Y) - 3, bottom = Math.trunc(Rect.bottom(g.Bounds)) + 3;
    const left = Math.trunc(g.Bounds.X) - 3, right = Math.trunc(Rect.right(g.Bounds)) + 3;
    for (const x of g.ColumnXs) eraseRule(data, W, H, thr, true, x, top, bottom);
    for (const y of g.RowYs) eraseRule(data, W, H, thr, false, y, left, right);
  }
  const scale = clamp(TARGET_WIDTH / W, 1, 3);
  const up = resize(raster(W, H, 1, data), roundHalfEven(W * scale), roundHalfEven(H * scale));
  return { image: adaptiveThreshold(up), scale };
}

function eraseRule(d, W, H, thr, vertical, pos, from, to) {
  const across = vertical ? W : H, along = vertical ? H : W;
  const idx = vertical ? (a, c) => a * W + c : (a, c) => c * W + a;
  const home = roundHalfEven(pos);
  let center = home;
  for (let a = Math.max(0, from); a <= Math.min(along - 1, to); a++) {
    let seed = -1;
    for (let k = 0; k <= 3 && seed < 0; k++) {
      if (center - k >= 0 && d[idx(a, center - k)] < thr) seed = center - k;
      else if (center + k < across && d[idx(a, center + k)] < thr) seed = center + k;
    }
    if (seed < 0) continue;
    let lo = seed, hi = seed;
    while (lo > 0 && seed - lo < 8 && d[idx(a, lo - 1)] < thr) lo--;
    while (hi < across - 1 && hi - seed < 8 && d[idx(a, hi + 1)] < thr) hi++;
    if (hi - lo + 1 <= 5) {
      if (lo > 0 && d[idx(a, lo - 1)] < 235) lo--;
      if (hi < across - 1 && d[idx(a, hi + 1)] < 235) hi++;
      for (let c = lo; c <= hi; c++) d[idx(a, c)] = 255;
      center = clamp(Math.trunc((lo + hi) / 2), home - 3, home + 3);
    } else {
      for (let c = Math.max(0, center - 1); c <= Math.min(across - 1, center + 1); c++) d[idx(a, c)] = 255;
    }
  }
}

/** Seuil de Bradley. */
export function adaptiveThreshold(gray, t = 0.15) {
  const W = gray.w, H = gray.h;
  const s = Math.trunc(Math.max(15, Math.trunc(W / 16)) / 2);
  const integral = new Float64Array((W + 1) * (H + 1));
  for (let y = 0; y < H; y++) {
    let rowSum = 0;
    for (let x = 0; x < W; x++) {
      rowSum += gray.data[y * W + x];
      integral[(y + 1) * (W + 1) + x + 1] = integral[y * (W + 1) + x + 1] + rowSum;
    }
  }
  const out = new Uint8ClampedArray(W * H);
  for (let y = 0; y < H; y++) {
    const y0 = Math.max(0, y - s), y1 = Math.min(H - 1, y + s);
    for (let x = 0; x < W; x++) {
      const x0 = Math.max(0, x - s), x1 = Math.min(W - 1, x + s);
      const count = (x1 - x0 + 1) * (y1 - y0 + 1);
      const sum = integral[(y1 + 1) * (W + 1) + x1 + 1] - integral[y0 * (W + 1) + x1 + 1]
                - integral[(y1 + 1) * (W + 1) + x0] + integral[y0 * (W + 1) + x0];
      out[y * W + x] = gray.data[y * W + x] * count < sum * (1 - t) ? 0 : 255;
    }
  }
  return raster(W, H, 1, out);
}

/** Cases vides qui contiennent pourtant de l'encre : à relire une par une. */
export function emptyFieldsWithInk(page, image) {
  const gray = toGray(image);
  const thr = Math.min(otsu(gray), 150);
  const result = [];
  for (const t of page.Tables) {
    if (!t.HasGrid) continue;
    for (const row of t.Rows)
      for (const line of row.Lines)
        for (const f of line.Cells) {
          if (f.Locked || f.Text.length > 0) continue;
          const r = Rect.inflate(f.Box, -2, -1);
          let dark = 0;
          for (let y = Math.max(0, Math.trunc(r.Y)); y < Math.min(gray.h, Math.trunc(Rect.bottom(r))); y++)
            for (let x = Math.max(0, Math.trunc(r.X)); x < Math.min(gray.w, Math.trunc(Rect.right(r))); x++)
              if (gray.data[y * gray.w + x] < thr) dark++;
          if (dark >= 20) result.push(f);
        }
  }
  return result;
}

/** Image d'une seule case, nettoyée, agrandie ×3, avec une marge blanche. */
export function prepareCell(page, box) {
  const gray = toGray(page);
  const r = Rect.inflate(box, -1, 0);
  const x0 = Math.max(0, Math.trunc(r.X)), y0 = Math.max(0, Math.trunc(r.Y));
  const x1 = Math.min(gray.w, Math.ceil(Rect.right(r))), y1 = Math.min(gray.h, Math.ceil(Rect.bottom(r)));
  const w = Math.max(1, x1 - x0), h = Math.max(1, y1 - y0);
  const crop = new Uint8ClampedArray(w * h);
  for (let y = 0; y < h; y++) crop.set(gray.data.subarray((y0 + y) * gray.w + x0, (y0 + y) * gray.w + x0 + w), y * w);
  const scale = 3, margin = 24;
  const up = resize(raster(w, h, 1, crop), w * 3, h * 3);
  const thr = otsu(up);
  const padded = white(up.w + 2 * margin, up.h + 2 * margin, 1);
  for (let y = 0; y < up.h; y++)
    for (let x = 0; x < up.w; x++)
      padded.data[(y + margin) * padded.w + x + margin] = up.data[y * up.w + x] < thr ? 0 : 255;
  return { image: padded, scale, origin: pt(x0 - margin / scale, y0 - margin / scale) };
}

const trimBars = (t) => t.trim().replace(/^\|+|\|+$/g, '');

export function applyCellWords(field, words, scale, origin) {
  const ws = words.map((w) => ({ ...w, Text: trimBars(w.Text) })).filter((w) => w.Text.length > 0);
  ws.sort((a, b) => a.Box.X - b.Box.X);
  if (ws.length === 0) return;
  field.OriginalText = field.Text = ws.map((w) => w.Text).join(' ');
  field.Ink = ws
    .map((w) => Rect.make(origin.X + w.Box.X / scale, origin.Y + w.Box.Y / scale, w.Box.Width / scale, w.Box.Height / scale))
    .reduce((a, b) => Rect.union(a, b), Rect.empty());
}

export const mapBack = (words, scale) =>
  words.map((w) => ({ Text: w.Text, Box: Rect.make(w.Box.X / scale, w.Box.Y / scale, w.Box.Width / scale, w.Box.Height / scale) }));
