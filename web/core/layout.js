// Rangée qui s'agrandit : quand le texte d'une case passe sur plusieurs lignes (même taille d'écriture),
// une bande est ajoutée sous la ligne et tout ce qui est en dessous descend d'autant.
// Rangées ajoutées (ligne dupliquée, « Séparer ici ») : une bande insérée dans la page (filet du haut et, pour une copie,
// les pixels de la ligne recopiée), tout ce qui est en dessous descend aussi. Voir rows.js.
// Portage de PageLayout.cs (entiers : même résultat sur Windows et sur iPhone/iPad).
import { Rect } from './geometry.js';
import { allLines, lineBounds } from './model.js';

export const LINE_HEIGHT = 1.15;

/** Bandes insérées dans la page (rangées ajoutées). */
export const insertsOf = (page) => page.Inserts ?? [];

/** Hauteur ajoutée à la page : rangées agrandies et rangées ajoutées. */
export const totalExtra = (page) => [...allLines(page)].reduce((s, l) => s + (l.Extra ?? 0), 0)
  + insertsOf(page).reduce((s, b) => s + b.Height, 0);

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

/** « Ligne plus haute » (Tall : lignes de texte en plus, page ajoutée) : hauteur à ajouter sous la ligne. */
export function tallExtra(line) {
  const n = line.Tall ?? 0, f = line.Cells[0];
  if (n <= 0 || !f) return 0;
  return extraFor(line, f, 1 + n, f.FontSize > 0 ? f.FontSize : 12);
}

const shiftRect = (r, at, delta) => {
  if (r.Y >= at) return { ...r, Y: r.Y + delta };
  if (r.Y + r.Height > at) return { ...r, Height: r.Height + delta };
  return r;
};

/**
 * Décale de `delta` tout ce qui est sous l'ordonnée `at` (filets, rangées, cases, encre, zones verrouillées,
 * autres bandes) et allonge la page. `self` : ligne dont la bande change (traitée par l'appelant).
 */
export function shiftBelow(page, at, delta, self = null) {
  for (const t of page.Tables) {
    t.RowYs = t.RowYs.map((y) => (y > at ? y + delta : y));
    t.Bounds = shiftRect(t.Bounds, at, delta);
    for (const r of t.Rows) r.Bounds = shiftRect(r.Bounds, at, delta);
  }
  page.LockZones = page.LockZones.map((z) => shiftRect(z, at, delta));
  for (const l of allLines(page)) {
    if (l === self) continue;
    if ((l.Extra ?? 0) > 0 && l.ExtraAt > at) l.ExtraAt += delta;
    for (const f of l.Cells) {
      if (Rect.cy(baseBox(l, f)) < at) continue;
      f.Box = { ...f.Box, Y: f.Box.Y + delta };
      if (!Rect.isEmpty(f.Ink)) f.Ink = { ...f.Ink, Y: f.Ink.Y + delta };
    }
  }
  for (const b of insertsOf(page)) if (b.At >= at) b.At += delta;
  page.Height += delta;
}

/** Fixe la hauteur ajoutée sous une ligne et décale tout ce qui est en dessous. Vrai si la page a changé. */
export function setExtra(page, line, extra) {
  extra = Math.max(0, extra);
  const cur = line.Extra ?? 0;
  if (extra === cur) return false;
  if (cur === 0) line.ExtraAt = cleanGap(page, line);
  const delta = extra - cur, at = line.ExtraAt;
  shiftBelow(page, at, delta, line);
  for (const f of line.Cells) f.Box = { ...f.Box, Height: f.Box.Height + delta };
  line.Extra = extra;
  if (extra === 0) line.ExtraAt = 0;
  return true;
}

/**
 * Insère une bande de hauteur Σ parts à l'ordonnée `at` : chaque morceau recopie des rangées entières de pixels
 * de l'image d'origine (`Src`, hauteur `Height`) ou, avec `Gap`, prolonge un interligne propre : filets verticaux
 * (entre X0 et X1) de la rangée la plus nette parmi [Src, Src + Span[ (voir cleanRow), papier ailleurs.
 * Tout ce qui est en dessous descend.
 */
