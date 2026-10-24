// Tests du cœur JavaScript (version iPad) sur les mêmes pages que la version Windows.
// Usage : node web/tests/test.mjs [fichier .txt exporté par la version Windows, pour comparaison]
import { readFileSync, readdirSync } from 'node:fs';
import { gunzipSync } from 'node:zlib';
import { fileURLToPath } from 'node:url';
import { dirname, join } from 'node:path';
import { normalize, raster } from '../core/imaging.js';
import { detectGrids } from '../core/rules.js';
import { emptyFieldsWithInk, prepareCell, applyCellWords } from '../core/ocrprep.js';
import { newPage, newProject, analyze, reclassify, allFields, cycleKind, addLockZone } from '../core/model.js';
import { buildTxt, composeIntoSource, paperColor } from '../core/exports.js';
import { parseDelimited, buildDataPage } from '../core/importers.js';
import { packProject, unpackProject } from '../core/store.js';
import { numberLots, setLot, resyncLots, clearLots, lotsActive, objectLines, repairName, removeLot, addLot, canAddLot } from '../core/lots.js';
import { drawLotColumns, frames } from '../core/lotcolumn.js';
import { setExtra, stretch, extraFor } from '../core/layout.js';
import { toggleStrike, isStruck, adjustStrikes } from '../core/strikes.js';
import { cutZeroRows, appendStrips } from '../core/zerorows.js';
import { isZeroLot } from '../core/lots.js';

const FIX = join(dirname(fileURLToPath(import.meta.url)), '..', '..', 'tests', 'fixtures');

