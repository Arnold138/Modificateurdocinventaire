// Passerelles entre canvas et rasters, rendu du texte modifié (écran et export).
import { raster, toRgba } from '../core/imaging.js';
import { isModified } from '../core/model.js';
import { Rect } from '../core/geometry.js';
import { editedFields, paperColor } from '../core/exports.js';
import { strikeColor, colorRuns, hasColors, textColor } from '../core/strikes.js';
import { allLines } from '../core/model.js';
import { baseBox, extraFor, tallExtra } from '../core/layout.js';
import { lotLabels } from '../core/lots.js';
import { framedTables, drawLotColumns, closeTables } from '../core/lotcolumn.js';

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
export const font = (size, bold = false) => `${bold ? 'bold ' : ''}${size}px "Times New Roman", Times, serif`;
const meter = makeCanvas(1, 1).getContext('2d');

/** Lignes imprimées en gras : totaux et en-têtes (« TOTAL STOCK », « II - STOCK »). */
/** Gras : totaux et en-têtes imprimés ; ligne TOTAL ajoutée : seulement si « En gras » est coché. */
export const isBoldLine = (line) => (line?.TotalScope ? !!line.TotalScope.Bold : line?.Kind === 'Total' || line?.Kind === 'EnTete');

export function measure(text, size, bold = false) {
  meter.font = font(size, bold);
  return meter.measureText(text).width;
}

/** Taille qui redonne la largeur du texte imprimé, bornée autour de l'estimation du cœur. */
export function baseSize(f, boxHeight = f.Box.Height, bold = false) {
  let size = f.FontSize;
  if (!Rect.isEmpty(f.Ink) && f.OriginalText.length > 0) {
    const w100 = measure(f.OriginalText, 100, bold);
    if (w100 > 0) size = Math.min(1.3 * f.FontSize, Math.max(0.7 * f.FontSize, (100 * f.Ink.Width) / w100));
  }
  return Math.max(5, Math.min(size, boxHeight / LINE_HEIGHT));
}

/** Désignation (1re colonne) : le texte passe à la ligne, même taille d'écriture ; les valeurs restent sur une ligne. */
export const isMultiline = (f) => f.Column === 0;