export function insertBand(page, at, parts, kind) {
  const height = parts.reduce((s, p) => s + p.Height, 0);
  shiftBelow(page, at, height);
  const band = { Id: Math.random().toString(16).slice(2, 10), Kind: kind, At: at, Height: height, Parts: parts };
  (page.Inserts ??= []).push(band);
  return band;
}

/**
 * « Réduire la rangée » : retire les rangées de pixels [at, at + h[ (espace vide imprimé), ce qui est en dessous remonte.
 * Bande « Trim » de hauteur négative (Skip : rangées de l'image d'origine sautées) ; removeBand la remet.
 */
export function cutBand(page, at, h, lineId = null) {
  shiftBelow(page, at, -h);
  const band = { Id: Math.random().toString(16).slice(2, 10), Kind: 'Trim', At: at, Height: -h, Skip: h, Line: lineId, Parts: [] };
  (page.Inserts ??= []).push(band);
  return band;
}

/** Retire une bande insérée (son contenu a déjà été retiré) : ce qui est en dessous remonte. */
export function removeBand(page, band) {
  page.Inserts = insertsOf(page).filter((b) => b !== band);
  shiftBelow(page, band.At, -band.Height);
}

/** Ordonnée de la bande : au milieu de l'interligne sous le texte de la ligne. */
function cleanGap(page, line) {
  const inked = line.Cells.filter((c) => !Rect.isEmpty(c.Ink));
  const lb = lineBounds(line);
  const a = inked.length > 0 ? Math.max(...inked.map((c) => Rect.bottom(c.Ink))) : Rect.cy(lb);
  let b = Rect.bottom(lb), hi = Infinity;
  for (const t of page.Tables)
    t.Rows.forEach((row, r) => {
      const i = row.Lines.indexOf(line);
      if (i < 0) return;
      const below = row.Lines.slice(i + 1).flatMap((l) => l.Cells).filter((c) => !Rect.isEmpty(c.Ink)).map((c) => c.Ink.Y);
      b = below.length > 0 ? Math.min(...below) : r + 1 < t.RowYs.length ? t.RowYs[r + 1] - 4 : b;
      if (r + 1 < t.RowYs.length) hi = Math.floor(t.RowYs[r + 1]) - 3;
    });
  // Toujours dans la rangée de la ligne (au-dessus de son filet du bas) : sinon c'est la rangée suivante qui grandirait.
  return Math.min(hi, b > a ? Math.floor((a + b) / 2) : Math.floor(a));
}

/**
 * Suite des rangées de la page agrandie, de haut en bas :
 *  { y0, n } rangées de l'image d'origine (y compris celles recopiées par une bande insérée) ;
 *  { extra, s, n, x0, x1 } bande d'interligne (filets verticaux de la rangée s prolongés) ;
 *  { gap, s, n, x0, x1 } interligne d'une bande insérée (même rendu).
 */