function readPgm(path) {
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

const cache = {};
function load(name) {
  if (cache[name]) return cache[name];
  const src = readPgm(join(FIX, name + '.pgm.gz'));
  const n = normalize(src);
  const words = readFileSync(join(FIX, name + '.words.tsv'), 'utf8').split('\n').filter((l) => l)
    .map((l) => l.split('\t')).map((p) => ({ Text: p[0], Box: { X: +p[1], Y: +p[2], Width: +p[3], Height: +p[4] } }));
  return (cache[name] = { src, n, words });
}

function analyzePage(name) {
  const { n, words } = load(name);
  const page = newPage({ Id: name, SourceName: name + '.jpg', Crop: n.crop, Skew: n.skew });
  analyze(page, n.image, words);
  const reread = {};
  for (const l of readFileSync(join(FIX, name + '.reread.tsv'), 'utf8').split('\n').filter((x) => x)) {
    const p = l.split('\t');
    (reread[p[0]] ??= []).push({ Text: p[1], Box: { X: +p[2], Y: +p[3], Width: +p[4], Height: +p[5] } });
  }
  for (const f of emptyFieldsWithInk(page, n.image))
    if (reread[f.Id]) { const c = prepareCell(n.image, f.Box); applyCellWords(f, reread[f.Id], c.scale, c.origin); }
  reclassify(page);
  return page;
}

let failed = 0, count = 0;
function test(name, fn) {
  count++;
  const t0 = Date.now();
  try { fn(); console.log(`  OK    ${name} (${Date.now() - t0} ms)`); }
  catch (e) { failed++; console.log(`  ÉCHEC ${name} : ${e.message}`); }
}
const check = (c, m) => { if (!c) throw new Error(m); };
const eq = (e, a, m) => { if (e !== a) throw new Error(`${m} : attendu « ${e} », obtenu « ${a} »`); };
const threePages = () => { const p = newProject(); for (const n of ['page1', 'page2', 'page5']) p.Pages.push(analyzePage(n)); return p; };
const lines = (p) => buildTxt(p).text.split('\r\n').slice(0, -1);

test('Redressement : cadre noir retiré, inclinaison mesurée', () => {
  const { n } = load('page5');
  check(n.crop.TopLeft.X > 28, 'cadre noir non exclu');
  check(n.skew < -0.1 && n.skew > -0.8, `inclinaison ${n.skew}`);
});

test('Grilles des trois pages', () => {
  eq('28', String(analyzePage('page1').Tables[0].Rows.length), 'page 1');
  eq('34', String(analyzePage('page2').Tables[0].Rows.length), 'page 2');
  eq('6,3,4', analyzePage('page5').Tables.map((t) => t.Rows.length).join(','), 'page 5');
});

test('Export : titres seuls, valeurs rattachées, une ligne par ligne imprimée', () => {
  const l = lines(threePages());
  const i = l.indexOf("DANS L'ENTREPOT");
  check(i >= 0, 'titre absent');
  eq('2 laves main en inox + 1 sanibroyeur SPEED\t130\t70', l[i + 1], 'article sous le titre');
  const j = l.findIndex((x) => x.startsWith('1 compresseur'));
  eq('CD3+ +/- 14130 heures', l[j + 1], 'suite de l\'article');
  check(l.includes('TOTAL STOCK\t44550\t9000'), 'total');
});

test('Modifier un titre, un chiffre, un texte ne déplace rien', () => {
  const p = threePages();
  const before = JSON.stringify([...p.Pages.flatMap((pg) => [...allFields(pg)])].map((f) => [f.Box, f.Ink, f.FontSize]));
  const ls = p.Pages[0].Tables[0].Rows.flatMap((r) => r.Lines);
  ls.find((l) => l.Cells[0].Text === 'SALLE LAVAGE :').Cells[0].Text = 'SALLE DE BAIN :';
  ls.find((l) => l.Cells[0].Text === '1 table pliante').Cells[1].Text = '30';
  const after = JSON.stringify([...p.Pages.flatMap((pg) => [...allFields(pg)])].map((f) => [f.Box, f.Ink, f.FontSize]));
  eq(before, after, 'géométrie');
  const l = lines(p);
  check(l.includes('SALLE DE BAIN :') && l.includes('1 table pliante\t30\tmémoire'), 'export modifié');
});

test('Type de ligne choisi par l\'utilisateur conservé', () => {
  const page = analyzePage('page5');
  const line = page.Tables[0].Rows.flatMap((r) => r.Lines).find((l) => l.Cells[0].Text === 'CARTONNAGE');
  eq('Titre', cycleKind(line), 'cycle');
  reclassify(page);
  eq('Titre', line.Kind, 'conservé');
});

test('Zone verrouillée', () => {
  const page = analyzePage('page1');
  const f = page.Tables[0].Rows.flatMap((r) => r.Lines).find((l) => l.Cells[0].Text === '1 table pliante').Cells[0];
  f.Text = 'x';
  addLockZone(page, { X: f.Box.X - 5, Y: f.Box.Y - 5, Width: f.Box.Width + 10, Height: f.Box.Height + 10 });
  check(f.Locked && f.Text === '1 table pliante', 'verrou');
});

test('Export image : seule la case modifiée change', () => {
  const page = analyzePage('page5');
  const { src, n } = load('page5');
  const f = page.Tables[0].Rows.flatMap((r) => r.Lines).find((l) => l.Cells[0].Text === 'CARTONNAGE').Cells[1];
  f.Text = '17000';
  const edited = raster(n.image.w, n.image.h, 1, new Uint8ClampedArray(n.image.data));
  for (let y = Math.trunc(f.Box.Y) + 1; y < Math.trunc(f.Box.Y + f.Box.Height); y++)
    for (let x = Math.trunc(f.Box.X) + 1; x < Math.trunc(f.Box.X + f.Box.Width); x++) edited.data[y * edited.w + x] = 0;
  const out = composeIntoSource(src, page, edited);
  let changed = 0;
  for (let i = 0; i < src.data.length; i++) if (src.data[i] !== out.data[i * 4]) changed++;
  check(changed > 0 && changed < f.Box.Width * f.Box.Height * 1.2, `pixels modifiés : ${changed}`);
  const [r] = paperColor(n.image, f.Box);
  check(r > 200, 'couleur du papier');
});

test('Projet .inventaire : écriture puis relecture', () => {
  const p = threePages();
  p.Pages[0].Tables[0].Rows[2].Lines[0].Cells[0].Text = 'modifié';
  const back = unpackProject(packProject(p, {}));
  eq(buildTxt(p).text, buildTxt(back.project).text, 'export identique');
});

test('Import CSV', () => {
  const rows = parseDelimited('Désignation;exploitation;réalisation\r\nDANS L\'ENTREPOT;;\r\n"2 laves; inox";130;70\r\nTOTAL;15 000;8 000\r\n', '.csv');
  const p = newProject(); p.Pages.push(buildDataPage('x.csv', rows));
  eq("Désignation\texploitation\tréalisation|DANS L'ENTREPOT|2 laves; inox\t130\t70|TOTAL\t15000\t8000", lines(p).join('|'), 'export CSV');
});

test('Lots : un par objet, continus, resynchro après saisie', () => {
  const p = threePages();
  numberLots(p, 1);
  const objs = [...objectLines(p)].map((o) => o.line);
  eq('1', objs[0].Lot, 'premier');
  eq(String(objs.length), objs[objs.length - 1].Lot, 'dernier');
  setLot(p, objs[9], '81');
  eq('9', objs[8].Lot, 'avant inchangé');
  eq('82', objs[10].Lot, 'suite recalée');
  objs[11].Kind = 'Titre'; objs[11].KindChosenByUser = true;
  resyncLots(p);
  eq('', objs[11].Lot, 'titre sans lot');
  eq('83', objs[12].Lot, 'suite reprend');
  clearLots(p);
  check(!lotsActive(p), 'enlevés');
});

test('Lots : poubelle et « + », les suivants se décalent', () => {
  const p = threePages();
  numberLots(p, 1);
  const objs = [...objectLines(p)].map((o) => o.line);
  const n = objs.length;
  removeLot(p, objs[30]);
  eq('', objs[30].Lot, 'ligne retirée');
  eq('31', objs[31].Lot, 'le suivant prend sa place');
  eq(String(n - 1), objs[n - 1].Lot, 'dernier décalé');
  resyncLots(p);
  eq('', objs[30].Lot, 'reste retirée après synchronisation');
  check(canAddLot(objs[30]), '« + » possible');
  addLot(p, objs[30]);
  eq('31', objs[30].Lot, 'remise');
  eq(String(n), objs[n - 1].Lot, 'dernier recalé');
  numberLots(p, 1);
  eq('31', objs[30].Lot, 'renumérotation');
});

test('Colonne des lots tracée dans la marge, rien d\'autre ne change', () => {
  const page = analyzePage('page5');
  const p = newProject(); p.Pages.push(page);
  numberLots(p, 1);
  const { n } = load('page5');
  const out = drawLotColumns(n.image, page);
  const fr = frames(n.image, page);
  eq(3, fr.length, 'trois tableaux encadrés');
  let changed = 0, outside = 0;
  for (let y = 0; y < out.h; y++)
    for (let x = 0; x < out.w; x++) {
      const i = y * out.w + x;
      if (out.data[i] === n.image.data[i]) continue;
      changed++;
      if (!fr.some((f) => x >= f.left - 8 && x < f.right && y >= f.top - 8 && y <= f.bottom + 8)) outside++;
    }
  check(changed > 1000, `pixels tracés : ${changed}`);
  eq(0, outside, 'pixels modifiés hors de la colonne');
});

test('Lots à 0 : pas de numéro, regroupés à la fin du .txt', () => {
  const p = threePages();
  numberLots(p, 1);
  const objs = [...objectLines(p)].map((o) => o.line);
  setLot(p, objs[3], '0');
  check(isZeroLot(objs[3]), 'zéro');
  eq('4', objs[4].Lot, 'le 0 ne prend pas de numéro');
  let l = lines(p);
  eq('0\t2 rolls en métal\t60\t30', l[l.length - 1], 'en fin');
  eq(1, l.filter((x) => x.includes('2 rolls en métal')).length, 'retiré de sa place');
  const comp = p.Pages[0].Tables[0].Rows.flatMap((r) => r.Lines).find((x) => x.Cells[0].Text.startsWith('1 compresseur'));
  setLot(p, comp, '0');
  l = lines(p);
  check(l[l.length - 2].startsWith('0\t1 compresseur') && l[l.length - 1] === '\tCD3+ +/- 14130 heures', 'objet et sa suite');
});

test('Rangée qui grandit puis revient', () => {
  const page = analyzePage('page1');
  const round = (s) => s.replace(/-?\d+\.\d+/g, (m) => String(Math.round(parseFloat(m) * 1e6) / 1e6));
  const before = round(JSON.stringify(page));
  const ls = page.Tables[0].Rows.flatMap((r) => r.Lines);
  const line = ls.find((x) => x.Cells[0].Text === '2 rolls en métal');
  const next = ls[ls.indexOf(line) + 1];
  const y = next.Cells[0].Box.Y, h = page.Height;
  check(setExtra(page, line, 40), 'agrandie');
  eq(y + 40, next.Cells[0].Box.Y, 'la suite descend');
  eq(h + 40, page.Height, 'page plus longue');
  setExtra(page, line, 0);
  eq(before, round(JSON.stringify(page)), 'revenue à l\'origine');
  const img = load('page1').n.image;
  setExtra(page, line, 30);
  const big = stretch(img, page);
  eq(img.h + 30, big.h, 'image allongée');
  const at = line.ExtraAt;
  check(img.data.subarray(at * img.w).every((v, i) => v === big.data[(at + 30) * img.w + i]), 'bas décalé intact');
  check(extraFor(line, line.Cells[0], 3, 20) > 30, 'trois lignes');
});

test('Nom de fichier iPhone (décomposé) réparé', () => {
  eq('BEENDHI prisé.pdf', repairName('BEENDHI prisÃ©.pdf'.normalize('NFD')), 'réparation');
});

test('Texte barré : suit la saisie, se retire, .txt inchangé', () => {
  const p = threePages();
  const l0 = lines(p);
  const f = p.Pages[0].Tables[0].Rows.flatMap((r) => r.Lines).find((x) => x.Cells[0].Text.includes('distributeur papier')).Cells[0];
  const a = f.Text.indexOf('+ 1');
  toggleStrike(f, a, f.Text.length, 'red');
  check(isStruck(f, a, f.Text.length) && !isStruck(f, 0, a), 'morceau barré');
  eq(l0.join('|'), lines(p).join('|'), '.txt inchangé');
  let old = f.Text; f.Text = 'Lot : ' + old; adjustStrikes(f, old, f.Text);
  eq('+ 1 distributeur papier', f.Text.substr(f.Strikes[0].Start, f.Strikes[0].Length), 'trait décalé');
  old = f.Text; f.Text = old.replace('distributeur papier', 'papier'); adjustStrikes(f, old, f.Text);
  eq('+ 1 papier', f.Text.substr(f.Strikes[0].Start, f.Strikes[0].Length), 'raccourci');
  toggleStrike(f, f.Strikes[0].Start, f.Strikes[0].Start + f.Strikes[0].Length, 'red');
  eq(0, f.Strikes.length, 'retiré');
});

test('Nom de fichier abîmé réparé', () => {
  eq('BEENDHI prisé.pdf', repairName('BEENDHI prisÃ©.pdf'), 'réparation');
});

test('Hors ligne : tous les fichiers de l\'app sont mis en cache', () => {
  const sw = readFileSync(join(dirname(fileURLToPath(import.meta.url)), '..', 'sw.js'), 'utf8');
  const root = join(dirname(fileURLToPath(import.meta.url)), '..');
  const missing = [];
  const walk = (dir) => {
    for (const e of readdirSync(join(root, dir), { withFileTypes: true })) {
      const rel = dir ? `${dir}/${e.name}` : e.name;
      if (e.isDirectory()) { if (rel !== 'tests') walk(rel); continue; }
      if (['package.json', 'sw.js'].includes(rel) || e.name.includes('LICENSE')) continue;
      if (!sw.includes(`'${rel}'`)) missing.push(rel);
    }
  };
  walk('');
  eq('', missing.join(', '), 'fichiers absents du cache');
});

const windowsTxt = process.argv[2];
if (windowsTxt) {
  test('Export .txt identique à la version Windows (octet pour octet)', () => {
    const win = readFileSync(windowsTxt, 'utf8').replace(/^﻿/, '');
    const js = buildTxt(threePages()).text;
    if (win !== js) {
      const a = win.split('\r\n'), b = js.split('\r\n');
      const k = a.findIndex((l, i) => l !== b[i]);
      throw new Error(`différence ligne ${k + 1} : Windows « ${a[k]} » / iPad « ${b[k]} »`);
    }
  });
}

const windowsFrame = process.argv[4];
if (windowsFrame) {
  test('Colonne des lots identique à Windows (pixel pour pixel)', () => {
    const p = threePages();
    numberLots(p, 1);
    const page = p.Pages[2];
    const carton = page.Tables.flatMap((t) => t.Rows).flatMap((r) => r.Lines).find((l) => l.Cells[0].Text === 'CARTONNAGE');
    setExtra(page, carton, 37);
    const js = drawLotColumns(stretch(load(page.Id).n.image, page), page);
    const win = readPgm(windowsFrame);
    eq(`${win.w}x${win.h}`, `${js.w}x${js.h}`, 'taille');
    const k = win.data.findIndex((v, i) => v !== js.data[i]);
    eq(-1, k, `premier pixel différent (${k % win.w}, ${Math.trunc(k / win.w)})`);
  });
}

const windowsZero = process.argv[5];
if (windowsZero) {
  test('Lots 0 déplacés en fin d\'inventaire : identique à Windows', () => {
    const p = newProject();
    for (const n of ['page1', 'page5']) p.Pages.push(analyzePage(n));
    numberLots(p, 1);
    const find = (pg, t) => pg.Tables.flatMap((x) => x.Rows).flatMap((r) => r.Lines).find((l) => l.Cells[0].Text === t);
    setLot(p, find(p.Pages[0], '2 rolls en métal'), '0');
    setLot(p, find(p.Pages[1], 'CARTONNAGE'), '0');
    const strips = [];
    p.Pages.forEach((page, i) => {
      const img = drawLotColumns(load(page.Id).n.image, page);
      const cut = cutZeroRows(img, page);
      strips.push(...cut.strips);
      const final = i === p.Pages.length - 1 ? appendStrips(cut.image, cut.insertAt, cut.tableLeft, strips) : cut.image;
      const win = readPgm(join(windowsZero, page.Id + '-zero.pgm'));
      eq(`${win.w}x${win.h}`, `${final.w}x${final.h}`, `taille ${page.Id}`);
      const k = win.data.findIndex((v, j) => v !== final.data[j]);
      eq(-1, k, `${page.Id} : premier pixel différent`);
    });
  });
}

const windowsLots = process.argv[3];
if (windowsLots) {
  test('Export .txt avec lots identique à Windows', () => {
    const win = readFileSync(windowsLots, 'utf8').replace(/^\uFEFF/, '');
    const p = threePages();
    numberLots(p, 1);
    const js = buildTxt(p).text;
    if (win !== js) {
      const a = win.split('\r\n'), b = js.split('\r\n');
      const k = a.findIndex((l, i) => l !== b[i]);
      throw new Error(`ligne ${k + 1} : Windows « ${a[k]} » / iPad « ${b[k]} »`);
    }
  });
}

console.log(failed === 0 ? `${count} tests, tous réussis.` : `${failed} échec(s) sur ${count}.`);
process.exit(failed === 0 ? 0 : 1);
