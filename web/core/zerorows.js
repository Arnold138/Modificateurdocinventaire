// Lots 0 (absents, barrés) à l'export JPEG : la rangée entière est retirée de sa page (ce qui suit remonte)
// et recollée en fin d'inventaire, sous le dernier tableau de la dernière page. Portage de ZeroRows.cs.
import { measureRules } from './lotcolumn.js';
import { isZeroLot } from './lots.js';

const R = (v) => Math.floor(v + 0.5);
const half = (n) => Math.trunc(n / 2);
const gridTables = (page) => page.Tables.filter((t) => t.HasGrid !== false && t.RowYs?.length >= 2);

/** Rangées à déplacer : tous les objets numérotés de la rangée sont au lot 0. */
export function zeroRows(page) {
  const list = [];
  for (const t of gridTables(page))
    t.Rows.forEach((row, r) => {
      if (r + 1 >= t.RowYs.length) return;
      const objects = row.Lines.filter((l) => (l.Lot ?? '').length > 0);
      if (objects.length > 0 && objects.every(isZeroLot)) list.push({ table: t, row: r });
    });
  return list;
}

export function zeroCuts(image, page) {
  const cuts = [];
  for (const { table: t, row: r } of zeroRows(page)) {
    const th = measureRules(image, t).hThick;
    const y0 = R(t.RowYs[r]) - half(th), y1 = R(t.RowYs[r + 1]) - half(th);
    const left = R(t.ColumnXs?.length ? t.ColumnXs[0] : t.Bounds.X);
    const last = cuts[cuts.length - 1];
    if (last && last.y1 === y0 && last.left === left) last.y1 = y1;
    else if (y1 > y0) cuts.push({ y0, y1, left });
  }
  cuts.sort((a, b) => a.y0 - b.y0);
  return cuts;
}

/** Retire les rangées au lot 0 : { image, strips, insertAt, tableLeft }. */
export function cutZeroRows(image, page) {
  const cuts = zeroCuts(image, page);
  const tables = gridTables(page);
  const last = tables[tables.length - 1];
  let insertAt = -1, tableLeft = 0;
  if (last) {
    const th = measureRules(image, last).hThick;
    insertAt = R(last.RowYs[last.RowYs.length - 1]) - half(th);
    insertAt -= cuts.filter((c) => c.y1 <= insertAt).reduce((s, c) => s + c.y1 - c.y0, 0);
    tableLeft = R(last.ColumnXs?.length ? last.ColumnXs[0] : last.Bounds.X);
  }
  const strips = [];
  if (cuts.length === 0) return { image, strips, insertAt, tableLeft };
  const w = image.w, ch = image.ch, stride = w * ch;
  const height = Math.max(1, image.h - cuts.reduce((s, c) => s + c.y1 - c.y0, 0));
  const data = new Uint8ClampedArray(height * stride);
  let src = 0, dst = 0;
  for (const c of cuts) {
    const a = Math.min(Math.max(c.y0, src), image.h), b = Math.min(Math.max(c.y1, a), image.h);
    if (a > src) { data.set(image.data.subarray(src * stride, a * stride), dst * stride); dst += a - src; }
    if (b > a) strips.push({ image: { w, h: b - a, ch, data: image.data.slice(a * stride, b * stride) }, tableLeft: c.left });
    src = b;
  }
  if (image.h > src) data.set(image.data.subarray(src * stride, image.h * stride), dst * stride);
  return { image: { w, h: height, ch, data }, strips, insertAt, tableLeft };
}

/** Recolle les morceaux à `at`, alignés sur le bord gauche du tableau d'arrivée. */
export function appendStrips(image, at, tableLeft, strips) {
  if (at < 0 || strips.length === 0) return image;
  at = Math.min(Math.max(at, 0), image.h);
  const w = image.w, ch = image.ch, stride = w * ch;
  const added = strips.reduce((s, x) => s + x.image.h, 0);
  const data = new Uint8ClampedArray((image.h + added) * stride);
  data.set(image.data.subarray(0, at * stride), 0);
  const paper = brightest(image, Math.max(0, at - 1));
  let dst = at;
  for (const s of strips) {
    const dx = tableLeft - s.tableLeft, img = s.image;
    for (let y = 0; y < img.h; y++, dst++)
      for (let x = 0; x < w; x++) {
        const sx = x - dx, o = dst * stride + x * ch;
        if (sx >= 0 && sx < img.w) for (let c = 0; c < ch; c++) data[o + c] = img.data[(y * img.w + sx) * img.ch + Math.min(c, img.ch - 1)];
        else for (let c = 0; c < ch; c++) data[o + c] = paper[c];
      }
  }
  data.set(image.data.subarray(at * stride), dst * stride);
  return { w, h: image.h + added, ch, data };
}

function brightest(img, y) {
  const ch = img.ch, d = img.data;
  let best = 0, bestL = -1;
  for (let x = 0; x < img.w; x++) {
    const o = (y * img.w + x) * ch;
    const l = ch === 1 ? d[o] : d[o] + d[o + 1] + d[o + 2];
    if (l > bestL) { bestL = l; best = o; }
  }
  return Array.from(d.subarray(best, best + ch));
}