export function pieces(page, baseH = baseHeight(page)) {
  const extras = [];
  for (const t of page.Tables)
    for (const l of t.Rows.flatMap((r) => r.Lines).filter((x) => (x.Extra ?? 0) > 0)) {
      const grid = t.HasGrid !== false && t.ColumnXs?.length > 0;
      const left = grid ? t.ColumnXs[0] : t.Bounds.X, right = grid ? t.ColumnXs[t.ColumnXs.length - 1] : Rect.right(t.Bounds);
      extras.push({ at: l.ExtraAt, height: l.Extra, x0: Math.floor(left) - 4, x1: Math.ceil(right) + 4 });
    }
  extras.sort((a, b) => a.at - b.at);
  // 1) Image d'origine et bandes insérées (positions sans les bandes d'interligne au-dessus d'elles).
  const above = (y) => extras.reduce((s, e) => s + (e.at < y ? e.height : 0), 0);
  const seq = [];
  let src = 0, inserted = 0;
  for (const b of [...insertsOf(page)].sort((a, c) => a.At - c.At)) {
    const until = Math.min(baseH, Math.max(src, b.At - above(b.At) - inserted));
    if (until > src) seq.push({ y0: src, n: until - src });
    src = until;
    if (b.Skip) { src = Math.min(baseH, until + b.Skip); inserted += b.Height; continue; } // rangée réduite : rangées sautées
    for (const p of b.Parts) if (p.Height > 0) seq.push(p.Gap ? { gap: true, s: p.Src, span: p.Span, cols: p.Cols, n: p.Height, x0: p.X0, x1: p.X1, anchor: until } : (p.Straight ? { y0: p.Src, n: p.Height, straight: true, anchor: until } : { y0: p.Src, n: p.Height }));
    inserted += b.Height;
  }
  if (baseH > src) seq.push({ y0: src, n: baseH - src });
  // 2) Bandes d'interligne, chacune avant la rangée qui suit sa position.
  const total = seq.reduce((s, x) => s + x.n, 0);
  let before = 0;
  for (const e of extras) {
    const q = Math.min(Math.max(0, total - 1), Math.max(0, e.at - before));
    let pos = 0, s = 0, i = 0, from = null;
    for (; i < seq.length; i++) {
      const x = seq[i];
      if (x.extra) continue;
      if (q < pos + x.n) {
        s = x.gap ? x.s : x.y0 + (q - pos);
        if (x.gap) from = x;
        if (q > pos) { seq.splice(i, 1, { ...x, n: q - pos }, x.gap ? { ...x, n: x.n - (q - pos) } : { ...x, y0: s, n: x.n - (q - pos) }); i++; }
        break;
      }
      pos += x.n;
    }
    const extra = { extra: true, s: Math.min(baseH - 1, Math.max(0, s)), n: e.height, x0: e.x0, x1: e.x1 };
    extra.anchor = extra.s;
    if (from) { extra.span = from.span; extra.cols = from.cols; extra.anchor = from.anchor; } // dans l'interligne d'une rangée ajoutée : même rangée source
    seq.splice(i, 0, extra);
    before += e.height;
  }
  return seq;
}

/** Rangées de l'image d'origine → ordonnée (la 1re) où chacune est affichée sur la page agrandie. */
export function baseToPage(page) {
  const map = new Map();
  let pos = 0;
  for (const x of pieces(page)) {
    if (!x.extra && !x.gap) for (let i = 0; i < x.n; i++) if (!map.has(x.y0 + i)) map.set(x.y0 + i, pos + i);
    pos += x.n;
  }
  return map;
}

/** Rangée de l'image d'origine affichée à l'ordonnée y de la page agrandie (null dans une bande d'interligne). */
export function toBase(page, y) {
  let pos = 0;
  for (const x of pieces(page)) {
    if (y < pos + x.n) return x.extra || x.gap ? null : x.y0 + (y - pos);
    pos += x.n;
  }
  return null;
}

/** Image de la page agrandie : l'image d'origine avec les bandes d'interligne et les rangées ajoutées. */
export function stretch(base, page) {
  if (totalExtra(page) === 0 && insertsOf(page).length === 0) return base;
  const seq = pieces(page, base.h);
  const w = base.w, ch = base.ch, stride = w * ch;
  const height = seq.reduce((s, x) => s + x.n, 0);
  const data = new Uint8ClampedArray(height * stride);
  let dst = 0;
  const profiles = new Map();
  for (const x of seq) {
    if (x.extra || x.gap) {
      const s = x.span ? cleanRow(base, x.s, x.span, x.cols) : x.s, paper = paperOf(base, s);
      const anchor = Math.min(base.h - 1, Math.max(0, x.anchor ?? x.s));
      // Filets verticaux tels qu'ils sont sur la page à l'endroit de l'ajout (même règle que Windows).
      let prof = profiles.get(anchor);
      if (!prof) { prof = ruleProfile(base, anchor, grayOf(paper, 0, ch)); profiles.set(anchor, prof); }
      const missing = missingRules(prof.has, x.cols, Math.min(w, Math.max(0, x.x0)), Math.min(w, Math.max(0, x.x1)));
      for (let k = 0; k < x.n; k++, dst++)
        for (let xx = 0; xx < w; xx++) {
          const o = dst * stride + xx * ch;
          if (prof.has[xx]) for (let c = 0; c < ch; c++) data[o + c] = prof.ink[xx * ch + c];
          else if (missing && missing[xx] >= 0) for (let c = 0; c < ch; c++) data[o + c] = prof.ink[missing[xx] * ch + c];
          else if (missing && missing[xx] === -2) for (let c = 0; c < ch; c++) data[o + c] = c === 3 ? 255 : RULE_INK;
          else for (let c = 0; c < ch; c++) data[o + c] = paper[c];
        }
      continue;
    }
    if (x.straight) {
      if (x.anchor !== undefined) tableRule(base, x.y0, x.anchor, x.n, data, dst);
      else straightRule(base, x.y0, x.n, data, dst);
      dst += x.n;
      continue;
    }
    const a = Math.min(base.h, Math.max(0, x.y0)), b = Math.min(base.h, a + x.n);
    if (b > a) data.set(base.data.subarray(a * stride, b * stride), dst * stride);
    dst += x.n;
  }
  return { w, h: height, ch, data };
}

