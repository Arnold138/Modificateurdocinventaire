// Tests du cœur JavaScript (version iPad) sur les mêmes pages que la version Windows.
// Usage : node web/tests/test.mjs [fichier .txt exporté par la version Windows, pour comparaison]
import { readFileSync, readdirSync } from 'node:fs';
import { gunzipSync } from 'node:zlib';
import { fileURLToPath } from 'node:url';
import { dirname, join } from 'node:path';
import { normalize, raster } from '../core/imaging.js';
import { detectGrids } from '../core/rules.js';
import { emptyFieldsWithInk, prepareCell, applyCellWords } from '../core/ocrprep.js';
import { newPage, newProject, analyze, reclassify, allFields, cycleKind, addLockZone, upgradePage } from '../core/model.js';
import { buildTxt, composeIntoSource, paperColor } from '../core/exports.js';
import { parseDelimited, buildDataPage } from '../core/importers.js';
import { packProject, unpackProject } from '../core/store.js';
import { numberLots, setLot, resyncLots, clearLots, lotsActive, objectLines, repairName, removeLot, addLot, canAddLot, createEmptyLots, fillLotsAutomatically } from '../core/lots.js';
import { drawLotColumns, frames, framedRows } from '../core/lotcolumn.js';
import { setExtra, stretch, extraFor } from '../core/layout.js';
import { toggleStrike, isStruck, adjustStrikes, setColor, colorRuns } from '../core/strikes.js';
import { planArrangement, composePage, unitCrop, viewOf, pageArranged } from '../core/arrange.js';
import { recomputeTotals, parseAmount, formatAmount } from '../core/totals.js';
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

test('Lots : un par objet, continus ; un numéro tapé ne change rien d\'autre, « Synchroniser » remplit', () => {
  const p = threePages();
  numberLots(p, 1);
  const objs = [...objectLines(p)].map((o) => o.line);
  eq('1', objs[0].Lot, 'premier');
  eq(String(objs.length), objs[objs.length - 1].Lot, 'dernier');
  setLot(p, objs[9], '81');
  eq('9', objs[8].Lot, 'avant inchangé');
  eq('11', objs[10].Lot, 'suivant inchangé (rien ne bouge tout seul)');
  fillLotsAutomatically(p);
  eq('82', objs[10].Lot, 'synchronisé : la suite repart du numéro tapé');
  objs[11].Kind = 'Titre'; objs[11].KindChosenByUser = true;
  resyncLots(p);
  eq('', objs[11].Lot, 'titre sans lot');
  eq('84', objs[12].Lot, 'les autres ne bougent pas');
  fillLotsAutomatically(p);
  eq('83', objs[12].Lot, 'suite reprise à la synchronisation');
  clearLots(p);
  check(!lotsActive(p), 'enlevés');
});

test('Lots : colonne vide, on remplit soi-même', () => {
  const p = threePages();
  createEmptyLots(p);
  const objs = [...objectLines(p)].map((o) => o.line);
  check(lotsActive(p) && objs.every((l) => l.LotSlot && l.Lot === ''), 'cases vides');
  setLot(p, objs[2], '5');
  check(objs.every((l, i) => i === 2 || l.Lot === ''), 'aucune autre case remplie');
  fillLotsAutomatically(p);
  eq('1,2,5,6', objs.slice(0, 4).map((l) => l.Lot).join(','), 'synchronisation');
});

test('Lots : poubelle et « + » sans renuméroter', () => {
  const p = threePages();
  numberLots(p, 1);
  const objs = [...objectLines(p)].map((o) => o.line);
  removeLot(p, objs[30]);
  eq('', objs[30].Lot, 'ligne retirée');
  check(!objs[30].LotSlot, 'case retirée');
  eq('32', objs[31].Lot, 'le suivant garde son numéro');
  check(canAddLot(objs[30]), '« + » possible');
  addLot(p, objs[30]);
  check(objs[30].LotSlot && objs[30].Lot === '', 'case vide remise');
  fillLotsAutomatically(p);
  eq('31', objs[30].Lot, 'synchronisée');
});

test('Lots : ni sur les totaux, ni sur l\'en-tête, ni sur le récapitulatif', () => {
  const rows = [['RECAPITULATIF', '', ''], ['', 'exploitation', 'réalisation'], ['I - MOBILIER ET MATERIEL', '148 760', '47 405'],
    ['II - STOCK', '44 550', '9 000'], ['TOTAL GENERAL', '193 310', '56 405']];
  const p = newProject(); p.Pages.push(buildDataPage('recap.csv', rows));
  const ls = p.Pages[0].Tables[0].Rows.flatMap((r) => r.Lines);
  eq('EnTete', ls[1].Kind, 'en-tête du récapitulatif');
  eq('Total', ls[4].Kind, 'total');
  createEmptyLots(p);
  eq(0, [...objectLines(p)].length, 'aucun objet');
});

