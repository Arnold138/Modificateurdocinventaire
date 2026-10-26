// Images en mémoire et redressement (portage fidèle du cœur C#).
// Raster = { w, h, ch, data } ; ch = 1 (gris) ou 4 (RGBA, ordre du canvas).
import { Quad, pt, roundHalfEven } from './geometry.js';

export const raster = (w, h, ch, data) => ({ w, h, ch, data: data ?? new Uint8ClampedArray(w * h * ch) });

export function white(w, h, ch) {
  const r = raster(w, h, ch);
  r.data.fill(255);
  return r;
}

export function toGray(r) {
  if (r.ch === 1) return r;
  const g = new Uint8ClampedArray(r.w * r.h);
  const d = r.data;
  for (let i = 0, j = 0; i < g.length; i++, j += 4) g[i] = (d[j + 2] * 29 + d[j + 1] * 150 + d[j] * 77) >> 8;
  return raster(r.w, r.h, 1, g);
}

export function toRgba(r) {
  if (r.ch === 4) return r;
  const d = new Uint8ClampedArray(r.w * r.h * 4);
  for (let i = 0, j = 0; i < r.data.length; i++, j += 4) { d[j] = d[j + 1] = d[j + 2] = r.data[i]; d[j + 3] = 255; }
  return raster(r.w, r.h, 4, d);
}

const clamp = (v, a, b) => (v < a ? a : v > b ? b : v);

/** Image outW × outH : chaque pixel (u, v) prend la couleur du point map(u, v) de la source (bilinéaire, blanc hors image). */
export function warp(src, outW, outH, map) {
  const ch = src.ch, sw = src.w, sh = src.h, s = src.data;
  const dst = new Uint8ClampedArray(outW * outH * ch);
  let o = 0;
  for (let v = 0; v < outH; v++) {
    for (let u = 0; u < outW; u++, o += ch) {
      const p = map(u, v);
      let x = p.X, y = p.Y;
      if (x < -0.5 || y < -0.5 || x > sw - 0.5 || y > sh - 0.5) { for (let c = 0; c < ch; c++) dst[o + c] = 255; continue; }
      x = clamp(x, 0, sw - 1); y = clamp(y, 0, sh - 1);
      const x0 = Math.trunc(x), y0 = Math.trunc(y);
      const x1 = Math.min(x0 + 1, sw - 1), y1 = Math.min(y0 + 1, sh - 1);
      const fx = x - x0, fy = y - y0;
      const i00 = (y0 * sw + x0) * ch, i10 = (y0 * sw + x1) * ch, i01 = (y1 * sw + x0) * ch, i11 = (y1 * sw + x1) * ch;
      for (let c = 0; c < ch; c++) {
        const top = s[i00 + c] + (s[i10 + c] - s[i00 + c]) * fx;
        const bot = s[i01 + c] + (s[i11 + c] - s[i01 + c]) * fx;
        dst[o + c] = clamp(roundHalfEven(top + (bot - top) * fy), 0, 255);
      }
    }
  }
  return raster(outW, outH, ch, dst);
}

export function resize(src, outW, outH) {
  if (outW === src.w && outH === src.h) return src;
  if (src.ch === 1 && outW < src.w && outH < src.h) return shrinkArea(src, outW, outH);
  const sx = src.w / outW, sy = src.h / outH;
  return warp(src, outW, outH, (u, v) => pt((u + 0.5) * sx - 0.5, (v + 0.5) * sy - 0.5));
}

function shrinkArea(src, outW, outH) {
  const dst = new Uint8ClampedArray(outW * outH);
  const sx = src.w / outW, sy = src.h / outH;
  for (let v = 0; v < outH; v++) {
    const y0 = Math.trunc(v * sy), y1 = Math.max(y0 + 1, Math.trunc((v + 1) * sy));
    for (let u = 0; u < outW; u++) {
      const x0 = Math.trunc(u * sx), x1 = Math.max(x0 + 1, Math.trunc((u + 1) * sx));
      let sum = 0, n = 0;
      for (let y = y0; y < y1 && y < src.h; y++)
        for (let x = x0; x < x1 && x < src.w; x++) { sum += src.data[y * src.w + x]; n++; }
      dst[v * outW + u] = n === 0 ? 255 : Math.floor(sum / n);
    }
  }
  return raster(outW, outH, 1, dst);
}

