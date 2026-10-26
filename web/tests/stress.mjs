// Banc d'essai : des centaines de manipulations comme à l'écran (lignes ajoutées, séparées, dupliquées, supprimées, texte
// tapé sur plusieurs lignes, mots ajoutés et enlevés, montants, lots, lots 0, rangées et lignes déplacées, lignes TOTAL…),
// avec contrôle de la mise en page après chaque pas. La liste des manipulations est enregistrée (ops.json) pour que la
// version Windows rejoue exactement la même (RetoucheInventaire.Core.Tests stress-replay) : le .txt et les pages doivent
// être identiques à l'octet près.
//
//   node web/tests/stress.mjs <projet.json> <dossier des images p1.pgm…> <dossier de sortie> [nombre] [graine]
import { readFileSync, writeFileSync, mkdirSync, existsSync } from 'node:fs';
import { join } from 'node:path';
import { gunzipSync } from 'node:zlib';
import { raster } from '../core/imaging.js';
import { upgradePage, firstText, isModified } from '../core/model.js';
import { buildTxt } from '../core/exports.js';
import {
  objectLines, lotsActive, setLot, resyncLots, addLot, createEmptyLots, fillLotsAutomatically, numberLots, isZeroLot,
  hasLotCell, itemTable, addLotHere, rowOrder, lotTables, setLotTables, defaultLotChoice, columnWidth, numberBox,
} from '../core/lots.js';
import { drawLotColumns, framedRows, frames, frameToggles } from '../core/lotcolumn.js';
import { setExtra, stretch, extraFor, baseHeight, tallExtra } from '../core/layout.js';
import { addBlankPage, blankBase, canBeModel, addColumn, removeColumn, canAddColumn, canRemoveColumn, addedTable, addRows } from '../core/addpage.js';
import { planArrangement, composePage, unitCrop, viewOf, syncEndPages } from '../core/arrange.js';
import { recomputeTotals } from '../core/totals.js';
import {
  copyLine, addBlankLine, splitAt, splitBelow, splitTypedText, removeAddedRow, canCopyLine, canSplit, canSplitBelow,
  addedRowOf, locate, addTotalLine, trimRow, untrimRow, trimsOf, rowTrimOf,
} from '../core/rows.js';

const [projectPath, imagesDir, outDir, countArg, seedArg] = process.argv.slice(2);
const COUNT = Number(countArg ?? 600), SEED = Number(seedArg ?? 7);

export function readPgm(path) {
  const raw = readFileSync(path);
  const b = raw[0] === 0x1f && raw[1] === 0x8b ? gunzipSync(raw) : raw;
  let pos = 0;
  const token = () => {
    while (/\s/.test(String.fromCharCode(b[pos]))) pos++;
    const s = pos;
    while (!/\s/.test(String.fromCharCode(b[pos]))) pos++;
    return String.fromCharCode(...b.subarray(s, pos));
  };
  token(); const w = +token(), h = +token(); token(); pos++;
  return raster(w, h, 1, new Uint8ClampedArray(b.subarray(pos, pos + w * h)));
}
const writePgm = (path, img) => {
  const g = new Uint8Array(img.w * img.h);
  for (let i = 0, j = 0; i < g.length; i++, j += img.ch) g[i] = img.data[j];
  writeFileSync(path, Buffer.concat([Buffer.from(`P5\n${img.w} ${img.h}\n255\n`), Buffer.from(g)]));
};

// ---------- État ----------

const project = JSON.parse(readFileSync(projectPath, 'utf8'));
for (const p of project.Pages) upgradePage(p);
const images = new Map(project.Pages.map((p, k) => [p.Id, readPgm(join(imagesDir, `p${k + 1}.pgm`))]));
const baseH = new Map(project.Pages.map((p) => [p.Id, baseHeight(p)]));
// Pages ajoutées : image de la source du modèle, redessinée vierge (comme restoreFlat dans l'application).
const raw = new Map(images);
const imageOf = (pg) => (pg.Added ? blankBase(raw.get(pg.Id), pg) : images.get(pg.Id));

let rnd = SEED >>> 0;
const random = () => { rnd = (rnd + 0x6d2b79f5) >>> 0; let t = rnd; t = Math.imul(t ^ (t >>> 15), t | 1); t ^= t + Math.imul(t ^ (t >>> 7), t | 61); return ((t ^ (t >>> 14)) >>> 0) / 4294967296; };
const pick = (a) => a[Math.floor(random() * a.length)];
const chance = (p) => random() < p;

const pages = () => project.Pages;
const gridTable = (t) => t.HasGrid !== false && t.RowYs?.length >= 2 && t.ColumnXs?.length >= 2;

