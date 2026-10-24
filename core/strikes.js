// Texte barré, comme dans Word : un trait fin au milieu des lettres sur le morceau choisi. Portage de Strikes.cs.
// Le .txt n'est pas touché ; seuls l'écran et le JPEG montrent le trait.
export const RED = 'red';
export const BLACK = 'black';

const strikesOf = (f) => (f.Strikes ??= []);

export function isStruck(f, start, end) {
  const list = strikesOf(f);
  for (let i = start; i < end; i++) if (!list.some((s) => i >= s.Start && i < s.Start + s.Length)) return false;
  return end > start;
}

/** Bouton « Barrer » : barre la sélection (tout le texte sans sélection), ou enlève le trait si elle l'était déjà. */
export function toggleStrike(f, start, end, color) {
  if (end <= start) { start = 0; end = f.Text.length; }
  start = Math.min(Math.max(start, 0), f.Text.length);
  end = Math.min(Math.max(end, start), f.Text.length);
  if (end === start) return;
  const struck = isStruck(f, start, end);
  cut(f, start, end);
  if (!struck) f.Strikes.push({ Start: start, Length: end - start, Color: color === BLACK ? BLACK : RED });
  normalize(f);
}

function cut(f, start, end) {
  const kept = [];
  for (const s of strikesOf(f)) {
    const a = s.Start, b = s.Start + s.Length;
    if (b <= start || a >= end) { kept.push(s); continue; }
    if (a < start) kept.push({ Start: a, Length: start - a, Color: s.Color });
    if (b > end) kept.push({ Start: end, Length: b - end, Color: s.Color });
  }
  f.Strikes = kept;
}

/** Le texte a été modifié : les traits et les couleurs suivent les caractères. */
export function adjustStrikes(f, oldText, newText) {
  adjustList(f, 'Strikes', oldText, newText);
  adjustList(f, 'Colors', oldText, newText);
}

function adjustList(f, key, oldText, newText) {
  if (!f[key]?.length || oldText === newText) return;
  let p = 0;
  while (p < oldText.length && p < newText.length && oldText[p] === newText[p]) p++;
  let sfx = 0;
  while (sfx < oldText.length - p && sfx < newText.length - p && oldText[oldText.length - 1 - sfx] === newText[newText.length - 1 - sfx]) sfx++;
  const oldEnd = oldText.length - sfx, ins = newText.length - sfx - p, delta = newText.length - oldText.length;
  const mapStart = (x) => (x <= p ? x : x >= oldEnd ? x + delta : p + ins);
  const mapEnd = (x) => (x <= p ? x : x >= oldEnd ? x + delta : p);
  for (const s of f[key]) {
    const a = mapStart(s.Start), b = mapEnd(s.Start + s.Length);
    s.Start = a;
    s.Length = b - a;
  }
  normalize(f, newText.length, key);
}

function normalize(f, n = f.Text.length, key = 'Strikes') {
  const clamp = (v) => Math.min(Math.max(v, 0), n);
  const list = (f[key] ??= [])
    .map((s) => ({ Start: clamp(s.Start), Length: clamp(s.Start + s.Length) - clamp(s.Start), Color: s.Color }))
    .filter((s) => s.Length > 0)
    .sort((a, b) => a.Start - b.Start);
  const merged = [];
  for (const s of list) {
    const last = merged[merged.length - 1];
    if (last && last.Color === s.Color && s.Start <= last.Start + last.Length) last.Length = Math.max(last.Length, s.Start + s.Length - last.Start);
    else merged.push(s);
  }
  f[key] = merged;
}

export const strikeColor = (c) => (c === BLACK ? '#000000' : '#d40000');

// ---------- Couleur du texte (rouge, bleu, vert) : sur la sélection, ou toute la case ----------

export const TEXT_COLORS = { red: '#d40000', blue: '#0050c8', green: '#107c10' };
export const textColor = (c) => TEXT_COLORS[c] ?? '#000000';

/** La sélection est-elle entièrement de cette couleur ? */
export function hasColor(f, start, end, color) {
  const list = f.Colors ?? [];
  for (let i = start; i < end; i++) if (!list.some((s) => s.Color === color && i >= s.Start && i < s.Start + s.Length)) return false;
  return end > start;
}

/**
 * Met la sélection (toute la case sans sélection) dans une couleur ; « noir » (ou la même couleur une 2e fois)
 * remet l'écriture normale.
 */
export function setColor(f, start, end, color) {
  if (end <= start) { start = 0; end = f.Text.length; }
  start = Math.min(Math.max(start, 0), f.Text.length);
  end = Math.min(Math.max(end, start), f.Text.length);
  if (end === start) return false;
  const remove = !TEXT_COLORS[color] || hasColor(f, start, end, color);
  const kept = [];
  for (const s of f.Colors ?? []) {
    const a = s.Start, b = s.Start + s.Length;
    if (b <= start || a >= end) { kept.push(s); continue; }
    if (a < start) kept.push({ Start: a, Length: start - a, Color: s.Color });
    if (b > end) kept.push({ Start: end, Length: b - end, Color: s.Color });
  }
  if (!remove) kept.push({ Start: start, Length: end - start, Color: color });
  f.Colors = kept;
  normalize(f, f.Text.length, 'Colors');
  return !remove;
}

/** Morceaux du texte avec leur couleur (null = noir), dans l'ordre. */
export function colorRuns(f, from = 0, to = f.Text.length) {
  const cuts = new Set([from, to]);
  for (const s of f.Colors ?? []) { cuts.add(Math.min(Math.max(s.Start, from), to)); cuts.add(Math.min(Math.max(s.Start + s.Length, from), to)); }
  const xs = [...cuts].sort((a, b) => a - b);
  const runs = [];
  for (let i = 0; i + 1 < xs.length; i++) {
    const a = xs[i], b = xs[i + 1];
    if (b <= a) continue;
    const c = (f.Colors ?? []).find((s) => a >= s.Start && a < s.Start + s.Length)?.Color ?? null;
    const last = runs[runs.length - 1];
    if (last && last.color === c && last.end === a) last.end = b;
    else runs.push({ start: a, end: b, color: c });
  }
  return runs;
}

export const hasColors = (f) => (f.Colors?.length ?? 0) > 0;
