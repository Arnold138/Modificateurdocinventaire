// Repère les filets (traits) des tableaux sur une page redressée. Portage de RuleDetector.cs.
import { Rect } from './geometry.js';
import { toGray, otsu } from './imaging.js';

const clamp = (v, a, b) => (v < a ? a : v > b ? b : v);

/** @returns {{Bounds, ColumnXs:number[], RowYs:number[]}[]} */
export function detectGrids(image) {
  const gray = toGray(image);
  const W = gray.w, H = gray.h;
  const thr = clamp(otsu(gray) + 30, 140, 200);
  const dark = new Uint8Array(W * H);
  for (let i = 0; i < dark.length; i++) dark[i] = gray.data[i] < thr ? 1 : 0;

  const gap = Math.max(6, Math.trunc(W / 150));
  const hRuns = horizontalRuns(dark, W, H, gap, Math.max(40, Math.trunc(W / 10)), 1);
  let tilted = null; // filets un peu penchés (±3 px), calculés seulement s'il le faut
  const vLines = verticalLines(dark, W, H, gap, Math.max(30, Math.trunc(H / 40)));

  const tables = [];
  for (const g0 of groupByVerticalOverlap(vLines)) {
    const maxLen = Math.max(...g0.map((l) => l.Y1 - l.Y0));
    const group = longColumns(g0, maxLen);
    if (group.length < 2) continue;
    const xs = cluster(group.map((l) => l.X), 6);
    if (xs.length < 2) continue;
    const top = Math.min(...group.map((l) => l.Y0)), bottom = Math.max(...group.map((l) => l.Y1));
    const left = xs[0], right = xs[xs.length - 1], width = right - left;
    if (width < W * 0.15) continue;

    const candidates = [];
    for (let y = Math.max(0, Math.trunc(top) - 8); y <= Math.min(H - 1, Math.trunc(bottom) + 8); y++) {
      let covered = coveredBy(hRuns[y], left, right);
      // Scan pas tout à fait droit : filet bien présent (60 %) et à 80 % en tolérant ±3 px (même règle que Windows).
      if (covered < 0.8 * width && covered >= 0.6 * width) {
        tilted ??= horizontalRuns(dark, W, H, gap, Math.max(40, Math.trunc(W / 10)), 3);
        covered = coveredBy(tilted[y], left, right);
      }
      if (covered >= 0.8 * width) candidates.push(y);
    }
    let ys = [];
    for (let i = 0; i < candidates.length; ) {
      let j = i;
      while (j + 1 < candidates.length && candidates[j + 1] - candidates[j] <= 2) j++;
      ys.push((candidates[i] + candidates[j]) / 2);
      i = j + 1;
    }
    if (ys.length === 0 || ys[0] > top + 8) ys.unshift(top);
    if (ys[ys.length - 1] < bottom - 8) ys.push(bottom);
    ys = mergeThinRows(cluster(ys, 8));
    if (ys.length < 2) continue;
    tables.push({ Bounds: Rect.fromEdges(left, ys[0], right, ys[ys.length - 1]), ColumnXs: xs, RowYs: ys });
  }
  return tables.map((t, i) => [t, i]).sort((a, b) => a[0].Bounds.Y - b[0].Bounds.Y || a[1] - b[1]).map((p) => p[0]);
}

function runs(line, n, gap, minLen) {
  const out = [];
  let start = -1, last = -1;
  for (let i = 0; i < n; i++) {
    if (!line[i]) continue;
    if (start < 0) { start = last = i; }
    else if (i - last <= gap + 1) last = i;
    else {
      if (last - start + 1 >= minLen) out.push([start, last]);
      start = last = i;
    }
  }
  if (start >= 0 && last - start + 1 >= minLen) out.push([start, last]);
  return out;
}

function coveredBy(rs, left, right) {
  let covered = 0;
  for (const [s, e] of rs) covered += Math.max(0, Math.min(e, right) - Math.max(s, left));
  return covered;
}

function horizontalRuns(dark, W, H, gap, minLen, reach) {
  const result = new Array(H);
  const band = new Uint8Array(W);
  for (let y = 0; y < H; y++) {
    const y0 = Math.max(0, y - reach), y1 = Math.min(H - 1, y + reach);
    for (let x = 0; x < W; x++) {
      let d = 0;
      for (let yy = y0; yy <= y1 && !d; yy++) d = dark[yy * W + x];
      band[x] = d ? 1 : 0;
    }
    result[y] = runs(band, W, gap, minLen);
  }
  return result;
}

export function verticalLines(dark, W, H, gap, minLen) {
  const segs = [];
  const band = new Uint8Array(H);
  for (let x = 0; x < W; x++) {
    for (let y = 0; y < H; y++)
      band[y] = dark[y * W + x] || (x > 0 && dark[y * W + x - 1]) || (x < W - 1 && dark[y * W + x + 1]) ? 1 : 0;
    for (const [s, e] of runs(band, H, gap, minLen)) segs.push({ x, s, e });
  }
  // Déjà triés par x puis s (ordre de parcours).
  const lines = [];
  for (const seg of segs) {
    let found = -1;
    for (let i = lines.length - 1; i >= 0; i--) {
      const l = lines[i];
      if (Math.abs(l.sumX / l.n - seg.x) <= 3 && seg.s <= l.y1 + 15 && seg.e >= l.y0 - 15) { found = i; break; }
    }
    if (found < 0) lines.push({ sumX: seg.x, n: 1, y0: seg.s, y1: seg.e, parts: [[seg.s, seg.e]] });
    else {
      const l = lines[found];
      l.parts.push([seg.s, seg.e]);
      l.sumX += seg.x; l.n++; l.y0 = Math.min(l.y0, seg.s); l.y1 = Math.max(l.y1, seg.e);
    }
  }
  const minLine = Math.max(50, H * 0.03);
  return lines
    .filter((l) => covered(l.parts) >= 0.6 * (l.y1 - l.y0))
    .map((l) => ({ X: l.sumX / l.n, Y0: l.y0, Y1: l.y1 }))
    .filter((l) => l.Y1 - l.Y0 >= minLine && l.X > W * 0.01 && l.X < W * 0.99);
}