/** Lignes visibles des pages normales : { p, ti, o, i, page, table, row, line } (o : rang dans l'ordre affiché). */
function targets(filter = () => true) {
  const out = [];
  pages().forEach((page, p) => {
    if (page.EndLots) return;
    page.Tables.forEach((table, ti) => {
      if (!gridTable(table)) return;
      rowOrder(table).forEach((r, o) => {
        if (r + 1 >= table.RowYs.length) return;
        table.Rows[r].Lines.forEach((line, i) => { const x = { p, ti, o, i, page, table, row: table.Rows[r], r, line }; if (filter(x)) out.push(x); });
      });
    });
  });
  return out;
}
const resolve = (t) => {
  const page = pages()[t.p], table = page.Tables[t.ti], r = rowOrder(table)[t.o];
  return { page, table, r, row: table.Rows[r], line: table.Rows[r].Lines[t.i] };
};
const ref = (x) => ({ p: x.p, ti: x.ti, o: x.o, i: x.i });

// Comme l'écran : après chaque action, cases de lot, mise en page (pages « lots de fin »), totaux.
const extraOf = (line) => Math.max(0, tallExtra(line), ...line.Cells.filter((f) => f.Text.includes('\n') && !f.Locked)
  .map((f) => extraFor(line, f, f.Text.split('\n').length, f.FontSize)));
function after() {
  resyncLots(project);
  syncEndPages(project);
  const plan = planArrangement(project);
  recomputeTotals(project, plan);
  return plan;
}

// ---------- Manipulations ----------

let tokenN = 0;
const word = () => `mot${++tokenN}`;
const live = new Set(); // mots tapés qui doivent rester dans le projet

const ACTIONS = {
  // Texte tapé sur plusieurs lignes (Entrée) dans la désignation : la rangée grandit.
  type(t, a) {
    const { page, line } = resolve(t);
    const f = line.Cells[0];
    f.Text = f.Text + a.lines.map((w) => '\n' + w).join('');
    setExtra(page, line, extraOf(line));
  },
  addWord(t, a) {
    const { page, line } = resolve(t);
    line.Cells[0].Text = line.Cells[0].Text.replace(/\s*$/, '') + ' ' + a.w;
    setExtra(page, line, extraOf(line));
  },
  removeWord(t, a) {
    const { page, line } = resolve(t);
    line.Cells[0].Text = line.Cells[0].Text.replace(new RegExp(`(?<![A-Za-z0-9_])${a.w}(?![A-Za-z0-9_])`, 'g'), '').replace(/ {2,}/g, ' ').replace(/\n\s*\n/g, '\n').trim();
    setExtra(page, line, extraOf(line));
  },
  value(t, a) { const { line } = resolve(t); const c = line.Cells[a.c]; if (c) c.Text = a.v; },
  typed(t, a) {
    const { page, line } = resolve(t);
    if (canCopyLine(page, line)) trimRow(page, line);
    const show = splitTypedText(page, line, a.from);
    if (show) { setExtra(page, line, extraOf(line)); setExtra(page, show, extraOf(show)); }
  },
  split(t) {
    const { page, line } = resolve(t);
    const at = locate(page, line), prev = at && at.i > 0 ? at.row.Lines[at.i - 1] : null;
    if (splitAt(page, line) && prev) { trimRow(page, prev); trimRow(page, line); }
  },
  splitBelow(t) {
    const { page, line } = resolve(t);
    const at = locate(page, line), next = at?.row.Lines[at.i + 1] ?? null;
    if (splitBelow(page, line) && next) { trimRow(page, line); trimRow(page, next); }
  },
  copy(t) {
    const { page, line } = resolve(t);
    if (canCopyLine(page, line)) trimRow(page, line);
    const c = copyLine(page, line);
    if (c) setExtra(page, c, extraOf(c));
  },
  blank(t, a) {
    const { page, line } = resolve(t);
    if (canCopyLine(page, line)) trimRow(page, line);
    const b = addBlankLine(page, line, a.above);
    if (b && a.text) b.Cells[0].Text = a.text;
    return b;
  },
  removeAdded(t) { const { page, line } = resolve(t); removeAddedRow(page, line); },
  // Page ajoutée : vierge sur le modèle de la page de la ligne, placée en `at`.
  addPage(t, a) {
    const model = pages()[t.p];
    const pg = addBlankPage(project, model, a.at, raw.get(model.Id), a.id, !!a.empty);
    if (pg) { raw.set(pg.Id, raw.get(model.Id)); baseH.set(pg.Id, baseHeight(pg)); }
    return pg;
  },
  // « Ajouter des lignes » : n rangées vierges ; la suite sur une page blanche si la page est pleine.
  addRows(t, a) {
    const { page, line } = resolve(t);
    const H0 = page.Height, base = baseHeight(page);
    const res = addRows(project, page, line, a.n, { above: a.above, img: raw.get(page.Id), id: a.id });
    for (const pg of res.pages) { raw.set(pg.Id, raw.get(page.Id)); baseH.set(pg.Id, baseHeight(pg)); }
    lastRows = { want: a.n, res, grew: page.Height > Math.max(H0, base) && canBeModel(page) };
    return res;
  },
  removePage(t) { if (pages()[t.p]?.Added) pages().splice(t.p, 1); },
  fill(t, a) { const { line } = resolve(t); line.Cells[0].Text = `1 objet ${a.w}`; line.Cells.slice(1).forEach((c, i) => { c.Text = a.v[i] ?? ''; }); },
  addCol(t, a) { const { page, table } = resolve(t); addColumn(page, table, a.c); },
  removeCol(t, a) { const { page, table } = resolve(t); removeColumn(page, table, a.k); },
  taller(t) { const { page, line } = resolve(t); line.Tall = (line.Tall ?? 0) + 1; setExtra(page, line, extraOf(line)); },
  shorter(t) { const { page, line } = resolve(t); line.Tall = Math.max(0, (line.Tall ?? 0) - 1); setExtra(page, line, extraOf(line)); },
  del(t) { resolve(t).line.Deleted = true; },
  addLotHere(t) { const { table, line } = resolve(t); addLotHere(project, table, line); },
  frameToggle(t, a) { for (const r of a.rows) resolve(t).table.Rows[r].LotFrame = a.on; },
  restore(t) { resolve(t).line.Deleted = false; },
  lot(t, a) { setLot(project, resolve(t).line, a.v); },
  addLot(t) { addLot(project, resolve(t).line); },
  sync(t, a) { fillLotsAutomatically(project, a.page === null ? null : [pages()[a.page]]); },
  number() { numberLots(project, 1); },
  trim(t) { const { page, line } = resolve(t); trimRow(page, line); },
  untrim(t) { const { page, line } = resolve(t); untrimRow(page, line); },
  moveRow(t, a) {
    const { table } = resolve(t);
    const order = rowOrder(table), from = order[t.o];
    const rest = order.filter((r) => r !== from);
    rest.splice(Math.min(a.to, rest.length), 0, from);
    table.RowOrder = rest;
  },
  moveLine(t, a) {
    const { page, table, line } = resolve(t);
    const at = locate(page, line);
    const order0 = rowOrder(table), target = a.to < order0.length ? table.Rows[order0[a.to]] : null;
    const prev = at.i > 0 ? at.row.Lines[at.i - 1] : null;
    if (prev) splitAt(page, line);
    const own = locate(page, line), next = own.row.Lines[own.i + 1] ?? null;
    if (next) splitBelow(page, line);
    for (const l of [prev, line, next]) if (l) trimRow(page, l);
    const from = table.Rows.findIndex((r) => r.Lines.includes(line)), to = target ? table.Rows.indexOf(target) : -1;
    if (to !== from) {
      const order = rowOrder(table).filter((r) => r !== from);
      order.splice(to < 0 ? order.length : order.indexOf(to), 0, from);
      table.RowOrder = order;
    }
  },
  placeZero(t, a) {
    const { page, table, line } = resolve(t);
    const order = rowOrder(table), anchorRow = table.Rows[order[Math.min(a.to, order.length - 1)]];
    const anchor = anchorRow.Lines.find((l) => l.Cells[0]?.Id);
    if (anchor) line.EndAt = { Page: page.Id, Line: anchor.Cells[0].Id };
  },
  total(t, a) {
    const { page, line } = resolve(t);
    trimRow(page, line);
    addTotalLine(page, line, { Mode: a.mode, Pages: a.mode === 'pages' ? [pages()[0].Id] : [], Auto: true, Bold: a.bold });
  },
  endMode(t, a) { project.EndLots = a.page ? { Mode: 'page', AfterId: pages()[a.after].Id } : { Mode: 'last', AfterId: null }; },
};

