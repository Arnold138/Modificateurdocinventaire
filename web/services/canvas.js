// Passerelles entre canvas et rasters, rendu du texte modifié (écran et export).
import { raster, toRgba } from '../core/imaging.js';
import { Rect } from '../core/geometry.js';
import { editedFields, paperColor } from '../core/exports.js';
import { allLines } from '../core/model.js';
import { baseBox, extraFor } from '../core/layout.js';
import { lotLabels } from '../core/lots.js';
import { framedTables, drawLotColumns } from '../core/lotcolumn.js';

export function makeCanvas(w, h) {
  const c = document.createElement('canvas');
  c.width = w; c.height = h;
  return c;
}

export function rasterToCanvas(r) {
  const rgba = toRgba(r);
  const c = makeCanvas(rgba.w, rgba.h);
  c.getContext('2d').putImageData(new ImageData(new Uint8ClampedArray(rgba.data), rgba.w, rgba.h), 0, 0);
  return c;
}

/** iPhone/iPad : la mémoire des images n'est rendue qu'en vidant le canvas, pas en l'oubliant. */
export function freeCanvas(c) {
  if (c) { c.width = 0; c.height = 0; }
}

export function canvasToRaster(c) {
  const d = c.getContext('2d', { willReadFrequently: true }).getImageData(0, 0, c.width, c.height);
  return raster(c.width, c.height, 4, d.data);
}

export const canvasToBlob = (c, type, quality) => new Promise((ok, ko) => c.toBlob((b) => (b ? ok(b) : ko(new Error('Image non créée.'))), type, quality));

// ---------- Texte ----------

export const LINE_HEIGHT = 1.15;
export const font = (size) => `${size}px "Times New Roman", Times, serif`;
const meter = makeCanvas(1, 1).getContext('2d');

export function measure(text, size) {
  meter.font = font(size);
  return meter.measureText(text).width;
}

/** Taille qui redonne la largeur du texte imprimé, bornée autour de l'estimation du cœur. */
export function baseSize(f, boxHeight = f.Box.Height) {
  let size = f.FontSize;
  if (!Rect.isEmpty(f.Ink) && f.OriginalText.length > 0) {
    const w100 = measure(f.OriginalText, 100);
    if (w100 > 0) size = Math.min(1.3 * f.FontSize, Math.max(0.7 * f.FontSize, (100 * f.Ink.Width) / w100));
  }
  return Math.max(5, Math.min(size, boxHeight / LINE_HEIGHT));
}

/** Désignation (1re colonne) : le texte passe à la ligne, même taille d'écriture ; les valeurs restent sur une ligne. */
export const isMultiline = (f) => f.Column === 0;

/** Coupe le texte en lignes qui tiennent dans la largeur (retours à la ligne tapés respectés). */
export function wrapText(text, size, width) {
  const out = [];
  for (const para of text.replace(/\r\n?/g, '\n').split('\n')) {
    let cur = '';
    for (const word of para.split(' ')) {
      const next = cur.length === 0 ? word : cur + ' ' + word;
      if (measure(next, size) <= width || cur.length === 0 && measure(word, size) <= width) { cur = next; continue; }
      if (cur.length > 0) out.push(cur);
      // Mot plus long que la case : coupé en morceaux.
      let rest = word;
      while (measure(rest, size) > width && rest.length > 1) {
        let k = rest.length - 1;
        while (k > 1 && measure(rest.slice(0, k), size) > width) k--;
        out.push(rest.slice(0, k));
        rest = rest.slice(k);
      }
      cur = rest;
    }
    out.push(cur);
  }
  return out;
}

/**
 * Mise en page du texte d'une case : taille, lignes, position de la 1re ligne (haut, dans la case d'origine).
 * Désignation : même taille que l'imprimé, plusieurs lignes. Valeurs : une ligne, réduite si besoin.
 */