/** Gris d'un filet redessiné. */
const RULE_INK = 70;

/**
 * Filets verticaux de la page autour de la rangée anchor (± 64 rangées, sans les filets horizontaux) : une abscisse est un
 * filet si elle est encrée sur au moins les 3/4 de ces rangées ; son encre est celle de la rangée médiane (même règle que Windows).
 */
export function ruleProfile(img, anchor, paperGray) {
  const w = img.w, ch = img.ch, H = img.h;
  const a = Math.max(0, anchor - 64), b = Math.min(H, anchor + 64);
  const rule = new Uint8Array(b - a);
  for (let y = a; y < b; y++) {
    let dark = 0;
    for (let x = 0; x < w; x++) if (gray(img, x, y) < 150) dark++;
    rule[y - a] = dark * 3 > w ? 1 : 0;
  }
  let rows = [];
  for (let y = a; y < b; y++) {
    let near = false;
    for (let d = -3; d <= 3 && !near; d++) near = y + d >= a && y + d < b && rule[y + d - a] === 1;
    if (!near) rows.push(y);
  }
  if (rows.length < 8) { rows = []; for (let y = a; y < b; y++) rows.push(y); }
  const n = rows.length, ink = new Uint8ClampedArray(w * ch), has = new Uint8Array(w);
  const g = new Array(n);
  for (let x = 0; x < w; x++) {
    for (let i = 0; i < n; i++) g[i] = [gray(img, x, rows[i]), rows[i]];
    g.sort((p, q) => (p[0] !== q[0] ? p[0] - q[0] : p[1] - q[1]));
    has[x] = g[Math.trunc(n * 3 / 4)][0] < paperGray - 60 ? 1 : 0;
    const o = (g[Math.trunc(n / 2)][1] * w + x) * ch;
    for (let c = 0; c < ch; c++) ink[x * ch + c] = img.data[o + c];
  }
  return { ink, has };
}

/**
 * Filets du tableau (cols) absents à cet endroit du scan : redessinés avec le trait du filet vertical trouvé le plus proche,
 * centré sur la colonne. Par pixel : abscisse du trait à recopier, -2 (gris par défaut), -1 ailleurs ; null si rien ne manque.
 */
export function missingRules(has, cols, x0, x1) {
  if (!cols || cols.length === 0) return null;
  const w = has.length;
  let missing = null;
  for (const c of cols) {
    if (c < x0 || c >= x1) continue;
    let there = false;
    for (let xx = Math.max(0, c - 3); xx <= Math.min(w - 1, c + 3) && !there; xx++) there = has[xx] === 1;
    if (there) continue;
    if (!missing) missing = new Int32Array(w).fill(-1);
    let r0 = -1, r1 = -1, bestD = Infinity;
    for (const o of cols) {
      const d = Math.abs(o - c);
      if (o === c || d >= bestD) continue;
      let k = -1;
      for (let xx = Math.max(0, o - 3); xx <= Math.min(w - 1, o + 3) && k < 0; xx++) if (has[xx]) k = xx;
      if (k < 0) continue;
      let e = k;
      while (e + 1 < w && has[e + 1] && e - k < 8) e++;
      r0 = k; r1 = e; bestD = d;
    }
    if (r0 < 0) { for (let xx = Math.max(0, c - 1); xx <= Math.min(w - 1, c); xx++) missing[xx] = -2; continue; }
    const len = r1 - r0 + 1, start = c - Math.trunc(len / 2);
    for (let i = 0; i < len; i++) if (start + i >= 0 && start + i < w) missing[start + i] = r0 + i;
  }
  return missing;
}

