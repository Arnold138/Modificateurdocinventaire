// Exports : texte (.txt) et image (JPEG). Portage de TxtExporter.cs et ImageExporter.cs.
import { Rect, Quad, pt } from './geometry.js';
import { homography, homographyPts, toRgba, raster } from './imaging.js';
import { allFields, firstText, isModified } from './model.js';
import { lotsActive, isZeroLot, hasLotCell, orderedRows, rowOrder } from './lots.js';
import { lotRegions } from './lotcolumn.js';
import { deletedLines, planArrangement } from './arrange.js';

// ---------- Texte ----------

const SPACED_NUMBER = /^[+-]?\d{1,3}(?:[   ]\d{3})+(?:,\d+)?$/;
const PLAIN_NUMBER = /^[+-]?\d+(?:[.,]\d+)?$/;

export const cleanText = (s) => s.replace(/[\t\r\n]/g, ' ').replace(/ {2,}/g, ' ').trim();
export function cleanValue(s) {
  const t = cleanText(s);
  return SPACED_NUMBER.test(t) ? t.replace(/[   ]/g, '') : t;
}

const hasValues = (l) => l.Cells.slice(1).some((c) => c.Text.trim().length > 0);

function exportRow(row, lots, gone = new Set()) {
  // Avec les lots : une 1re colonne « Lot » sur toutes les lignes, pour garder les colonnes alignées.
  const prefix = (l) => (!lots ? '' : (l.Kind === 'EnTete' ? 'Lot' : l.Lot ?? '') + '\t');
  // Lignes qui partent en fin d'inventaire : l'objet au lot 0 et, s'il est le seul objet de la rangée, sa suite.
  const objects = row.Lines.filter((l) => (l.Lot ?? '').length > 0);
  const allZero = lots && objects.length > 0 && objects.every(isZeroLot);
  const zero = (l) => lots && (isZeroLot(l) || (allZero && l.Kind === 'Article' && (l.Lot ?? '').length === 0));
  const lines = row.Lines.filter((l) => l.Kind !== 'Ignoree' && !gone.has(l));
  const moved = new Map();
  const titlesKeeping = new Set();
  lines.forEach((l, i) => {
    if (l.Kind !== 'Titre' || !hasValues(l)) return;
    const target = lines.slice(i + 1).find((x) => x.Kind !== 'Titre' && !hasValues(x) && !moved.has(x));
    if (!target) titlesKeeping.add(l);
    else moved.set(target, l.Cells.slice(1).map((c) => c.Text));
  });
  const out = [];
  // Lignes d'un objet numéroté (l'objet et sa suite) : les lots 0 se placent après la dernière.
  let inObject = false;
  const objectPart = (l) => {
    if (hasLotCell(l)) inObject = !isZeroLot(l);
    else if (l.Kind !== 'Article') inObject = false;
    return inObject;
  };
  for (const line of lines) {
    const obj = lots && objectPart(line);
    if (line.Kind === 'Titre' && !titlesKeeping.has(line)) {
      const t = cleanText(firstText(line));
      if (t.length > 0) out.push({ text: prefix(line) + t, zero: zero(line), obj, line });
      continue;
    }
    const cells = line.Cells.map((c) => c.Text);
    const values = moved.get(line);
    if (values) for (let c = 1; c < cells.length && c - 1 < values.length; c++) cells[c] = values[c - 1];
    const parts = cells.map((t, c) => (c === 0 ? cleanText(t) : cleanValue(t)));
    while (parts.length > 0 && parts[parts.length - 1].length === 0) parts.pop();
    if (parts.length > 0) out.push({ text: prefix(line) + parts.join('\t'), zero: zero(line), obj, line });
  }
  return out;
}

function looksLikeFormula(cell) {
  if (cell.length < 2) return false;
  const c = cell[0];
  if (c === '=' || c === '@') return true;
  if (c === '-' || c === '+') return !cell.startsWith('+/-') && !PLAIN_NUMBER.test(cell);
  return false;
}

