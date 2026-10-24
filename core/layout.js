// Rangée qui s'agrandit : quand le texte d'une case passe sur plusieurs lignes (même taille d'écriture),
// une bande est ajoutée sous la ligne et tout ce qui est en dessous descend d'autant.
// Portage de PageLayout.cs (entiers : même résultat sur Windows et sur iPhone/iPad).
import { Rect } from './geometry.js';
import { allLines, lineBounds } from './model.js';

export const LINE_HEIGHT = 1.15;

export const totalExtra = (page) => [...allLines(page)].reduce((s, l) => s + (l.Extra ?? 0), 0);

/** Hauteur de la page redressée d'origine (sans les bandes ajoutées). */
export const baseHeight = (page) => page.Height - totalExtra(page);

/** Case d'origine (sans la hauteur ajoutée). */
export const baseBox = (line, f) => ({ ...f.Box, Height: f.Box.Height - (line.Extra ?? 0) });

/** Hauteur à ajouter pour que `lines` lignes de texte en taille `size` tiennent dans la case. */
export function extraFor(line, f, lines, size) {
  if (lines <= 1) return 0;
  const b = baseBox(line, f);
  const center = Rect.isEmpty(f.Ink) ? Rect.cy(b) : Rect.cy(f.Ink);
  const top = Math.min(Math.max(center - 0.58 * size, b.Y), Math.max(b.Y, Rect.bottom(b) - LINE_HEIGHT * size));
  const bottom = top + lines * LINE_HEIGHT * size + 2;
  return Math.max(0, Math.ceil(bottom - Rect.bottom(b)));
}

const shiftRect = (r, at, delta) => {
  if (r.Y >= at) return { ...r, Y: r.Y + delta };
  if (r.Y + r.Height > at) return { ...r, Height: r.Height + delta };
  return r;
};

/** Fixe la hauteur ajoutée sous une ligne et décale tout ce qui est en dessous. Vrai si la page a changé. */
export function setExtra(page, line, extra) {
  extra = Math.max(0, extra);
  const cur = line.Extra ?? 0;
  if (extra === cur) return false;
  if (cur === 0) line.ExtraAt = cleanGap(page, line);
  const delta = extra - cur, at = line.ExtraAt;

  for (const t of page.Tables) {
    t.RowYs = t.RowYs.map((y) => (y > at ? y + delta : y));
    t.Bounds = shiftRect(t.Bounds, at, delta);
    for (const r of t.Rows) r.Bounds = shiftRect(r.Bounds, at, delta);
  }
  page.LockZones = page.LockZones.map((z) => shiftRect(z, at, delta));
  for (const l of allLines(page)) {
    if (l === line) {
      for (const f of l.Cells) f.Box = { ...f.Box, Height: f.Box.Height + delta };
      continue;
    }
    if ((l.Extra ?? 0) > 0 && l.ExtraAt > at) l.ExtraAt += delta;
    for (const f of l.Cells) {
      if (Rect.cy(baseBox(l, f)) < at) continue;
      f.Box = { ...f.Box, Y: f.Box.Y + delta };
      if (!Rect.isEmpty(f.Ink)) f.Ink = { ...f.Ink, Y: f.Ink.Y + delta };
    }
  }
  page.Height += delta;
  line.Extra = extra;
  if (extra === 0) line.ExtraAt = 0;
  return true;
}

/** Ordonnée de la bande : au milieu de l'interligne sous le texte de la ligne. */
function cleanGap(page, line) {
  const inked = line.Cells.filter((c) => !Rect.isEmpty(c.Ink));
  const lb = lineBounds(line);
  const a = inked.length > 0 ? Math.max(...inked.map((c) => Rect.bottom(c.Ink))) : Rect.cy(lb);
  let b = Rect.bottom(lb);
  for (const t of page.Tables)
    t.Rows.forEach((row, r) => {
      const i = row.Lines.indexOf(line);
      if (i < 0) return;
      const below = row.Lines.slice(i + 1).flatMap((l) => l.Cells).filter((c) => !Rect.isEmpty(c.Ink)).map((c) => c.Ink.Y);
      b = below.length > 0 ? Math.min(...below) : r + 1 < t.RowYs.length ? t.RowYs[r + 1] - 4 : b;
    });
  return b > a ? Math.floor((a + b) / 2) : Math.floor(a);
}

/** Image de la page agrandie : l'image d'origine avec, pour chaque bande, l'interligne propre repris. */
export function stretch(base, page) {
  const bands = [];
  for (const t of page.Tables)
    for (const l of t.Rows.flatMap((r) => r.Lines).filter((x) => (x.Extra ?? 0) > 0)) {
      const grid = t.HasGrid !== false && t.ColumnXs?.length > 0;
      const left = grid ? t.ColumnXs[0] : t.Bounds.X, right = grid ? t.ColumnXs[t.ColumnXs.length - 1] : Rect.right(t.Bounds);
      bands.push({ at: l.ExtraAt, height: l.Extra, x0: Math.floor(left) - 4, x1: Math.ceil(right) + 4 });
    }
  if (bands.length === 0) return base;
  bands.sort((a, b) => a.at - b.at);
  const w = base.w, ch = base.ch, stride = w * ch;
  const height = base.h + bands.reduce((s, b) => s + b.height, 0);
  const data = new Uint8ClampedArray(height * stride);
  let src = 0, dst = 0, before = 0;
  for (const band of bands) {
    const s = Math.min(base.h - 1, Math.max(0, band.at - before));
    const n = s - src;
    if (n > 0) { data.set(base.data.subarray(src * stride, (src + n) * stride), dst * stride); src += n; dst += n; }
    const paper = paperOf(base, s);
    const x0 = Math.min(w, Math.max(0, band.x0)), x1 = Math.min(w, Math.max(0, band.x1));
    for (let k = 0; k < band.height; k++, dst++)
      for (let x = 0; x < w; x++) {
        const o = dst * stride + x * ch;
        // Dans le tableau : les filets verticaux (sombres) sont prolongés, le reste devient papier propre.
        if (x >= x0 && x < x1 && gray(base, x, s) < 150) for (let c = 0; c < ch; c++) data[o + c] = base.data[s * stride + x * ch + c];
        else for (let c = 0; c < ch; c++) data[o + c] = paper[c];
      }
    before += band.height;
  }
  const rest = base.h - src;
  if (rest > 0) data.set(base.data.subarray(src * stride, (src + rest) * stride), dst * stride);
  return { w, h: height, ch, data };
}

function gray(img, x, y) {
  const o = (y * img.w + x) * img.ch, d = img.data;
  return img.ch === 1 ? d[o] : (d[o] * 77 + d[o + 1] * 150 + d[o + 2] * 29) >> 8;
}

function paperOf(img, y) {
  const w = img.w, ch = img.ch, d = img.data;
  const lum = [];
  for (let x = 0; x < w; x++) {
    const o = (y * w + x) * ch;
    lum.push([ch === 1 ? d[o] : d[o] + d[o + 1] + d[o + 2], x]);
  }
  lum.sort((a, b) => (b[0] !== a[0] ? b[0] - a[0] : a[1] - b[1]));
  const n = Math.max(1, Math.trunc(w / 2));
  const sum = new Array(ch).fill(0);
  for (let i = 0; i < n; i++) {
    const o = (y * w + lum[i][1]) * ch;
    for (let c = 0; c < ch; c++) sum[c] += d[o + c];
  }
  return sum.map((v) => Math.trunc(v / n));
}