/** Coupe le texte en lignes qui tiennent dans la largeur (retours à la ligne tapés respectés). */
export function wrapText(text, size, width, bold = false) {
  const measure2 = (t, z) => measure(t, z, bold);
  const out = [];
  for (const para of text.replace(/\r\n?/g, '\n').split('\n')) {
    let cur = '';
    for (const word of para.split(' ')) {
      const next = cur.length === 0 ? word : cur + ' ' + word;
      if (measure2(next, size) <= width || cur.length === 0 && measure2(word, size) <= width) { cur = next; continue; }
      if (cur.length > 0) out.push(cur);
      // Mot plus long que la case : coupé en morceaux.
      let rest = word;
      while (measure2(rest, size) > width && rest.length > 1) {
        let k = rest.length - 1;
        while (k > 1 && measure2(rest.slice(0, k), size) > width) k--;
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
  const bold = isBoldLine(line);
  const base = baseSize(f, b.Height, bold);
  let size = base, lines = [text];
  if (isMultiline(f)) lines = wrapText(text, base, Math.max(4, f.Box.Width - f.Indent - 3), bold);
  else size = fitSize(f, text, base, bold);
  const center = Rect.isEmpty(f.Ink) ? Rect.cy(b) : Rect.cy(f.Ink);
  const top = Math.min(Math.max(center - 0.58 * size, b.Y), Math.max(b.Y, Rect.bottom(b) - LINE_HEIGHT * size));
  return { size, lines, top, baseHeight: b.Height, bold };
}

// ---------- Écriture de l'imprimé (export) ----------

const median = (v) => { const s = [...v].sort((a, b) => a - b); return s.length ? s[Math.trunc(s.length / 2)] : null; };
const grayAt = (img, x, y) => { const o = (y * img.w + x) * img.ch, d = img.data; return img.ch === 1 ? d[o] : (d[o] * 77 + d[o + 1] * 150 + d[o + 2] * 29) >> 8; };

/** Épaisseur des traits (largeur médiane des passages sombres) sur des lignes horizontales d'une zone. */
function stemsIn(img, x0, x1, ys, out, max) {
  for (const y of ys) {
    if (y < 0 || y >= img.h) continue;
    let run = 0;
    for (let x = Math.max(0, x0); x <= Math.min(img.w - 1, x1); x++) {
      if (grayAt(img, x, y) < 140) run++;
      else { if (run > 0 && run <= max) out.push(run); run = 0; }
    }
  }
}

/**
 * Taille de l'écriture d'après la hauteur des minuscules sur le scan : la bande la plus dense (de « a », « e », « n »…)
 * va de la hauteur x à la ligne de base ; Times New Roman a une hauteur x de 0,448 de sa taille.
 */
function xHeightSize(img, ink) {
  const xh = xHeightPx(img, ink);
  return xh === null ? null : (xh * 100) / timesRef().xh;
}

/** Hauteur x (px) d'un morceau d'écriture : bande des lignes de pixels les plus denses. */
function xHeightPx(img, ink) {
  const x0 = Math.max(0, Math.floor(ink.X)), x1 = Math.min(img.w - 1, Math.ceil(Rect.right(ink)));
  const y0 = Math.max(0, Math.floor(ink.Y)), y1 = Math.min(img.h - 1, Math.ceil(Rect.bottom(ink)));
  const rows = [];
  for (let y = y0; y <= y1; y++) { let n = 0; for (let x = x0; x <= x1; x++) if (grayAt(img, x, y) < 140) n++; rows.push(n); }
  const max = Math.max(...rows);
  if (max < 4) return null;
  let top = rows.findIndex((n) => n >= 0.5 * max), bottom = rows.length - 1;
  while (bottom > top && rows[bottom] < 0.5 * max) bottom--;
  const xh = bottom - top + 1;
  return xh >= 4 ? xh : null;
}

let tnrRef = null;
/**
 * Écriture de l'app (Times New Roman de l'appareil) à 100 px, mesurée comme le scan : hauteur x et épaisseur des traits.
 * Mesurée une fois sur un texte d'essai : rien ne dépend de la police exacte installée.
 */
function timesRef() {
  if (tnrRef) return tnrRef;
  const c = makeCanvas(1400, 170), x = c.getContext('2d', { willReadFrequently: true });
  x.fillStyle = '#fff'; x.fillRect(0, 0, 1400, 170);
  x.fillStyle = '#000'; x.font = font(100); x.textBaseline = 'alphabetic';
  const sample = '1 armoire en métal avec 15 casiers';
  x.fillText(sample, 10, 130);
  const img = canvasToRaster(c);
  freeCanvas(c);
  const w = Math.min(1380, x.measureText(sample).width);
  const xh = xHeightPx(img, { X: 10, Y: 20, Width: w, Height: 140 }) ?? 45;
  const runs = [];
  stemsIn(img, 10, 10 + w, [130 - Math.round(0.1 * 100 * xh / 45), 130 - Math.round(0.18 * 100 * xh / 45), 130 - Math.round(0.26 * 100 * xh / 45)], runs, 20);
  tnrRef = { xh, stem: median(runs) ?? 9 };
  return tnrRef;
}

const profiles = new WeakMap();
/**
 * Écriture de l'imprimé sur cette page, mesurée sur le scan : lettres plus hautes que Times New Roman à largeur égale
 * (sx : facteur de largeur), traits plus épais (weight, en px pour une taille donnée) et couleur de l'encre. Le texte
 * retapé est écrit de la même façon : même hauteur, même largeur, même épaisseur qu'une ligne imprimée.
 */
export function inkProfile(page, img) {
  const cached = profiles.get(img);
  if (cached) return cached;
  const ratios = [], stems = [], inks = [];
  for (const line of allLines(page)) {
    if (line.Kind !== 'Article' && line.Kind !== 'Titre') continue;
    const f = line.Cells[0];
    if (!f || f.Text !== f.OriginalText || f.OriginalText.trim().length < 6 || Rect.isEmpty(f.Ink)) continue;
    // Surtout des minuscules (la bande la plus dense est alors celle des minuscules : hauteur x).
    const letters = f.OriginalText.match(/\p{L}/gu) ?? [];
    if (letters.length < 5 || letters.filter((c) => c !== c.toUpperCase()).length < 0.7 * letters.length) continue;
    const hs = xHeightSize(img, f.Ink); // taille d'après la hauteur des minuscules (x) mesurée sur le scan
    if (hs === null) continue;
    const ws = baseSize(f, f.Box.Height + 1000, false); // taille qui redonne la largeur imprimée
    if (hs < 8) continue;
    ratios.push(ws / hs);
    const y0 = Math.round(Rect.bottom(f.Ink));
    const before = stems.length;
    stemsIn(img, Math.floor(f.Ink.X), Math.ceil(Rect.right(f.Ink)), [y0 - Math.round(0.1 * hs), y0 - Math.round(0.18 * hs), y0 - Math.round(0.26 * hs)], stems, Math.round(0.2 * hs));
    for (let k = before; k < stems.length; k++) stems[k] /= hs; // en fraction de la taille
    for (let x = Math.floor(f.Ink.X); x < Rect.right(f.Ink); x += 2) { const g = grayAt(img, x, y0 - Math.round(0.18 * hs)); if (g < 90) inks.push(g); }
  }
  const r = Math.min(1.2, Math.max(0.8, median(ratios) ?? 1));
  const sx = Math.abs(1 - r) < 0.05 ? 1 : r; // écart faible : les lettres gardent leur forme
  const sorted = [...stems].sort((a, b) => a - b);
  const stem = sorted.length ? sorted[Math.trunc(sorted.length * 0.4)] : null; // fraction de la taille (traits fins comptés)
  const ink = Math.min(60, median(inks) ?? 0);
  const p = { sx, stem, ink, n: ratios.length };
  profiles.set(img, p);
  return p;
}

/**
 * Écrit `text` en (x, y = ligne de base) comme l'imprimé : taille `size` pour la largeur (mise en page inchangée), lettres
 * rehaussées de 1/sx et traits épaissis jusqu'à l'épaisseur mesurée sur le scan.
 */
function inkText(ctx, profile, text, x, y, size, bold, color) {
  const sx = profile?.n >= 3 ? profile.sx : 1;
  const h = size / sx;
  ctx.save();
  ctx.translate(x, y);
  ctx.scale(sx, 1);
  ctx.font = font(h, bold);
  ctx.fillStyle = color;
  ctx.fillText(text, 0, 0);
  if (profile?.n >= 3 && profile.stem) {
    const want = profile.stem * h, have = (timesRef().stem * h) / 100 * (bold ? 1.6 : 1);
    // Le trait s'ajoute de part et d'autre : moitié de l'écart (et un peu moins : le scan est flou, il paraît plus gras).
    const extra = Math.min(0.04 * h, 0.6 * (want - have));
    if (extra > 0.3) { ctx.lineWidth = extra; ctx.lineJoin = 'round'; ctx.strokeStyle = color; ctx.strokeText(text, 0, 0); }
  }
  ctx.restore();
}

/** Couleur du texte retapé : noir de l'encre du scan (le noir pur se voit sur une photocopie). */
const inkColor = (color, profile) => { const hex = textColor(color); return hex === '#000000' && profile?.n >= 3 ? `rgb(${profile.ink},${profile.ink},${profile.ink})` : hex; };

/** Lignes de texte affichées et position de leur 1er caractère dans le texte de la case. */
function linesWithOffsets(text, lines) {
  let cursor = 0;
  return lines.map((l) => {
    const at = text.indexOf(l, cursor);
    const start = at < 0 ? cursor : at;
    cursor = start + l.length;
    return { text: l, start };
  });
}

/**
 * Traits des morceaux barrés : { x0, x1, y, thick, color }.
 * Texte d'origine non retouché : le trait est posé sur l'écriture du scan (position des lettres estimée
 * d'après la largeur de l'encre) ; texte retapé : sur le texte écrit par l'app.
 */
export function strikeSegments(line, f) {
  if (!f.Strikes?.length || f.Locked) return [];
  const out = [];
  const add = (x0, x1, baseline, size, color) => {
    if (x1 - x0 < 0.5) return;
    out.push({ x0, x1, y: baseline - 0.28 * size, thick: Math.max(1.5, 0.06 * size), color: strikeColor(color) });
  };
  const b = baseBox(line, f);
  const bold = isBoldLine(line);
  if (f.Text === f.OriginalText && !Rect.isEmpty(f.Ink) && !f.Text.includes('\n')) {
    const size = baseSize(f, b.Height, bold);
    const total = measure(f.Text, size, bold) || 1;
    const k = f.Ink.Width / total;
    const baseline = Rect.bottom(f.Ink) - (/[gjpqyç,;()]/.test(f.Text) ? 0.21 * size : 0);
    for (const s of f.Strikes)
      add(f.Ink.X + measure(f.Text.slice(0, s.Start), size, bold) * k, f.Ink.X + measure(f.Text.slice(0, s.Start + s.Length), size, bold) * k, baseline, size, s.Color);
    return out;
  }
  const t = textLayout(line, f);
  linesWithOffsets(f.Text, t.lines).forEach((l, i) => {
    const w = measure(l.text, t.size, bold);
    const x = f.Align === 'Right' ? Rect.right(f.Box) - f.Indent - w : f.Align === 'Center' ? f.Box.X + (f.Box.Width - w) / 2 : f.Box.X + f.Indent;
    const baseline = t.top + i * LINE_HEIGHT * t.size + 0.91 * t.size;
    for (const s of f.Strikes) {
      const a = Math.max(s.Start, l.start), e = Math.min(s.Start + s.Length, l.start + l.text.length);
      if (e <= a) continue;
      add(x + measure(l.text.slice(0, a - l.start), t.size, bold), x + measure(l.text.slice(0, e - l.start), t.size, bold), baseline, t.size, s.Color);
    }
  });
  return out;
}

/** Hauteur à ajouter sous une ligne pour que le texte de toutes ses cases tienne. */
export function lineExtra(line) {
  let extra = 0;
  for (const f of line.Cells) {
    if (!isMultiline(f) || f.Locked) continue;
    const t = textLayout(line, f);
    extra = Math.max(extra, extraFor(line, f, t.lines.length, t.size));
  }
  return Math.max(extra, tallExtra(line));
}

/** Réduit la police jusqu'à ce que le texte tienne dans sa case. */
export function fitSize(f, text, base = baseSize(f), bold = false) {
  let size = base;
  const avail = Math.max(4, f.Box.Width - f.Indent - 3);
  while (size > 5 && measure(text || ' ', size, bold) > avail) size -= 0.5;
  return size;
}

/**
 * Fond tramé (gris des totaux et des en-têtes) : un morceau propre de la même rangée, sans texte ni filet,
 * à répéter sous le texte retapé (un aplat blanc se verrait). Null si le fond est du papier uni.
 * Rectangle en pixels de la page d'origine.
 */
export function cleanPatch(img, page, line, f, dy = 0) {
  const b = { ...f.Box, Y: f.Box.Y + dy };
  const x0 = Math.max(0, Math.floor(b.X)), x1 = Math.min(img.w - 1, Math.ceil(Rect.right(b)) - 1);
  const y0 = Math.max(0, Math.floor(b.Y)), y1 = Math.min(img.h - 1, Math.ceil(Rect.bottom(b)) - 1);
  if (x1 <= x0 || y1 <= y0) return null;
  const g = (x, y) => { const o = (y * img.w + x) * img.ch, d = img.data; return img.ch === 1 ? d[o] : (d[o] * 77 + d[o + 1] * 150 + d[o + 2] * 29) >> 8; };
  let sum = 0, n = 0;
  for (let x = x0; x <= x1; x += 2) { sum += g(x, y0) + g(x, y1); n += 2; }
  if (sum / n >= 245) return null; // papier uni
  const table = page.Tables.find((t) => t.Rows.some((r) => r.Lines.includes(line)));
  if (!table?.ColumnXs?.length) return null;
  const row = table.Rows.find((r) => r.Lines.includes(line));
  const ink = row.Lines.flatMap((l) => l.Cells).filter((c) => !Rect.isEmpty(c.Ink)).map((c) => ({ ...c.Ink, Y: c.Ink.Y + dy }));
  const h = y1 - y0 + 1, pw = Math.min(48, x1 - x0 + 1);
  let best = null, bestSum = -1;
  for (let x = Math.ceil(table.ColumnXs[0]) + 4; x + pw < table.ColumnXs[table.ColumnXs.length - 1] - 4; x += 2) {
    if (table.ColumnXs.some((cx) => cx > x - 5 && cx < x + pw + 5)) continue;
    if (ink.some((i) => i.X < x + pw + 2 && x - 2 < Rect.right(i) && i.Y < y0 + h && y0 < Rect.bottom(i))) continue;
    let s2 = 0;
    for (let y = y0; y <= y1; y += 2) for (let xx = x; xx < x + pw; xx += 2) s2 += g(xx, y);
    if (s2 > bestSum) { bestSum = s2; best = { X: x, Y: y0, Width: pw, Height: h }; }
  }
  return best;
}

/** Remplit `box` en répétant le morceau `patch` de `source` (canvas). */
export function fillPatch(ctx, source, patch, box) {
  for (let x = box.X; x < Rect.right(box); x += patch.Width) {
    const w = Math.min(patch.Width, Rect.right(box) - x);
    ctx.drawImage(source, patch.X, patch.Y, w, patch.Height, x, box.Y, w, Math.min(patch.Height, box.Height));
  }
}

/**
 * Écriture d'origine mise en couleur (texte non retapé) : les pixels d'encre du morceau prennent la couleur choisie,
 * le papier reste tel quel (« éclaircir » : on garde, canal par canal, le plus clair des deux).
 */
export function tintColors(page, img) {
  const fields = [...allLines(page)].flatMap((l) => l.Cells.map((f) => [l, f]))
    .filter(([, f]) => hasColors(f) && !f.Locked && !isModified(f) && !Rect.isEmpty(f.Ink) && !f.Text.includes('\n'));
  if (fields.length === 0) return img;
  const out = toRgba(img);
  const data = out === img ? new Uint8ClampedArray(img.data) : out.data;
  for (const [line, f] of fields) {
    const bold = isBoldLine(line);
    const size = baseSize(f, baseBox(line, f).Height, bold);
    const total = measure(f.Text, size, bold) || 1;
    const k = f.Ink.Width / total;
    for (const r of colorRuns(f)) {
      if (!r.color) continue;
      const hex = textColor(r.color);
      const rgb = [1, 3, 5].map((i) => parseInt(hex.slice(i, i + 2), 16));
      const x0 = Math.max(0, Math.floor(f.Ink.X + measure(f.Text.slice(0, r.start), size, bold) * k) - 1);
      const x1 = Math.min(out.w, Math.ceil(f.Ink.X + measure(f.Text.slice(0, r.end), size, bold) * k) + 1);
      const y0 = Math.max(0, Math.floor(f.Ink.Y) - 2), y1 = Math.min(out.h, Math.ceil(Rect.bottom(f.Ink)) + 2);
      for (let y = y0; y < y1; y++)
        for (let x = x0; x < x1; x++) {
          const o = (y * out.w + x) * 4;
          for (let c = 0; c < 3; c++) data[o + c] = Math.max(data[o + c], rgb[c]);
        }
    }
  }
  return raster(out.w, out.h, 4, data);
}

/** Image d'une page avant les corrections : colonne des lots tracée et écriture d'origine mise en couleur. */
export function pageRaster(page, stretched, lotFrame = true) {
  const closed = closeTables(stretched, page);
  const img = lotFrame && framedTables(page).length > 0 ? drawLotColumns(closed, page) : closed;
  return tintColors(page, img);
}

/**
 * Page redressée + colonne des lots tracée + cases modifiées et numéros de lot écrits
 * (fond couleur papier, texte noir ou en couleur), pour l'export JPEG.
 */
export function renderEdits(page, flat, lotFrame = true) {
  const framed = lotFrame ? framedTables(page) : [];
  const c = rasterToCanvas(pageRaster(page, flat, lotFrame));
  const ctx = c.getContext('2d');
  const original = rasterToCanvas(flat);
  const edited = new Set(editedFields(page));
  const prof = inkProfile(page, flat);
  for (const line of allLines(page))
    for (const f of line.Cells) {
      if (!edited.has(f)) continue;
      const patch = cleanPatch(flat, page, line, f);
      if (patch) fillPatch(ctx, original, patch, f.Box);
      else {
        const [r, g, b] = paperColor(flat, f.Box);
        ctx.fillStyle = `rgb(${r},${g},${b})`;
        ctx.fillRect(f.Box.X, f.Box.Y, f.Box.Width, f.Box.Height);
      }
      if (f.Text.trim().length === 0) continue;
      const t = textLayout(line, f);
      ctx.save();
      ctx.beginPath();
      ctx.rect(f.Box.X, f.Box.Y, f.Box.Width, f.Box.Height);
      ctx.clip();
      ctx.font = font(t.size, t.bold);
      ctx.textBaseline = 'alphabetic';
      linesWithOffsets(f.Text, t.lines).forEach((l, i) => {
        const w = ctx.measureText(l.text).width;
        let x = f.Align === 'Right' ? Rect.right(f.Box) - f.Indent - w : f.Align === 'Center' ? f.Box.X + (f.Box.Width - w) / 2 : f.Box.X + f.Indent;
        // Même position qu'à l'écran : ligne de base à ~0,9 taille sous le haut de chaque ligne.
        const y = t.top + i * LINE_HEIGHT * t.size + 0.91 * t.size;
        for (const run of colorRuns(f, l.start, l.start + l.text.length)) {
          const part = l.text.slice(run.start - l.start, run.end - l.start);
          inkText(ctx, prof, part, x, y, t.size, t.bold, inkColor(run.color, prof));
          ctx.font = font(t.size, t.bold);
          x += ctx.measureText(part).width;
        }
      });
      ctx.restore();
    }
  // Texte barré : un trait fin au milieu des lettres, rouge ou noir.
  for (const line of allLines(page))
    for (const f of line.Cells)
      for (const g of strikeSegments(line, f)) {
        ctx.fillStyle = g.color;
        ctx.fillRect(g.x0, g.y - g.thick / 2, g.x1 - g.x0, g.thick);
      }
  // Numéros de lot : centrés dans la colonne, à la hauteur de l'objet, même taille que son texte, en noir.
  for (const { line, table, box } of lotLabels(page)) {
    if (!line.Lot) continue; // case vide (lots remplis à la main) : rien à écrire
    if (!framed.includes(table)) {
      const [r, g, b] = paperColor(flat, box);
      ctx.fillStyle = `rgb(${r},${g},${b})`;
      ctx.fillRect(box.X, box.Y, box.Width, box.Height);
    }
    const size = lotSize(line, box);
    ctx.font = font(size);
    const w = ctx.measureText(line.Lot).width;
    ctx.textBaseline = 'alphabetic';
    inkText(ctx, prof, line.Lot, box.X + (box.Width - w) / 2, Math.min(Rect.bottom(box) - 0.2 * size, Rect.cy(box) + 0.33 * size), size, false, inkColor(null, prof));
  }
  const out = canvasToRaster(c);
  freeCanvas(c);
  freeCanvas(original);
  return out;
}

/** Taille du numéro de lot : celle du texte de l'objet, réduite si la colonne est étroite. */
export function lotSize(line, box) {
  const c = line.Cells[0];
  let size = Math.max(5, Math.min(baseSize(c, c.Box.Height - (line.Extra ?? 0)), box.Height / LINE_HEIGHT));
  while (size > 5 && measure(line.Lot || ' ', size) > box.Width - 2) size -= 0.5;
  return size;
}