/** Une ligne par ligne imprimée, tabulations, titres seuls, fins de ligne Windows. */
export function buildTxt(project) {
  const output = [];
  const lots = lotsActive(project);
  const zeroLines = []; // objets au lot 0 sans endroit connu : placés après le dernier lot
  let after = -1;       // fin du dernier objet numéroté
  let endAt = -1;       // page « lots de fin » (mode page dédiée) : les lots 0 y sont
  const gone = deletedLines(project);
  // Lots 0 recollés comme à l'écran : juste après la rangée où ils arrivent (fin de leur partie, ou endroit choisi).
  const full = lots ? planArrangement(project) : null;
  const plan = lots && project.EndLots?.Mode !== 'page' ? full : null;
  const unitOf = new Map();
  for (const u of plan?.moving ?? []) for (const l of u.lines) unitOf.set(l, u);
  // Tri par numéro de lot : les objets déplacés s'écrivent à leur nouvelle place, dans l'ordre des numéros.
  const sortOf = new Map();
  for (const u of full?.moving ?? []) if (u.kind === 'sort') for (const l of u.lines) sortOf.set(l, u);
  const sortText = new Map();
  const key = (p, ti, r) => `${p},${ti},${r}`;
  const pending = new Map();
  const pages = project.Pages.map((page, p) => page.Tables.map((table, ti) => rowOrder(table).map((r) => {
    const all = exportRow(table.Rows[r], lots, gone);
    for (const x of all) if (sortOf.has(x.line)) sortText.set(x.line, x);
    const entries = all.filter((x) => !sortOf.has(x.line));
    let dest = null;
    for (const x of entries.filter((e) => e.zero)) {
      dest = unitOf.get(x.line)?.dest ?? dest;
      if (dest) { const k = key(dest.p, dest.ti, dest.r); if (!pending.has(k)) pending.set(k, []); pending.get(k).push(x.text); }
      else zeroLines.push(x.text);
    }
    return { k: key(p, ti, r), kept: entries.filter((e) => !e.zero) };
  })));
  const sortPending = new Map();
  for (const u of full?.moving ?? []) {
    if (u.kind !== 'sort') continue;
    const k = key(u.dest.p, u.dest.ti, u.dest.r);
    if (!sortPending.has(k)) sortPending.set(k, []);
    for (const l of u.lines) { const x = sortText.get(l); if (x) sortPending.get(k).push(x); }
  }
  project.Pages.forEach((page, p) => {
    if (page.EndLots && project.EndLots?.Mode === 'page' && endAt < 0) endAt = output.length;
    page.Tables.forEach((table, ti) => {
      const rows = pages[p][ti];
      // Page ajoutée : un tableau encore vierge (seulement son en-tête) n'est pas exporté.
      const info = page.Added?.Tables?.[ti];
      if (info && !rows.some((x) => x.kept.some((e) => e.line.Kind !== 'EnTete') || pending.has(x.k) || sortPending.has(x.k))) return;
      const first = orderedRows(table).flatMap((r) => r.Lines).find((l) => l.Kind !== 'Ignoree');
      if (rows.some((x) => x.kept.length > 0) && first?.Kind === 'EnTete' && output.length > 0 && output[output.length - 1].length > 0) output.push('');
      for (const row of rows) {
        for (const x of row.kept) { output.push(x.text); if (x.obj) after = output.length; }
        for (const x of sortPending.get(row.k) ?? []) { output.push(x.text); if (x.obj) after = output.length; }
        for (const t of pending.get(row.k) ?? []) output.push(t);
      }
    });
  });
  output.splice(endAt >= 0 ? endAt : after < 0 ? output.length : after, 0, ...zeroLines);
  const warnings = [];
  output.forEach((l, i) => {
    for (const cell of l.split('\t'))
      if (looksLikeFormula(cell))
        warnings.push(`Ligne ${i + 1} : « ${cell.length <= 40 ? cell : cell.slice(0, 40) + '…'} » commence par « ${cell[0]} » — Excel pourrait le lire comme une formule.`);
  });
  return { text: output.length === 0 ? '' : output.join('\r\n') + '\r\n', lineCount: output.length, warnings };
}

/** Fichier .txt en UTF-8 avec BOM (Excel lit alors les accents correctement). */
export const txtBlob = (text) => new Blob(['﻿' + text], { type: 'text/plain;charset=utf-8' });

// ---------- Image ----------

export const editedFields = (page) => [...allFields(page)].filter((f) => isModified(f) && !f.Locked);

/** Champs qui portent un trait (texte barré). */
export const struckFields = (page) => [...allFields(page)].filter((f) => ((f.Strikes?.length ?? 0) > 0 || (f.Colors?.length ?? 0) > 0) && !f.Locked);

