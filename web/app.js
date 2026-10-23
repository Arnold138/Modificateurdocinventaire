// Retouche d'inventaire — version iPhone / iPad (application web installable, hors ligne).
// Même logique que la version Windows : importer → corriger sur la page → exporter JPEG ou .txt.
// Une seule page est affichée à la fois (navigation « 3 / 10 ») : les longs inventaires restent légers en mémoire.
import { Quad } from './core/geometry.js';
import { normalize, render, rotate90 } from './core/imaging.js';
import { detectGrids } from './core/rules.js';
import { prepare, mapBack, emptyFieldsWithInk, prepareCell, applyCellWords } from './core/ocrprep.js';
import { newPage, newProject, analyze, reclassify, allFields, isModified, cycleKind, kindLabel, findLine, firstText } from './core/model.js';
import { buildTxt, txtBlob, composeIntoSource, outputName } from './core/exports.js';
import { decodeText, parseDelimited, readXlsx, buildDataPage } from './core/importers.js';
import { packProject, unpackProject, EXTENSION } from './core/store.js';
import { objectLines, lotsActive, numberLots, clearLots, setLot, resyncLots, lotLabels, repairName } from './core/lots.js';
import { decodeImage, pdfPages, isImageName, mimeOf } from './services/decode.js';
import { recognize, warmUp } from './services/ocr.js';
import { rasterToCanvas, canvasToBlob, renderEdits, freeCanvas } from './services/canvas.js';
import * as db from './services/db.js';
import { PageView } from './ui/pageview.js';
import { cropDialog } from './ui/crop.js';

