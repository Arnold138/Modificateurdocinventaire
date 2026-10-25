// Totaux recalculés : une case de total suit les changements de sa rubrique (valeurs modifiées, lots 0 retirés),
// tant qu'on ne l'a pas tapée à la main (Field.Manual). « Recalculer » la remet en automatique.
// Total = total imprimé + écarts : une erreur de lecture sur une ligne qu'on ne touche pas ne fausse rien.
// Récapitulatif (« II - STOCK  44 550  9 000 ») : suit le total de sa rubrique ; TOTAL GENERAL suit le récapitulatif.
// Portage identique dans Totals.cs.
import { firstText, isRomanHeading } from './model.js';
import { rowOrder, hasLotCell, isZeroLot } from './lots.js';
import { deletedLines } from './arrange.js';

const blank = (s) => (s ?? '').trim().length === 0;

/** « 15 000 » → 15000 ; « mémoire » → 0 ; « S 500 » (lecture) → 5500 ; null si ce n'est pas un montant. */
export function parseAmount(s) {
  let t = (s ?? '').trim();
  if (t.length === 0) return null;
  if (/^m[ée]moire$/i.test(t)) return 0;
  t = t.replace(/[\s  ]/g, '').replace(/€$/, '');
  if (!/\d/.test(t)) return null;
  if (/^[+-]?[\dSOolI]+(?:[.,]\d{1,2})?$/.test(t)) t = t.replace(/S/g, '5').replace(/[Oo]/g, '0').replace(/[lI]/g, '1');
  if (!/^[+-]?\d+(?:[.,]\d{1,2})?$/.test(t)) return null;
  return Math.round(parseFloat(t.replace(',', '.')) * 100);
}

/** 19831000 (centimes) → « 198 310 » ; décimales à la virgule si besoin. */
export function formatAmount(cents) {
  const neg = cents < 0;
  let v = Math.abs(cents);
  const units = Math.trunc(v / 100), dec = v % 100;
  const digits = String(units);
  let grouped = '';
  for (let i = 0; i < digits.length; i++) {
    if (i > 0 && (digits.length - i) % 3 === 0) grouped += ' ';
    grouped += digits[i];
  }
  return (neg ? '-' : '') + grouped + (dec ? ',' + String(dec).padStart(2, '0') : '');
}

/** Nom d'une rubrique, sans son numéro romain ni accents : « II - STOCK » → « STOCK ». */
export function sectionName(s) {
  return (s ?? '').replace(/^\s*[IVX]{1,5}\s*[-–.]\s*/, '')
    .normalize('NFD').replace(/\p{Mn}/gu, '').toUpperCase().replace(/[^A-Z]/g, '');
}

/** Ligne de récapitulatif : rubrique numérotée (« II - STOCK ») avec des montants, hors en-tête. */
export const isRecapLine = (l) => l.Kind !== 'EnTete' && l.Kind !== 'Total' && isRomanHeading(firstText(l)) && l.Cells.slice(1).some((c) => !blank(c.Text));

/** Case calculée : montant d'une ligne de total ou de récapitulatif. */
export const isComputedCell = (line, f) => f.Column > 0 && (line.Kind === 'Total' || isRecapLine(line));

/** Lignes du document dans l'ordre affiché. */
function documentLines(project) {
  const out = [];
  for (const page of project.Pages)
    for (const t of page.Tables)
      for (const r of rowOrder(t))
        for (const l of t.Rows[r].Lines) if (!l.OutsideTable && l.Kind !== 'Ignoree') out.push(l);
  return out;
}

/** Cases retirées des totaux : objets au lot 0, leurs lignes de suite et les valeurs de titre qu'ils reprennent. */
function removedCells(project, lines, plan) {
  const removed = new Set();
  for (const u of plan?.units ?? []) {
    for (const l of u.lines) for (const c of l.Cells) removed.add(c);
    if (u.title) for (const c of u.title.Cells) if (c.Column > 0) removed.add(c);
  }
  for (const l of lines) if (hasLotCell(l) && isZeroLot(l)) for (const c of l.Cells) removed.add(c);
  for (const l of deletedLines(project)) for (const c of l.Cells) removed.add(c);
  return removed;
}

/**
 * Recalcule les cases de total et de récapitulatif qui ne sont pas tapées à la main.
 * `plan` (facultatif) : la mise en page des lots 0 (pour retirer aussi les valeurs de titre qu'ils emportent).
 * Renvoie les cases dont le texte a changé.
 */