// ---------- Choix d'une manipulation (aléatoire, reproductible) ----------

const hasText = (x) => !x.line.Deleted && firstText(x.line).length > 0;
const addedRow = (x) => !!addedRowOf(x.page, x.line);
let added = 0;

let pageN = 0;
let lastRows = null;
/** Pages ajoutées : ajouter, retirer, remplir, lignes, colonnes, hauteur. */
function chooseAdded() {
  const q = random();
  const onAdded = targets((x) => !!x.page.Added && !x.line.Deleted && !x.line.Cells[0]?.Locked);
  if (!onAdded.length || q < 0.10) {
    const x = pick(targets((y) => canBeModel(y.page) && y.i === 0));
    return x && ['addPage', x, { at: Math.floor(random() * (pages().length + 1)), id: `aj${++pageN}`, empty: chance(0.5) }];
  }
  if (q < 0.17) { const x = pick(onAdded); return x && ['removePage', x, {}]; }
  if (q < 0.27) {
    const x = pick(onAdded.filter((y) => canCopyLine(y.page, y.line)));
    return x && ['addRows', x, { n: pick([1, 3, 5, 10, 15]), above: chance(0.2) && x.o > 0, id: `ln${++pageN}` }];
  }
  if (q < 0.42) {
    const x = pick(onAdded.filter((y) => !firstText(y.line) && addedTable(y.page, y.table)));
    return x && ['fill', x, { w: word(), v: Array.from({ length: Math.max(0, x.line.Cells.length - 1) }, () => pick(['10', '250', '1 500', 'mémoire', ''])) }];
  }
  if (q < 0.57) { const x = pick(onAdded.filter((y) => canCopyLine(y.page, y.line))); return x && ['blank', x, { above: chance(0.4) && x.o > 0, text: chance(0.3) ? `ajout ${word()}` : '' }]; }
  if (q < 0.69) {
    const x = pick(onAdded.filter((y) => addedTable(y.page, y.table)));
    const cs = x ? x.table.ColumnXs.map((_, c) => c).filter((c) => canAddColumn(x.page, x.table, c)) : [];
    return cs.length && ['addCol', x, { c: pick(cs) }];
  }
  if (q < 0.79) {
    const x = pick(onAdded.filter((y) => addedTable(y.page, y.table)));
    const ks = x ? x.table.ColumnXs.map((_, k) => k).filter((k) => canRemoveColumn(x.page, x.table, k)) : [];
    return ks.length && ['removeCol', x, { k: pick(ks) }];
  }
  if (q < 0.88) { const x = pick(onAdded.filter((y) => addedTable(y.page, y.table) && y.r >= y.page.Added.Tables[y.ti].Head)); return x && ['taller', x, {}]; }
  if (q < 0.94) { const x = pick(onAdded.filter((y) => (y.line.Tall ?? 0) > 0)); return x && ['shorter', x, {}]; }
  const x = pick(onAdded.filter(addedRow)); return x && ['removeAdded', x, {}];
}