function covered(parts) {
  let total = 0, curS = -1, curE = -2;
  for (const [s, e] of [...parts].sort((a, b) => a[0] - b[0])) {
    if (s > curE + 1) { if (curE >= curS && curS >= 0) total += curE - curS + 1; curS = s; curE = e; }
    else curE = Math.max(curE, e);
  }
  if (curS >= 0) total += curE - curS + 1;
  return total;
}

function groupByVerticalOverlap(lines) {
  const parent = lines.map((_, i) => i);
  const find = (i) => (parent[i] === i ? i : (parent[i] = find(parent[i])));
  for (let i = 0; i < lines.length; i++)
    for (let j = i + 1; j < lines.length; j++) {
      const a = lines[i], b = lines[j];
      const overlap = Math.min(a.Y1, b.Y1) - Math.max(a.Y0, b.Y0);
      if (overlap >= 0.5 * Math.min(a.Y1 - a.Y0, b.Y1 - b.Y0)) parent[find(i)] = find(j);
    }
  const groups = new Map();
  lines.forEach((l, i) => {
    const r = find(i);
    if (!groups.has(r)) groups.set(r, []);
    groups.get(r).push(l);
  });
  return [...groups.values()];
}

/**
 * Trame (fond hachuré d'un en-tête ou d'un total) lue comme plusieurs filets serrés : une suite de rangées plus fines
 * qu'une ligne de texte (moins de 70 % de la rangée médiane) ne fait qu'une rangée ; on garde son premier et son dernier filet.
 */
export function mergeThinRows(ys) {
  if (ys.length < 5) return ys;
  const gaps = ys.slice(1).map((y, i) => y - ys[i]).sort((a, b) => a - b);
  const min = 0.7 * gaps[Math.trunc(gaps.length / 2)];
  return ys.filter((y, i) => !(i > 0 && i + 1 < ys.length && y - ys[i - 1] < min && ys[i + 1] - y < min));
}

/**
 * Filets de colonne d'un groupe : ceux d'au moins la moitié du plus long, ou les morceaux d'un filet coupé (même abscisse)
 * qui couvrent ensemble cette moitié (même règle que Windows : bord gauche effacé par endroits sur le scan).
 */
export function longColumns(g0, maxLen) {
  const sorted = [...g0].sort((a, b) => a.X - b.X);
  const result = [], buckets = [];
  for (let i = 0; i < sorted.length;) {
    let j = i;
    while (j + 1 < sorted.length && sorted[j + 1].X - sorted[j].X <= 6) j++;
    const bucket = sorted.slice(i, j + 1);
    buckets.push(bucket);
    const tall = bucket.filter((l) => l.Y1 - l.Y0 >= 0.5 * maxLen);
    if (tall.length > 0) result.push(...tall);
    else if (bucket.length > 1 && union(bucket) >= 0.5 * maxLen) result.push(...bucket);
    i = j + 1;
  }
  // Bord gauche tracé seulement sur une partie du tableau : le plus à gauche compte dès 45 % s'il touche le haut ou le bas.
  if (result.length >= 2 && buckets.length > 0 && !result.includes(buckets[0][0])) {
    const first = buckets[0];
    const top = Math.min(...result.map((l) => l.Y0)), bottom = Math.max(...result.map((l) => l.Y1));
    if (Math.max(...first.map((l) => l.X)) < Math.min(...result.map((l) => l.X)) && union(first) >= 0.45 * maxLen
      && (Math.abs(Math.max(...first.map((l) => l.Y1)) - bottom) <= 12 || Math.abs(Math.min(...first.map((l) => l.Y0)) - top) <= 12))
      result.push(...first);
  }
  return result;
}

function union(lines) {
  let total = 0, s = NaN, e = NaN;
  for (const l of [...lines].sort((a, b) => a.Y0 - b.Y0)) {
    if (Number.isNaN(s) || l.Y0 > e) { if (!Number.isNaN(s)) total += e - s; s = l.Y0; e = l.Y1; }
    else e = Math.max(e, l.Y1);
  }
  if (!Number.isNaN(s)) total += e - s;
  return total;
}

export function cluster(values, tol) {
  const sorted = [...values].sort((a, b) => a - b);
  const result = [];
  let bucket = [];
  for (const v of sorted) {
    if (bucket.length > 0 && v - bucket[bucket.length - 1] > tol) { result.push(avg(bucket)); bucket = []; }
    bucket.push(v);
  }
  if (bucket.length > 0) result.push(avg(bucket));
  return result;
}

const avg = (a) => a.reduce((s, v) => s + v, 0) / a.length;