export function otsu(gray) {
  const hist = new Float64Array(256);
  for (const b of gray.data) hist[b]++;
  const total = gray.data.length;
  let sumAll = 0;
  for (let i = 0; i < 256; i++) sumAll += i * hist[i];
  let sumB = 0, best = -1, wB = 0, thr = 128;
  for (let t = 0; t < 256; t++) {
    wB += hist[t];
    if (wB === 0) continue;
    const wF = total - wB;
    if (wF === 0) break;
    sumB += t * hist[t];
    const mB = sumB / wB, mF = (sumAll - sumB) / wF;
    const between = wB * wF * (mB - mF) * (mB - mF);
    if (between > best) { best = between; thr = t; }
  }
  return thr;
}

/** Quarts de tour dans le sens horaire. */
export function rotate90(src, quarterTurns) {
  const q = ((quarterTurns % 4) + 4) % 4;
  if (q === 0) return src;
  const { w, h, ch } = src;
  const ow = q === 2 ? w : h, oh = q === 2 ? h : w;
  const dst = new Uint8ClampedArray(ow * oh * ch);
  for (let y = 0; y < h; y++)
    for (let x = 0; x < w; x++) {
      let nx, ny;
      if (q === 1) { nx = h - 1 - y; ny = x; } else if (q === 2) { nx = w - 1 - x; ny = h - 1 - y; } else { nx = y; ny = w - 1 - x; }
      const s = (y * w + x) * ch, d = (ny * ow + nx) * ch;
      for (let c = 0; c < ch; c++) dst[d + c] = src.data[s + c];
    }
  return raster(ow, oh, ch, dst);
}

/** Homographie qui envoie les 4 points dstPts sur srcPts. */
export function homographyPts(dstPts, srcPts) {
  const a = Array.from({ length: 8 }, () => new Float64Array(9));
  for (let i = 0; i < 4; i++) {
    const u = dstPts[i].X, v = dstPts[i].Y, x = srcPts[i].X, y = srcPts[i].Y, r = 2 * i;
    a[r][0] = u; a[r][1] = v; a[r][2] = 1; a[r][6] = -u * x; a[r][7] = -v * x; a[r][8] = x;
    a[r + 1][3] = u; a[r + 1][4] = v; a[r + 1][5] = 1; a[r + 1][6] = -u * y; a[r + 1][7] = -v * y; a[r + 1][8] = y;
  }
  const h = solve(a, 8);
  return (u, v) => {
    const d = h[6] * u + h[7] * v + 1;
    return pt((h[0] * u + h[1] * v + h[2]) / d, (h[3] * u + h[4] * v + h[5]) / d);
  };
}

/** Homographie qui envoie le rectangle de sortie sur le quadrilatère source. */
export const homography = (quad, outW, outH) =>
  homographyPts([pt(0, 0), pt(outW - 1, 0), pt(outW - 1, outH - 1), pt(0, outH - 1)], Quad.points(quad));

function solve(a, n) {
  for (let col = 0; col < n; col++) {
    let piv = col;
    for (let r = col + 1; r < n; r++) if (Math.abs(a[r][col]) > Math.abs(a[piv][col])) piv = r;
    if (Math.abs(a[piv][col]) < 1e-12) throw new Error('Recadrage impossible : coins alignés.');
    if (piv !== col) [a[col], a[piv]] = [a[piv], a[col]];
    for (let r = 0; r < n; r++) {
      if (r === col) continue;
      const f = a[r][col] / a[col][col];
      if (f === 0) continue;
      for (let c = col; c <= n; c++) a[r][c] -= f * a[col][c];
    }
  }
  const x = new Float64Array(n);
  for (let i = 0; i < n; i++) x[i] = a[i][n] / a[i][i];
  return x;
}

