// Tests du cœur JavaScript (version iPad) sur les mêmes pages que la version Windows.
// Usage : node web/tests/test.mjs [fichier .txt exporté par la version Windows, pour comparaison]
import { readFileSync } from 'node:fs';
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

const FIX = join(dirname(fileURLToPath(import.meta.url)), '..', '..', 'tests', 'fixtures');

function readPgm(path) {
  const b = gunzipSync(readFileSync(path));
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

console.log(failed === 0 ? `${count} tests, tous réussis.` : `${failed} échec(s) sur ${count}.`);
process.exit(failed === 0 ? 0 : 1);