/**
 * Filet du haut d'une rangée ajoutée : le filet de la page à l'endroit de l'ajout, redressé ; là où il est croisé par une
 * écriture ou effacé, la colonne vient du filet de la rangée recopiée (même règle que Windows).
 */
export function tableRule(img, src, anchor, n, data, dst) {
  const w = img.w, ch = img.ch, stride = w * ch, H = img.h;
  let ruleHere = false;
  for (let y = Math.max(0, anchor - 2); y < Math.min(H, anchor + n + 2) && !ruleHere; y++) {
    let run = 0, best = 0, gap = 0;
    for (let x = 0; x < w; x++) {
      if (gray(img, x, y) < 150) { run += gap + 1; gap = 0; best = Math.max(best, run); }
      else if (run > 0 && ++gap > 2) { run = 0; gap = 0; }
    }
    ruleHere = best * 4 > w;
  }
  if (!ruleHere || anchor === src) { straightRule(img, src, n, data, dst); return; }
  const here = new Uint8ClampedArray(n * stride), model = new Uint8ClampedArray(n * stride);
  straightRule(img, anchor, n, here, 0);
  straightRule(img, src, n, model, 0);
  const badHere = badColumns(here, w, ch, n), badModel = badColumns(model, w, ch, n);
  for (let x = 0; x < w; x++) {
    const from = badHere[x] && !badModel[x] ? model : here;
    for (let k = 0; k < n; k++) for (let c = 0; c < ch; c++) data[(dst + k) * stride + x * ch + c] = from[k * stride + x * ch + c];
  }
}

/** Colonnes d'un filet redressé où il n'a pas l'air d'un filet : bien plus d'encre que d'habitude, presque pas, ou de la couleur. */
function badColumns(band, w, ch, n) {
  const ink = new Int32Array(w), colored = new Uint8Array(w);
  for (let x = 0; x < w; x++)
    for (let k = 0; k < n; k++) {
      const o = (k * w + x) * ch;
      ink[x] += 255 - grayOf(band, o, ch);
      if (ch >= 3 && Math.max(band[o], band[o + 1], band[o + 2]) - Math.min(band[o], band[o + 1], band[o + 2]) > 60) colored[x] = 1;
    }
  const sorted = Int32Array.from(ink).sort();
  const typical = sorted[Math.trunc(w / 2)];
  const bad = new Uint8Array(w);
  for (let x = 0; x < w; x++) bad[x] = colored[x] || ink[x] * 10 > typical * 16 + 1500 || ink[x] * 2 < typical ? 1 : 0;
  return bad;
}

function grayOf(d, o, ch) {
  return ch === 1 ? d[o] : (d[o] * 77 + d[o + 1] * 150 + d[o + 2] * 29) >> 8;
}

/**
 * Filet redressé (même règle que Windows) : pour chaque abscisse, la rangée la plus sombre autour de [y0, y0 + n[ (± 4 px),
 * lissée par blocs de 32 px ; les n rangées écrites suivent ce filet.
 */