// ---------- Détection de la feuille ----------

export function detectPage(image) {
  const gray = toGray(image);
  const W = gray.w, H = gray.h;
  const scale = Math.min(1, 500 / Math.max(W, H));
  const w = Math.max(1, roundHalfEven(W * scale)), h = Math.max(1, roundHalfEven(H * scale));
  const small = resize(gray, w, h);
  const sx = W / w, sy = H / h;
  const full = Quad.full(W, H);

  const thr = otsu(small);
  const bright = new Uint8Array(w * h);
  let brightCount = 0;
  for (let i = 0; i < bright.length; i++) if (small.data[i] > thr) { bright[i] = 1; brightCount++; }
  if (brightCount > 0.97 * w * h) return full;
  // Scan, PDF, capture : le papier touche les quatre bords de l'image (une photo a un fond sombre autour).
  // Sans ce garde-fou, une grande case blanche d'un tableau qui touche les bords était prise pour la feuille.
  if (paperTouchesEdges(bright, w, h)) return full;

  const label = new Int32Array(w * h), queue = new Int32Array(w * h);
  let bestLabel = 0, bestSize = 0, current = 0;
  for (let start = 0; start < label.length; start++) {
    if (!bright[start] || label[start] !== 0) continue;
    current++;
    let head = 0, tail = 0, size = 0;
    queue[tail++] = start; label[start] = current;
    while (head < tail) {
      const p = queue[head++]; size++;
      const x = p % w, y = (p / w) | 0;
      if (x > 0 && bright[p - 1] && label[p - 1] === 0) { label[p - 1] = current; queue[tail++] = p - 1; }
      if (x < w - 1 && bright[p + 1] && label[p + 1] === 0) { label[p + 1] = current; queue[tail++] = p + 1; }
      if (y > 0 && bright[p - w] && label[p - w] === 0) { label[p - w] = current; queue[tail++] = p - w; }
      if (y < h - 1 && bright[p + w] && label[p + w] === 0) { label[p + w] = current; queue[tail++] = p + w; }
    }
    if (size > bestSize) { bestSize = size; bestLabel = current; }
  }
  if (bestSize < 0.2 * w * h) return full;

  let minS = Infinity, maxS = -Infinity, minD = Infinity, maxD = -Infinity;
  let tl, br, tr, bl;
  for (let y = 0; y < h; y++)
    for (let x = 0; x < w; x++) {
      if (label[y * w + x] !== bestLabel) continue;
      const s = x + y, d = x - y;
      if (s < minS) { minS = s; tl = pt(x, y); }
      if (s > maxS) { maxS = s; br = pt(x, y); }
      if (d > maxD) { maxD = d; tr = pt(x, y); }
      if (d < minD) { minD = d; bl = pt(x, y); }
    }
  const up = (p) => pt(clamp((p.X + 0.5) * sx - 0.5, 0, W - 1), clamp((p.Y + 0.5) * sy - 0.5, 0, H - 1));
  let quad = Quad.fromPoints([up(tl), up(tr), up(br), up(bl)]);
  quad = inset(quad, Math.max(sx, sy) * 1.5);
  if (Quad.area(quad) < 0.2 * W * H) return full;
  const tol = 0.02 * Math.max(W, H);
  const fp = Quad.points(full);
  const nearFull = Quad.points(quad).every((p, i) => Math.hypot(p.X - fp[i].X, p.Y - fp[i].Y) < tol);
  return nearFull ? full : quad;
}