function choose() {
  if (!process.env.NOADDED && random() < Number(process.env.ADDED ?? 0.25)) return chooseAdded();
  const r = random();
  const all = targets(hasText);
  if (!all.length) return null;
  const objs = targets((x) => hasText(x) && hasLotCell(x.line) && !x.table.LotsOff);
  if (r < 0.10) { const x = pick(all.filter((y) => canCopyLine(y.page, y.line))); const lines = Array.from({ length: 1 + Math.floor(random() * 4) }, word); return x && ['type', x, { lines }]; }
  if (r < 0.17) { const x = pick(all); return x && ['addWord', x, { w: word() }]; }
  if (r < 0.21) {
    const xs = all.filter((y) => /mot\d+/.test(y.line.Cells[0].Text));
    const x = pick(xs);
    return x && ['removeWord', x, { w: pick(x.line.Cells[0].Text.match(/mot\d+/g)) }];
  }
  if (r < 0.26) { const x = pick(all.filter((y) => y.line.Cells.length > 1)); return x && ['value', x, { c: 1 + Math.floor(random() * (x.line.Cells.length - 1)), v: pick(['10', '250', '1 500', '15 000', 'mémoire', '']) }]; }
  if (r < 0.34) { const x = pick(all.filter((y) => y.line.Cells[0].Text.includes('\n') && canCopyLine(y.page, y.line))); return x && ['typed', x, { from: 1 + Math.floor(random() * (x.line.Cells[0].Text.split('\n').length - 1)) }]; }
  if (r < 0.40) { const x = pick(all.filter((y) => canSplit(y.page, y.line))); return x && ['split', x, {}]; }
  if (r < 0.46) { const x = pick(all.filter((y) => canSplitBelow(y.page, y.line))); return x && ['splitBelow', x, {}]; }
  if (r < 0.54) { const x = pick(all.filter((y) => canCopyLine(y.page, y.line))); return x && ['copy', x, {}]; }
  if (r < 0.63) { const x = pick(all.filter((y) => canCopyLine(y.page, y.line))); return x && ['blank', x, { above: chance(0.4) && x.o > 0, text: chance(0.7) ? `ajout ${word()}` : '' }]; }
  if (r < 0.66) { const x = pick(all.filter(addedRow)); return x && ['removeAdded', x, {}]; }
  if (r < 0.69) { const x = pick(objs); return x && ['del', x, {}]; }
  if (r < 0.71) { const x = pick(targets((y) => y.line.Deleted)); return x && ['restore', x, {}]; }
  if (r < 0.77) { const x = pick(objs); return x && ['lot', x, { v: chance(0.25) ? '0' : chance(0.2) ? '' : String(1 + Math.floor(random() * 300)) }]; }
  if (r < 0.78) { const x = pick(all.filter((y) => !hasLotCell(y.line) && !y.table.LotsOff && y.line.Kind === 'Article')); return x && ['addLot', x, {}]; }
  if (r < 0.79) { const x = pick(all.filter((y) => !hasLotCell(y.line) && itemTable(y.table) && (y.line.Kind === 'Article' || y.line.Kind === 'Titre'))); return x && ['addLotHere', x, {}]; }
  if (r < 0.83) return ['sync', all[0], { page: chance(0.5) ? all[Math.floor(random() * all.length)].p : null }];
  if (r < 0.84) {
    if (chance(0.3)) { const x = pick(all.filter((y) => !y.table.LotsOff && frameToggles(y.table).length)); if (x) { const g = pick(frameToggles(x.table)); return ['frameToggle', x, { rows: g.rows, on: !g.framed }]; } }
    if (chance(0.7)) { const x = pick(all.filter((y) => canCopyLine(y.page, y.line))); return x && ['addRows', x, { n: pick([1, 3, 5, 10, 15]), above: chance(0.2) && x.o > 0, id: `ln${++pageN}` }]; }
    return ['number', all[0], {}];
  }
  if (r < 0.86) { const x = pick(all.filter((y) => rowTrimOf(y.page, y.line))); return x && ['trim', x, {}]; }
  if (r < 0.87) { const x = pick(all.filter((y) => trimsOf(y.page, y.line).length)); return x && ['untrim', x, {}]; }
  if (r < 0.92) { const x = pick(all); return x && ['moveRow', x, { to: Math.floor(random() * rowOrder(x.table).length) }]; }
  if (r < 0.95) { const x = pick(all.filter((y) => y.row.Lines.filter((l) => firstText(l)).length > 1)); return x && ['moveLine', x, { to: Math.floor(random() * rowOrder(x.table).length) }]; }
  if (r < 0.97) { const x = pick(objs.filter((y) => isZeroLot(y.line))); return x && ['placeZero', x, { to: Math.floor(random() * rowOrder(x.table).length) }]; }
  if (r < 0.99) { const x = pick(all.filter((y) => canCopyLine(y.page, y.line))); return x && ['total', x, { mode: pick(['above', 'page', 'pages', 'all']), bold: chance(0.3) }]; }
  return ['endMode', all[0], { page: chance(0.3), after: Math.floor(random() * Math.min(6, pages().filter((p) => !p.EndLots).length)) }];
}

