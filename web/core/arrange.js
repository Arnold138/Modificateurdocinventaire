// Mise en page des rangées déplacées : lots 0 envoyés après le dernier lot, rangées montées/descendues à la main.
// Rien n'est modifié dans le projet : la page affichée (et exportée) est recomposée à partir des pages d'origine.
//  - lot 0 : l'objet (avec ses lignes de suite et, s'il les reprend, les valeurs de son titre) quitte sa rangée,
//    ce qui suit remonte ; il est recollé juste après la rangée du dernier objet numéroté (même s'il est sur
//    une autre page), dans une rangée refaite aux colonnes du tableau d'arrivée ; ce qui suit descend.
//  - rangée déplacée à la main : l'ordre des rangées du tableau change (Table.RowOrder).
// Portage identique (entiers, pixel pour pixel) dans Arrangement.cs.
import { Rect } from './geometry.js';
import { allLines } from './model.js';
import { rowOrder, hasLotCell, isZeroLot, lotsActive, objectLines, columnWidth, numberBox, tableLeft } from './lots.js';
import { baseBox } from './layout.js';

export const CUT = 2;    // une rangée commence 2 px au-dessus du milieu de son filet du haut
export const FILET = 5;  // hauteur réservée au filet du haut
export const MARGIN = 3; // marge entre un filet vertical et le contenu recopié d'une case

const R = (v) => Math.floor(v + 0.5);
export const cutY = (y) => R(y) - CUT;
const blank = (s) => (s ?? '').trim().length === 0;
const hasValues = (l) => l.Cells.slice(1).some((c) => !blank(c.Text));
const gridTable = (t) => t.HasGrid !== false && t.RowYs?.length >= 2 && t.ColumnXs?.length >= 2;

/** Haut et bas de l'écriture d'une ligne (bande ajoutée comprise). */
export function lineTop(l) {
  const ink = l.Cells.filter((c) => !Rect.isEmpty(c.Ink));
  return ink.length ? Math.min(...ink.map((c) => c.Ink.Y)) : Math.min(...l.Cells.map((c) => baseBox(l, c).Y));
}
export function lineBottom(l) {
  const ink = l.Cells.filter((c) => !Rect.isEmpty(c.Ink));
  let b = ink.length ? Math.max(...ink.map((c) => Rect.bottom(c.Ink))) : Math.max(...l.Cells.map((c) => Rect.bottom(baseBox(l, c))));
  if ((l.Extra ?? 0) > 0) b = Math.max(b, l.ExtraAt + l.Extra);
  return b;
}
export const gapMid = (a, b) => Math.floor((lineBottom(a) + lineTop(b)) / 2);

/** Titre de la rangée dont les valeurs descendent sur `target` (comme dans le .txt). */
function carriedTitle(lines, target) {
  const taken = new Set();
  for (let i = 0; i < lines.length; i++) {
    if (lines[i].Kind !== 'Titre' || !hasValues(lines[i])) continue;
    const t = lines.slice(i + 1).find((x) => x.Kind !== 'Titre' && !hasValues(x) && !taken.has(x));
    if (!t) continue;
    taken.add(t);
    if (t === target) return lines[i];
  }
  return null;
}