/** Les quatre bords (à 2 px du bord de l'image réduite) sont clairs à 90 % au moins. */
export function paperTouchesEdges(bright, w, h) {
  if (w < 8 || h < 8) return false;
  const d = 2;
  const side = (n, at) => { let c = 0; for (let i = 0; i < n; i++) c += bright[at(i)]; return c * 10 >= n * 9; };
  return side(w, (x) => d * w + x) && side(w, (x) => (h - 1 - d) * w + x)
    && side(h, (y) => y * w + d) && side(h, (y) => y * w + (w - 1 - d));
}

function inset(q, d) {
  const p = Quad.points(q);
  const cx = p.reduce((s, a) => s + a.X, 0) / 4, cy = p.reduce((s, a) => s + a.Y, 0) / 4;
  const move = (a) => {
    const dx = cx - a.X, dy = cy - a.Y, len = Math.sqrt(dx * dx + dy * dy);
    return len < 1e-6 ? a : pt(a.X + (dx / len) * d, a.Y + (dy / len) * d);
  };
  return Quad.fromPoints(p.map(move));
}

// ---------- Inclinaison ----------

export function estimateSkew(image, maxDegrees = 5) {
  const gray = toGray(image);
  const thr = Math.min(otsu(gray), 160);
  const xs = [], ys = [];
  for (let y = 0; y < gray.h; y++)
    for (let x = 0; x < gray.w; x++)
      if (gray.data[y * gray.w + x] < thr) { xs.push(x); ys.push(y); }
  if (xs.length < 200) return 0;
  const step = Math.max(1, Math.trunc(xs.length / 250000));
  const score = (deg) => {
    const t = Math.tan((deg * Math.PI) / 180);
    const off = Math.ceil(Math.abs(t) * gray.w) + 2;
    const hist = new Float64Array(gray.h + 2 * off);
    for (let i = 0; i < xs.length; i += step) hist[roundHalfEven(ys[i] - xs[i] * t) + off]++;
    let s = 0;
    for (const v of hist) s += v * v;
    return s;
  };
  let best = 0, bestScore = score(0);
  for (let d = -maxDegrees; d <= maxDegrees + 1e-9; d += 0.1) {
    const sc = score(d);
    if (sc > bestScore * 1.0001) { bestScore = sc; best = d; }
  }
  const center = best;
  for (let d = center - 0.1; d <= center + 0.1 + 1e-9; d += 0.02) {
    const sc = score(d);
    if (sc > bestScore * 1.0001) { bestScore = sc; best = d; }
  }
  return roundHalfEven(best * 100) / 100;
}

// ---------- Redressement complet ----------

export const MAX_SIDE = 2400;

export function outputSize(quad) {
  const [w, h] = Quad.naturalSize(quad);
  const k = Math.min(1, MAX_SIDE / Math.max(w, h));
  return [Math.max(1, roundHalfEven(w * k)), Math.max(1, roundHalfEven(h * k))];
}

/** Perspective (4 coins) puis inclinaison, en un seul rééchantillonnage. */
export function normalize(source, crop = null) {
  const quad = crop ?? detectPage(source);
  const [w, h] = outputSize(quad);
  const preview = Math.min(1, 1400 / Math.max(w, h));
  const pw = Math.max(1, Math.trunc(w * preview)), ph = Math.max(1, Math.trunc(h * preview));
  const flat = warp(toGray(source), pw, ph, homography(quad, pw, ph));
  const skew = estimateSkew(flat);
  return { image: render(source, quad, skew, w, h), crop: quad, skew };
}

export function render(source, crop, skew, width, height) {
  const identity = Math.abs(skew) < 0.05 && width === source.w && height === source.h && Quad.equals(crop, Quad.full(source.w, source.h));
  if (identity) return source;
  const hmap = homography(crop, width, height);
  if (Math.abs(skew) < 0.05) return warp(source, width, height, hmap);
  const a = (skew * Math.PI) / 180, cos = Math.cos(a), sin = Math.sin(a), cx = width / 2, cy = height / 2;
  return warp(source, width, height, (u, v) => {
    const du = u - cx, dv = v - cy;
    return hmap(cx + cos * du - sin * dv, cy + sin * du + cos * dv);
  });
}
