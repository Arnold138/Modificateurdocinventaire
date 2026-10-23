// Retouche d'inventaire — version iPad (application web installable, hors ligne).
// Même logique que la version Windows : importer → corriger sur la page → exporter JPEG ou .txt.
import { Quad } from './core/geometry.js';
import { normalize, render, rotate90 } from './core/imaging.js';
import { detectGrids } from './core/rules.js';
import { prepare, mapBack, emptyFieldsWithInk, prepareCell, applyCellWords } from './core/ocrprep.js';
import { newPage, newProject, analyze, reclassify, allFields, isModified, cycleKind, kindLabel, findLine, firstText } from './core/model.js';
import { buildTxt, txtBlob, composeIntoSource, outputName } from './core/exports.js';
import { decodeText, parseDelimited, readXlsx, buildDataPage } from './core/importers.js';
import { packProject, unpackProject, EXTENSION } from './core/store.js';
import { decodeImage, renderPdf, isImageName, mimeOf } from './services/decode.js';
import { recognize, warmUp } from './services/ocr.js';
import { rasterToCanvas, canvasToBlob, renderEdits } from './services/canvas.js';
import * as db from './services/db.js';
import { PageView } from './ui/pageview.js';
import { cropDialog } from './ui/crop.js';

const $ = (id) => document.getElementById(id);
const tick = () => new Promise((r) => setTimeout(r, 30));
const uid = () => (crypto.randomUUID?.() ?? Math.random().toString(16).slice(2) + Date.now().toString(16)).replace(/-/g, '');

// ---------- État ----------

const state = {
  project: newProject(),
  projectId: uid(),
  sources: {},          // entrée d'archive → octets du fichier importé
  flats: new Map(),     // page → image redressée
  oriented: new Map(),  // page → source orientée (recadrage, export JPEG)
  views: new Map(),     // page → PageView
  frames: new Map(),    // page → bloc DOM (titre + page)
  current: null,        // { view, field }
  busy: false,
  lock: false,
  zoom: 1,
};

// ---------- Interface ----------

const status = (t) => { $('status').textContent = t; };
const setBusy = (on) => {
  state.busy = on;
  $('busy').hidden = !on;
  updateButtons();
};

function updateButtons() {
  const any = state.project.Pages.length > 0;
  const images = state.project.Pages.some((p) => p.Source === 'Image');
  const f = state.current?.field;
  $('btnPhoto').disabled = $('btnImport').disabled = $('btnProjects').disabled = state.busy;
  $('btnCrop').disabled = state.busy || !images;
  $('btnLock').disabled = state.busy || !any;
  $('btnKind').disabled = state.busy || !f;
  $('btnRevert').disabled = state.busy || !f || !isModified(f);
  $('btnJpeg').disabled = state.busy || !images;
  $('btnTxt').disabled = state.busy || !any;
  $('emptyHint').hidden = any;
}

async function run(work) {
  if (state.busy) return;
  setBusy(true);
  try { await work(); }
  catch (e) { console.error(e); await dialog('Erreur', `<p>${escapeHtml(e?.message ?? String(e))}</p>`, [{ label: 'OK' }]); }
  finally { setBusy(false); }
}