// ---------- Contrôles ----------

const problems = [];
const fail = (step, what) => { if (problems.length < 200) problems.push(`#${step} ${what}`); };
const lotsBefore = () => new Map(targets(() => true).map((x) => [x.line, x.line.Lot ?? '']));
const allLinesOf = (p) => p.Tables.flatMap((t) => t.Rows.flatMap((r) => r.Lines));
const allText = () => pages().flatMap((p) => p.Tables.flatMap((t) => t.Rows.flatMap((r) => r.Lines.flatMap((l) => l.Cells.map((c) => c.Text))))).join('\n');

function check(step, op, beforeLots, plan) {
  for (const page of pages()) {
    if (page.EndLots) continue;
    // 1. Image de base inchangée (rien n'a été perdu ni ajouté en dehors des bandes).
    if (baseHeight(page) !== baseH.get(page.Id)) fail(step, `${op} : page ${page.Id} hauteur d'origine ${baseHeight(page)} ≠ ${baseH.get(page.Id)}`);
    page.Tables.forEach((t, ti) => {
      if (!gridTable(t)) return;
      // 2. Filets dans l'ordre, une rangée entre deux filets, ordre affiché valide.
      for (let i = 1; i < t.RowYs.length; i++) if (!(t.RowYs[i] > t.RowYs[i - 1])) fail(step, `${op} : p${page.Id} t${ti} filets dans le désordre (${t.RowYs[i - 1]} ≥ ${t.RowYs[i]})`);
      if (t.Rows.length + 1 !== t.RowYs.length) fail(step, `${op} : p${page.Id} t${ti} ${t.Rows.length} rangées pour ${t.RowYs.length} filets`);
      if (Array.isArray(t.RowOrder) && (t.RowOrder.length !== t.Rows.length || new Set(t.RowOrder).size !== t.Rows.length)) fail(step, `${op} : p${page.Id} t${ti} ordre des rangées invalide`);
      // 3. Chaque case dans sa rangée.
      t.Rows.forEach((row, r) => {
        if (r + 1 >= t.RowYs.length) return;
        for (const l of row.Lines) for (const f of l.Cells) {
          const cy = f.Box.Y + (f.Box.Height - (l.Extra ?? 0)) / 2;
          if (!(cy > t.RowYs[r] - 1 && cy < t.RowYs[r + 1] + 1)) {
            fail(step, `${op} : « ${firstText(l).slice(0, 30)} » hors de sa rangée (${cy.toFixed(1)} pas dans ${t.RowYs[r].toFixed(1)}–${t.RowYs[r + 1].toFixed(1)})`);
            if (process.env.DUMP && !globalThis.dumped) {
              globalThis.dumped = true;
              console.log(`DUMP #${step} ${op} ${JSON.stringify(ops[ops.length - 1])} page ${page.Id} t${ti}`);
              t.Rows.forEach((rw, k) => Math.abs(k - r) <= 3 && console.log(`  r${k} ${t.RowYs[k]?.toFixed(1)}–${t.RowYs[k + 1]?.toFixed(1)} ins=${rw.Inserted ?? '-'} ` + rw.Lines.map((x) => `${firstText(x).replace(/\n/g, '⏎').slice(0, 14)}@${(x.Cells[0].Box.Y + (x.Cells[0].Box.Height - (x.Extra ?? 0)) / 2).toFixed(1)} box=${x.Cells[0].Box.Y.toFixed(0)}+${x.Cells[0].Box.Height.toFixed(0)} x=${x.Extra ?? 0}@${x.ExtraAt ?? 0}`).join(' | ')));
              console.log('  ordre', JSON.stringify(t.RowOrder), 'inserts', (page.Inserts ?? []).map((x) => `${x.Kind}@${x.At}+${x.Height}`).join(', '));
            }
          }
        }
      });
      // 4. Colonne des lots continue jusqu'au dernier objet.
      if (lotsActive(project) && !t.LotsOff) {
        const order = rowOrder(t), framed = new Set(framedRows(t));
        const objRows = order.filter((r) => t.Rows[r]?.Lines.some((l) => hasLotCell(l) && !l.Deleted));
        if (objRows.length) {
          const a = order.indexOf(objRows[0]), b = order.indexOf(objRows[objRows.length - 1]);
          for (let k = a; k <= b; k++) if (!framed.has(order[k]) && t.Rows[order[k]].Lines.some((l) => hasLotCell(l))) fail(step, `${op} : p${page.Id} rangée ${order[k]} sans colonne de lot`);
        }
      }
    });
  }
  // Pages ajoutées : une case par colonne sur chaque ligne, dans sa colonne ; repères cohérents.
  for (const page of pages()) {
    if (!page.Added) continue;
    if ((page.Added.Tables ?? []).length !== page.Tables.length) fail(step, `${op} : page ajoutée ${page.Id} repères ${page.Added.Tables.length} ≠ ${page.Tables.length} tableaux`);
    page.Tables.forEach((t, ti) => {
      if (!page.Added.Tables[ti]) return;
      const n = t.ColumnXs.length - 1;
      for (let c = 1; c <= n; c++) if (!(t.ColumnXs[c] > t.ColumnXs[c - 1])) fail(step, `${op} : page ajoutée colonnes dans le désordre`);
      for (const l of t.Rows.flatMap((r) => r.Lines)) {
        const cols = l.Cells.map((f) => f.Column);
        if (new Set(cols).size !== cols.length || cols.some((c) => c < 0 || c >= n)) fail(step, `${op} : page ajoutée cases ${cols.join(',')} pour ${n} colonnes`);
        for (const f of l.Cells) if (f.Column >= 0 && f.Column < n && !(f.Box.X >= t.ColumnXs[f.Column] - 2 && f.Box.X + f.Box.Width <= t.ColumnXs[f.Column + 1] + 2)) fail(step, `${op} : page ajoutée case hors de sa colonne`);
      }
    });
  }
  // Ajouter des lignes : toutes ajoutées, et la page n'a pas grandi (la place est prise en bas, sinon page suivante).
  if (op === 'addRows' && lastRows) {
    if (lastRows.res.added !== lastRows.want) fail(step, `addRows : ${lastRows.res.added} lignes ajoutées sur ${lastRows.want}`);
    if (lastRows.grew) fail(step, 'addRows : la page a grandi');
    lastRows = null;
  }
  // « + » ici : la ligne a sa case.
  if (op === 'addLotHere' && ops.length && !hasLotCell(resolve(ops[ops.length - 1].t).line)) fail(step, 'addLotHere : pas de case sur la ligne');
  // 5. Chaque objet a sa case.
  for (const o of objectLines(project)) if (!hasLotCell(o.line)) fail(step, `${op} : objet « ${firstText(o.line).slice(0, 30)} » sans case de lot`);
  // 6. Mots tapés toujours là.
  const text = allText();
  for (const w of live) if (!text.includes(w)) fail(step, `${op} : mot tapé « ${w} » perdu`);
  // 7. Numéros : ne changent que là où l'action le veut.
  if (!['lot', 'sync', 'number', 'addLot', 'removeAdded'].includes(op))
    for (const [l, v] of beforeLots) if ((l.Lot ?? '') !== v && hasLotCell(l) && v !== '') fail(step, `${op} : numéro de « ${firstText(l).slice(0, 30)} » changé ${v} → ${l.Lot}`);
  // Synchroniser : les numéros déjà là ne font que monter (décalés par les cases remplies avant eux), jamais à l'envers.
  if (op === 'sync')
    for (const [l, v] of beforeLots) if (v !== '' && (l.Lot ?? '') !== v && !(/^\d+$/.test(v) && /^\d+$/.test(l.Lot) && +l.Lot > +v)) fail(step, `sync : numéro ${v} → ${l.Lot}`);
  // 8. Écran : chaque page s'affiche ; chaque objet visible a sa case de lot à l'écran.
  pages().forEach((page, k) => {
    const v = viewOf(project, plan, k);
    if (!(plan.pages[k].height > 0)) fail(step, `${op} : page ${k + 1} hauteur ${plan.pages[k].height}`);
    if (lotsActive(project))
      for (const e of v.lines) if (!e.carriedOnly && hasLotCell(e.line) && !e.table.LotsOff && !e.lotBox) fail(step, `${op} : « ${firstText(e.line).slice(0, 30)} » sans case à l'écran`);
  });
}

