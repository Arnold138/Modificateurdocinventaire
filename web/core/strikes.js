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

/** Le texte a été modifié : les traits suivent les caractères. */
export function adjustStrikes(f, oldText, newText) {
  if (!f.Strikes?.length || oldText === newText) return;
  let p = 0;
  while (p < oldText.length && p < newText.length && oldText[p] === newText[p]) p++;
  let sfx = 0;
  while (sfx < oldText.length - p && sfx < newText.length - p && oldText[oldText.length - 1 - sfx] === newText[newText.length - 1 - sfx]) sfx++;
  const oldEnd = oldText.length - sfx, ins = newText.length - sfx - p, delta = newText.length - oldText.length;
  const mapStart = (x) => (x <= p ? x : x >= oldEnd ? x + delta : p + ins);
  const mapEnd = (x) => (x <= p ? x : x >= oldEnd ? x + delta : p);
  for (const s of f.Strikes) {
    const a = mapStart(s.Start), b = mapEnd(s.Start + s.Length);
    s.Start = a;
    s.Length = b - a;
  }
  normalize(f, newText.length);
}

function normalize(f, n = f.Text.length) {
  const clamp = (v) => Math.min(Math.max(v, 0), n);
  const list = strikesOf(f)
    .map((s) => ({ Start: clamp(s.Start), Length: clamp(s.Start + s.Length) - clamp(s.Start), Color: s.Color }))
    .filter((s) => s.Length > 0)
    .sort((a, b) => a.Start - b.Start);
  const merged = [];
  for (const s of list) {
    const last = merged[merged.length - 1];
    if (last && last.Color === s.Color && s.Start <= last.Start + last.Length) last.Length = Math.max(last.Length, s.Start + s.Length - last.Start);
    else merged.push(s);
  }
  f.Strikes = merged;
}

export const strikeColor = (c) => (c === BLACK ? '#000000' : '#d40000');