export function straightRule(img, y0, n, data, dst) {
  const w = img.w, ch = img.ch, stride = w * ch, H = img.h;
  const a = Math.max(0, y0 - 4), b = Math.min(H, y0 + n + 4);
  if (b <= a) return;
  const yd = new Int32Array(w), ok = new Uint8Array(w);
  for (let xx = 0; xx < w; xx++) {
    let best = 256, by = -1;
    for (let y = a; y < b; y++) { const g = gray(img, xx, y); if (g < best) { best = g; by = y; } }
    yd[xx] = by; ok[xx] = best < 160 ? 1 : 0;
  }
  const BLOCK = 32, nb = Math.ceil(w / BLOCK);
  const mid = new Float64Array(nb), has = new Uint8Array(nb);
  for (let k = 0; k < nb; k++) {
    const v = [];
    for (let xx = k * BLOCK; xx < Math.min(w, (k + 1) * BLOCK); xx++) if (ok[xx]) v.push(yd[xx]);
    if (v.length >= BLOCK / 4) { v.sort((p, q) => p - q); mid[k] = v[Math.trunc(v.length / 2)]; has[k] = 1; }
  }
  const center = y0 + (n - 1) / 2;
  if (!has.some((h) => h)) {
    for (let k = 0; k < n; k++) { const sy = Math.min(H - 1, Math.max(0, y0 + k)); data.set(img.data.subarray(sy * stride, (sy + 1) * stride), (dst + k) * stride); }
    return;
  }
  for (let k = 0; k < nb; k++)
    if (!has[k])
      for (let d = 1; ; d++) {
        if (k - d >= 0 && has[k - d]) { mid[k] = mid[k - d]; break; }
        if (k + d < nb && has[k + d]) { mid[k] = mid[k + d]; break; }
      }
  // Hauteur de référence : la médiane du filet (un filet droit est recopié tel quel ; même règle que Windows).
  const sortedMid = [...mid].sort((p, q) => p - q);
  const reference = sortedMid[Math.trunc(sortedMid.length / 2)];
  const off = Math.abs(reference - center) >= 2 ? csRound(reference - center) : 0;
  // Petit trou dans le filet : recopié depuis la colonne voisine la plus proche où il est là (jusqu'à 150 px de chaque côté).
  const from = new Int32Array(w);
  let lastOk = -100000;
  for (let xx = 0; xx < w; xx++) { if (ok[xx]) lastOk = xx; from[xx] = lastOk; }
  let nextOk = 100000;
  for (let xx = w - 1; xx >= 0; xx--) {
    if (ok[xx]) { nextOk = xx; from[xx] = xx; continue; }
    const l = from[xx];
    from[xx] = xx - l <= 150 && nextOk - xx <= 150 ? (xx - l <= nextOk - xx ? l : nextOk) : xx;
  }
  for (let xx = 0; xx < w; xx++) {
    const sx = from[xx];
    const f = (xx + 0.5) / BLOCK - 0.5;
    const k0 = Math.min(nb - 1, Math.max(0, Math.floor(f))), k1 = Math.min(nb - 1, k0 + 1);
    const t = Math.min(1, Math.max(0, f - k0));
    const line = mid[k0] * (1 - t) + mid[k1] * t;
    const shift = csRound(line - reference) + off;
    for (let k = 0; k < n; k++) {
      // Colonne bouchée : le filet de la voisine, centré sur sa rangée la plus sombre.
      const sy = Math.min(H - 1, Math.max(0, sx !== xx ? y0 + k + csRound(yd[sx] - reference) + off : y0 + k + shift));
      for (let c = 0; c < ch; c++) data[(dst + k) * stride + xx * ch + c] = img.data[sy * stride + sx * ch + c];
    }
  }
}

/** Arrondi comme Math.Round de C# (au pair le plus proche). */
function csRound(v) {
  const r = Math.round(v);
  return Math.abs(v % 1) === 0.5 && r % 2 !== 0 ? r - 1 : r;
}

/**
 * Rangée la plus nette pour prolonger les filets verticaux, parmi [s0, s0 + span[ : le plus de pixels sombres près des
 * filets du tableau (cols, ± 3 px) et le moins ailleurs (texte). À égalité, la plus haute.
 */
export function cleanRow(img, s0, span, cols) {
  const w = img.w, near = new Uint8Array(w);
  for (const c of cols ?? []) for (let x = Math.max(0, c - 3); x <= Math.min(w - 1, c + 3); x++) near[x] = 1;
  let best = Math.min(img.h - 1, Math.max(0, s0)), bestScore = -Infinity;
  for (let y = Math.max(0, s0); y < Math.min(img.h, s0 + span); y++) {
    let score = 0;
    for (let x = 0; x < w; x++) if (gray(img, x, y) < 150) score += near[x] ? 1 : -1;
    if (score > bestScore) { bestScore = score; best = y; }
  }
  return best;
}

function gray(img, x, y) {
  const o = (y * img.w + x) * img.ch, d = img.data;
  return img.ch === 1 ? d[o] : (d[o] * 77 + d[o + 1] * 150 + d[o + 2] * 29) >> 8;
}

export function paperOf(img, y) {
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
