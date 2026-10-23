// Passerelles entre canvas et rasters, rendu du texte modifié (écran et export).
import { raster, toRgba } from '../core/imaging.js';
import { Rect } from '../core/geometry.js';
import { editedFields, paperColor } from '../core/exports.js';
import { lotLabels } from '../core/lots.js';

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
export function baseSize(f) {
  let size = f.FontSize;
  if (!Rect.isEmpty(f.Ink) && f.OriginalText.length > 0) {
    const w100 = measure(f.OriginalText, 100);
    if (w100 > 0) size = Math.min(1.3 * f.FontSize, Math.max(0.7 * f.FontSize, (100 * f.Ink.Width) / w100));
  }
  return Math.max(5, Math.min(size, f.Box.Height / LINE_HEIGHT));
}

/** Réduit la police jusqu'à ce que le texte tienne dans sa case. */
export function fitSize(f, text, base = baseSize(f)) {
  let size = base;
  const avail = Math.max(4, f.Box.Width - f.Indent - 3);
  while (size > 5 && measure(text || ' ', size) > avail) size -= 0.5;
  return size;
}

/** Page redressée + cases modifiées dessinées (fond couleur papier, texte noir), pour l'export JPEG. */
export function renderEdits(page, flat) {
  const c = rasterToCanvas(flat);
  const ctx = c.getContext('2d');
  for (const f of editedFields(page)) {
    const [r, g, b] = paperColor(flat, f.Box);
    ctx.fillStyle = `rgb(${r},${g},${b})`;
    ctx.fillRect(f.Box.X, f.Box.Y, f.Box.Width, f.Box.Height);
    if (f.Text.trim().length === 0) continue;
    const size = fitSize(f, f.Text);
    ctx.font = font(size);
    const w = ctx.measureText(f.Text).width;
    const x = f.Align === 'Right' ? Rect.right(f.Box) - f.Indent - w : f.Align === 'Center' ? f.Box.X + (f.Box.Width - w) / 2 : f.Box.X + f.Indent;
    const center = Rect.isEmpty(f.Ink) ? Rect.cy(f.Box) : Rect.cy(f.Ink);
    const baseline = Math.min(Rect.bottom(f.Box) - 0.2 * size, Math.max(f.Box.Y + 0.9 * size, center + 0.33 * size));
    ctx.save();
    ctx.beginPath();
    ctx.rect(f.Box.X, f.Box.Y, f.Box.Width, f.Box.Height);
    ctx.clip();
    ctx.fillStyle = '#000';
    ctx.textBaseline = 'alphabetic';
    ctx.fillText(f.Text, x, baseline);
    ctx.restore();
  }
  // Numéros de lot : dans la marge gauche, à la hauteur de l'objet, même taille que son texte, en noir.
  for (const { line, box } of lotLabels(page)) {
    const [r, g, b] = paperColor(flat, box);
    ctx.fillStyle = `rgb(${r},${g},${b})`;
    ctx.fillRect(box.X, box.Y, box.Width, box.Height);
    const size = lotSize(line, box);
    ctx.font = font(size);
    const w = ctx.measureText(line.Lot).width;
    ctx.fillStyle = '#000';
    ctx.textBaseline = 'alphabetic';
    ctx.fillText(line.Lot, Rect.right(box) - lotGap(size) - w, Math.min(Rect.bottom(box) - 0.2 * size, Rect.cy(box) + 0.33 * size));
  }
  const out = canvasToRaster(c);
  freeCanvas(c);
  return out;
}

/** Espace entre le numéro et le bord du tableau. */
export const lotGap = (size) => Math.max(2, 0.35 * size);

/** Taille du numéro de lot : celle du texte de l'objet, réduite si la marge est étroite. */
export function lotSize(line, box) {
  let size = Math.max(5, Math.min(baseSize(line.Cells[0]), box.Height / LINE_HEIGHT));
  while (size > 5 && measure(line.Lot, size) > box.Width - 1 - lotGap(size)) size -= 0.5;
  return size;
}