const escapeHtml = (s) => s.replace(/[&<>"]/g, (c) => ({ '&': '&amp;', '<': '&lt;', '>': '&gt;', '"': '&quot;' })[c]);

/** Fenêtre simple ; chaque bouton peut lancer une action (dans le geste de l'utilisateur, utile pour « Partager »). */
function dialog(title, html, buttons) {
  return new Promise((resolve) => {
    $('dialogTitle').textContent = title;
    $('dialogBody').innerHTML = html;
    const box = $('dialogButtons');
    box.replaceChildren();
    for (const b of buttons) {
      const btn = document.createElement('button');
      btn.textContent = b.label;
      if (b.className) btn.className = b.className;
      btn.onclick = async () => {
        if (b.action) { const keep = await b.action(); if (keep) return; }
        $('dialog').hidden = true;
        resolve(b.value ?? b.label);
      };
      box.appendChild(btn);
    }
    $('dialog').hidden = false;
  });
}

const confirmDialog = (title, text) =>
  dialog(title, `<p>${escapeHtml(text)}</p>`, [{ label: 'Annuler', value: false }, { label: 'Continuer', value: true, className: 'accent' }]);

// ---------- Zoom (pincer, boutons) ----------

function applyZoom(z, cx = null, cy = null) {
  const vp = $('viewport');
  const old = state.zoom;
  z = Math.min(4, Math.max(0.2, z));
  const px = cx ?? vp.clientWidth / 2, py = cy ?? vp.clientHeight / 2;
  const ux = (vp.scrollLeft + px) / old, uy = (vp.scrollTop + py) / old;
  state.zoom = z;
  $('zoomer').style.transform = `scale(${z})`;
  const pages = $('pages');
  Object.assign($('sizer').style, { width: pages.offsetWidth * z + 'px', height: pages.offsetHeight * z + 'px' });
  vp.scrollLeft = ux * z - px;
  vp.scrollTop = uy * z - py;
}

function fitWidth() {
  const pages = $('pages');
  if (!state.project.Pages.length || !pages.offsetWidth) return applyZoom(state.zoom);
  applyZoom(Math.min(1.5, $('viewport').clientWidth / pages.offsetWidth));
}

function initZoom() {
  const vp = $('viewport');
  let start = 1;
  // Safari (iPad) : événements de geste natifs.
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

// ---------- Pages à l'écran ----------

function showPage(page, flat, oriented) {
  if (flat) state.flats.set(page, flat);
  if (oriented) state.oriented.set(page, oriented);
  const view = new PageView(page, flat, {
    zoom: () => state.zoom,
    onFocus: (v, f) => { state.current = { view: v, field: f }; updateSelection(); },
    onEdited: () => { updateSelection(); scheduleSave(); },
    onTouch: (v) => { if (state.current?.view !== v) state.current = { view: v, field: null }; updateButtons(); },
    onLocks: () => { status('Zones verrouillées mises à jour.'); scheduleSave(); },
  });
  view.setLockMode(state.lock);
  state.views.set(page, view);

  let frame = state.frames.get(page);
  if (!frame) {
    frame = document.createElement('div');
    const head = document.createElement('div');
    head.className = 'page-head';
    const title = document.createElement('span');
    const remove = document.createElement('button');
    remove.textContent = 'Retirer la page';
    remove.onclick = () => removePage(page);
    head.append(title, remove);
    frame.append(head);
    frame.title_ = title;
    state.frames.set(page, frame);
    $('pages').appendChild(frame);
  } else frame.querySelector('.page')?.remove();
  frame.appendChild(view.root);
  renumber();
  updateButtons();
}

function renumber() {
  const pages = state.project.Pages;
  pages.forEach((p, i) => { const f = state.frames.get(p); if (f) f.title_.textContent = `Page ${i + 1}/${pages.length} · ${p.SourceName}`; });
}

function clearScreen() {
  $('pages').replaceChildren();
  state.views.clear(); state.frames.clear(); state.flats.clear(); state.oriented.clear();
  state.current = null;
}

async function removePage(page) {
  if (state.busy || !(await confirmDialog('Retirer cette page ?', `« ${page.SourceName} » sera retirée du projet et des exports.`))) return;
  const i = state.project.Pages.indexOf(page);
  if (i >= 0) state.project.Pages.splice(i, 1);
  state.frames.get(page)?.remove();
  state.frames.delete(page); state.views.delete(page); state.flats.delete(page); state.oriented.delete(page);
  if (state.current?.view?.page === page) state.current = null;
  renumber(); updateSelection(); scheduleSave(); applyZoom(state.zoom);
}

function updateSelection() {
  const f = state.current?.field;
  const hit = f ? findLine(state.project, f) : null;
  if (hit) {
    $('kindLabel').textContent = `Type : ${kindLabel(hit.line.Kind)}`;
    const t = firstText(hit.line);
    status(`« ${t.length > 40 ? t.slice(0, 40) + '…' : t} » · ${kindLabel(hit.line.Kind)}` + (isModified(f) ? ` · original : « ${f.OriginalText} »` : ''));
  } else $('kindLabel').textContent = 'Type de ligne';
  updateButtons();
}

// ---------- Traitement d'une photo ----------

const readable = (words) => words.filter((w) => /^\p{L}{3,}$/u.test(w.Text) && /[aeiouyàâéèêëîïôûù]/i.test(w.Text)).length;

/** Redressement → grille → lecture de la page → seconde lecture des cases vides → analyse. */
async function processImage(page, oriented, crop) {
  status(`${page.SourceName} : redressement…`); await tick();
  const n = normalize(oriented, crop);
  page.Crop = n.crop; page.Skew = n.skew;
  const grids = detectGrids(n.image);
  const prep = prepare(n.image, grids);
  status(`${page.SourceName} : lecture du texte…`); await tick();
  const words = mapBack(await recognize(prep.image, 11), prep.scale);
  analyze(page, n.image, words, grids);
  const empty = emptyFieldsWithInk(page, n.image);
  for (let i = 0; i < empty.length; i++) {
    status(`${page.SourceName} : relecture des cases (${i + 1}/${empty.length})…`);
    const c = prepareCell(n.image, empty[i].Box);
    applyCellWords(empty[i], await recognize(c.image, 7), c.scale, c.origin);
  }
  reclassify(page);
  return { flat: n.image, words };
}

async function importImage(bytes, entry, name, type) {
  let source = await decodeImage(bytes, type);
  const page = newPage({ Source: 'Image', SourceName: name, SourceEntry: entry });
  let { flat, words } = await processImage(page, source, null);
  // Page tournée d'un quart de tour : on essaie les autres sens.
  const found = readable(words);
  if (found < 12) {
    let best = found, bestTurns = 0;
    for (let k = 1; k <= 3; k++) {
      status(`${name} : recherche du bon sens (${k}/3)…`); await tick();
      const n = readable(await recognize(prepare(rotate90(flat, k), []).image, 11));
      if (n > best) { best = n; bestTurns = k; }
    }
    if (bestTurns && best >= 12 && best > 2 * found) {
      page.QuarterTurns = bestTurns;
      source = rotate90(source, bestTurns);
      ({ flat } = await processImage(page, source, null));
    }
  }
  return { page, flat, source };
}

async function addSource(entry, bytes) {
  state.sources[entry] = bytes;
  await db.putSource(state.projectId, entry, bytes);
}

async function importFiles(files) {
  let added = 0;
  const wasEmpty = state.project.Pages.length === 0;
  for (const file of [...files].sort((a, b) => a.name.localeCompare(b.name, 'fr'))) {
    try {
      const name = file.name || `Photo ${new Date().toLocaleString('fr-FR')}.jpg`;
      const ext = (name.match(/\.[^.]+$/)?.[0] ?? '').toLowerCase();
      const bytes = new Uint8Array(await file.arrayBuffer());
      if (ext === EXTENSION) { await openProjectFile(bytes, name); continue; }
      if (ext === '.txt' || ext === '.csv') {
        addPage(buildDataPage(name, parseDelimited(decodeText(bytes), ext)));
      } else if (ext === '.xlsx') {
        for (const { sheet, rows } of readXlsx(bytes)) addPage(buildDataPage(`${name} – ${sheet}`, rows));
      } else if (ext === '.pdf' || file.type === 'application/pdf') {
        status(`${name} : lecture du PDF…`); await tick();
        const pages = await renderPdf(bytes);
        for (let i = 0; i < pages.length; i++) {
          const entry = `sources/${uid()}.png`;
          await addSource(entry, pages[i]);
          const r = await importImage(pages[i], entry, `${name} – page ${i + 1}`, 'image/png');
          addPage(r.page, r.flat, r.source);
        }
      } else if (isImageName(name) || file.type.startsWith('image/')) {
        const entry = `sources/${uid()}${ext || '.jpg'}`;
        await addSource(entry, bytes);
        const r = await importImage(bytes, entry, name, file.type || mimeOf(name));
        addPage(r.page, r.flat, r.source);
      } else {
        throw new Error('format non pris en charge');
      }
      added++;
    } catch (e) {
      console.error(e);
      await dialog('Import impossible', `<p>${escapeHtml(file.name)} : ${escapeHtml(e?.message ?? String(e))}</p>`, [{ label: 'OK' }]);
    }
  }
  if (added && wasEmpty) state.project.Name = (files[0].name || 'Inventaire').replace(/\.[^.]+$/, '');
  if (added) {
    status(`${added} fichier(s) importé(s). Touchez une case pour la corriger ; l'original s'affiche en loupe au-dessus.`);
    requestAnimationFrame(fitWidth);
    scheduleSave();
  }
}

function addPage(page, flat = null, source = null) {
  state.project.Pages.push(page);
  showPage(page, flat, source);
}

async function orientedSource(page) {
  if (state.oriented.has(page)) return state.oriented.get(page);
  const bytes = state.sources[page.SourceEntry];
  if (!bytes) return null;
  const src = rotate90(await decodeImage(bytes, mimeOf(page.SourceEntry)), page.QuarterTurns);
  state.oriented.set(page, src);
  return src;
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
  saveTimer = setTimeout(saveNow, 1000);
}

async function saveNow() {
  clearTimeout(saveTimer);
  if (state.project.Pages.length === 0) return;
  try {
    await db.saveProject(state.projectId, state.project);
    await db.setMeta('last', state.projectId);
    $('saveState').textContent = `✓ Enregistré ${new Date().toLocaleTimeString('fr-FR')}`;
  } catch (e) {
    $('saveState').textContent = '⚠ Non enregistré';
    status(`Enregistrement impossible : ${e?.message ?? e}`);
  }
}

async function loadProjectById(id) {
  const rec = await db.loadProject(id);
  if (!rec) return false;
  clearScreen();
  state.project = rec.project;
  state.projectId = id;
  state.sources = rec.sources;
  for (const page of state.project.Pages) {
    status(`Ouverture : ${page.SourceName}…`); await tick();
    showPage(page, page.Source === 'Image' ? await restoreFlat(page) : null);
  }
  await db.setMeta('last', id);
  requestAnimationFrame(fitWidth);
  updateSelection();
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
  clearScreen();
  state.project = newProject();
  state.projectId = uid();
  state.sources = {};
  $('saveState').textContent = '';
  updateSelection();
  applyZoom(1);
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
  await dialog(title, `${intro}<ul>${list}</ul>${canShare ? "<p>« Enregistrer / Partager » ouvre le menu de l'iPad : <b>Enregistrer dans Fichiers</b>, <b>Enregistrer l'image</b>, Mail, AirDrop…</p>" : ''}`, [
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

  $('btnLock').onclick = () => {
    state.lock = !state.lock;
    $('btnLock').setAttribute('aria-pressed', String(state.lock));
    for (const v of state.views.values()) v.setLockMode(state.lock);
    status(state.lock
      ? 'Verrouillage : tracez un rectangle au doigt sur une signature ou un tampon. Touchez une zone orange pour la retirer. Retouchez « Verrouiller » pour terminer.'
      : 'Verrouillage terminé.');
  };

  $('btnKind').onclick = () => {
    const cur = state.current;
    const hit = cur?.field ? findLine(state.project, cur.field) : null;
    if (!hit) return;
    cycleKind(hit.line);
    cur.view.refresh();
    cur.view.focus(cur.field);
    updateSelection();
    scheduleSave();
  };

  $('btnRevert').onclick = () => {
    const cur = state.current;
    if (!cur?.field) return;
    cur.view.revert(cur.field);
    updateSelection();
    scheduleSave();
  };

  $('btnCrop').onclick = () => run(async () => {
    const view = state.current?.view?.image ? state.current.view : [...state.views.values()].find((v) => v.image);
    if (!view) return;
    const page = view.page;
    const src = await orientedSource(page);
    if (!src) return;
    const result = await cropDialog(src, page.Crop);
    if (!result) return status('Recadrage annulé.');
    if ([...allFields(page)].some(isModified) && !(await confirmDialog('Recadrer la page ?', 'Le texte de cette page va être relu : ses modifications seront perdues.'))) return;
    const fresh = newPage({ Id: page.Id, Source: 'Image', SourceName: page.SourceName, SourceEntry: page.SourceEntry, QuarterTurns: (page.QuarterTurns + result.turns) % 4 });
    const { flat } = await processImage(fresh, result.source, result.quad);
    const i = state.project.Pages.indexOf(page);
    if (i < 0) return;
    state.project.Pages[i] = fresh;
    const frame = state.frames.get(page);
    state.frames.delete(page); state.views.delete(page); state.flats.delete(page); state.oriented.delete(page);
    state.frames.set(fresh, frame);
    frame.querySelector('.page-head button').onclick = () => removePage(fresh);
    showPage(fresh, flat, result.source);
    state.current = null;
    updateSelection();
    status('Page recadrée et relue. Vérifiez les zones à verrouiller.');
    scheduleSave();
  });

  $('btnTxt').onclick = () => run(async () => {
    const { text, lineCount, warnings } = buildTxt(state.project);
    const file = new File([txtBlob(text)], `${state.project.Name}.txt`, { type: 'text/plain' });
    const warn = warnings.length
      ? `<p><b>À vérifier :</b></p><ul>${warnings.slice(0, 8).map((w) => `<li>${escapeHtml(w)}</li>`).join('')}</ul>`
      : '';
    await deliver([file], 'Fichier texte prêt', `<p>${lineCount} lignes, colonnes séparées par des tabulations (UTF-8). Les lignes non corrigées reprennent ce que la lecture automatique a vu.</p>${warn}`);
  });

  $('btnJpeg').onclick = () => run(async () => {
    const pages = state.project.Pages.filter((p) => p.Source === 'Image');
    const files = [];
    for (let i = 0; i < pages.length; i++) {
      const page = pages[i];
      status(`Création de l'image ${i + 1}/${pages.length}…`); await tick();
      const flat = state.flats.get(page) ?? (await restoreFlat(page));
      const src = await orientedSource(page);
      if (!flat || !src) continue;
      const final = composeIntoSource(src, page, renderEdits(page, flat));
      const blob = await canvasToBlob(rasterToCanvas(final), 'image/jpeg', 0.93);
      files.push(new File([blob], outputName(page), { type: 'image/jpeg' }));
    }
    status(`${files.length} image(s) prête(s).`);
    await deliver(files, 'Images prêtes', "<p>Chaque photo d'origine, inchangée sauf dans les cases modifiées.</p>");
  });

  $('btnProjects').onclick = () => run(projectsDialog);
}

async function projectsDialog() {
  await saveNow();
  const list = await db.listProjects();
  const html = `<ul class="project-list">${list.map((p) => `
    <li class="${p.id === state.projectId ? 'current' : ''}" data-id="${p.id}">
      <span class="name">${escapeHtml(p.name)}<small>${p.pages} page(s) · ${new Date(p.updated).toLocaleString('fr-FR')}</small></span>
      ${p.id === state.projectId ? '<span>ouvert</span>' : '<button data-act="open">Ouvrir</button>'}
      <button data-act="del">Supprimer</button>
    </li>`).join('') || '<li>Aucun projet enregistré.</li>'}</ul>
    <p>Les projets sont enregistrés automatiquement sur l'iPad. « Copie de sauvegarde » crée un fichier <b>.inventaire</b> (ouvrable aussi sur la Surface).</p>`;
  const choice = await new Promise((resolve) => {
    dialog('Projets', html, [
      { label: 'Nouveau projet', value: 'new' },
      { label: 'Renommer', value: 'rename' },
      { label: 'Copie de sauvegarde', value: 'backup', className: 'accent' },
      { label: 'Fermer', value: 'close' },
    ]).then(resolve);
    $('dialogBody').onclick = (e) => {
      const btn = e.target.closest('button[data-act]');
      if (!btn) return;
      $('dialog').hidden = true;
      resolve({ act: btn.dataset.act, id: btn.closest('li').dataset.id });
    };
  });
  $('dialogBody').onclick = null;
  const action = typeof choice === 'object' ? choice : null;

  if (action?.act === 'open') {
    await loadProjectById(action.id);
    status(`Projet ouvert : ${state.project.Name}`);
  } else if (action?.act === 'del') {
    const p = list.find((x) => x.id === action.id);
    if (await confirmDialog('Supprimer ce projet ?', `« ${p?.name} » sera supprimé de l'iPad (les fichiers exportés ne sont pas touchés).`)) {
      await db.deleteProject(action.id);
      if (action.id === state.projectId) newProjectState();
      status('Projet supprimé.');
    }
  } else if (choice === 'new') {
    newProjectState();
    status('Nouveau projet : touchez « Photo » ou « Importer ».');
  } else if (choice === 'rename') {
    const name = prompt('Nom du projet', state.project.Name);
    if (name?.trim()) { state.project.Name = name.trim(); await saveNow(); status(`Projet renommé : ${state.project.Name}`); }
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
  try {
    const last = await db.getMeta('last');
    if (last && (await loadProjectById(last))) status('Travail en cours rechargé.');
    else status("Prêt. Fonctionne sans Internet : rien ne quitte l'iPad.");
  } catch (e) {
    console.error(e);
    status("Prêt. (Le dernier projet n'a pas pu être rechargé.)");
  }
  updateButtons();
  warmUp();
}

window.addEventListener('DOMContentLoaded', start);
export { state };