test('Colonne des lots tracée dans la marge, seulement sur les objets (pas sous le dernier, pas sur les totaux)', () => {
  const page = analyzePage('page5');
  const p = newProject(); p.Pages.push(page);
  numberLots(p, 1);
  const { n } = load('page5');
  const out = drawLotColumns(n.image, page);
  const fr = frames(n.image, page);
  eq(3, fr.length, 'trois tableaux encadrés');
  eq('0,1,2,3,4', framedRows(page.Tables[0]).join(','), 'en-tête et objets, pas TOTAL STOCK');
  let changed = 0, outside = 0;
  for (let y = 0; y < out.h; y++)
    for (let x = 0; x < out.w; x++) {
      const i = y * out.w + x;
      if (out.data[i] === n.image.data[i]) continue;
      changed++;
      const inside = fr.some((f) => x >= f.left - 8 && x < f.right && f.rows.some((r) => y >= f.table.RowYs[r] - 8 && y <= f.table.RowYs[r + 1] + 8));
      if (!inside) outside++;
    }
  check(changed > 1000, `pixels tracés : ${changed}`);
  eq(0, outside, 'pixels modifiés hors de la colonne');
  const total = page.Tables[0].RowYs[5] + 6;
  let onTotal = 0;
  for (let x = fr[0].left - 4; x < fr[0].right; x++) if (out.data[Math.round(total) * out.w + x] !== n.image.data[Math.round(total) * out.w + x]) onTotal++;
  eq(0, onTotal, 'rien dessiné sur la rangée TOTAL STOCK');
});

test('Rangée sans texte lu (total sur fond gris) : cases vides modifiables', () => {
  const { n, words } = load('page5');
  const page = newPage({ Id: 'page5' });
  analyze(page, n.image, words.filter((w) => !(w.Box.Y + w.Box.Height / 2 > 530 && w.Box.Y + w.Box.Height / 2 < 556)));
  const row = page.Tables[0].Rows[5];
  eq(1, row.Lines.length, 'une ligne vide');
  eq(3, row.Lines[0].Cells.length, 'une case par colonne');
  check(row.Lines[0].Cells.every((c) => !c.Locked && c.Text === '' && c.Box.Width > 10), 'cases modifiables');
  check(row.Lines[0].Cells[0].FontSize > 10, 'taille d\'écriture du tableau');
});