/** Objets au lot 0 à déplacer, dans l'ordre du document. */
function zeroUnits(project) {
  const units = [];
  if (!lotsActive(project)) return units;
  project.Pages.forEach((page, p) => page.Tables.forEach((table, ti) => {
    if (!gridTable(table)) return;
    for (const r of rowOrder(table)) {
      if (r + 1 >= table.RowYs.length) continue;
      const row = table.Rows[r];
      const lines = row.Lines.filter((l) => l.Kind !== 'Ignoree' && l.Cells.some((c) => !blank(c.Text)));
      const found = [];
      lines.forEach((l, i) => {
        if (!hasLotCell(l) || !isZeroLot(l)) return;
        const own = [l];
        for (let j = i + 1; j < lines.length && lines[j].Kind !== 'Titre' && !hasLotCell(lines[j]); j++) own.push(lines[j]);
        found.push({ object: l, lines: own, title: hasValues(l) ? null : carriedTitle(lines, l) });
      });
      if (found.length === 0) continue;
      const covered = new Set(found.flatMap((u) => u.lines));
      const whole = lines.every((l) => covered.has(l)) && found.every((u) => !u.title);
      const base = { p, page, ti, table, r };
      if (whole) {
        const top = cutY(table.RowYs[r]);
        units.push({ ...base, object: found[0].object, lines: row.Lines.slice(), title: null, whole: true, top, bodyTop: top + FILET, bottom: cutY(table.RowYs[r + 1]) });
        continue;
      }
      for (const u of found) {
        const first = lines.indexOf(u.lines[0]), last = lines.indexOf(u.lines[u.lines.length - 1]);
        const top = first > 0 ? gapMid(lines[first - 1], lines[first]) : cutY(table.RowYs[r]) + FILET;
        const bottom = last + 1 < lines.length ? gapMid(lines[last], lines[last + 1]) : cutY(table.RowYs[r + 1]);
        if (bottom <= top) continue;
        units.push({ ...base, ...u, whole: false, top, bodyTop: top, bottom });
      }
    }
  }));
  return units;
}

/** Rangée du dernier objet numéroté (pas au lot 0) : c'est là que les lots 0 sont recollés. */
function destination(project) {
  let last = null;
  for (const o of objectLines(project)) if (!isZeroLot(o.line)) last = o;
  if (!last || !gridTable(last.table)) return null;
  const p = project.Pages.indexOf(last.page);
  return { p, ti: last.page.Tables.indexOf(last.table), r: last.row, table: last.table, page: last.page };
}

/** Décalage horizontal d'une colonne vers le tableau d'arrivée (selon l'alignement de la colonne). */
function columnShift(src, dst, c, align) {
  const a = src.ColumnXs, b = dst.ColumnXs;
  if (a.length !== b.length) return R(b[0] - a[0]);
  if (align === 'Right') return R(b[c + 1] - a[c + 1]);
  if (align === 'Center') return R((b[c] + b[c + 1] - a[c] - a[c + 1]) / 2);
  return R(b[c] - a[c]);
}

/**
 * Plan de toutes les pages : morceaux recopiés (dans l'ordre) et rangées des lots 0.
 * Chaque page : { height, pieces: [{ kind: 'copy', y0, y1, dst } | { kind: 'unit', unit, dst, h }] }.
 */