export function recomputeTotals(project, plan = null) {
  const lines = documentLines(project);
  const removed = removedCells(project, lines, plan);
  const changed = [];
  const cols = (l) => l.Cells.filter((c) => c.Column > 0);
  // Ligne dupliquée (rangée ajoutée) : ses montants ne sont pas dans le total imprimé, ils comptent en entier.
  const copied = new Set(lines.filter((l) => l.Copy).flatMap((l) => l.Cells));
  const delta = (f) => {
    const o = copied.has(f) ? 0 : parseAmount(f.OriginalText) ?? 0;
    if (removed.has(f)) return -o;
    return (parseAmount(f.Text) ?? 0) - o;
  };
  const current = (f) => (removed.has(f) ? 0 : parseAmount(f.Text) ?? 0);
  const setAuto = (f, d, fallback) => {
    if (f.Manual) return;
    let text = f.OriginalText;
    if (d !== 0) {
      const o = parseAmount(f.OriginalText);
      text = o !== null ? formatAmount(o + d) : formatAmount(fallback());
    }
    if (f.Text !== text) { f.Text = text; changed.push(f); }
  };
  const cell = (l, c) => l.Cells.find((x) => x.Column === c);

  // Rangée de chaque total : de l'en-tête ou du total précédent jusqu'à lui.
  const totals = [];
  let start = 0;
  lines.forEach((l, i) => {
    if (l.TotalScope) return; // ligne TOTAL ajoutée : calculée à part, ne coupe pas les rubriques imprimées
    if (l.Kind === 'EnTete') start = i + 1;
    else if (l.Kind === 'Total') { totals.push({ line: l, items: lines.slice(start, i).filter((x) => x.Kind !== 'EnTete' && !x.TotalScope) }); start = i + 1; }
  });
  const computeTotals = () => {
    for (const { line, items } of totals)
      for (const f of cols(line)) {
        const d = items.reduce((s, l) => { const c = cell(l, f.Column); return s + (c ? delta(c) : 0); }, 0);
        setAuto(f, d, () => items.reduce((s, l) => { const c = cell(l, f.Column); return s + (c ? current(c) : 0); }, 0));
      }
  };

  // Rubriques : de leur en-tête (« II - STOCK ») au suivant.
  const sections = new Map();
  let cur = null;
  for (const l of lines) {
    if (l.TotalScope) continue;
    if (l.Kind === 'EnTete') {
      // Rubrique (en-tête répété sur la page suivante : la même rubrique continue).
      const name = sectionName(firstText(l));
      cur = name.length === 0 ? null : sections.get(name) ?? { items: [], total: null };
      if (cur) sections.set(name, cur);
      continue;
    }
    if (!cur) continue;
    if (l.Kind === 'Total') { cur.total ??= l; continue; }
    if (!isRecapLine(l)) cur.items.push(l);
  }

  computeTotals();
  for (const l of lines) {
    if (!isRecapLine(l)) continue;
    const sec = sections.get(sectionName(firstText(l)));
    if (!sec) continue;
    for (const f of cols(l)) {
      const t = sec.total ? cell(sec.total, f.Column) : null;
      const d = t ? (parseAmount(t.Text) ?? 0) - (parseAmount(t.OriginalText) ?? 0)
        : sec.items.reduce((s, x) => { const c = cell(x, f.Column); return s + (c ? delta(c) : 0); }, 0);
      setAuto(f, d, () => (t ? parseAmount(t.Text) ?? 0 : sec.items.reduce((s, x) => { const c = cell(x, f.Column); return s + (c ? current(c) : 0); }, 0)));
    }
  }
  computeTotals();
  addedTotals(project, cols, cell, current, changed);
  return changed;
}

/**
 * Lignes TOTAL ajoutées (« Ajouter une ligne TOTAL ») : somme des montants actuels, colonne par colonne, selon leur choix :
 * 'above' (les lignes au-dessus dans le tableau, depuis l'en-tête ou le total précédent), 'page' (toute la page),
 * 'pages' (pages choisies), 'all' (tout le document). Lots 0 et lignes supprimées : comptés 0. Auto = false : à taper.
 */
function addedTotals(project, cols, cell, current, changed) {
  const countable = (x) => !x.OutsideTable && (x.Kind === 'Article' || x.Kind === 'Titre') && !x.TotalScope && !isRecapLine(x);
  const linesOf = (pg) => pg.Tables.flatMap((t) => rowOrder(t).flatMap((r) => t.Rows[r].Lines));
  for (const page of project.Pages)
    for (const t of page.Tables) {
      const ordered = rowOrder(t).flatMap((r) => t.Rows[r].Lines);
      ordered.forEach((l, i) => {
        const sc = l.TotalScope;
        if (!sc || sc.Auto === false) return;
        let items;
        if (sc.Mode === 'page') items = linesOf(page);
        else if (sc.Mode === 'all') items = project.Pages.flatMap(linesOf);
        else if (sc.Mode === 'pages') items = project.Pages.filter((pg) => (sc.Pages ?? []).includes(pg.Id)).flatMap(linesOf);
        else {
          let from = 0;
          for (let j = i - 1; j >= 0; j--) if (ordered[j].Kind === 'EnTete' || ordered[j].Kind === 'Total') { from = j + 1; break; }
          items = ordered.slice(from, i);
        }
        items = items.filter(countable);
        for (const f of cols(l)) {
          if (f.Manual) continue;
          const text = formatAmount(items.reduce((sum, x) => { const c = cell(x, f.Column); return sum + (c ? current(c) : 0); }, 0));
          if (f.Text !== text) { f.Text = text; changed.push(f); }
        }
      });
    }
}

/** Montant calculé d'une case (pour afficher l'écart avec un total tapé à la main). */
export function computedValue(project, plan, f) {
  const was = f.Manual, text = f.Text;
  f.Manual = false;
  recomputeTotals(project, plan);
  const value = f.Text;
  f.Manual = was;
  f.Text = text;
  recomputeTotals(project, plan);
  return value;
}