const $ = (id) => document.getElementById(id);
const tick = () => new Promise((r) => setTimeout(r, 30));
const uid = () => (crypto.randomUUID?.() ?? Math.random().toString(16).slice(2) + Date.now().toString(16)).replace(/-/g, '');
const escapeHtml = (s) => String(s).replace(/[&<>"]/g, (c) => ({ '&': '&amp;', '<': '&lt;', '>': '&gt;', '"': '&quot;' })[c]);

// ---------- État ----------

const state = {
  project: newProject(),
  projectId: uid(),
  sources: {},          // entrée d'archive → octets du fichier importé
  flats: new Map(),     // page → image redressée (quelques pages récentes seulement)
  oriented: new Map(),  // page → photo d'origine orientée (la dernière seulement)
  index: 0,             // page affichée
  view: null,           // PageView de la page affichée
  field: null,          // case sélectionnée
  busy: false,
  lock: false,
  zoom: 1,
};

/** Garde au plus `max` éléments (les plus récents) : la mémoire de l'iPhone est limitée. */
function remember(map, key, value, max) {
  map.delete(key);
  map.set(key, value);
  while (map.size > max) map.delete(map.keys().next().value);
  return value;
}

const pages = () => state.project.Pages;
const currentPage = () => pages()[state.index] ?? null;

// ---------- Interface ----------

const status = (t) => { $('status').textContent = t; };

function progress(fraction) {
  const on = fraction !== null && fraction !== undefined;
  $('progress').hidden = !on;
  if (on) $('progressBar').style.width = `${Math.round(Math.min(1, Math.max(0, fraction)) * 100)}%`;
}

const setBusy = (on) => { state.busy = on; if (!on) progress(null); updateButtons(); };

function updateButtons() {
  const n = pages().length, page = currentPage();
  const b = state.busy;
  $('btnPhoto').disabled = $('btnImport').disabled = $('btnProjects').disabled = b;
  $('btnCrop').disabled = b || page?.Source !== 'Image';
  $('btnLock').disabled = b || !page;
  $('btnLots').disabled = b || n === 0;
  $('btnExport').disabled = b || n === 0;
  $('pager').hidden = n === 0;
  $('pageLabel').textContent = n ? `${state.index + 1} / ${n}` : '';
  $('pagePrev').disabled = b || state.index <= 0;
  $('pageNext').disabled = b || state.index >= n - 1;
  $('pageLabel').disabled = b;
  $('emptyHint').hidden = n > 0;
  $('projectName').textContent = state.project.Name;
  $('btnLots').classList.toggle('on', lotsActive(state.project));
  updateEditBar();
}

function updateEditBar() {
  const f = state.field;
  const hit = f ? findLine(state.project, f) : null;
  $('editbar').hidden = !hit;
  if (!hit) return;
  $('kindLabel').textContent = kindLabel(hit.line.Kind);
  $('btnRevert').disabled = state.busy || !isModified(f);
  $('btnKind').disabled = state.busy;
  const t = firstText(hit.line);
  $('editInfo').textContent = isModified(f) ? `Original : « ${f.OriginalText || '(vide)'} »` : `« ${t.length > 48 ? t.slice(0, 48) + '…' : t} »`;
}

async function run(work) {
  if (state.busy) return;
  setBusy(true);
  try { await work(); }
  catch (e) { console.error(e); await dialog('Erreur', `<p>${escapeHtml(e?.message ?? String(e))}</p>`, [{ label: 'OK' }]); }
  finally { setBusy(false); }
}

/** Fenêtre simple ; chaque bouton peut lancer une action (dans le geste de l'utilisateur, utile pour « Partager »). */
function dialog(title, html, buttons, onBody = null) {
  return new Promise((resolve) => {
    const close = (v) => { $('dialog').hidden = true; $('dialogBody').onclick = null; resolve(v); };
    $('dialogTitle').textContent = title;
    $('dialogBody').innerHTML = html;
    $('dialogBody').onclick = onBody ? (e) => onBody(e, close) : null;
    const box = $('dialogButtons');
    box.replaceChildren();
    for (const b of buttons) {
      const btn = document.createElement('button');
      btn.textContent = b.label;
      if (b.className) btn.className = b.className;
      btn.onclick = async () => {
        if (b.action) { const keep = await b.action(); if (keep) return; }
        close(b.value ?? b.label);
      };
      box.appendChild(btn);
    }
    $('dialog').hidden = false;
  });
}

const confirmDialog = (title, text, ok = 'Continuer') =>
  dialog(title, `<p>${escapeHtml(text)}</p>`, [{ label: 'Annuler', value: false }, { label: ok, value: true, className: 'accent' }]);

// ---------- Zoom (pincer, boutons) ----------

function applyZoom(z, cx = null, cy = null) {
  const vp = $('viewport');
  const old = state.zoom;
  z = Math.min(4, Math.max(0.15, z));
  const px = cx ?? vp.clientWidth / 2, py = cy ?? vp.clientHeight / 2;
  const ux = (vp.scrollLeft + px) / old, uy = (vp.scrollTop + py) / old;
  state.zoom = z;
  $('zoomer').style.transform = `scale(${z})`;
  const box = $('pages');
  Object.assign($('sizer').style, { width: box.offsetWidth * z + 'px', height: box.offsetHeight * z + 'px' });
  vp.scrollLeft = ux * z - px;
  vp.scrollTop = uy * z - py;
}

function fitWidth() {
  const box = $('pages');
  if (!state.view || !box.offsetWidth) return applyZoom(state.zoom);
  applyZoom(Math.min(1.5, $('viewport').clientWidth / box.offsetWidth));
}

function initZoom() {
  const vp = $('viewport');
  let start = 1;
  // Safari (iPhone/iPad) : événements de geste natifs.
  vp.addEventListener('gesturestart', (e) => { e.preventDefault(); start = state.zoom; });
  vp.addEventListener('gesturechange', (e) => {
    e.preventDefault();
    const r = vp.getBoundingClientRect();
    applyZoom(start * e.scale, e.clientX - r.left, e.clientY - r.top);
  });
  vp.addEventListener('gestureend', (e) => e.preventDefault());
  // Autres navigateurs : Ctrl + molette / pincement du pavé tactile.
  vp.addEventListener('wheel', (e) => {
    if (!e.ctrlKey) return;
    e.preventDefault();
    const r = vp.getBoundingClientRect();
    applyZoom(state.zoom * Math.exp(-e.deltaY / 200), e.clientX - r.left, e.clientY - r.top);
  }, { passive: false });
  $('zoomIn').onclick = () => applyZoom(state.zoom * 1.25);
  $('zoomOut').onclick = () => applyZoom(state.zoom / 1.25);
  $('zoomFit').onclick = fitWidth;
  window.addEventListener('resize', () => applyZoom(state.zoom));
}

// ---------- Affichage d'une page ----------

function disposeView() {
  state.view?.dispose();
  state.view = null;
  state.field = null;
  $('pages').replaceChildren();
}

/** Affiche la page n (et seulement elle). */
async function showPage(n, { flat = null, fit = true } = {}) {
  const list = pages();
  disposeView();
  if (list.length === 0) { state.index = 0; updateButtons(); applyZoom(1); return; }
  state.index = Math.min(list.length - 1, Math.max(0, n));
  const page = list[state.index];
  updateButtons();

  let image = null;
  if (page.Source === 'Image') {
    image = flat ?? state.flats.get(page) ?? null;
    if (!image) {
      status(`Ouverture de la page ${state.index + 1}…`);
      await tick();
      image = await restoreFlat(page);
    }
    if (image) remember(state.flats, page, image, 3);
    if (list[state.index] !== page) return; // l'utilisateur a déjà changé de page
  }

  const head = document.createElement('div');
  head.className = 'page-head';
  const title = document.createElement('span');
  title.textContent = `Page ${state.index + 1} sur ${list.length} · ${page.SourceName}`;
  const remove = document.createElement('button');
  remove.textContent = 'Retirer la page';
  remove.onclick = () => removePage(page);
  head.append(title, remove);

  const view = new PageView(page, image, {
    zoom: () => state.zoom,
    onFocus: (v, f) => { state.field = f; updateEditBar(); },
    onEdited: (v) => { afterEdit(v); updateEditBar(); scheduleSave(); },
    onTouch: () => {},
    onLocks: () => { status('Zones verrouillées mises à jour.'); scheduleSave(); },
    onLot: (v, line, value) => {
      setLot(state.project, line, value);
      v.refreshLots();
      status(`Lot ${line.Lot || '(automatique)'} : les numéros suivants ont été recalés.`);
      scheduleSave();
    },
  });
  view.setLockMode(state.lock);
  state.view = view;
  $('pages').append(head, view.row);
  $('viewport').scrollTop = 0;
  if (fit) requestAnimationFrame(fitWidth); else applyZoom(state.zoom);
  status(page.Source === 'Image'
    ? 'Touchez une case pour la corriger ; l\'original s\'affiche en loupe au-dessus.'
    : 'Touchez une case pour la corriger.');
  updateButtons();
}

const lotSignature = (page) => lotLabels(page).map((l) => l.line.Cells[0]?.Id + ':' + l.line.Lot).join('|');

/** Après une saisie : si une ligne devient (ou cesse d'être) un objet, les lots se recalent. */
function afterEdit(view) {
  if (!lotsActive(state.project)) return;
  const before = lotSignature(view.page);
  resyncLots(state.project);
  if (lotSignature(view.page) !== before) view.refreshLots();
}

function goTo(n) {
  if (state.busy || n === state.index || n < 0 || n >= pages().length) return;
  saveNow();
  run(() => showPage(n));
}

async function removePage(page) {
  if (state.busy || !(await confirmDialog('Retirer cette page ?', `« ${page.SourceName} » sera retirée du projet et des exports.`, 'Retirer'))) return;
  const i = pages().indexOf(page);
  if (i < 0) return;
  pages().splice(i, 1);
  state.flats.delete(page); state.oriented.delete(page);
  resyncLots(state.project);
  await showPage(Math.min(i, pages().length - 1));
  scheduleSave();
}

async function pagesDialog() {
  const list = pages();
  const html = `<ul class="list pagelist">${list.map((p, i) => {
    const edits = [...allFields(p)].filter(isModified).length;
    return `<li class="${i === state.index ? 'current' : ''}" data-i="${i}">
      <span class="num">${i + 1}</span>
      <span class="name">${escapeHtml(p.SourceName)}<small>${edits ? `${edits} case(s) modifiée(s)` : 'non modifiée'}</small></span>
      <button data-act="go">${i === state.index ? 'Affichée' : 'Voir'}</button></li>`;
  }).join('')}</ul>`;
  const choice = await dialog(`${list.length} page(s)`, html, [{ label: 'Fermer', value: null }], (e, close) => {
    const li = e.target.closest('li[data-i]');
    if (li) close(+li.dataset.i);
  });
  if (typeof choice === 'number') goTo(choice);
}

// ---------- Traitement d'une photo ----------

const readable = (words) => words.filter((w) => /^\p{L}{3,}$/u.test(w.Text) && /[aeiouyàâéèêëîïôûù]/i.test(w.Text)).length;

/** Redressement → grille → lecture de la page → seconde lecture des cases vides → analyse. */
async function processImage(page, oriented, crop, step = () => {}) {
  step(0.05, 'redressement…'); await tick();
  const n = normalize(oriented, crop);
  page.Crop = n.crop; page.Skew = n.skew;
  const grids = detectGrids(n.image);
  const prep = prepare(n.image, grids);
  step(0.15, 'lecture du texte…'); await tick();
  const words = mapBack(await recognize(prep.image, 11), prep.scale);
  analyze(page, n.image, words, grids);
  const empty = emptyFieldsWithInk(page, n.image);
  for (let i = 0; i < empty.length; i++) {
    step(0.6 + (0.4 * i) / empty.length, `relecture des cases (${i + 1}/${empty.length})…`);
    const c = prepareCell(n.image, empty[i].Box);
    applyCellWords(empty[i], await recognize(c.image, 7), c.scale, c.origin);
  }
  reclassify(page);
  return { flat: n.image, words };
}

async function importImage(bytes, entry, name, type, step) {
  let source = await decodeImage(bytes, type);
  const page = newPage({ Source: 'Image', SourceName: name, SourceEntry: entry });
  let { flat, words } = await processImage(page, source, null, step);
  // Page tournée d'un quart de tour : on essaie les autres sens.
  const found = readable(words);
  if (found < 12) {
    let best = found, bestTurns = 0;
    for (let k = 1; k <= 3; k++) {
      step(0.9, `recherche du bon sens (${k}/3)…`); await tick();
      const n = readable(await recognize(prepare(rotate90(flat, k), []).image, 11));
      if (n > best) { best = n; bestTurns = k; }
    }
    if (bestTurns && best >= 12 && best > 2 * found) {
      page.QuarterTurns = bestTurns;
      source = rotate90(source, bestTurns);
      ({ flat } = await processImage(page, source, null, step));
    }
  }
  remember(state.oriented, page, source, 1);
  return { page, flat };
}

async function addSource(entry, bytes) {
  state.sources[entry] = bytes;
  await db.putSource(state.projectId, entry, bytes);
}

/** Ajoute une page au projet, l'affiche et enregistre aussitôt (rien n'est perdu si l'app est fermée en route). */
async function addPage(page, flat = null) {
  pages().push(page);
  resyncLots(state.project);
  if (flat) remember(state.flats, page, flat, 3);
  await showPage(pages().length - 1, { flat, fit: true });
  await saveNow();
}

async function importFiles(files) {
  let added = 0;
  const wasEmpty = pages().length === 0;
  const firstNew = pages().length;
  const sorted = [...files].sort((a, b) => a.name.localeCompare(b.name, 'fr'));
  for (let k = 0; k < sorted.length; k++) {
    const file = sorted[k];
    const name = repairName(file.name || `Photo ${new Date().toLocaleString('fr-FR')}.jpg`);
    // Progression globale : fichier k sur N, page i sur n à l'intérieur d'un PDF.
    const report = (part, text) => {
      progress((k + part) / sorted.length);
      status(text);
    };
    try {
      const ext = (name.match(/\.[^.]+$/)?.[0] ?? '').toLowerCase();
      const bytes = new Uint8Array(await file.arrayBuffer());
      if (ext === EXTENSION) { await openProjectFile(bytes, name); continue; }
      if (ext === '.txt' || ext === '.csv') {
        await addPage(buildDataPage(name, parseDelimited(decodeText(bytes), ext)));
      } else if (ext === '.xlsx') {
        for (const { sheet, rows } of readXlsx(bytes)) await addPage(buildDataPage(`${name} – ${sheet}`, rows));
      } else if (ext === '.pdf' || file.type === 'application/pdf') {
        report(0, `${name} : ouverture du PDF…`); await tick();
        for await (const { index, count, png } of pdfPages(bytes)) {
          const label = `${name} – page ${index}`;
          const step = (f, text) => report((index - 1 + f) / count, `Page ${index} sur ${count} : ${text}`);
          step(0, 'préparation…');
          const entry = `sources/${uid()}.png`;
          await addSource(entry, png);
          const r = await importImage(png, entry, label, 'image/png', step);
          await addPage(r.page, r.flat);
        }
      } else if (isImageName(name) || file.type.startsWith('image/')) {
        const entry = `sources/${uid()}${ext || '.jpg'}`;
        await addSource(entry, bytes);
        const label = sorted.length > 1 ? `Photo ${k + 1} sur ${sorted.length}` : name;
        const r = await importImage(bytes, entry, name, file.type || mimeOf(name), (f, text) => report(f, `${label} : ${text}`));
        await addPage(r.page, r.flat);
      } else {
        throw new Error('format non pris en charge');
      }
      added++;
    } catch (e) {
      console.error(e);
      await dialog('Import impossible', `<p>${escapeHtml(name)} : ${escapeHtml(e?.message ?? String(e))}</p>`, [{ label: 'OK' }]);
    }
  }
  if (added && wasEmpty) state.project.Name = repairName(sorted[0].name || 'Inventaire').replace(/\.[^.]+$/, '');
  if (added) {
    // On revient sur la première page importée.
    if (firstNew < pages().length && state.index !== firstNew) await showPage(firstNew);
    await saveNow();
    const n = pages().length - firstNew;
    status(`${n} page(s) ajoutée(s). Touchez une case pour la corriger ; ◀ ▶ en haut pour changer de page.`);
  }
}

async function orientedSource(page) {
  if (state.oriented.has(page)) return state.oriented.get(page);
  const bytes = state.sources[page.SourceEntry];
  if (!bytes) return null;
  const src = rotate90(await decodeImage(bytes, mimeOf(page.SourceEntry)), page.QuarterTurns);
  return remember(state.oriented, page, src, 1);
}

/** Image affichée d'une page enregistrée : recalculée à l'identique, sans relire le texte. */
async function restoreFlat(page) {
  const src = await orientedSource(page);
  if (!src) return null;
  return render(src, page.Crop ?? Quad.full(src.w, src.h), page.Skew, page.Width, page.Height);
}

// ---------- Enregistrement continu ----------

let saveTimer = null;
function scheduleSave() {
  clearTimeout(saveTimer);
  $('saveState').textContent = '…';
  saveTimer = setTimeout(saveNow, 800);
}

async function saveNow() {
  clearTimeout(saveTimer);
  if (pages().length === 0) return;
  try {
    await db.saveProject(state.projectId, state.project);
    await db.setMeta('last', state.projectId);
    $('saveState').textContent = `✓ ${new Date().toLocaleTimeString('fr-FR', { hour: '2-digit', minute: '2-digit' })}`;
  } catch (e) {
    $('saveState').textContent = '⚠ Non enregistré';
    status(`Enregistrement impossible : ${e?.message ?? e}`);
  }
}

function resetMemory() {
  disposeView();
  state.flats.clear();
  state.oriented.clear();
}

async function loadProjectById(id) {
  const rec = await db.loadProject(id);
  if (!rec) return false;
  resetMemory();
  state.project = rec.project;
  state.projectId = id;
  state.sources = rec.sources;
  state.project.Name = repairName(state.project.Name);
  for (const p of pages()) p.SourceName = repairName(p.SourceName);
  await db.setMeta('last', id);
  await showPage(0);
  return true;
}

async function openProjectFile(bytes, name) {
  const { project, sources } = unpackProject(bytes);
  const id = uid();
  for (const [entry, data] of Object.entries(sources)) await db.putSource(id, entry, data);
  await db.saveProject(id, project);
  await loadProjectById(id);
  status(`Projet ouvert : ${name}`);
}

function newProjectState() {
  resetMemory();
  state.project = newProject();
  state.projectId = uid();
  state.sources = {};
  state.index = 0;
  $('saveState').textContent = '';
  applyZoom(1);
  updateButtons();
}

// ---------- Partage / enregistrement des fichiers exportés ----------

async function deliver(files, title, intro) {
  const canShare = !!navigator.canShare?.({ files });
  const share = async () => {
    try { await navigator.share({ files, title }); }
    catch (e) { if (e?.name !== 'AbortError') downloadAll(files); }
    return true;
  };
  const list = files.map((f) => `<li>${escapeHtml(f.name)}</li>`).join('');
  await dialog(title, `${intro}<ul>${list}</ul>${canShare ? '<p>« Enregistrer / Partager » ouvre le menu de partage : <b>Enregistrer dans Fichiers</b>, <b>Enregistrer l\'image</b>, Mail, AirDrop…</p>' : ''}`, [
    ...(canShare ? [{ label: 'Enregistrer / Partager', className: 'accent', action: share }] : []),
    { label: canShare ? 'Télécharger' : 'Enregistrer', className: canShare ? '' : 'accent', action: () => { downloadAll(files); return true; } },
    { label: 'Fermer' },
  ]);
}

function downloadAll(files) {
  for (const f of files) {
    const a = document.createElement('a');
    a.href = URL.createObjectURL(f);
    a.download = f.name;
    document.body.appendChild(a);
    a.click();
    a.remove();
    setTimeout(() => URL.revokeObjectURL(a.href), 30000);
  }
}

// ---------- Exports ----------

async function exportTxt() {
  const { text, lineCount, warnings } = buildTxt(state.project);
  const file = new File([txtBlob(text)], `${state.project.Name}.txt`, { type: 'text/plain' });
  const warn = warnings.length
    ? `<p><b>À vérifier :</b></p><ul>${warnings.slice(0, 8).map((w) => `<li>${escapeHtml(w)}</li>`).join('')}</ul>`
    : '';
  const lots = lotsActive(state.project) ? ' La 1re colonne contient le numéro de lot.' : '';
  await deliver([file], 'Fichier texte prêt', `<p>${lineCount} lignes, colonnes séparées par des tabulations (UTF-8).${lots}</p>${warn}`);
}

async function exportJpeg(only = null) {
  const list = only ? [only] : pages().filter((p) => p.Source === 'Image');
  const files = [];
  for (let i = 0; i < list.length; i++) {
    const page = list[i];
    progress(i / list.length);
    status(`Création de l'image ${i + 1} sur ${list.length}…`); await tick();
    const flat = state.flats.get(page) ?? (await restoreFlat(page));
    const src = await orientedSource(page);
    if (!flat || !src) continue;
    const final = composeIntoSource(src, page, renderEdits(page, flat));
    const canvas = rasterToCanvas(final);
    const blob = await canvasToBlob(canvas, 'image/jpeg', 0.93);
    freeCanvas(canvas);
    files.push(new File([blob], outputName(page), { type: 'image/jpeg' }));
  }
  progress(null);
  status(`${files.length} image(s) prête(s).`);
  await deliver(files, files.length > 1 ? 'Images prêtes' : 'Image prête', "<p>Chaque photo d'origine, inchangée sauf dans les cases modifiées" + (lotsActive(state.project) ? ' et les numéros de lot dans la marge gauche' : '') + '.</p>');
}

async function exportDialog() {
  await saveNow();
  const images = pages().filter((p) => p.Source === 'Image').length;
  const page = currentPage();
  const choice = await dialog('Exporter', '<p>Choisissez ce que vous voulez obtenir.</p>', [
    ...(images ? [{ label: images > 1 ? `Toutes les images JPEG (${images})` : 'Image JPEG', value: 'all', className: 'accent' }] : []),
    ...(images > 1 && page?.Source === 'Image' ? [{ label: `Image de la page ${state.index + 1} seulement`, value: 'one' }] : []),
    { label: 'Fichier texte (.txt)', value: 'txt', className: images ? '' : 'accent' },
    { label: 'Annuler', value: null },
  ]);
  if (choice === 'all') await exportJpeg();
  else if (choice === 'one') await exportJpeg(page);
  else if (choice === 'txt') await exportTxt();
}

// ---------- Lots ----------

async function lotsDialog() {
  const objects = [...objectLines(state.project)].map((o) => o.line);
  const active = lotsActive(state.project);
  const first = objects[0]?.Lot, last = objects[objects.length - 1]?.Lot;
  const html = `
    <p>Un numéro de lot par objet, dans une colonne à gauche de la page. Il apparaît dans le <b>.txt</b> (1re colonne) et sur le <b>JPEG</b> (marge gauche). Les titres, totaux et en-têtes n'en ont pas.</p>
    ${active ? `<p><b>${objects.length} objets</b> numérotés de <b>${escapeHtml(first ?? '')}</b> à <b>${escapeHtml(last ?? '')}</b>, toutes pages confondues.</p>
      <p class="muted">Pour changer un numéro, touchez-le dans la colonne de gauche : les suivants se recalent tout seuls.</p>`
    : `<p><b>${objects.length} objets</b> trouvés sur ${pages().length} page(s).</p>`}
    <label>Commencer à <input id="lotStart" type="number" inputmode="numeric" min="0" value="${active && /^\d+$/.test(first ?? '') ? first : 1}"></label>`;
  const choice = await dialog('Numéros de lot', html, [
    { label: active ? 'Renuméroter' : 'Numéroter', value: 'number', className: 'accent' },
    ...(active ? [{ label: 'Enlever les numéros', value: 'clear', className: 'danger' }] : []),
    { label: 'Fermer', value: null },
  ]);
  if (choice === 'number') {
    const start = parseInt($('lotStart')?.value ?? '1', 10);
    numberLots(state.project, Number.isFinite(start) ? start : 1);
    status(`Lots numérotés de ${objects[0]?.Lot ?? ''} à ${objects[objects.length - 1]?.Lot ?? ''}.`);
  } else if (choice === 'clear') {
    if (!(await confirmDialog('Enlever les numéros de lot ?', 'Tous les numéros seront retirés (texte et images).', 'Enlever'))) return;
    clearLots(state.project);
    status('Numéros de lot enlevés.');
  } else return;
  state.view?.refreshLots();
  updateButtons();
  scheduleSave();
}

// ---------- Boutons ----------

function initButtons() {
  $('btnPhoto').onclick = () => $('filePhoto').click();
  $('btnImport').onclick = () => $('fileImport').click();
  for (const id of ['filePhoto', 'fileImport'])
    $(id).onchange = (e) => {
      const files = [...e.target.files];
      e.target.value = '';
      if (files.length) run(() => importFiles(files));
    };

  $('pagePrev').onclick = () => goTo(state.index - 1);
  $('pageNext').onclick = () => goTo(state.index + 1);
  $('pageLabel').onclick = () => { if (!state.busy) pagesDialog(); };
  document.addEventListener('keydown', (e) => {
    if (e.target.closest?.('input, textarea') || !$('dialog').hidden || !$('crop').hidden) return;
    if (e.key === 'ArrowLeft' || e.key === 'PageUp') goTo(state.index - 1);
    if (e.key === 'ArrowRight' || e.key === 'PageDown') goTo(state.index + 1);
  });

  $('btnLock').onclick = () => {
    state.lock = !state.lock;
    $('btnLock').setAttribute('aria-pressed', String(state.lock));
    state.view?.setLockMode(state.lock);
    status(state.lock
      ? 'Verrouillage : tracez un rectangle au doigt sur une signature ou un tampon. Touchez une zone orange pour la retirer. Retouchez « Verrouiller » pour terminer.'
      : 'Verrouillage terminé.');
  };

  $('btnKind').onclick = () => {
    const f = state.field, view = state.view;
    const hit = f ? findLine(state.project, f) : null;
    if (!hit || !view) return;
    cycleKind(hit.line);
    resyncLots(state.project);
    view.refresh();
    view.focus(f);
    state.field = f;
    updateEditBar();
    status(`Type de ligne : ${kindLabel(hit.line.Kind)}.`);
    scheduleSave();
  };

  $('btnRevert').onclick = () => {
    const f = state.field;
    if (!f || !state.view) return;
    state.view.revert(f);
    afterEdit(state.view);
    updateEditBar();
    scheduleSave();
  };

  $('btnCrop').onclick = () => run(async () => {
    const page = currentPage();
    if (page?.Source !== 'Image') return;
    const src = await orientedSource(page);
    if (!src) return;
    const result = await cropDialog(src, page.Crop);
    if (!result) return status('Recadrage annulé.');
    if ([...allFields(page)].some(isModified) && !(await confirmDialog('Recadrer la page ?', 'Le texte de cette page va être relu : ses modifications seront perdues.'))) return;
    const fresh = newPage({ Id: page.Id, Source: 'Image', SourceName: page.SourceName, SourceEntry: page.SourceEntry, QuarterTurns: (page.QuarterTurns + result.turns) % 4 });
    const { flat } = await processImage(fresh, result.source, result.quad, (f, text) => { progress(f); status(`Recadrage : ${text}`); });
    const i = pages().indexOf(page);
    if (i < 0) return;
    pages()[i] = fresh;
    state.flats.delete(page); state.oriented.delete(page);
    remember(state.oriented, fresh, result.source, 1);
    resyncLots(state.project);
    await showPage(i, { flat });
    status('Page recadrée et relue. Vérifiez les zones à verrouiller.');
    scheduleSave();
  });

  $('btnLots').onclick = () => run(lotsDialog);
  $('btnExport').onclick = () => run(exportDialog);
  $('btnProjects').onclick = () => run(projectsDialog);
}

async function projectsDialog() {
  await saveNow();
  const list = await db.listProjects();
  const html = `<ul class="list">${list.map((p) => `
    <li class="${p.id === state.projectId ? 'current' : ''}" data-id="${p.id}">
      <span class="name">${escapeHtml(repairName(p.name))}<small>${p.pages} page(s) · ${new Date(p.updated).toLocaleString('fr-FR')}</small></span>
      ${p.id === state.projectId ? '<span class="muted">ouvert</span>' : '<button data-act="open">Ouvrir</button>'}
      <button data-act="del">Supprimer</button>
    </li>`).join('') || '<li>Aucun projet enregistré.</li>'}</ul>
    <p class="muted">Les projets sont enregistrés automatiquement sur l'appareil. « Copie de sauvegarde » crée un fichier <b>.inventaire</b> (ouvrable aussi sur la Surface).</p>`;
  const choice = await dialog('Projets', html, [
    { label: 'Nouveau projet', value: 'new' },
    { label: 'Renommer', value: 'rename' },
    { label: 'Copie de sauvegarde', value: 'backup', className: 'accent' },
    { label: 'Fermer', value: 'close' },
  ], (e, close) => {
    const btn = e.target.closest('button[data-act]');
    if (btn) close({ act: btn.dataset.act, id: btn.closest('li').dataset.id });
  });
  const action = typeof choice === 'object' ? choice : null;

  if (action?.act === 'open') {
    await loadProjectById(action.id);
    status(`Projet ouvert : ${state.project.Name}`);
  } else if (action?.act === 'del') {
    const p = list.find((x) => x.id === action.id);
    if (await confirmDialog('Supprimer ce projet ?', `« ${p?.name} » sera supprimé de l'appareil (les fichiers exportés ne sont pas touchés).`, 'Supprimer')) {
      await db.deleteProject(action.id);
      if (action.id === state.projectId) newProjectState();
      status('Projet supprimé.');
    }
  } else if (choice === 'new') {
    newProjectState();
    status('Nouveau projet : touchez « Photo » ou « Importer ».');
  } else if (choice === 'rename') {
    const name = prompt('Nom du projet', state.project.Name);
    if (name?.trim()) { state.project.Name = name.trim(); await saveNow(); updateButtons(); status(`Projet renommé : ${state.project.Name}`); }
  } else if (choice === 'backup') {
    const blob = new Blob([packProject(state.project, state.sources)], { type: 'application/zip' });
    await deliver([new File([blob], `${state.project.Name}${EXTENSION}`, { type: 'application/zip' })], 'Copie de sauvegarde',
      '<p>Fichier projet complet (photos et corrections), à garder dans Fichiers ou à ouvrir sur la Surface.</p>');
  }
}

// ---------- Démarrage ----------

async function start() {
  initButtons();
  initZoom();
  updateButtons();
  if ('serviceWorker' in navigator) navigator.serviceWorker.register('sw.js').catch(() => {});
  navigator.storage?.persist?.().catch(() => {});
  document.addEventListener('visibilitychange', () => { if (document.visibilityState === 'hidden') saveNow(); });
  window.addEventListener('pagehide', saveNow);
  setBusy(true);
  try {
    const last = await db.getMeta('last');
    if (last && (await loadProjectById(last))) status(`Travail en cours rechargé (${pages().length} page(s)).`);
    else status('Prêt. Fonctionne sans Internet : rien ne quitte l\'appareil.');
  } catch (e) {
    console.error(e);
    status('Prêt. (Le dernier projet n\'a pas pu être rechargé.)');
  } finally {
    setBusy(false);
  }
  warmUp();
}

window.addEventListener('DOMContentLoaded', start);
export { state };