export function textLayout(line, f, text = f.Text) {
  const b = baseBox(line, f);
  const base = baseSize(f, b.Height);
  let size = base, lines = [text];
  if (isMultiline(f)) lines = wrapText(text, base, Math.max(4, f.Box.Width - f.Indent - 3));
  else size = fitSize(f, text, base);
  const center = Rect.isEmpty(f.Ink) ? Rect.cy(b) : Rect.cy(f.Ink);
  const top = Math.min(Math.max(center - 0.58 * size, b.Y), Math.max(b.Y, Rect.bottom(b) - LINE_HEIGHT * size));
  return { size, lines, top, baseHeight: b.Height };
}

/** Hauteur à ajouter sous une ligne pour que le texte de toutes ses cases tienne. */
export function lineExtra(line) {
  let extra = 0;
  for (const f of line.Cells) {
    if (!isMultiline(f) || f.Locked) continue;
    const t = textLayout(line, f);
    extra = Math.max(extra, extraFor(line, f, t.lines.length, t.size));
  }
  return extra;
}

/** Réduit la police jusqu'à ce que le texte tienne dans sa case. */
export function fitSize(f, text, base = baseSize(f)) {
  let size = base;
  const avail = Math.max(4, f.Box.Width - f.Indent - 3);
  while (size > 5 && measure(text || ' ', size) > avail) size -= 0.5;
  return size;
}

/**
 * Page redressée + colonne des lots tracée + cases modifiées et numéros de lot écrits
 * (fond couleur papier, texte noir), pour l'export JPEG.
 */
export function renderEdits(page, flat, lotFrame = true) {
  const framed = lotFrame ? framedTables(page) : [];
  const c = rasterToCanvas(framed.length > 0 ? drawLotColumns(flat, page) : flat);
  const ctx = c.getContext('2d');
  const edited = new Set(editedFields(page));
  for (const line of allLines(page))
    for (const f of line.Cells) {
      if (!edited.has(f)) continue;
      const [r, g, b] = paperColor(flat, f.Box);
      ctx.fillStyle = `rgb(${r},${g},${b})`;
      ctx.fillRect(f.Box.X, f.Box.Y, f.Box.Width, f.Box.Height);
      if (f.Text.trim().length === 0) continue;
      const t = textLayout(line, f);
      ctx.save();
      ctx.beginPath();
      ctx.rect(f.Box.X, f.Box.Y, f.Box.Width, f.Box.Height);
      ctx.clip();
      ctx.font = font(t.size);
      ctx.fillStyle = '#000';
      ctx.textBaseline = 'alphabetic';
      t.lines.forEach((text, i) => {
        const w = ctx.measureText(text).width;
        const x = f.Align === 'Right' ? Rect.right(f.Box) - f.Indent - w : f.Align === 'Center' ? f.Box.X + (f.Box.Width - w) / 2 : f.Box.X + f.Indent;
        // Même position qu'à l'écran : ligne de base à ~0,9 taille sous le haut de chaque ligne.
        ctx.fillText(text, x, t.top + i * LINE_HEIGHT * t.size + 0.91 * t.size);
      });
      ctx.restore();
    }
  // Numéros de lot : centrés dans la colonne, à la hauteur de l'objet, même taille que son texte, en noir.
  for (const { line, table, box } of lotLabels(page)) {
    if (!framed.includes(table)) {
      const [r, g, b] = paperColor(flat, box);
      ctx.fillStyle = `rgb(${r},${g},${b})`;
      ctx.fillRect(box.X, box.Y, box.Width, box.Height);
    }
    const size = lotSize(line, box);
    ctx.font = font(size);
    const w = ctx.measureText(line.Lot).width;
    ctx.fillStyle = '#000';
    ctx.textBaseline = 'alphabetic';
    ctx.fillText(line.Lot, box.X + (box.Width - w) / 2, Math.min(Rect.bottom(box) - 0.2 * size, Rect.cy(box) + 0.33 * size));
  }
  const out = canvasToRaster(c);
  freeCanvas(c);
  return out;
}

/** Taille du numéro de lot : celle du texte de l'objet, réduite si la colonne est étroite. */
export function lotSize(line, box) {
  const c = line.Cells[0];
  let size = Math.max(5, Math.min(baseSize(c, c.Box.Height - (line.Extra ?? 0)), box.Height / LINE_HEIGHT));
  while (size > 5 && measure(line.Lot || ' ', size) > box.Width - 2) size -= 0.5;
  return size;
}