// ---------- Contrôle sur l'image : le trait entre le numéro et la désignation suit partout ----------

/** Chaque rangée tracée de la colonne des lots : trait gauche de la colonne et trait gauche du tableau présents (3 hauteurs). */
function pixelCheck(step, op) {
  for (const pg0 of pages()) {
    if (pg0.EndLots) continue;
    // Sur une copie : la mesure des traits (gardée dans le projet à la 1re mesure) ne doit pas dépendre de ce contrôle.
    const pg = structuredClone(pg0);
    const img = drawLotColumns(stretch(imageOf(pg0), pg), pg);
    for (const f of frames(img, pg)) {
      for (const r of f.rows) {
        const y0 = Math.floor(f.table.RowYs[r] + 0.5) + 3, y1 = Math.floor(f.table.RowYs[r + 1] + 0.5) - 3;
        for (const y of [y0, (y0 + y1) >> 1, y1]) {
          if (y1 <= y0 || y < 0 || y >= img.h) continue;
          const dark = (a, b) => { for (let x = Math.max(0, a); x <= Math.min(img.w - 1, b); x++) if (img.data[(y * img.w + x) * img.ch] < 150) return true; return false; };
          if (!dark(f.right - 8, f.right + 3)) fail(step, `${op} : p${pg.Id} rangée ${r} : trait numéro / désignation absent (y ${y})` + (process.env.PIXDBG ? ` right ${f.right} rangée ${f.table.RowYs[r].toFixed(1)}-${f.table.RowYs[r + 1].toFixed(1)} ` + Array.from({ length: 30 }, (_, i) => (img.data[(y * img.w + f.right - 20 + i) * img.ch] < 150 ? '#' : '.')).join('') : ''));
          if (!dark(f.left - 3, f.left + 3)) fail(step, `${op} : p${pg.Id} rangée ${r} : trait gauche de la colonne absent (y ${y})`);
        }
        // Case de lot en face de sa ligne : dans sa rangée.
        for (const l of f.table.Rows[r].Lines) {
          if (!hasLotCell(l) || l.Deleted) continue;
          const b = numberBox(f.table, l, columnWidth(pg)), cy = b.Y + b.Height / 2;
          if (!(cy > f.table.RowYs[r] - 1 && cy < f.table.RowYs[r + 1] + 1)) fail(step, `${op} : case de lot de « ${firstText(l).slice(0, 25)} » hors de sa rangée`);
        }
      }
    }
  }
}
const PIXEL = Number(process.env.PIXEL ?? 25);