export function planArrangement(project) {
  const units = zeroUnits(project);
  const dest = units.length ? destination(project) : null;
  const moving = dest ? units : [];
  const lots = lotsActive(project);
  for (const u of moving) {
    const align = (c) => u.object.Cells[c]?.Align ?? (c === 0 ? 'Left' : 'Right');
    u.dest = dest;
    u.shift = u.table.ColumnXs.slice(0, -1).map((_, c) => columnShift(u.table, dest.table, c, align(c)));
    u.lotShift = R(tableLeft(dest.table)) - R(tableLeft(u.table));
    u.srcLotWidth = lots ? R(columnWidth(u.page)) : 0;
    u.dstLotWidth = lots ? R(columnWidth(dest.page)) : 0;
    u.height = FILET + (u.bottom - u.bodyTop);
  }
  const pages = project.Pages.map((page, p) => {
    const pieces = [];
    let dst = 0;
    const copy = (y0, y1) => {
      if (y1 <= y0) return;
      const last = pieces[pieces.length - 1];
      if (last?.kind === 'copy' && last.y1 === y0) { last.y1 = y1; dst += y1 - y0; return; }
      pieces.push({ kind: 'copy', y0, y1, dst });
      dst += y1 - y0;
    };
    let cursor = 0;
    const tables = page.Tables.map((t, ti) => ({ t, ti })).filter((x) => gridTable(x.t)).sort((a, b) => a.t.RowYs[0] - b.t.RowYs[0]);
    for (const { t, ti } of tables) {
      const n = t.RowYs.length - 1;
      const A = Math.max(cursor, cutY(t.RowYs[0])), B = cutY(t.RowYs[n]);
      if (B <= A) continue;
      copy(cursor, A);
      for (const r of rowOrder(t)) {
        if (r >= n) continue;
        const s0 = Math.max(A, cutY(t.RowYs[r])), s1 = Math.min(B, cutY(t.RowYs[r + 1]));
        const cuts = moving.filter((u) => u.p === p && u.ti === ti && u.r === r).sort((a, b) => a.top - b.top);
        let y = s0;
        for (const u of cuts) { copy(y, Math.max(y, u.top)); y = Math.max(y, u.bottom); }
        copy(y, s1);
        if (dest && dest.p === p && dest.ti === ti && dest.r === r)
          for (const u of moving) { pieces.push({ kind: 'unit', unit: u, dst, h: u.height }); dst += u.height; }
      }
      cursor = B;
    }
    copy(cursor, page.Height);
    return { height: Math.max(1, dst), pieces };
  });
  // Valeurs d'un titre parties avec son objet : effacées de leur place.
  const erase = moving.filter((u) => u.title).flatMap((u) =>
    u.title.Cells.filter((c) => c.Column > 0 && !blank(c.Text)).map((c) => ({ p: u.p, rect: cellRect(u.table, c) })));
  return { pages, units: moving, dest, erase };
}

/** Partie d'une case (zone d'édition) à l'intérieur de sa colonne, en pixels entiers. */
function cellRect(table, f) {
  const c = f.Column;
  const x0 = Math.max(R(table.ColumnXs[c]) + MARGIN, Math.floor(f.Box.X)), x1 = Math.min(R(table.ColumnXs[c + 1]) - MARGIN, Math.ceil(Rect.right(f.Box)));
  return { x0, y0: Math.floor(f.Box.Y), x1, y1: Math.ceil(Rect.bottom(f.Box)) };
}

/** Ordonnée sur la page affichée d'une ordonnée de la page d'origine (null si ce morceau est parti ailleurs). */
export function mapY(planPage, y) {
  for (const pc of planPage.pieces) if (pc.kind === 'copy' && y >= pc.y0 && y < pc.y1) return pc.dst + y - pc.y0;
  const last = planPage.pieces.filter((pc) => pc.kind === 'copy').pop();
  if (last && y >= last.y1) return last.dst + (last.y1 - last.y0) + (y - last.y1);
  return null;
}

/** Inverse de mapY pour la page elle-même (zones verrouillées tracées à l'écran). */
export function unmapY(planPage, y) {
  for (const pc of planPage.pieces) {
    const h = pc.kind === 'copy' ? pc.y1 - pc.y0 : pc.h;
    if (y >= pc.dst && y < pc.dst + h) return pc.kind === 'copy' ? pc.y0 + y - pc.dst : null;
  }
  return y;
}

// ---------- Placement des cases sur la page affichée ----------

const unitBodyDy = (u, pc) => pc.dst + FILET - u.bodyTop;
const centerY = (line, f) => Math.floor(Rect.cy(baseBox(line, f)));

/**
 * Ce qu'affiche la page k : lignes (avec le décalage de chaque case), tableaux (rangées à l'écran) et hauteur.
 * lines : [{ line, page, table, cells: [{ f, dx, dy, box, ink }], dy, lotBox, moved, unit }]
 */
