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
    for (const p of b.Parts) if (p.Height > 0) seq.push(p.Gap ? { gap: true, s: p.Src, span: p.Span, cols: p.Cols, n: p.Height, x0: p.X0, x1: p.X1 } : { y0: p.Src, n: p.Height });
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
    if (from) { extra.span = from.span; extra.cols = from.cols; } // dans l'interligne d'une rangée ajoutée : même rangée source
    seq.splice(i, 0, extra);
    before += e.height;
  }
  return seq;
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
  for (const x of seq) {
    if (x.extra || x.gap) {
      const s = x.span ? cleanRow(base, x.s, x.span, x.cols) : x.s, paper = paperOf(base, s);
      const x0 = Math.min(w, Math.max(0, x.x0)), x1 = Math.min(w, Math.max(0, x.x1));
      for (let k = 0; k < x.n; k++, dst++)
        for (let xx = 0; xx < w; xx++) {
          const o = dst * stride + xx * ch;
          // Dans le tableau : les filets verticaux (sombres) sont prolongés, le reste devient papier propre.
          if (xx >= x0 && xx < x1 && gray(base, xx, s) < 150) for (let c = 0; c < ch; c++) data[o + c] = base.data[s * stride + xx * ch + c];
          else for (let c = 0; c < ch; c++) data[o + c] = paper[c];
        }
      continue;
    }
    const a = Math.min(base.h, Math.max(0, x.y0)), b = Math.min(base.h, a + x.n);
    if (b > a) data.set(base.data.subarray(a * stride, b * stride), dst * stride);
    dst += x.n;
  }
  return { w, h: height, ch, data };
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