// ---------- Rendu (pour comparer avec Windows) ----------

function renderAll(plan) {
  const imgs = pages().map((pg) => (pg.EndLots ? null : drawLotColumns(stretch(imageOf(pg), pg), pg)));
  return pages().map((pg, k) => {
    const src = imgs[plan.pages[k].template ?? k];
    if (!src) return null;
    const crops = new Map(plan.pages[k].pieces.filter((x) => x.kind === 'unit').map((x) => [x.unit, unitCrop(x.unit, imgs[x.unit.p])]));
    const out = composePage(project, plan, k, src, crops);
    if (out.h !== plan.pages[k].height) fail('fin', `page ${k + 1} : image ${out.h} ≠ plan ${plan.pages[k].height}`);
    return out;
  });
}

// ---------- Déroulé ----------

const ops = [];
function run(op, x, args) {
  const beforeLots = lotsBefore();
  const t = ref(x);
  if (op === 'type') for (const w of args.lines.join(' ').match(/mot\d+/g) ?? []) live.add(w);
  if (op === 'addWord') live.add(args.w);
  if (op === 'removeWord') live.delete(args.w);
  if (op === 'blank' && args.text) live.add(args.text.split(' ')[1]);
  if (op === 'removeAdded') for (const l of resolve(t).row.Lines) for (const w of l.Cells[0].Text.match(/mot\d+/g) ?? []) live.delete(w);
  if (op === 'fill') live.add(args.w);
  if (op === 'removePage') for (const l of allLinesOf(pages()[t.p])) for (const c of l.Cells) for (const w of c.Text.match(/mot\d+/g) ?? []) live.delete(w);
  try {
    const res = ACTIONS[op](t, args);
    if (op === 'blank' && !res && args.text) live.delete(args.text.split(' ')[1]); // ligne vide refusée : rien de tapé
  } catch (e) { fail(ops.length, `${op} : ERREUR ${e.message}\n${e.stack?.split('\n').slice(1, 4).join('\n')}`); }
  ops.push({ op, t, a: args });
  let plan;
  try { plan = after(); check(ops.length, op, beforeLots, plan); if (PIXEL > 0 && ops.length % PIXEL === 0) pixelCheck(ops.length, op); } catch (e) { fail(ops.length, `${op} (après) : ERREUR ${e.message}\n${e.stack?.split('\n').slice(1, 4).join('\n')}`); }
  return plan;
}

// Départ : lots sur les pages proposées, synchronisés.
setLotTables(project, new Set(pages().flatMap((p) => lotTables(p).filter((x) => defaultLotChoice(project, x.table)).map((x) => x.table))));
createEmptyLots(project);
fillLotsAutomatically(project);
after();
// Comme à l'écran : la page s'affiche (colonne des lots tracée) avant toute manipulation.
for (const pg of pages()) if (!pg.EndLots) frames(stretch(images.get(pg.Id), pg), pg);