export function viewOf(project, plan, k) {
  const page = project.Pages[k], pp = plan.pages[k];
  const out = { page, height: pp.height, width: page.Width, lines: [], tables: [] };
  const movedLines = new Set(plan.units.flatMap((u) => u.lines));
  const carried = new Map();
  for (const u of plan.units) if (u.title) for (const c of u.title.Cells) if (c.Column > 0 && !blank(c.Text)) carried.set(c, u);
  const lots = lotsActive(project);
  const w = columnWidth(page);

  // Lignes restées sur leur page.
  for (const t of page.Tables)
    for (const r of rowOrder(t))
      for (const line of t.Rows[r].Lines) {
        if (movedLines.has(line)) continue;
        const anchor = centerY(line, line.Cells[0]);
        const my = gridTable(t) ? mapY(pp, anchor) : anchor;
        if (my === null) continue;
        const dy = my - anchor;
        const cells = line.Cells.filter((f) => !carried.has(f)).map((f) => place(f, 0, dy, null));
        out.lines.push({ line, page, table: t, cells, dy, moved: false, lotBox: lots && hasLotCell(line) ? shiftRect(numberBox(t, line, w), 0, dy) : null });
      }
  // Lots 0 recollés ici.
  for (const pc of pp.pieces) {
    if (pc.kind !== 'unit') continue;
    const u = pc.unit, dt = u.dest.table;
    const dy = unitBodyDy(u, pc);
    const top = pc.dst + FILET, bottom = pc.dst + pc.h;
    const clipCol = (c) => Rect.fromEdges(dt.ColumnXs[Math.min(c, dt.ColumnXs.length - 2)] + 1.5, top, dt.ColumnXs[Math.min(c + 1, dt.ColumnXs.length - 1)] - 1.5, bottom);
    for (const line of u.lines) {
      const cells = line.Cells.map((f) => place(f, u.shift[f.Column] ?? 0, dy, clipCol(f.Column)));
      let lotBox = null;
      if (lots && hasLotCell(line)) {
        const b = shiftRect(numberBox(u.table, line, u.srcLotWidth || w), 0, dy);
        const right = tableLeft(dt), left = Math.max(0, right - w);
        lotBox = Rect.make(left + 2, b.Y, Math.max(4, right - left - 4), b.Height);
      }
      out.lines.push({ line, page: u.page, table: u.table, cells, dy, moved: true, unit: u, lotBox });
    }
    if (u.title) {
      const dyc = dy + centerY(u.object, u.object.Cells[0]) - centerY(u.title, u.title.Cells.find((c) => c.Column > 0 && !blank(c.Text)));
      const cells = u.title.Cells.filter((c) => carried.get(c) === u).map((f) => place(f, u.shift[f.Column] ?? 0, dyc, clipCol(f.Column)));
      out.lines.push({ line: u.title, page: u.page, table: u.table, cells, dy: dyc, moved: true, unit: u, lotBox: null, carriedOnly: true });
    }
  }
  // Tableaux tels qu'à l'écran : rangées dans l'ordre affiché (pour les poignées et le quadrillage des pages sans image).
  page.Tables.forEach((t, ti) => {
    if (!gridTable(t)) { out.tables.push({ table: t, ColumnXs: t.ColumnXs, RowYs: t.RowYs, strips: [] }); return; }
    const strips = [];
    for (const r of rowOrder(t)) {
      if (r + 1 >= t.RowYs.length) continue;
      const whole = plan.units.some((u) => u.p === k && u.ti === ti && u.r === r && u.whole);
      if (whole) continue;
      const a = mapY(pp, cutY(t.RowYs[r]) + CUT);
      if (a === null) continue;
      strips.push({ r, top: a, bottom: a + (cutY(t.RowYs[r + 1]) - cutY(t.RowYs[r])) - cutHeight(plan, k, ti, r) });
    }
    const ys = [];
    let units = [];
    if (plan.dest && plan.dest.p === k && plan.dest.ti === ti) units = pp.pieces.filter((pc) => pc.kind === 'unit');
    for (const s of strips) ys.push(s.top);
    for (const pc of units) ys.push(pc.dst + CUT);
    const end = mapY(pp, R(t.RowYs[t.RowYs.length - 1]));
    if (end !== null) ys.push(end);
    ys.sort((a, b) => a - b);
    out.tables.push({ table: t, ColumnXs: t.ColumnXs, RowYs: ys, strips });
  });
  return out;
}