test('Lots à 0 : pas de numéro, après le dernier lot dans le .txt', () => {
  const p = threePages();
  numberLots(p, 1);
  const objs = [...objectLines(p)].map((o) => o.line);
  setLot(p, objs[3], '0');
  fillLotsAutomatically(p);
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

test('Projet d\'une version précédente : rangée de total vide rendue modifiable', () => {
  const page = analyzePage('page5');
  page.Tables[0].Rows[5].Lines = [];
  upgradePage(page);
  const l = page.Tables[0].Rows[5].Lines;
  eq(1, l.length, 'une ligne ajoutée');
  check(l[0].Cells.length === 3 && l[0].Cells.every((c) => c.Box.Width > 10 && c.Id), 'cases modifiables');
  upgradePage(page);
  eq(1, page.Tables[0].Rows[5].Lines.length, 'pas en double');
});

// ---------- Mise en page : lots 0 en fin, rangées déplacées ----------

const fromPages = (names) => { const p = newProject(); for (const n of names) p.Pages.push(analyzePage(n)); return p; };
const findLine = (pg, t) => pg.Tables.flatMap((x) => x.Rows).flatMap((r) => r.Lines).find((l) => l.Cells[0].Text === t);
/** Scénario partagé avec la version Windows (commande export-arrange). */
function arrangeScenario() {
  const p = fromPages(['page1', 'page5']);
  createEmptyLots(p);
  fillLotsAutomatically(p);
  setLot(p, findLine(p.Pages[0], '2 rolls en métal'), '0');
  setLot(p, findLine(p.Pages[0], '1 gazelle TULESCA inox'), '0');
  setLot(p, findLine(p.Pages[1], 'CARTONNAGE'), '0');
  p.Pages[1].Tables[2].RowOrder = [0, 2, 1, 3];   // « Appartient à Orange » monté au-dessus de VDM
  findLine(p.Pages[1], 'EN COURS DE FABRICATION').Cells[1].Text = '150';
  return p;
}
function composeAll(p, imageOf) {
  const plan = planArrangement(p);
  const imgs = p.Pages.map((pg) => imageOf(pg));
  return { plan, out: p.Pages.map((pg, k) => composePage(p, plan, k, imgs[k],
    new Map(plan.pages[k].pieces.filter((x) => x.kind === 'unit').map((x) => [x.unit, unitCrop(x.unit, imgs[x.unit.p])])))) };
}

test('Lot 0 : la ligne quitte sa place et passe après le dernier lot (écran et image)', () => {
  const p = arrangeScenario();
  const plan = planArrangement(p);
  eq(3, plan.units.length, 'trois objets déplacés');
  eq('1,2,3', [plan.dest.p, plan.dest.ti, plan.dest.r].join(','), 'après « Appartenant à ULMA »');
  check(plan.units[1].title && !plan.units[1].whole, 'gazelle : objet seul + valeurs du titre');
  const h0 = p.Pages[0].Height, h1 = p.Pages[1].Height;
  const cut = plan.units.filter((u) => u.p === 0).reduce((s, u) => s + u.bottom - u.top, 0);
  eq(h0 - cut, plan.pages[0].height, 'page 1 raccourcie');
  const add = plan.units.reduce((s, u) => s + u.height, 0) - plan.units.filter((u) => u.p === 1).reduce((s, u) => s + u.bottom - u.top, 0);
  eq(h1 + add, plan.pages[1].height, 'page 5 allongée');
  const v0 = viewOf(p, plan, 0), v1 = viewOf(p, plan, 1);
  check(!v0.lines.some((l) => l.line.Cells[0].Text === '2 rolls en métal'), 'retirée de la page 1');
  const moved = v1.lines.filter((l) => l.moved && !l.carriedOnly).map((l) => l.line.Cells[0].Text);
  eq('2 rolls en métal|1 gazelle TULESCA inox|CARTONNAGE|42057 € à l\'achat HT', moved.join('|'), 'recollées en fin, dans l\'ordre');
  const gaz = v1.lines.find((l) => l.line.Cells[0].Text === '1 gazelle TULESCA inox');
  const vals = v1.lines.find((l) => l.carriedOnly);
  check(Math.abs(vals.cells[0].box.Y + vals.cells[0].box.Height / 2 - (gaz.cells[0].box.Y + gaz.cells[0].box.Height / 2)) < 12, 'valeurs du titre à la hauteur de l\'objet');
  const lavage = v0.lines.find((l) => l.line.Cells[0].Text === 'SALLE LAVAGE :');
  eq(1, lavage.cells.length, 'le titre reste, sans ses valeurs');
  const { out } = composeAll(p, (pg) => drawLotColumns(load(pg.Id).n.image, pg));
  const src = drawLotColumns(load('page1').n.image, p.Pages[0]);
  const u = plan.units[0];
  // Sous la rangée retirée : l'image d'origine, remontée de sa hauteur.
  const y = u.bottom + 40, w = src.w;
  check(src.data.subarray(y * w, (y + 1) * w).every((v, i) => v === out[0].data[(y - (u.bottom - u.top)) * w + i]), 'la suite remonte intacte');
  check(pageArranged(p, plan, 0) && pageArranged(p, plan, 1), 'pages recomposées');
  // Le numéro repris : tout revient.
  setLot(p, findLine(p.Pages[0], '2 rolls en métal'), '4');
  setLot(p, findLine(p.Pages[0], '1 gazelle TULESCA inox'), '28');
  setLot(p, findLine(p.Pages[1], 'CARTONNAGE'), '31');
  p.Pages[1].Tables[2].RowOrder = null;
  const back = planArrangement(p);
  eq(0, back.units.length, 'plus rien à déplacer');
  check(!pageArranged(p, back, 0) && !pageArranged(p, back, 1), 'pages d\'origine');
});

test('Rangée déplacée à la main : ordre du .txt, des lots et de l\'image', () => {
  const p = fromPages(['page1']);
  numberLots(p, 1);
  const t = p.Pages[0].Tables[0];
  const order = [...Array(t.Rows.length).keys()];
  order.splice(2, 1); order.splice(5, 0, 2); // « aspirateur » descend sous « distributeurs »
  t.RowOrder = order;
  const l = lines(p);
  check(l.findIndex((x) => x.includes('aspirateur')) > l.findIndex((x) => x.includes('distributeurs')), '.txt dans le nouvel ordre');
  fillLotsAutomatically(p);
  eq('5', findLine(p.Pages[0], '1 aspirateur KARSHER WDS cuve en inox').Lot, 'numéro selon l\'ordre affiché');
  const img = load('page1').n.image;
  const { plan, out } = composeAll(p, () => img);
  eq(img.h, out[0].h, 'même hauteur');
  const strip = (r) => [Math.round(t.RowYs[r]) - 2, Math.round(t.RowYs[r + 1]) - 2];
  const [a0, a1] = strip(2), [e0] = strip(6);
  const moved = plan.pages[0].pieces.find((pc) => pc.y0 === a0);
  eq(e0 - (a1 - a0), moved?.dst, 'rangée posée sous « distributeurs »');
  check(img.data.subarray(a0 * img.w, a1 * img.w).every((v, i) => v === out[0].data[moved.dst * img.w + i]), 'pixels de la rangée intacts');
});

// ---------- Totaux ----------

test('Totaux : recalculés (écarts, lots 0 retirés), saisie à la main, Recalculer', () => {
  eq(4455000, parseAmount('44 550'), 'lecture');
  eq(550000, parseAmount('S 500'), 'lecture OCR');
  eq(0, parseAmount('mémoire'), 'mémoire');
  eq('198 310', formatAmount(19831000), 'écriture');
  const p = fromPages(['page5']);
  const rows = [['', 'exploitation', 'réalisation'], ['II - STOCK', '44 550', '9 000'], ['TOTAL GENERAL', '44 550', '9 000']];
  p.Pages.push(buildDataPage('recap.csv', rows));
  const total = findLine(p.Pages[0], 'TOTAL STOCK');
  const recap = findLine(p.Pages[1], 'II - STOCK'), general = findLine(p.Pages[1], 'TOTAL GENERAL');
  eq(0, recomputeTotals(p).length, 'rien ne change sans modification');
  findLine(p.Pages[0], 'CARTONNAGE').Cells[1].Text = '17 000';
  recomputeTotals(p);
  eq('45 550', total.Cells[1].Text, 'total de la rubrique');
  eq('45 550', recap.Cells[1].Text, 'récapitulatif');
  eq('45 550', general.Cells[1].Text, 'total général');
  createEmptyLots(p);
  setLot(p, findLine(p.Pages[0], 'PRODUITS FINIS'), '0');
  recomputeTotals(p, planArrangement(p));
  eq('42 050', total.Cells[1].Text, 'lot 0 retiré du total');
  eq('8 000', total.Cells[2].Text, 'autre colonne');
  total.Cells[1].Text = '50 000'; total.Cells[1].Manual = true;
  recomputeTotals(p);
  eq('50 000', total.Cells[1].Text, 'tapé à la main : gardé');
  eq('50 000', recap.Cells[1].Text, 'le récapitulatif suit le total tapé');
  total.Cells[1].Manual = false;
  recomputeTotals(p, planArrangement(p));
  eq('42 050', total.Cells[1].Text, 'Recalculer');
});

test('Couleur du texte : sur la sélection, suit la saisie, .txt inchangé', () => {
  const p = threePages();
  const l0 = lines(p);
  const f = findLine(p.Pages[0], '2 rolls en métal').Cells[0];
  setColor(f, 2, 7, 'red');
  eq('null,red,null', colorRuns(f).map((r) => String(r.color)).join(','), 'morceaux');
  const old = f.Text; f.Text = 'Lot ' + old; adjustStrikes(f, old, f.Text);
  eq('rolls', f.Text.substr(f.Colors[0].Start, f.Colors[0].Length), 'suit le texte');
  eq(l0.join('|'), lines(p).join('|').replace('Lot 2 rolls', '2 rolls'), '.txt sans couleur');
  setColor(f, f.Colors[0].Start, f.Colors[0].Start + f.Colors[0].Length, 'red');
  eq(0, f.Colors.length, 'même couleur une 2e fois : retirée');
  setColor(f, 0, 0, 'blue');
  eq(f.Text.length, f.Colors[0].Length, 'sans sélection : toute la case');
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

const windowsArrange = process.argv[5];
if (windowsArrange) {
  test('Lots 0 en fin, rangée déplacée, totaux : identique à Windows (texte, pixels)', () => {
    const p = arrangeScenario();
    const plan = planArrangement(p);
    recomputeTotals(p, plan);
    const win = readFileSync(join(windowsArrange, 'export.txt'), 'utf8').replace(/^\uFEFF/, '');
    const js = buildTxt(p).text;
    if (win !== js) {
      const a = win.split('\r\n'), b = js.split('\r\n');
      const k = a.findIndex((l, i) => l !== b[i]);
      throw new Error(`.txt ligne ${k + 1} : Windows « ${a[k]} » / iPad « ${b[k]} »`);
    }
    const { out } = composeAll(p, (pg) => drawLotColumns(load(pg.Id).n.image, pg));
    p.Pages.forEach((page, k) => {
      const w = readPgm(join(windowsArrange, page.Id + '.pgm'));
      eq(`${w.w}x${w.h}`, `${out[k].w}x${out[k].h}`, `taille ${page.Id}`);
      const i = w.data.findIndex((v, j) => v !== out[k].data[j]);
      eq(-1, i, `${page.Id} : premier pixel différent (${i % w.w}, ${Math.trunc(i / w.w)})`);
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