// 1) La manipulation de la vidéo (page 7, RICE COOKER) si elle est dans le document.
const rice = process.env.NOVIDEO ? null : targets((x) => /^1 RICE COOKER/.test(firstText(x.line)))[0];
if (rice) {
  run('type', rice, { lines: ['REPARTIS SUR LE SITE : mot9001', '2 Mac book pro Annee 2017/2018 Model A1990 mot9002', '2 ordinateurs portables Asus mot9003', '2 ordinateurs poitables Acer mot9004'] });
  for (let k = 0; k < 4; k++) {
    const x = targets((y) => y.line.Cells[0].Text.includes('\n') && /mot900/.test(y.line.Cells[0].Text))[0];
    if (x) run('typed', x, { from: 1 });
  }
  for (const w of ['mot9002', 'mot9003']) { const x = targets((y) => y.line.Cells[0].Text.includes(w))[0]; if (x) run('blank', x, { above: false, text: '' }); }
  for (const w of ['mot9001', 'mot9002', 'mot9003', 'mot9004']) { const x = targets((y) => y.line.Cells[0].Text.includes(w) && hasLotCell(y.line))[0]; if (x) run('lot', x, { v: String(500 + Number(w.slice(-1))) }); }
  const x = targets((y) => y.line.Cells[0].Text.includes('mot9004'))[0];
  if (x) run('sync', x, { page: x.p });
}
const video = ops.length;

// 2) Manipulations au hasard.
let guard = 0;
while (ops.length - video < COUNT && guard++ < COUNT * 20) {
  const c = choose();
  if (!c || !c[1]) continue;
  const [op, x, args] = c;
  if (['copy', 'blank', 'typed', 'total'].includes(op)) added++;
  if (op === 'split' || op === 'splitBelow' || op === 'moveLine') added += 0; // rangées en plus, pas de ligne en plus
  run(op, x, args);
}

const plan = after();
pixelCheck('fin', 'fin');
mkdirSync(outDir, { recursive: true });
writeFileSync(join(outDir, 'ops.json'), JSON.stringify({ seed: SEED, video, ops }));
writeFileSync(join(outDir, 'export.txt'), buildTxt(project).text);
if (process.env.SAVE) writeFileSync(join(outDir, 'project.json'), JSON.stringify(project));
renderAll(plan).forEach((img, k) => img && writePgm(join(outDir, `page${k + 1}.pgm`), img));
if (process.env.COLS) pages().forEach((pg, k) => console.log(`page ${k + 1} ${pg.EndLots ? 'FIN' : pg.Id} tableaux ${pg.Tables.map((t) => Math.round(t.ColumnXs?.[0] ?? -1)).join(',')} colonne lots ${Math.round(columnWidth(pg))} hauteurs texte ${[...new Set(pg.Tables.flatMap((t) => t.Rows.flatMap((r) => r.Lines.map((l) => Math.round(l.Cells[0]?.FontSize ?? 0)))))].sort((x, y) => x - y).slice(0, 8).join('/')}`));
if (process.env.FRAMES) pages().forEach((pg, k) => { if (pg.EndLots) return; const img = stretch(imageOf(pg), pg); for (const f of frames(img, pg)) console.log(`page ${k + 1} cadre left ${f.left} right ${f.right} vThick ${f.vThick} hThick ${f.hThick} couleur ${f.color} rangées ${f.rows.length}`); });
if (process.env.UNITS) plan.pages.forEach((pp, k) => { const us = pp.pieces.filter((x) => x.kind === 'unit'); if (us.length) console.log(`page ${k + 1} : ${us.length} lots 0 recollés : ` + us.slice(0, 6).map((x) => `[p${x.unit.p + 1} « ${firstText(x.unit.object).slice(0, 18)} » shift ${x.unit.shift.join('/')} src ${x.unit.table.ColumnXs.slice(0, 2).map(Math.round)} dst ${x.unit.dest.table.ColumnXs.slice(0, 2).map(Math.round)}]`).join(' ')); });
if (process.env.FIRSTP) console.log(problems.slice(0, 4).join('\n'));
const counts = ops.reduce((m, o) => ((m[o.op] = (m[o.op] ?? 0) + 1), m), {});
console.log(`${ops.length} manipulations (dont ${video} de la vidéo), ${added} lignes ajoutées, ${pages().length} pages, ${[...objectLines(project)].length} objets`);
console.log(Object.entries(counts).map(([k, v]) => `${k} ${v}`).join(', '));
const kinds = problems.reduce((m, p) => { const k = p.replace(/^#\S+ /, '').replace(/« [^»]*»/g, '«…»').replace(/[\d.]+/g, 'N').slice(0, 70); (m[k] ??= []).push(p); return m; }, {});
console.log(problems.length ? `${problems.length} problème(s) :\n${Object.entries(kinds).map(([k, v]) => `  ${v.length} × ${k}\n      ex. ${v[0].slice(0, 220)}`).join('\n')}` : 'Aucun problème.');
process.exit(problems.length ? 1 : 0);