function cutHeight(plan, k, ti, r) {
  return plan.units.filter((u) => u.p === k && u.ti === ti && u.r === r && !u.whole).reduce((s, u) => s + u.bottom - u.top, 0);
}

const shiftRect = (b, dx, dy) => ({ ...b, X: b.X + dx, Y: b.Y + dy });

function place(f, dx, dy, clip) {
  let box = shiftRect(f.Box, dx, dy);
  if (clip) {
    const x0 = Math.max(box.X, clip.X), y0 = Math.max(box.Y, clip.Y);
    const x1 = Math.min(Rect.right(box), Rect.right(clip)), y1 = Math.min(Rect.bottom(box), Rect.bottom(clip));
    if (x1 > x0 && y1 > y0) box = Rect.fromEdges(x0, y0, x1, y1);
  }
  return { f, dx, dy, box, ink: Rect.isEmpty(f.Ink) ? f.Ink : shiftRect(f.Ink, dx, dy) };
}

/** La page k change-t-elle de forme (rangées parties, arrivées ou déplacées) ? */
export function pageArranged(project, plan, k) {
  const pp = plan.pages[k];
  return pp.pieces.length !== 1 || pp.pieces[0].kind !== 'copy' || pp.pieces[0].y0 !== 0 || pp.height !== project.Pages[k].Height
    || plan.erase.some((e) => e.p === k);
}

/** Pages dont l'image est nécessaire pour composer la page k (elle-même et celles des lots 0 recollés). */
export const sourcesOf = (plan, k) => [...new Set([k, ...plan.pages[k].pieces.filter((pc) => pc.kind === 'unit').map((pc) => pc.unit.p)])];

/** Morceau d'image utile d'un lot 0 (de quoi recomposer sa rangée), pris sur l'image de sa page. */
export function unitCrop(unit, image) {
  let y0 = unit.bodyTop;
  if (unit.title) for (const c of unit.title.Cells) if (c.Column > 0 && !blank(c.Text)) y0 = Math.min(y0, cellRect(unit.table, c).y0);
  y0 = Math.max(0, y0);
  const y1 = Math.min(image.h, unit.bottom);
  const stride = image.w * image.ch;
  return { w: image.w, h: Math.max(0, y1 - y0), ch: image.ch, oy: y0, data: image.data.slice(y0 * stride, Math.max(y0, y1) * stride) };
}

// ---------- Pixels ----------

const lum = (img, x, y) => {
  const o = ((y - (img.oy ?? 0)) * img.w + x) * img.ch, d = img.data;
  return img.ch === 1 ? d[o] : (d[o] * 77 + d[o + 1] * 150 + d[o + 2] * 29) >> 8;
};
function put(dst, dx, dy, src, sx, sy) {
  const o = (dy * dst.w + dx) * dst.ch, i = ((sy - (src.oy ?? 0)) * src.w + sx) * src.ch;
  if (src.ch === dst.ch) { for (let c = 0; c < dst.ch; c++) dst.data[o + c] = src.data[i + c]; return; }
  if (dst.ch === 1) { dst.data[o] = lum(src, sx, sy); return; }
  const g = src.data[i];
  dst.data[o] = g; dst.data[o + 1] = g; dst.data[o + 2] = g; dst.data[o + 3] = 255;
}
function paint(dst, x, y, color) {
  const o = (y * dst.w + x) * dst.ch;
  for (let c = 0; c < dst.ch; c++) dst.data[o + c] = color[Math.min(c, color.length - 1)];
}