/** Couleur du papier autour d'une case (moyenne des pixels clairs du pourtour). */
export function paperColor(image, box) {
  const samples = [];
  const cl = (v, a, b) => Math.min(b, Math.max(a, v));
  const x0 = cl(Math.trunc(box.X), 0, image.w - 1), x1 = cl(Math.trunc(Rect.right(box)), 0, image.w - 1);
  const y0 = cl(Math.trunc(box.Y), 0, image.h - 1), y1 = cl(Math.trunc(Rect.bottom(box)), 0, image.h - 1);
  const add = (x, y) => {
    const i = (y * image.w + x) * image.ch;
    const r = image.data[i], g = image.ch === 4 ? image.data[i + 1] : r, b = image.ch === 4 ? image.data[i + 2] : r;
    samples.push([(r * 77 + g * 150 + b * 29) >> 8, r, g, b]);
  };
  for (let x = x0; x <= x1; x++) { add(x, y0); add(x, y1); }
  for (let y = y0; y <= y1; y++) { add(x0, y); add(x1, y); }
  if (samples.length === 0) return [255, 255, 255];
  samples.sort((a, b) => b[0] - a[0]);
  const light = samples.slice(0, Math.max(1, Math.trunc(samples.length / 2)));
  const mean = (k) => Math.trunc(light.reduce((s, v) => s + v[k], 0) / light.length);
  return [mean(1), mean(2), mean(3)];
}

/**
 * Recopie les cases modifiées de la page redressée (déjà retouchée) dans la photo d'origine.
 * Tout le reste de la photo est inchangé, pixel pour pixel.
 */
export function composeIntoSource(orientedSource, page, editedPage, lotFrame = true) {
  const src = toRgba(orientedSource), edited = toRgba(editedPage);
  const out = raster(src.w, src.h, 4, new Uint8ClampedArray(src.data));
  const boxes = [...editedFields(page), ...struckFields(page)].map((f) => f.Box).concat(lotRegions(page, lotFrame));
  if (boxes.length === 0) return out;
  const crop = page.Crop ?? Quad.full(src.w, src.h);
  const W = page.Width, H = page.Height;
  const a = (page.Skew * Math.PI) / 180, cos = Math.cos(a), sin = Math.sin(a), cx = W / 2, cy = H / 2;
  const toSource = homography(crop, W, H);
  const toFlat = homographyPts(Quad.points(crop), [pt(0, 0), pt(W - 1, 0), pt(W - 1, H - 1), pt(0, H - 1)]);
  const forward = (u, v) => { const du = u - cx, dv = v - cy; return toSource(cx + cos * du - sin * dv, cy + sin * du + cos * dv); };
  const backward = (x, y) => { const p = toFlat(x, y); const du = p.X - cx, dv = p.Y - cy; return pt(cx + cos * du + sin * dv, cy - sin * du + cos * dv); };

  for (const b of boxes) {
    const c = [forward(b.X, b.Y), forward(Rect.right(b), b.Y), forward(Rect.right(b), Rect.bottom(b)), forward(b.X, Rect.bottom(b))];
    const sx0 = Math.max(0, Math.floor(Math.min(...c.map((p) => p.X))) - 1), sx1 = Math.min(src.w - 1, Math.ceil(Math.max(...c.map((p) => p.X))) + 1);
    const sy0 = Math.max(0, Math.floor(Math.min(...c.map((p) => p.Y))) - 1), sy1 = Math.min(src.h - 1, Math.ceil(Math.max(...c.map((p) => p.Y))) + 1);
    for (let y = sy0; y <= sy1; y++)
      for (let x = sx0; x <= sx1; x++) {
        const q = backward(x, y);
        if (Rect.contains(b, q.X, q.Y)) sample(edited, q.X, q.Y, out.data, (y * src.w + x) * 4);
      }
  }
  return out;
}

function sample(img, x, y, dst, o) {
  x = Math.min(img.w - 1, Math.max(0, x)); y = Math.min(img.h - 1, Math.max(0, y));
  const x0 = Math.trunc(x), y0 = Math.trunc(y), x1 = Math.min(x0 + 1, img.w - 1), y1 = Math.min(y0 + 1, img.h - 1);
  const fx = x - x0, fy = y - y0, d = img.data;
  for (let c = 0; c < 4; c++) {
    const top = d[(y0 * img.w + x0) * 4 + c] * (1 - fx) + d[(y0 * img.w + x1) * 4 + c] * fx;
    const bot = d[(y1 * img.w + x0) * 4 + c] * (1 - fx) + d[(y1 * img.w + x1) * 4 + c] * fx;
    dst[o + c] = Math.round(top * (1 - fy) + bot * fy);
  }
}

/** « photo.jpg » → « photo - modifié.jpg ». */
export function outputName(page) {
  let name = page.SourceName.replace(/\.pdf – page/i, ' - page');
  name = name.replace(/\.(jpe?g|png|tiff?|bmp|heic|heif|webp)$/i, '');
  return name.replace(/[\\/:*?"<>|]/g, '_').trim() + ' - modifié.jpg';
}