/** Couleur du papier sur une rangée de pixels : moyenne de la moitié la plus claire (comme PageLayout). */
function paperRow(img, y) {
  const w = img.w, ch = img.ch, d = img.data, base = (y - (img.oy ?? 0)) * w;
  const l = [];
  for (let x = 0; x < w; x++) { const o = (base + x) * ch; l.push([ch === 1 ? d[o] : d[o] + d[o + 1] + d[o + 2], x]); }
  l.sort((a, b) => (b[0] !== a[0] ? b[0] - a[0] : a[1] - b[1]));
  const n = Math.max(1, Math.trunc(w / 2));
  const sum = new Array(ch).fill(0);
  for (let i = 0; i < n; i++) { const o = (base + l[i][1]) * ch; for (let c = 0; c < ch; c++) sum[c] += d[o + c]; }
  return sum.map((v) => Math.trunc(v / n));
}

/** Couleur du papier autour d'un rectangle : moyenne de la moitié la plus claire de son pourtour. */
function paperAround(img, r) {
  const s = [];
  const x0 = Math.max(0, r.x0), x1 = Math.min(img.w - 1, r.x1 - 1), y0 = Math.max(0, r.y0), y1 = Math.min(img.h - 1, r.y1 - 1);
  const add = (x, y) => { const o = (y * img.w + x) * img.ch; s.push([lum(img, x, y), o]); };
  for (let x = x0; x <= x1; x++) { add(x, y0); add(x, y1); }
  for (let y = y0; y <= y1; y++) { add(x0, y); add(x1, y); }
  if (s.length === 0) return new Array(img.ch).fill(255);
  s.sort((a, b) => (b[0] !== a[0] ? b[0] - a[0] : a[1] - b[1]));
  const n = Math.max(1, Math.trunc(s.length / 2));
  const sum = new Array(img.ch).fill(0);
  for (let i = 0; i < n; i++) for (let c = 0; c < img.ch; c++) sum[c] += img.data[s[i][1] + c];
  return sum.map((v) => Math.trunc(v / n));
}

/**
 * Image de la page k telle qu'affichée : `images[k]` est l'image de la page (redressée, colonne des lots tracée,
 * corrections dessinées pour l'export) ; `crops` donne, pour chaque lot 0 recollé ici, son morceau d'image (unitCrop).
 */
export function composePage(project, plan, k, image, crops) {
  const pp = plan.pages[k];
  const w = image.w, ch = image.ch;
  const out = { w, h: pp.height, ch, data: new Uint8ClampedArray(w * pp.height * ch) };
  const stride = w * ch;
  for (const pc of pp.pieces) {
    if (pc.kind === 'copy') {
      const a = Math.min(Math.max(pc.y0, 0), image.h), b = Math.min(Math.max(pc.y1, a), image.h);
      out.data.set(image.data.subarray(a * stride, b * stride), pc.dst * stride);
      const white = new Array(ch).fill(255);
      for (let y = pc.dst + (b - a); y < pc.dst + (pc.y1 - pc.y0); y++) for (let x = 0; x < w; x++) paint(out, x, y, white);
    } else synthesize(out, pc, image, crops.get(pc.unit));
  }
  // Valeurs de titre parties avec leur objet : papier à la place.
  for (const e of plan.erase) {
    if (e.p !== k) continue;
    const paper = paperAround(image, e.rect);
    for (let y = e.rect.y0; y < e.rect.y1; y++) {
      const my = mapY(pp, y);
      if (my === null || my < 0 || my >= out.h) continue;
      for (let x = Math.max(0, e.rect.x0); x < Math.min(w, e.rect.x1); x++) paint(out, x, my, paper);
    }
  }
  return out;
}

/** Rangée refaite pour un lot 0 : filet et fond du tableau d'arrivée, contenu des cases recopié colonne par colonne. */
function synthesize(out, pc, image, crop) {
  const u = pc.unit, d = u.dest, dt = d.table, st = u.table;
  const w = out.w;
  const fs = cutY(dt.RowYs[d.r]), re = cutY(dt.RowYs[d.r + 1]);
  // Filet du haut : celui de la rangée d'arrivée.
  for (let k = 0; k < FILET; k++) {
    const sy = Math.min(image.h - 1, Math.max(0, fs + k));
    for (let x = 0; x < w; x++) put(out, x, pc.dst + k, image, x, sy);
  }
  // Fond : l'interligne le plus propre de la rangée d'arrivée (filets verticaux prolongés, papier ailleurs).
  const x0 = Math.max(0, R(dt.ColumnXs[0]) - (u.dstLotWidth > 0 ? u.dstLotWidth + 8 : 4)), x1 = Math.min(w, R(dt.ColumnXs[dt.ColumnXs.length - 1]) + 4);
  let yc = Math.min(image.h - 1, Math.max(0, fs + FILET)), best = -1;
  for (let y = fs + FILET; y < re; y++) {
    if (y < 0 || y >= image.h) continue;
    let s = 0;
    for (let x = x0; x < x1; x++) s += lum(image, x, y);
    if (s > best) { best = s; yc = y; }
  }
  const paper = paperRow(image, yc);
  const bodyH = pc.h - FILET;
  for (let k = 0; k < bodyH; k++) {
    const y = pc.dst + FILET + k;
    for (let x = 0; x < w; x++) {
      if (x >= x0 && x < x1 && lum(image, x, yc) < 150) put(out, x, y, image, x, yc);
      else paint(out, x, y, paper);
    }
  }
  if (!crop) return;
  const dy = pc.dst + FILET - u.bodyTop;
  const blit = (sx0, sx1, sy0, sy1, dx, ddy, cx0, cx1) => {
    for (let y = Math.max(sy0, crop.oy); y < Math.min(sy1, crop.oy + crop.h); y++) {
      const ty = y + ddy;
      if (ty < pc.dst + FILET || ty >= pc.dst + pc.h) continue;
      for (let x = Math.max(0, sx0); x < Math.min(crop.w, sx1); x++) {
        const tx = x + dx;
        if (tx < cx0 || tx >= cx1 || tx < 0 || tx >= w) continue;
        put(out, tx, ty, crop, x, y);
      }
    }
  };
  // Contenu de chaque colonne, recalé sur la colonne d'arrivée.
  const nd = dt.ColumnXs.length - 1;
  for (let c = 0; c < st.ColumnXs.length - 1; c++) {
    const dc = Math.min(c, nd - 1);
    blit(R(st.ColumnXs[c]) + MARGIN, R(st.ColumnXs[c + 1]) - MARGIN, u.bodyTop, u.bottom, u.shift[c], dy,
      R(dt.ColumnXs[dc]) + MARGIN, R(dt.ColumnXs[dc + 1]) - MARGIN);
  }
  // Case du numéro de lot.
  if (u.srcLotWidth > 0 && u.dstLotWidth > 0) {
    const sl = R(tableLeft(st)), dl = R(tableLeft(dt));
    blit(sl - u.srcLotWidth + MARGIN, sl - MARGIN, u.bodyTop, u.bottom, u.lotShift, dy, dl - u.dstLotWidth + MARGIN, dl - MARGIN);
  }
  // Valeurs du titre reprises par l'objet : à la hauteur de l'objet.
  if (u.title) {
    const ref = u.title.Cells.find((c) => c.Column > 0 && !blank(c.Text));
    const dyc = dy + centerY(u.object, u.object.Cells[0]) - centerY(u.title, ref);
    for (const f of u.title.Cells) {
      if (f.Column === 0 || blank(f.Text)) continue;
      const r = cellRect(st, f), dc = Math.min(f.Column, nd - 1);
      blit(r.x0, r.x1, r.y0, r.y1, u.shift[f.Column] ?? 0, dyc, R(dt.ColumnXs[dc]) + MARGIN, R(dt.ColumnXs[dc + 1]) - MARGIN);
    }
  }
}

/** Lignes effacées de leur page parce qu'elles sont recollées ailleurs (pour l'export .txt et les totaux). */
export const movedLinesOf = (plan) => new Set(plan.units.flatMap((u) => u.lines));

export { allLines };
