// Retouche d'inventaire — version iPhone / iPad (application web installable, hors ligne).
// Même logique que la version Windows : importer → corriger sur la page → exporter JPEG ou .txt.
// Une seule page est affichée à la fois (navigation « 3 / 10 ») : les longs inventaires restent légers en mémoire.
import { Quad } from './core/geometry.js';
import { normalize, render, rotate90, warp } from './core/imaging.js';
import { detectGrids } from './core/rules.js';
import { prepare, mapBack, emptyFieldsWithInk, prepareCell, applyCellWords } from './core/ocrprep.js';
import { newPage, newProject, analyze, reclassify, allFields, isModified, cycleKind, kindLabel, findLine, firstText, upgradePage, carryOver, allLines, lineBounds } from './core/model.js';
import { buildPdf } from './core/pdf.js';
import { buildTxt, txtBlob, composeIntoSource, outputName } from './core/exports.js';
import { decodeText, parseDelimited, readXlsx, buildDataPage } from './core/importers.js';
import { packProject, unpackProject, EXTENSION } from './core/store.js';
import { itemTable, addLotHere, objectLines, lotsActive, numberLots, clearLots, setLot, resyncLots, removeLot, addLot, repairName, naturalCompare, createEmptyLots, fillLotsAutomatically, emptyLots, isZeroLot, rowOrder, lotTables, defaultLotChoice, setLotTables } from './core/lots.js';
import { unzipSync } from './vendor/fflate.js';
import { stretch, setExtra, baseHeight, totalExtra } from './core/layout.js';
import { toggleStrike, isStruck, setColor, RED, BLACK, TEXT_COLORS } from './core/strikes.js';
import { planArrangement, viewOf, composePage, unitCrop, pageArranged, mapY, unmapY, syncEndPages } from './core/arrange.js';
import { recomputeTotals, isComputedCell, computedValue } from './core/totals.js';
import { hasLotCell, lotSegment, printedLotZone, applyPrintedLots, numberBox, columnWidth } from './core/lots.js';
import { frameSignature, lotFrameEnabled, frameToggles } from './core/lotcolumn.js';
import { canCopyLine, canAddRows, canAddAbove, canSplit, addedRowOf, copyLine, addBlankLine, locate, splitAt, splitTypedText, removeAddedRow, canSplitBelow, splitBelow, addTotalLine, lastObjectLine, rowTrimOf, trimRow, untrimRow, trimsOf } from './core/rows.js';
import { decodeImage, pdfPages, isImageName, mimeOf } from './services/decode.js';
import { recognize, warmUp } from './services/ocr.js';
import { rasterToCanvas, canvasToBlob, renderEdits, freeCanvas, lineExtra, pageRaster } from './services/canvas.js';
import * as db from './services/db.js';
import { PageView } from './ui/pageview.js';
import { addBlankPage, blankBase, canBeModel, addColumn, removeColumn, addRows } from './core/addpage.js';
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
  plan: null,           // mise en page : lots 0 recollés après le dernier lot, rangées déplacées
  crops: new Map(),     // morceaux d'image des lots 0 (pour recomposer la page où ils arrivent)
  undo: [],             // états précédents du projet (bouton ↶)
  editSession: null,    // case en cours de frappe (un seul retour en arrière pour toute la saisie)
  lock: false,
  move: false, // « Modifier » : poignées de déplacement affichées
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
  $('btnCrop').disabled = b || page?.Source !== 'Image' || !!page?.EndLots || !!page?.Added;
  $('btnLock').disabled = b || !page || !!page?.EndLots;
  $('btnMove').disabled = b || !page || !!page?.EndLots;
  $('btnLots').disabled = b || n === 0;
  $('btnEdit').disabled = b || n === 0;
  $('btnUndo').disabled = b || state.undo.length === 0;
  $('btnExport').disabled = b || n === 0;
  $('pager').hidden = n === 0;
  $('pageLabel').textContent = n ? `${state.index + 1} / ${n}` : '';
  $('pagePrev').disabled = b || state.index <= 0;
  $('pageNext').disabled = b || state.index >= n - 1;
  $('pageLabel').disabled = b;
  $('emptyHint').hidden = n > 0;
  $('projectName').textContent = state.project.Name;
  $('btnLots').classList.toggle('on', lotsActive(state.project));
  document.querySelector('.page-head .sync')?.removeAttribute('hidden'); // toujours là : sans lots, il les crée puis les remplit
  updateEditBar();
}

function updateEditBar() {
  const f = state.field;
  const hit = f ? findLine(state.project, f) : null;
  $('editbar').hidden = !hit;
  if (!hit) { hidePalette(); return; }
  $('kindLabel').textContent = kindLabel(hit.line.Kind);
  $('btnRevert').disabled = state.busy || !isModified(f);
  $('btnKind').disabled = state.busy;
  $('btnLine').disabled = state.busy;
  const computed = isComputedCell(hit.line, f);
  $('btnRecalc').hidden = !computed || !f.Manual;
  const t = firstText(hit.line);
  if (computed) {
    $('editInfo').textContent = f.Manual
      ? `Total tapé à la main · calculé : ${computedValue(state.project, state.plan, f) || '(vide)'}`
      : `Total calculé automatiquement${isModified(f) ? ` (imprimé : ${f.OriginalText || 'illisible'})` : ''}`;
  } else
    $('editInfo').textContent = isModified(f) ? `Original : « ${f.OriginalText || '(vide)'} »` : `« ${t.length > 48 ? t.slice(0, 48) + '…' : t} »`;
}

async function run(work) {
  if (state.busy) return;
  setBusy(true);
  const depth = state.undo.length, top = state.undo[depth - 1];
  try { await work(); }
  catch (e) {
    console.error(e);
    // Garde-fou : une action qui échoue en cours de route ne laisse rien à moitié fait, on revient à l'état d'avant.
    let back = false;
    if (state.undo.length > depth || (state.undo.length && state.undo[state.undo.length - 1] !== top)) {
      while (state.undo.length > depth + 1) state.undo.pop();
      try { await undo(); back = true; } catch (e2) { console.error(e2); }
    }
    await dialog('Erreur', `<p>${escapeHtml(e?.message ?? String(e))}</p>${back ? '<p>Rien n\'a changé, vous pouvez continuer.</p>' : ''}`, [{ label: 'OK' }]);
  }
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

const hidePalette = () => { $('palette').hidden = true; };

const confirmDialog = (title, text, ok = 'Continuer') =>
  dialog(title, `<p>${escapeHtml(text)}</p>`, [{ label: 'Annuler', value: false }, { label: ok, value: true, className: 'accent' }]);

// ---------- Réglages de l'appareil (Mise en page) ----------

/** Loupe « Original » au-dessus de la case ; zoom de l'iPad quand on touche une case. Gardés sur cet appareil. */
const prefs = (() => {
  const p = { loupe: true, caseZoom: false };
  try { Object.assign(p, JSON.parse(localStorage.getItem('retouche-prefs') ?? '{}')); } catch { /* réglages par défaut */ }
  return p;
})();
function savePrefs() {
  try { localStorage.setItem('retouche-prefs', JSON.stringify(prefs)); } catch { /* sans importance */ }
  applyPrefs();
}
function applyPrefs() {
  const meta = document.querySelector('meta[name=viewport]');
  if (meta) meta.content = `width=device-width, initial-scale=1${prefs.caseZoom ? '' : ', maximum-scale=1'}, viewport-fit=cover`;
}

// ---------- Zoom (pincer, boutons) ----------

function applyZoom(z, cx = null, cy = null) {
  const vp = $('viewport');
  const old = state.zoom;
  z = Math.min(4, Math.max(0.15, z));
  const px = cx ?? vp.clientWidth / 2, py = cy ?? vp.clientHeight / 2;
  const ux = (vp.scrollLeft + px) / old, uy = (vp.scrollTop + py) / old;
  state.zoom = z;
  $('zoomer').style.transform = `scale(${z})`;
  $('zoomer').style.setProperty('--uiscale', String(Math.min(2.5, Math.max(1, 1 / z)))); // boutons de la page ajoutée : même taille à l'écran
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

// ---------- Retour en arrière ----------

/** Mémorise l'état du projet avant une action (30 derniers états). */
function pushUndo() {
  state.undo.push(JSON.stringify(state.project));
  if (state.undo.length > 30) state.undo.shift();
  updateButtons();
}

/** Clé de l'image d'une page : si elle n'a pas changé, l'image en mémoire reste valable. */
const imageKey = (p) => JSON.stringify([p.Crop, p.Skew, p.QuarterTurns, p.Width, baseHeight(p), p.SourceEntry, p.Added ? p.Tables.map((t) => t.ColumnXs) : 0]);

/** Bouton ↶ : revient à l'état d'avant la dernière action. */
async function undo() {
  const snap = state.undo.pop();
  if (!snap) return;
  const old = new Map(pages().map((p) => [p.Id, p]));
  const flats = new Map([...state.flats].map(([p, f]) => [p.Id, { key: imageKey(p), f }]));
  const oriented = new Map([...state.oriented].map(([p, f]) => [p.Id, { key: JSON.stringify([p.QuarterTurns, p.SourceEntry]), f }]));
  state.project = JSON.parse(snap);
  state.flats.clear(); state.oriented.clear();
  for (const p of pages()) {
    const f = flats.get(p.Id);
    if (f && f.key === imageKey(p)) state.flats.set(p, f.f);
    const o = oriented.get(p.Id);
    if (o && o.key === JSON.stringify([p.QuarterTurns, p.SourceEntry])) state.oriented.set(p, o.f);
  }
  const currentId = old.size ? [...old.values()][state.index]?.Id : null;
  const index = Math.max(0, pages().findIndex((p) => p.Id === currentId));
  state.editSession = null;
  state.field = null;
  state.crops.clear();
  await showPage(Math.min(index, pages().length - 1), { fit: false, keepScroll: index === state.index });
  status('Dernière action annulée.');
  scheduleSave();
}

// ---------- Affichage d'une page ----------

function disposeView() {
  state.view?.dispose();
  state.view = null;
  state.field = null;
  $('pages').replaceChildren();
}

// ---------- Mise en page et images ----------

/** Recalcule la mise en page (lots 0, lignes supprimées, rangées déplacées, pages « lots de fin »). */
function replan() {
  const cur = currentPage();
  if (syncEndPages(state.project)) {
    const i = pages().indexOf(cur);
    state.index = i >= 0 ? i : Math.max(0, Math.min(state.index, pages().length - 1));
    state.crops.clear();
    updateButtons();
  }
  state.plan = planArrangement(state.project);
  return state.plan;
}

/** Totaux recalculés (cases non tapées à la main) ; renvoie les cases changées. */
function recomputeAll() {
  return recomputeTotals(state.project, state.plan ?? replan());
}

const colorSig = (page) => JSON.stringify([...allFields(page)].map((f) => (f.Colors?.length ? [f.Text === f.OriginalText, f.Colors] : 0)));

/** Image de la page avant corrections : redressée, agrandie, colonne des lots tracée, écriture en couleur. */
async function ownDisplay(page) {
  if (page.Source !== 'Image') return null;
  let flat = state.flats.get(page);
  if (!flat) {
    flat = await restoreFlat(page);
    if (!flat) return null;
  }
  remember(state.flats, page, flat, 3);
  return pageRaster(page, stretch(flat, page), lotFrameEnabled(state.project));
}

/** Morceau d'image d'un lot 0 (gardé en mémoire : sa page n'a pas besoin d'être rouverte). */
async function cropOf(u) {
  if (u.page.Source !== 'Image') return null;
  const key = JSON.stringify([u.page.Id, imageKey(u.page), frameSignature(u.page, lotFrameEnabled(state.project)), colorSig(u.page), u.bodyTop, u.bottom, !!u.title]);
  if (state.crops.has(key)) return state.crops.get(key);
  const own = await ownDisplay(u.page);
  const crop = own ? unitCrop(u, own) : null;
  remember(state.crops, key, crop, 60);
  return crop;
}

/** Image affichée de la page k : la sienne, recomposée si des rangées partent ou arrivent. */
async function displayOf(k) {
  const page = pages()[k];
  const tpl = state.plan.pages[k]?.template;
  const own = page.EndLots ? (tpl >= 0 ? await ownDisplay(pages()[tpl]) : null) : await ownDisplay(page);
  if (!own || !pageArranged(state.project, state.plan, k)) return own;
  const crops = new Map();
  for (const pc of state.plan.pages[k].pieces) if (pc.kind === 'unit') crops.set(pc.unit, await cropOf(pc.unit));
  return composePage(state.project, state.plan, k, own, crops);
}

/** Affiche la page n (et seulement elle). */
async function showPage(n, { flat = null, fit = true, keepScroll = false } = {}) {
  const list = pages();
  const vp = $('viewport');
  const scroll = keepScroll ? { x: vp.scrollLeft, y: vp.scrollTop } : null;
  const focusedField = keepScroll ? state.field : null;
  disposeView();
  if (list.length === 0) { state.index = 0; updateButtons(); applyZoom(1); return; }
  state.index = Math.min(list.length - 1, Math.max(0, n));
  const page = list[state.index];
  updateButtons();
  if (flat) remember(state.flats, page, flat, 3);
  replan();
  if (page.Source === 'Image' && !state.flats.get(page)) { status(`Ouverture de la page ${state.index + 1}…`); await tick(); }
  const k = state.index;
  const display = await displayOf(k);
  if (list[state.index] !== page) return; // l'utilisateur a déjà changé de page

  const head = document.createElement('div');
  head.className = 'page-head';
  const title = document.createElement('span');
  title.textContent = `Page ${state.index + 1} sur ${list.length} · ${page.SourceName}${page.EndLots ? ' (lots 0, créée automatiquement)' : ''}`;
  const remove = document.createElement('button');
  remove.innerHTML = 'Retirer… <svg><use href="#i-plus"/></svg>';
  remove.title = 'Retirer cette page, des pages au choix ou tout le dossier';
  remove.onclick = () => run(() => retireMenu(page));
  const totals = document.createElement('button');
  totals.innerHTML = '<svg><use href="#i-sum"/></svg>Totaux';
  totals.title = 'Recalculer les totaux de la page, ajouter une ligne TOTAL';
  totals.onclick = () => run(() => totalsMenu(page));
  const sync = document.createElement('button');
  sync.className = 'sync';
  sync.innerHTML = '<svg><use href="#i-sync"/></svg>Synchroniser…';
  sync.title = 'Remplir les cases vides de cette page ou de toutes les pages (les numéros déjà là ne bougent pas)';
  sync.onclick = () => run(syncDialog);
  const addPage = document.createElement('button');
  addPage.innerHTML = '<svg><use href="#i-plus"/></svg>Ajouter une page';
  addPage.title = 'Nouvelle page vierge sur le modèle de celle-ci (mêmes tableaux, mêmes lignes), pour les objets inventoriés à la main';
  addPage.onclick = () => run(() => addPageDialog(page));
  if (page.EndLots) head.append(title, sync);
  else head.append(title, sync, totals, addPage, remove);

  const pp = () => state.plan.pages[k];
  const view = new PageView(viewOf(state.project, state.plan, k), display, {
    lotsActive: () => lotsActive(state.project),
    lotFrame: () => lotFrameEnabled(state.project),
    onFrameToggle: (v, table, rows, on) => {
      pushUndo();
      for (const r of rows) table.Rows[r].LotFrame = on;
      replan();
      refreshView();
      status(on ? 'Colonne des lots tracée sur cette rangée. ↶ pour annuler.' : 'Colonne des lots arrêtée sur cette rangée (ex. au total). ↶ pour annuler.');
      scheduleSave();
    },
    zoom: () => state.zoom,
    loupe: () => prefs.loupe,
    mapY: (y) => mapY(pp(), y),
    unmapY: (y) => unmapY(pp(), y),
    onFocus: (v, f) => { state.field = f; state.editSession = null; updateEditBar(); },
    beforeEdit: (v, f) => { if (state.editSession !== f) { pushUndo(); state.editSession = f; } },
    beforeChange: () => pushUndo(),
    onEdited: (v, f) => {
      if (isComputedCell(v.lineOf.get(f), f)) f.Manual = true; // total tapé à la main : il ne bouge plus tout seul
      afterEdit(v, f);
      updateEditBar();
      scheduleSave();
    },
    onLocks: () => { status('Zones verrouillées mises à jour.'); scheduleSave(); },
    onLot: (v, line, value) => { if (!state.busy) run(() => applyLot(v, line, value)); },
    onLotRemove: (v, line) => {
      pushUndo();
      removeLot(state.project, line);
      recomputeAll();
      refreshView();
      status('Case de lot retirée : cette ligne n\'est pas un objet.');
      scheduleSave();
    },
    onLotAdd: (v, line) => {
      pushUndo();
      addLotHere(state.project, locate(page, line)?.table ?? null, line); // tableau sans lots : seule cette ligne reçoit une case
      replan();
      // La case est prête : le curseur y est, on tape le numéro tout de suite.
      refreshView().then(() => state.view?.lotInputs.get(line)?.focus({ preventScroll: true }));
      status('Case de lot ajoutée : tapez le numéro.');
      scheduleSave();
    },
    onLineMenu: (v, f, at) => { if (!state.busy) lineMenu(f, at); },
    onAddColumn: (v, table, c) => run(() => columnAction(page, table, c, true)),
    onAddRows: (v, line) => run(() => rowsDialog(page, line)),
    onRemoveColumn: (v, table, k) => run(() => columnAction(page, table, k, false)),
    onMoveRow: (v, table, from, before) => {
      pushUndo();
      const order = rowOrder(table).filter((r) => r !== from);
      order.splice(before === null ? order.length : order.indexOf(before), 0, from);
      table.RowOrder = order;
      refreshView();
      status('Rangée déplacée. ↶ pour annuler.');
      scheduleSave();
    },
    onMoveLine: (v, table, line, before) => run(() => moveLine(page, table, line, before)),
    onMoveUnit: (v, table, unit, before) => run(() => placeZero(page, table, unit, before)),
  });
  view.baseFlat = state.flats.get(page) ?? null;
  view.setLockMode(state.lock);
  view.setMoveMode(state.move);
  state.view = view;
  $('pages').append(head, view.row);
  if (scroll) { applyZoom(state.zoom); vp.scrollLeft = scroll.x; vp.scrollTop = scroll.y; }
  else { vp.scrollTop = 0; if (fit) requestAnimationFrame(fitWidth); else applyZoom(state.zoom); }
  if (focusedField && view.inputs.has(focusedField)) { state.field = focusedField; view.focus(focusedField); }
  if (!keepScroll)
    status(page.Source === 'Image'
      ? (prefs.loupe ? 'Touchez une case pour la corriger ; l\'original s\'affiche en loupe au-dessus (réglable : Mise en page).' : 'Touchez une case pour la corriger.')
      : 'Touchez une case pour la corriger.');
  updateButtons();
}

/** Reconstruit la page affichée (même zoom, même endroit). */
function refreshView() {
  return showPage(state.index, { fit: false, keepScroll: true });
}

/** La page a changé (rangée agrandie, couleur…) : nouvelle image et cases replacées, sans fermer le clavier. */
async function redraw(view = state.view) {
  if (!view) return;
  replan();
  const k = state.index;
  const display = await displayOf(k);
  if (state.view !== view) return;
  view.relayout(viewOf(state.project, state.plan, k), display);
}

// ---------- Lignes ajoutées : menu « Ligne » (bouton, clic droit, appui long) ----------

const short = (t) => (t.length > 40 ? t.slice(0, 40) + '…' : t);

/** Ligne de texte (dans la désignation tapée sur plusieurs lignes) où se trouve le curseur. */
function caretLine(view, f) {
  if (f.Column !== 0 || !f.Text.includes('\n')) return 0;
  const sel = view?.selectionFor(f) ?? { start: 0 };
  return f.Text.slice(0, sel.start).split('\n').length - 1;
}

/** Ce que le menu propose pour la case touchée. */
function lineChoices(f) {
  const hit = f ? findLine(state.project, f) : null;
  if (!hit) return null;
  const { page, line } = hit;
  const items = [];
  const typed = caretLine(state.view, f);
  const typedRest = typed > 0 ? f.Text.replace(/\r\n?/g, '\n').split('\n').slice(typed).join(' ').trim() : '';
  const moved = state.plan?.moving.some((u) => u.kind === 'zero' && u.lines.includes(line));
  if (moved) {
    // Ligne au lot 0, affichée à la fin : la remettre, ou la supprimer.
    items.push({ act: 'back', icon: 'i-merge', label: 'Remettre à sa place', hint: 'La ligne retourne où elle était (case de lot vide : tapez son numéro).' });
    items.push({ act: 'delete', icon: 'i-trash', label: 'Supprimer cette ligne', hint: 'Elle disparaît de la vente (page, .txt, totaux).', danger: true });
    return { page, line, items };
  }
  // Ajouter des lignes : sur toute ligne du tableau (même vide, un titre ou un total) ; modèle pris sur une ligne normale
  // du tableau quand celle-ci ne peut pas servir de modèle.
  if (canAddRows(page, line)) {
    const copyable = canCopyLine(page, line);
    items.push({ act: copyable ? 'blankBelow' : 'oneBelow', icon: 'i-addbelow', label: 'Ajouter une ligne vide en dessous', hint: 'Rangée complète (case de lot, désignation, prix) sous cette rangée. La suite descend.' });
    if (canAddAbove(page, line))
      items.push({ act: copyable ? 'blankAbove' : 'oneAbove', icon: 'i-addabove', label: 'Ajouter une ligne vide au-dessus', hint: 'Rangée complète (case de lot, désignation, prix) au-dessus de cette rangée.' });
    items.push({ act: 'rows', icon: 'i-rows', label: 'Ajouter des lignes… (1, 5, 10, 15)', hint: 'Plusieurs lignes vides d\'un coup, mêmes colonnes et même hauteur que les lignes du tableau.' });
    if (copyable) items.push({ act: 'copy', icon: 'i-copy', label: 'Dupliquer la ligne', hint: 'Copie juste en dessous (texte et prix), à corriger. La suite descend.' });
  }
  if (typedRest && canCopyLine(page, line))
    items.push({ act: 'typed', from: typed, icon: 'i-split', label: 'Séparer ici', hint: `« ${short(typedRest)} » passe dans sa propre ligne, juste en dessous.` });
  else if (canSplit(page, line))
    items.push({ act: 'split', icon: 'i-split', label: 'Couper ici (trait au-dessus de cette ligne)', hint: 'Un trait au-dessus de cette ligne : elle devient une rangée à part (sortie de la grande case), avec sa case de lot et ses prix.' });
  if (canSplitBelow(page, line))
    items.push({ act: 'splitBelow', icon: 'i-split', label: 'Séparer en dessous (tracer un trait)', hint: 'Un trait sous cette ligne : ce qui suit devient une rangée à part.' });
  if (hasLotCell(line)) {
    // Numéro de lot dans une grande case : en haut (par défaut), au milieu ou en bas.
    const at = locate(page, line);
    const seg = at ? lotSegment(at.table, at.r, line) : null;
    if (seg && seg.bottom - seg.top > lineBounds(line).Height - (line.Extra ?? 0) + 24)
      for (const [v, label] of [[null, 'en haut'], ['Middle', 'au milieu'], ['Bottom', 'en bas']])
        if ((line.LotAlign ?? null) !== v) items.push({ act: 'lotAlign', value: v, icon: 'i-rows', label: `Numéro de lot ${label}`, hint: 'Place du numéro dans sa case (grande case).' });
    items.push({ act: 'lotRemove', icon: 'i-trash', label: 'Retirer la case de lot', hint: 'Cette ligne n\'est pas un objet : sa case disparaît (les autres numéros ne bougent pas).', danger: true });
  }
  if (hasLotCell(line) && !isZeroLot(line)) {
    items.push({ act: 'moveLot', icon: 'i-rows', label: 'Déplacer ce lot vers une autre page…', hint: 'Choisir la page et l\'endroit (début, fin, après un lot).' });
    if (line.MoveTo) items.push({ act: 'unmoveLot', icon: 'i-merge', label: 'Remettre ce lot à sa place', hint: 'Il revient là où il était imprimé.' });
  }
  if (hasLotCell(line) && !isZeroLot(line))
    items.push({ act: 'end', icon: 'i-addbelow', label: 'Mettre à la fin (lot 0)', hint: 'Objet absent : sa ligne part à la fin, ce qui suit remonte.' });
  const at0 = locate(page, line);
  // Colonne des lots sur une rangée sans case (total, titre…) : la tracer ou non.
  if (lotsActive(state.project) && at0 && !at0.table.LotsOff && !at0.row.Lines.some(hasLotCell)) {
    const g = frameToggles(at0.table).find((x) => x.rows.includes(at0.r));
    if (g) items.push({ act: 'frame', rows: g.rows, on: !g.framed, icon: g.framed ? 'i-trash' : 'i-plus', label: g.framed ? 'Ne pas tracer la colonne des lots ici' : 'Tracer la colonne des lots ici', hint: 'Seulement le cadre de la colonne sur cette rangée (pas de numéro).' });
  }
  if (lotsActive(state.project) && at0 && (!at0.table.LotsOff || itemTable(at0.table)) && !hasLotCell(line) && firstText(line))
    items.push({ act: 'addLot', icon: 'i-plus', label: 'Ajouter une case de lot ici', hint: 'Une case vide à la hauteur de cette ligne (même dans une grande case) : tapez le numéro. Les autres numéros ne bougent pas.' });
  if (rowTrimOf(page, line))
    items.push({ act: 'trim', icon: 'i-merge', label: 'Réduire la rangée (enlever l\'espace vide)', hint: 'La rangée prend la hauteur des autres ; ce qui suit remonte.' });
  else if (trimsOf(page, line).length)
    items.push({ act: 'untrim', icon: 'i-split', label: 'Remettre la hauteur d\'origine', hint: 'L\'espace vide imprimé revient.' });
  if (line.TotalScope)
    items.push({ act: 'bold', icon: 'i-sum', label: line.TotalScope.Bold ? 'Enlever le gras' : 'Mettre en gras', hint: 'Ligne TOTAL ajoutée : écriture en gras ou comme les autres lignes.' });
  if (page.Added && locate(page, line) && !line.Deleted) {
    items.push({ act: 'taller', icon: 'i-addbelow', label: 'Ligne plus haute', hint: 'Une ligne d\'écriture de plus dans cette case ; ce qui suit descend.' });
    if ((line.Tall ?? 0) > 0) items.push({ act: 'shorter', icon: 'i-merge', label: 'Ligne moins haute', hint: 'Enlève la hauteur ajoutée.' });
  }
  const added = addedRowOf(page, line);
  if (added?.band.Kind === 'Split' && added.row.Lines[0] === line)
    items.push({ act: 'merge', icon: 'i-merge', label: 'Remettre dans la rangée du dessus', hint: 'Annule « Séparer ici » : le trait ajouté disparaît.' });
  if (added?.band.Kind === 'Copy')
    items.push({ act: 'remove', icon: 'i-trash', label: 'Supprimer cette ligne ajoutée', hint: 'La ligne ajoutée disparaît, la suite remonte.', danger: true });
  else if (locate(page, line) && (firstText(line) || page.Added))
    items.push({ act: 'delete', icon: 'i-trash', label: 'Supprimer cette ligne', hint: 'Objet retiré de la vente : la ligne (et sa suite) disparaît, ce qui suit remonte.', danger: true });
  return { page, line, items };
}

const hideLineMenu = () => { $('lineMenu').hidden = true; };

/** Affiche le menu « Ligne » près du doigt / de la souris, ou sous le bouton. */
function lineMenu(f, at = null) {
  hidePalette();
  const c = lineChoices(f);
  if (!c) return;
  state.field = f;
  updateEditBar();
  const menu = $('lineMenu');
  const t = firstText(c.line);
  menu.innerHTML = `<div class="title">Ligne « ${escapeHtml(short(t || '(vide)'))} »</div>` + (c.items.length
    ? c.items.map((it, i) => `<button role="menuitem" data-i="${i}" class="${it.danger ? 'danger' : ''}"><svg><use href="#${it.icon}"/></svg><b>${escapeHtml(it.label)}</b><small>${escapeHtml(it.hint)}</small></button>`).join('')
    : '<div class="none">Rien à faire ici : choisissez une ligne d\'article du tableau (pas un en-tête, un total ou un texte hors tableau).</div>');
  for (const b of menu.querySelectorAll('button')) {
    b.addEventListener('pointerdown', (e) => e.preventDefault()); // garde la case et son curseur
    b.onclick = () => { hideLineMenu(); run(() => lineAction(c.items[+b.dataset.i], f)); };
  }
  menu.hidden = false;
  const w = menu.offsetWidth, h = menu.offsetHeight;
  let x, y;
  if (at) { x = at.x + 4; y = at.y + 4; }
  else { const r = $('btnLine').getBoundingClientRect(); x = r.left; y = r.bottom + 6; }
  Object.assign(menu.style, { left: Math.max(8, Math.min(x, innerWidth - w - 8)) + 'px', top: Math.max(8, Math.min(y, innerHeight - h - 8)) + 'px' });
  menu.querySelector('button')?.focus({ preventScroll: true });
}

/** Exécute un choix du menu « Ligne » ; une seule action à annuler (↶). */
async function lineAction(item, f) {
  const hit = findLine(state.project, f);
  if (!hit || !item) return;
  const { page, line } = hit;
  const name = short(firstText(line)) || 'ligne vide';
  if (item.act === 'end') return applyLot(state.view, line, '0');
  if (item.act === 'rows') return rowsDialog(page, line);
  if (item.act === 'moveLot') return moveLotsDialog(line);
  if (item.act === 'unmoveLot') {
    pushUndo();
    delete line.MoveTo;
    replan(); recomputeAll();
    await refreshView();
    status(`« ${name} » remis à sa place. ↶ pour annuler.`);
    return scheduleSave();
  }
  if (item.act === 'frame') {
    pushUndo();
    const t = locate(page, line)?.table;
    for (const r of item.rows) if (t?.Rows[r]) t.Rows[r].LotFrame = item.on;
    replan(); recomputeAll();
    await refreshView();
    status(item.on ? 'Colonne des lots tracée sur cette rangée. ↶ pour annuler.' : 'Colonne des lots arrêtée sur cette rangée. ↶ pour annuler.');
    return scheduleSave();
  }
  if (item.act === 'oneBelow' || item.act === 'oneAbove') return addRowsNow(page, line, 1, item.act === 'oneAbove');
  if (item.act === 'lotAlign' || item.act === 'lotRemove') {
    pushUndo();
    if (item.act === 'lotAlign') { if (item.value) line.LotAlign = item.value; else delete line.LotAlign; } else removeLot(state.project, line);
    replan(); recomputeAll();
    await refreshView();
    status(item.act === 'lotRemove' ? `Case de lot retirée : « ${name} » n'est pas un objet. ↶ pour annuler.` : 'Numéro de lot déplacé dans sa case. ↶ pour annuler.');
    return scheduleSave();
  }
  if (item.act === 'addLot') {
    pushUndo();
    addLotHere(state.project, locate(page, line)?.table ?? null, line);
    replan(); recomputeAll();
    await refreshView();
    state.view?.lotInputs.get(line)?.focus({ preventScroll: true });
    status(`Case de lot ajoutée sur « ${name} » : tapez le numéro, ou « Synchroniser… » (les autres numéros ne bougent pas).`);
    return scheduleSave();
  }
  if (item.act === 'trim' || item.act === 'untrim') {
    pushUndo();
    if (item.act === 'trim') trimRow(page, line); else untrimRow(page, line);
    replan(); recomputeAll();
    await refreshView();
    state.view?.flashLine(line);
    const rowName = short(locate(page, line)?.row.Lines.map(firstText).find(Boolean) ?? '') || 'cette ligne';
    status(item.act === 'trim' ? `Rangée de « ${rowName} » réduite : même hauteur que les autres. ↶ pour annuler.` : `Rangée de « ${rowName} » remise à sa hauteur d'origine.`);
    return scheduleSave();
  }
  if (item.act === 'taller' || item.act === 'shorter') {
    pushUndo();
    line.Tall = Math.max(0, (line.Tall ?? 0) + (item.act === 'taller' ? 1 : -1));
    setExtra(page, line, lineExtra(line));
    replan(); recomputeAll();
    await refreshView();
    state.view?.flashLine(line);
    status(item.act === 'taller' ? 'Ligne plus haute : ce qui suit est descendu. ↶ pour annuler.' : 'Ligne remise à sa hauteur.');
    return scheduleSave();
  }
  if (item.act === 'bold') {
    pushUndo();
    line.TotalScope.Bold = !line.TotalScope.Bold;
    await refreshView();
    status(line.TotalScope.Bold ? `« ${name} » en gras.` : `« ${name} » : même écriture que les autres lignes.`);
    return scheduleSave();
  }
  if (item.act === 'back') {
    pushUndo();
    setLot(state.project, line, '');
    replan(); recomputeAll();
    await refreshView();
    status(`« ${name} » est revenu à sa place : tapez son numéro (ou « Synchroniser »).`);
    return scheduleSave();
  }
  if (item.act === 'delete') {
    if (!(await confirmDialog('Supprimer cette ligne ?', `« ${name} » (et sa suite) disparaît de la page, du fichier texte et des totaux. Les autres numéros de lot ne changent pas. ↶ ou Retirer… › Remettre les lignes supprimées pour la retrouver.`, 'Supprimer'))) return;
    pushUndo();
    line.Deleted = true;
    resyncLots(state.project);
    replan(); recomputeAll();
    state.field = null;
    await refreshView();
    status(`« ${name} » supprimée : ce qui suit est remonté. ↶ pour annuler.`);
    return scheduleSave();
  }
  pushUndo();
  let show = null, msg = '';
  // Rangée haute (espace vide imprimé) : réduite avant d'ajouter ou de séparer, pour que tout ait la hauteur des autres.
  const neighbour = (d) => { const at = locate(page, line); return at?.row.Lines[at.i + d] ?? null; };
  const prev = neighbour(-1), next = neighbour(1);
  if (['copy', 'blankBelow', 'blankAbove', 'typed'].includes(item.act) && canCopyLine(page, line)) trimRow(page, line);
  if (item.act === 'blankBelow' || item.act === 'blankAbove') {
    show = addBlankLine(page, line, item.act === 'blankAbove');
    msg = `Ligne vide ajoutée ${item.act === 'blankAbove' ? 'au-dessus de' : 'sous'} « ${name} » : tapez la désignation et les prix. Case de lot vide : tapez le numéro ou « Synchroniser ».`;
  } else if (item.act === 'copy') {
    show = copyLine(page, line);
    if (show) setExtra(page, show, lineExtra(show));
    msg = `« ${name} » dupliquée juste en dessous : corrigez la quantité et le prix. Case de lot vide : tapez le numéro ou « Synchroniser ».`;
  } else if (item.act === 'typed') {
    show = splitTypedText(page, line, item.from);
    if (show) { setExtra(page, line, lineExtra(line)); setExtra(page, show, lineExtra(show)); }
    msg = `« ${short(firstText(show ?? line))} » est maintenant sur sa propre ligne : tapez ses prix et son numéro de lot.`;
  } else if (item.act === 'splitBelow') {
    show = splitBelow(page, line) ? line : null;
    msg = `Trait tracé sous « ${name} » : ce qui suit est une rangée à part.`;
  } else if (item.act === 'split') {
    show = splitAt(page, line) ? line : null;
    msg = `« ${name} » est maintenant une rangée à part, avec sa case de lot et ses prix.`;
  } else if (item.act === 'merge') {
    removeAddedRow(page, line);
    msg = `« ${name} » est remise dans la rangée du dessus.`;
  } else if (item.act === 'remove') {
    removeAddedRow(page, line);
    msg = 'Ligne ajoutée supprimée, la suite est remontée.';
  }
  if (['copy', 'typed', 'split', 'splitBelow', 'blankBelow', 'blankAbove'].includes(item.act) && !show) {
    state.undo.pop();
    updateButtons();
    status('Impossible ici : choisissez une ligne d\'article d\'un tableau.');
    return;
  }
  if (item.act === 'split' && prev) { trimRow(page, prev); trimRow(page, line); }
  if (item.act === 'splitBelow' && next) { trimRow(page, line); trimRow(page, next); }
  resyncLots(state.project);
  replan();
  recomputeAll();
  state.editSession = null;
  await refreshView();
  if (show && state.view) {
    state.view.flashLine(show);
    const target = item.act === 'split' ? f : show.Cells[0];
    if (state.view.inputs.has(target)) { state.field = target; state.view.focus(target); }
  }
  updateEditBar();
  status(msg + ' ↶ pour annuler.');
  scheduleSave();
}

// ---------- Lots 0 : à la fin du document ----------

/** Où est maintenant la ligne : « page 9 « Lots de fin », 3e ligne » ou « page 7, après « 1 gazelle… » ». */
function placeOf(line) {
  const u = state.plan.moving.find((x) => x.kind === 'zero' && x.lines.includes(line));
  if (!u) return null;
  const k = u.endPage ?? u.dest.p;
  const onPage = state.plan.pages[k].pieces.filter((pc) => pc.kind === 'unit');
  const rank = onPage.findIndex((pc) => pc.unit === u) + 1;
  return { k, text: pages()[k].EndLots ? `page ${k + 1} « Lots de fin », ${rank === 1 ? '1re' : rank + 'e'} ligne` : `page ${k + 1}, après « ${short(firstText(u.dest.table.Rows[u.dest.r].Lines.find((l) => firstText(l)) ?? u.object))} » ${u.object.EndAt ? '(endroit choisi)' : '(fin de sa partie, avant la ligne TOTAL)'}` };
}

/**
 * Lot 0 : la 1re fois, on demande où mettre les lots à la fin (après le dernier lot, ou sur une page « Lots de fin »
 * placée après la page choisie). Le choix vaut pour les suivants (modifiable dans Lots › Lots de fin…). Null : annulé.
 */
async function chooseEndPlace(line, force = false) {
  if (state.project.EndLots && !force) return state.project.EndLots;
  const cur = state.project.EndLots;
  const options = pages().map((p, i) => (p.EndLots ? '' : `<option value="${escapeHtml(p.Id)}" ${(cur?.AfterId ?? pages()[state.index]?.Id) === p.Id ? 'selected' : ''}>après la page ${i + 1} · ${escapeHtml(short(p.SourceName))}</option>`)).join('');
  const html = `${line ? `<p><b>« ${escapeHtml(short(firstText(line)))} »</b> n'est plus dans la vente (lot 0) : sa ligne quitte sa place, ce qui suit remonte, et elle passe à la fin.</p>` : ''}
    <p>Où mettre les lots à la fin ?</p>
    <label class="choice"><input type="radio" name="endMode" value="last" ${cur?.Mode !== 'page' ? 'checked' : ''}> <span><b>À la fin de sa partie</b><small>Après le dernier lot, juste avant la ligne TOTAL de sa partie (ex. « TOTAL MOBILIER ET MATERIEL »). Pour le mettre ailleurs : Modifier, puis glisser sa poignée.</small></span></label>
    <label class="choice"><input type="radio" name="endMode" value="page" ${cur?.Mode === 'page' ? 'checked' : ''}> <span><b>Sur une page « Lots de fin »</b><small>Une page créée exprès, même mise en page que la vente, placée
      <select id="endAfter">${options}</select>. Les pages suivantes sont renumérotées ; une nouvelle page s'ajoute quand elle est pleine.</small></span></label>
    <p class="muted">Ce choix vaut pour tous les lots 0 (modifiable : Lots › Lots de fin…). Pour remettre un lot à sa place : retapez son numéro.</p>`;
  const choice = await dialog(line ? 'Mettre ce lot à la fin ?' : 'Lots de fin : où ?', html, [
    { label: 'Annuler', value: null },
    { label: line ? 'Oui, à la fin' : 'OK', value: 'ok', className: 'accent' },
  ]);
  if (choice !== 'ok') return null;
  const mode = document.querySelector('input[name=endMode]:checked')?.value ?? 'last';
  return { Mode: mode, AfterId: mode === 'page' ? $('endAfter').value : null };
}

/** Numéro tapé dans une case de lot (0 : à la fin, après confirmation). */
async function applyLot(v, line, value) {
  const wasZero = isZeroLot(line);
  const goingZero = /^[+-]?0+$/.test(value.trim()) && !wasZero;
  let end = null;
  if (goingZero) {
    end = await chooseEndPlace(line);
    if (!end) { v.updateLots(); return status('Lot 0 annulé : la ligne reste à sa place.'); }
  }
  pushUndo();
  if (end) state.project.EndLots = end;
  const before = pages().length;
  setLot(state.project, line, value);
  replan();
  recomputeAll();
  await refreshView();
  const zero = isZeroLot(line);
  if (zero && !wasZero) {
    const where = placeOf(line);
    const added = pages().length - before;
    status(where
      ? `Lot 0 : « ${short(firstText(line))} » est maintenant à la fin : ${where.text}.${added > 0 ? ` Page « Lots de fin » ajoutée (page ${where.k + 1}) : les pages suivantes ont été renumérotées.` : ''} Pour le placer ailleurs : Modifier, puis glissez sa poignée orange ⋮⋮. Pour le remettre : retapez son numéro, ou clic droit › Remettre à sa place.`
      : 'Lot 0 : aucun autre lot numéroté, la ligne reste en place.');
  } else if (wasZero && !zero) status(`Lot ${line.Lot || '(vide)'} : « ${short(firstText(line))} » est revenu à sa place.`);
  else status(line.Lot ? `Lot ${line.Lot} enregistré.` : 'Case de lot vidée.');
  scheduleSave();
}

/**
 * Poignée d'un lot 0 recollé (mode Modifier) : il est posé avant la rangée `before` (null : à la fin du tableau) ; l'endroit
 * est gardé (EndAt) tant qu'il est au lot 0.
 */
async function placeZero(page, table, unit, before) {
  const order = rowOrder(table);
  const i = before === null ? order.length : order.indexOf(before);
  const anchorRow = table.Rows[order[Math.max(0, i - 1)]];
  const anchor = anchorRow?.Lines.find((l) => l.Cells[0]?.Id);
  if (!anchor) return;
  pushUndo();
  unit.object.EndAt = { Page: page.Id, Line: anchor.Cells[0].Id };
  replan(); recomputeAll();
  await refreshView();
  state.view?.flashLine(unit.object);
  status(`Lot 0 « ${short(firstText(unit.object))} » placé après « ${short(firstText(anchor) || '(rangée)')} ». Il reste là ; retaper son numéro le remet à sa place d'origine. ↶ pour annuler.`);
  scheduleSave();
}

/**
 * Poignée d'une ligne dans un bloc : la ligne devient une rangée à part (trait au-dessus et, s'il le faut, en dessous ;
 * rangées réduites à la hauteur des autres), puis cette rangée est posée avant la rangée `before` (null : à la fin).
 */
async function moveLine(page, table, line, before) {
  const at = locate(page, line);
  if (!at) return;
  pushUndo();
  const target = before === null ? null : table.Rows[before];
  const prev = at.i > 0 ? at.row.Lines[at.i - 1] : null;
  if (prev) splitAt(page, line);
  const own = locate(page, line);
  const next = own.row.Lines[own.i + 1] ?? null;
  if (next) splitBelow(page, line);
  for (const l of [prev, line, next]) if (l) trimRow(page, l);
  const from = table.Rows.findIndex((r) => r.Lines.includes(line));
  const to = target ? table.Rows.indexOf(target) : -1;
  if (to !== from) {
    const order = rowOrder(table).filter((r) => r !== from);
    order.splice(to < 0 ? order.length : order.indexOf(to), 0, from);
    table.RowOrder = order;
  }
  resyncLots(state.project);
  replan(); recomputeAll();
  await refreshView();
  state.view?.flashLine(line);
  status(`« ${short(firstText(line))} » déplacée : c'est maintenant une rangée à part (case de lot vide : tapez son numéro ou « Synchroniser »). ↶ pour annuler.`);
  scheduleSave();
}

// ---------- Page : retirer, totaux ----------

/** Cases à cocher des pages (hors pages « lots de fin »). */
function pageChecklist(checked = () => false) {
  return `<ul class="list lotpages">${pages().map((p, i) => (p.EndLots ? '' : `<li><label><input type="checkbox" data-page="${i}" ${checked(p, i) ? 'checked' : ''}> <b>Page ${i + 1}</b> <small>${escapeHtml(short(p.SourceName))}</small></label></li>`)).join('')}</ul>`;
}
const checkedPages = () => [...$('dialogBody').querySelectorAll('input[data-page]')].filter((c) => c.checked).map((c) => pages()[+c.dataset.page]).filter(Boolean);

/** « Retirer… » : cette page, des pages au choix, tout le dossier ; remettre les lignes supprimées. */
async function retireMenu(page) {
  const k = pages().indexOf(page);
  const deleted = [...allLines(page)].filter((l) => l.Deleted);
  const choice = await dialog('Retirer', '<p>Que voulez-vous retirer ? (↶ pour revenir en arrière)</p>', [
    { label: `Cette page (page ${k + 1})`, value: 'one', className: 'danger' },
    { label: 'Pages au choix…', value: 'some' },
    { label: 'Tout le dossier', value: 'all', className: 'danger' },
    ...(deleted.length ? [{ label: `Remettre les lignes supprimées (${deleted.length})`, value: 'restore' }] : []),
    { label: 'Annuler', value: null },
  ]);
  if (choice === 'one') return removePage(page);
  if (choice === 'restore') {
    pushUndo();
    for (const l of deleted) l.Deleted = false;
    resyncLots(state.project);
    replan(); recomputeAll();
    await refreshView();
    status(`${deleted.length} ligne(s) remise(s) à leur place.`);
    return scheduleSave();
  }
  let targets = [];
  if (choice === 'all') {
    if (!(await confirmDialog('Retirer tout le dossier ?', `Les ${pages().length} pages seront retirées (projet vide). ↶ pour revenir en arrière.`, 'Tout retirer'))) return;
    targets = pages().slice();
  } else if (choice === 'some') {
    const ok = await dialog('Retirer des pages', `<p>Cochez les pages à retirer.</p>${pageChecklist((p) => p === page)}`, [{ label: 'Annuler', value: null }, { label: 'Retirer', value: 'ok', className: 'danger' }]);
    if (ok !== 'ok') return;
    targets = checkedPages();
    if (!targets.length) return status('Aucune page cochée.');
    if (!(await confirmDialog('Retirer ces pages ?', `${targets.length} page(s) : ${targets.map((p) => pages().indexOf(p) + 1).join(', ')}. ↶ pour revenir en arrière.`, 'Retirer'))) return;
  } else return;
  pushUndo();
  for (const p of targets) { const i = pages().indexOf(p); if (i >= 0) pages().splice(i, 1); state.flats.delete(p); state.oriented.delete(p); }
  if (pages().every((p) => p.EndLots)) pages().splice(0);
  resyncLots(state.project);
  replan(); recomputeAll();
  await showPage(Math.min(k, pages().length - 1));
  status(`${targets.length} page(s) retirée(s).`);
  scheduleSave();
}

/** « Totaux » : recalculer les totaux de la page ; ajouter une ligne TOTAL (calcul au choix). */
async function totalsMenu(page) {
  const choice = await dialog('Totaux', '<p>Les totaux se recalculent tout seuls quand un montant change. Ici, vous pouvez aussi :</p>', [
    { label: 'Ajouter une ligne TOTAL…', value: 'add', className: 'accent' },
    { label: 'Recalculer les totaux de la page', value: 'recalc' },
    { label: 'Annuler', value: null },
  ]);
  if (choice === 'recalc') {
    pushUndo();
    let n = 0;
    for (const l of allLines(page)) for (const f of l.Cells) if (isComputedCell(l, f) && f.Manual) { f.Manual = false; n++; }
    replan(); recomputeAll();
    await refreshView();
    status(`Totaux de la page recalculés${n ? ` (${n} total(aux) tapé(s) à la main remis en automatique)` : ''}.`);
    return scheduleSave();
  }
  if (choice !== 'add') return;
  const hit = state.field ? findLine(state.project, state.field) : null;
  const here = hit?.page === page && canCopyLine(page, hit.line) ? hit.line : null;
  // Tableaux possibles : ceux qui ont des lignes d'objet ; proposé : celui de la case touchée, sinon le 1er avec des montants.
  const tables = page.Tables.map((t) => ({ t, last: lastObjectLine(page, t) })).filter((x) => x.last);
  if (!tables.length) return status('Pas de tableau d\'objets sur cette page pour y ajouter une ligne TOTAL.');
  const hasAmounts = (t) => t.Rows.some((r) => r.Lines.some((l) => l.Cells.slice(1).some((c) => /\d/.test(c.Text))));
  const pick = here ? tables.findIndex((x) => x.t.Rows.some((r) => r.Lines.includes(here))) : Math.max(0, tables.findIndex((x) => hasAmounts(x.t)));
  const where = tables.length > 1 || here
    ? `<label class="switch">Sous <select id="totTable">${here ? `<option value="here">la ligne touchée « ${escapeHtml(short(firstText(here)))} »</option>` : ''}${tables.map((x, i) => `<option value="${i}" ${!here && i === pick ? 'selected' : ''}>la fin du tableau ${i + 1} (« ${escapeHtml(short(firstText(x.last)))} »)</option>`).join('')}</select></label>`
    : `<p>Sous « ${escapeHtml(short(firstText(tables[0].last)))} » (fin du tableau).</p>`;
  const html = `<p>La ligne <b>TOTAL</b> s'ajoute comme les autres lignes du tableau.</p>${where}<p>Elle additionne :</p>
    <label class="choice"><input type="radio" name="totScope" value="above" checked> <span><b>Les lignes au-dessus</b><small>Du début du tableau (ou du total précédent) jusqu'à elle.</small></span></label>
    <label class="choice"><input type="radio" name="totScope" value="page"> <span><b>Toute cette page</b></span></label>
    <label class="choice"><input type="radio" name="totScope" value="pages"> <span><b>Les pages au choix</b><small>À cocher dans la liste qui s'ouvre.</small></span></label>
    <div id="totPages" hidden>${pageChecklist((p) => p === page)}</div>
    <label class="choice"><input type="radio" name="totScope" value="all"> <span><b>Tout le document</b></span></label>
    <label class="switch"><input id="totAuto" type="checkbox" checked> Calculer automatiquement (sinon : à taper soi-même)</label>
    <label class="switch"><input id="totBold" type="checkbox"> En gras (sinon : même écriture que les autres lignes)</label>`;
  const showPages = (e) => { if (e.target.name === 'totScope') $('totPages').hidden = e.target.value !== 'pages'; };
  if ((await dialog('Ajouter une ligne TOTAL', html, [{ label: 'Annuler', value: null }, { label: 'Ajouter', value: 'ok', className: 'accent' }], showPages)) !== 'ok') return;
  const sel = $('totTable')?.value;
  const anchor = sel === 'here' ? here : tables[sel !== undefined ? +sel : 0].last;
  const mode = document.querySelector('input[name=totScope]:checked')?.value ?? 'above';
  const scope = { Mode: mode, Pages: mode === 'pages' ? checkedPages().map((p) => p.Id) : [], Auto: $('totAuto').checked, Bold: $('totBold').checked };
  pushUndo();
  trimRow(page, anchor);
  const t = addTotalLine(page, anchor, scope);
  if (!t) { state.undo.pop(); updateButtons(); return status('Impossible d\'ajouter la ligne ici.'); }
  resyncLots(state.project);
  replan(); recomputeAll();
  await refreshView();
  state.view?.flashLine(t);
  const sums = t.Cells.slice(1).map((c) => c.Text).filter(Boolean).join(' / ');
  status(`Ligne TOTAL ajoutée sous « ${short(firstText(anchor))} »${scope.Auto ? ` : ${sums || '0'}. Elle se recalcule toute seule` : ' : tapez les montants'}. Touchez « TOTAL » pour renommer (ex. « TOTAL SALLE 2 »). ↶ pour annuler.`);
  scheduleSave();
}

/** « Synchroniser » : cette page ou toutes les pages. */
/**
 * Document déjà numéroté (ex. PDF sorti du logiciel puis réimporté) : les numéros imprimés dans la marge sont lus et
 * mis dans les cases vides, pour que créer la colonne ne les efface pas. Renvoie le nombre de numéros repris.
 */
/** Traits du cadre (verticaux et horizontaux) effacés d'un morceau d'image : la lecture ne voit plus que les chiffres. */
function withoutRules(img) {
  const { w, h, ch, data } = img;
  const dark = (x, y) => { const o = (y * w + x) * ch; return (ch === 1 ? data[o] : (data[o] * 77 + data[o + 1] * 150 + data[o + 2] * 29) >> 8) < 150; };
  const out = new Uint8ClampedArray(data);
  const clear = (x, y) => { const o = (y * w + x) * ch; for (let c = 0; c < ch; c++) out[o + c] = 255; };
  for (let x = 0; x < w; x++) { let n = 0; for (let y = 0; y < h; y++) if (dark(x, y)) n++; if (n > 0.6 * h) for (let y = 0; y < h; y++) clear(x, y); }
  for (let y = 0; y < h; y++) { let n = 0; for (let x = 0; x < w; x++) if (dark(x, y)) n++; if (n > 0.6 * w) for (let x = 0; x < w; x++) clear(x, y); }
  return { w, h, ch, data: out };
}

async function readPrintedLots(only = null) {
  let n = 0;
  for (const page of only ?? pages()) {
    if (page.Source !== 'Image' || page.Added || page.EndLots) continue;
    const tables = page.Tables.map((t) => ({ t, zone: printedLotZone(page, t) })).filter((x) => x.zone);
    if (!tables.length) continue;
    status(`Lecture des numéros déjà imprimés, page ${pages().indexOf(page) + 1}…`); await tick();
    const flat = state.flats.get(page) ?? (await restoreFlat(page));
    if (!flat) continue;
    const img = stretch(flat, page);
    for (const { t, zone } of tables) {
      const k = 2; // agrandie : les petits chiffres se lisent mieux
      const crop = warp(img, zone.Width * k, zone.Height * k, (u, v) => ({ X: zone.X + u / k, Y: zone.Y + v / k }));
      let words = [];
      try { words = await recognize(withoutRules(crop), 11); } catch { words = []; }
      n += applyPrintedLots(state.project, page, t, words.map((w) => ({ Text: w.Text, Box: { X: zone.X + w.Box.X / k, Y: zone.Y + w.Box.Y / k, Width: w.Box.Width / k, Height: w.Box.Height / k } })));
      // Numéro pas lu dans la bande : relu seul, à la hauteur de sa case (plus grand, une seule ligne).
      for (const o of [...objectLines({ Pages: [page] })].filter((x) => x.table === t && !x.line.Lot)) {
        const b = numberBox(t, o.line, columnWidth(page));
        const box = { X: zone.X, Y: Math.max(0, Math.floor(b.Y - 0.4 * b.Height)), Width: zone.Width, Height: Math.ceil(1.8 * b.Height) };
        const k3 = 3;
        const one = warp(img, box.Width * k3, box.Height * k3, (u, v) => ({ X: box.X + u / k3, Y: box.Y + v / k3 }));
        let ws = [];
        try { ws = await recognize(withoutRules(one), 7, '0123456789'); } catch { ws = []; }
        n += applyPrintedLots(state.project, page, t, ws.map((w) => ({ Text: w.Text, Box: { X: box.X + w.Box.X / k3, Y: box.Y + w.Box.Y / k3, Width: w.Box.Width / k3, Height: w.Box.Height / k3 } })), true);
      }
    }
  }
  return n;
}

// ---------- Déplacer des lots d'une page à l'autre ----------

/** Lots du document (numérotés ou non, pas les lots 0) : { line, page, k (n° de page), lot, name }. */
function lotEntries() {
  return [...objectLines(state.project)].filter((o) => !isZeroLot(o.line) && hasLotCell(o.line))
    .map((o) => ({ line: o.line, page: o.page, k: pages().indexOf(o.page), lot: o.line.Lot || '', name: short(firstText(o.line)) }));
}

/**
 * « Déplacer des lots… » : on coche un ou plusieurs lots (recherche par numéro ou par texte), on choisit la page et
 * l'endroit (début, fin, après un lot) ; ils s'y affichent, rien d'autre ne bouge. « Remettre à leur place » annule.
 */
async function moveLotsDialog(preselect = null) {
  const all = lotEntries();
  if (!all.length) return status('Aucun lot à déplacer : créez d\'abord les cases (Lots).');
  const targetPages = pages().map((p, k) => ({ p, k })).filter((x) => !x.p.EndLots && all.some((e) => e.page === x.p));
  const cur = currentPage();
  const defPage = targetPages.find((x) => x.p === cur) ?? targetPages[0];
  const where = (e) => (e.line.MoveTo ? ` <small>→ page ${pages().findIndex((p) => p.Id === e.line.MoveTo.Page) + 1}</small>` : '');
  const html = `<p>Cochez les lots à déplacer, puis choisissez où ils vont. Les autres lignes ne bougent pas.</p>
    <input id="mvSearch" type="search" placeholder="Chercher un numéro ou un mot…" style="width:100%">
    <ul class="list lotpages" id="mvList" style="max-height:38vh;overflow:auto">${all.map((e, i) => `<li data-i="${i}"><label><input type="checkbox" data-i="${i}" ${e.line === preselect ? 'checked' : ''}> <b>${escapeHtml(e.lot || '(vide)')}</b> ${escapeHtml(e.name)} <small>page ${e.k + 1}</small>${where(e)}</label></li>`).join('')}</ul>
    <p><label>Vers la page <select id="mvPage">${targetPages.map((x) => `<option value="${x.k}" ${x === defPage ? 'selected' : ''}>Page ${x.k + 1}</option>`).join('')}</select></label></p>
    <label class="choice"><input type="radio" name="mvWhere" value="start" checked> <span><b>Au début de la page</b></span></label>
    <label class="choice"><input type="radio" name="mvWhere" value="end"> <span><b>À la fin de la page</b></span></label>
    <label class="choice"><input type="radio" name="mvWhere" value="after"> <span><b>Juste après le lot</b> <select id="mvAnchor"></select></span></label>`;
  const shown = dialog('Déplacer des lots', html, [
    { label: 'Annuler', value: null },
    { label: 'Remettre à leur place', value: 'back' },
    { label: 'Déplacer', value: 'move', className: 'accent' },
  ]);
  const fillAnchors = () => {
    const k = +$('mvPage').value;
    $('mvAnchor').innerHTML = all.filter((e) => e.k === k).map((e) => `<option value="${all.indexOf(e)}">${escapeHtml(e.lot || '(vide)')} ${escapeHtml(e.name)}</option>`).join('');
  };
  fillAnchors();
  $('mvPage').onchange = fillAnchors;
  $('mvAnchor').onchange = () => { document.querySelector('input[name=mvWhere][value=after]').checked = true; };
  $('mvSearch').oninput = () => {
    const q = $('mvSearch').value.trim().toLowerCase();
    for (const li of $('mvList').children) { const e = all[+li.dataset.i]; li.hidden = !!q && !(`${e.lot} ${e.name}`.toLowerCase().includes(q)); }
  };
  const chosen = () => [...$('mvList').querySelectorAll('input[type=checkbox]:checked')].map((c) => all[+c.dataset.i]);
  let picked = [], target = null, mode = 'start', anchor = null;
  // Lu avant la fermeture (le contenu du dialogue est remplacé ensuite).
  const read = () => { picked = chosen(); target = pages()[+$('mvPage').value]; mode = document.querySelector('input[name=mvWhere]:checked')?.value ?? 'start'; anchor = all[+$('mvAnchor').value] ?? null; };
  for (const b of $('dialogButtons').querySelectorAll('button')) b.addEventListener('pointerdown', read, true);
  const choice = await shown;
  if (!choice) return;
  if (!picked.length) return status('Aucun lot coché : rien n\'a bougé.');
  pushUndo();
  if (choice === 'back') {
    for (const e of picked) delete e.line.MoveTo;
    replan(); recomputeAll();
    await refreshView();
    status(`${picked.length} lot(s) remis à leur place.`);
    return scheduleSave();
  }
  for (const e of picked) e.line.MoveTo = { Page: target.Id, Where: mode, Anchor: mode === 'after' && anchor && !picked.includes(anchor) ? anchor.line.Cells[0]?.Id ?? '' : '' };
  replan(); recomputeAll();
  const k = pages().indexOf(target);
  if (k !== state.index) await showPage(k, { fit: false }); else await refreshView();
  status(`${picked.length} lot(s) (${picked.map((e) => e.lot || e.name).slice(0, 6).join(', ')}${picked.length > 6 ? '…' : ''}) déplacé(s) page ${k + 1}, ${mode === 'start' ? 'au début' : mode === 'end' ? 'à la fin' : `après le lot ${anchor?.lot || anchor?.name}`}. ↶ pour annuler ; Lots › Déplacer des lots › Remettre à leur place pour revenir.`);
  scheduleSave();
}

/** « Synchroniser » sans colonne des lots : on choisit les pages, les cases sont créées (numéros imprimés gardés), puis remplies. */
async function syncWithoutLots() {
  const chosen = await chooseLotTables();
  if (!chosen) return;
  if (chosen.size === 0) return status('Aucune page cochée : pas de numéros de lot.');
  pushUndo();
  setLotTables(state.project, chosen);
  createEmptyLots(state.project);
  const read = await readPrintedLots();
  syncLots(null, read);
}

async function syncDialog() {
  if (!lotsActive(state.project)) return syncWithoutLots();
  const page = currentPage();
  const choice = await dialog('Synchroniser les lots', `<p>Remplit les <b>cases vides</b>. Les numéros déjà là <b>ne bougent pas</b> : une case vide prend le numéro qui suit et décale seulement les lots qui la suivent directement (6, 7, 8 → 7, 8, 9). Un ordre fait à la main (30, 29, 6…) est gardé.</p>
    <p class="muted">Pour tout renuméroter dans l'ordre affiché : Lots › Renuméroter tout.</p>`, [
    ...(page && !page.EndLots ? [{ label: `Cette page (page ${state.index + 1})`, value: 'page', className: 'accent' }] : []),
    { label: 'Toutes les pages', value: 'all' },
    { label: 'Annuler', value: null },
  ]);
  if (!choice) return;
  pushUndo();
  syncLots(choice === 'page' ? [page] : null);
}

/** Remplit les cases vides (pages choisies, ou toutes) sans toucher aux numéros déjà là ; dit ce qui a changé. */
function syncLots(only = null, read = 0) {
  const before = new Map([...objectLines(state.project)].map((o) => [o.line, o.line.Lot ?? '']));
  fillLotsAutomatically(state.project, only);
  replan(); recomputeAll();
  refreshView();
  const objs = [...objectLines(state.project)];
  const filled = objs.filter((o) => !(before.get(o.line) ?? '') && o.line.Lot).length;
  const shifted = objs.filter((o) => (before.get(o.line) ?? '') && before.get(o.line) !== o.line.Lot).length;
  const where = only ? ` sur la page ${only.map((p) => pages().indexOf(p) + 1).join(', ')}` : '';
  // Numéros en double (ex. après un ordre fait à la main 30, 29 puis une case vide → 30) : signalés, pas corrigés en douce.
  const seen = new Map();
  for (const o of objs) if (o.line.Lot && !isZeroLot(o.line)) seen.set(o.line.Lot, (seen.get(o.line.Lot) ?? 0) + 1);
  const dup = [...seen].filter(([, n]) => n > 1).map(([v]) => v);
  const warn = dup.length ? ` Attention, numéro(s) en double : ${dup.slice(0, 6).join(', ')}${dup.length > 6 ? '…' : ''} (touchez la case pour corriger).` : '';
  status((read ? `${read} numéro(s) déjà imprimé(s) repris. ` : '') + (!objs.length ? 'Aucun objet à numéroter.'
    : filled === 0 ? `Rien à remplir${where} : toutes les cases ont un numéro (rien n'a bougé).`
      : `${filled} case(s) remplie(s)${where}${shifted ? `, ${shifted} lot(s) suivant(s) décalé(s) d'un cran` : ''} ; les autres numéros n'ont pas bougé. ↶ pour annuler.`) + warn);
  scheduleSave();
}

/**
 * Après une saisie : la rangée grandit si le texte passe sur plus de lignes (tout ce qui est en dessous descend),
 * les totaux suivent, et si une ligne devient (ou cesse d'être) un objet, sa case de lot apparaît ou disparaît.
 */
function afterEdit(view, f = null) {
  const entry = f ? view.entryOf(f) : null;
  const grew = entry && setExtra(entry.page, entry.line, lineExtra(entry.line));
  const changed = recomputeAll();
  let lots = false;
  if (lotsActive(state.project)) {
    // Cases de lot avant / après (une ligne qui devient un objet en tapant reçoit sa case : la page est redessinée).
    const cells = () => pages().flatMap((p) => [...allLines(p)].filter(hasLotCell));
    const before = new Set(cells());
    resyncLots(state.project);
    const after = cells();
    lots = after.length !== before.size || after.some((l) => !before.has(l));
  }
  if (grew || lots) redraw(view);
  else if (changed.length) view.syncFields(changed);
}

function goTo(n) {
  if (state.busy || n === state.index || n < 0 || n >= pages().length) return;
  saveNow();
  run(() => showPage(n));
}

// ---------- Page ajoutée (objets inventoriés à la main) ----------

/** « Ajouter une page » : page vierge sur le modèle de la page affichée, placée où on veut. */
async function addPageDialog(model) {
  if (!canBeModel(model)) return status('Cette page n\'a pas de tableau d\'objets : affichez une page avec un tableau (désignation, prix) pour la prendre comme modèle.');
  const list = pages();
  const end = list.reduce((k, p, i) => (p.EndLots ? k : i + 1), 0); // à la fin, avant les pages « Lots de fin »
  const options = [`<option value="${end}" selected>à la fin de l'inventaire${list.some((p) => p.EndLots) ? ' (avant « Lots de fin »)' : ''}</option>`,
    `<option value="0">au début (avant la page 1)</option>`,
    ...list.map((p, i) => `<option value="${i + 1}">après la page ${i + 1} · ${escapeHtml(short(p.SourceName))}</option>`)].join('');
  const html = `<p>Nouvelle page sur le modèle de cette page (mêmes colonnes, mêmes traits, mêmes en-têtes), à remplir à la main :</p>
    <label class="choice"><input type="radio" name="addKind" value="empty" checked> <span><b>Page blanche</b><small>L'en-tête du tableau et une ligne ; vous ajoutez les lignes qu'il faut (« + » à droite d'une ligne › Ajouter des lignes : 1, 5, 10, 15…).</small></span></label>
    <label class="choice"><input type="radio" name="addKind" value="full"> <span><b>Page remplie de lignes vides</b><small>Tous les tableaux et toutes les lignes du modèle, vides.</small></span></label>
    <p><label>Placer la page <select id="addAt">${options}</select></label></p>
    <p class="muted">Sur la page ajoutée : « + » à droite d'une ligne (ligne au-dessus, en dessous, plus haute, case de lot…), « + » au-dessus d'une colonne pour la couper en deux, « − » sous un trait pour réunir deux colonnes.</p>`;
  const choice = await dialog('Ajouter une page', html, [{ label: 'Annuler', value: null }, { label: 'Ajouter', value: 'ok', className: 'accent' }]);
  if (choice !== 'ok') return;
  const at = Math.max(0, Math.min(list.length, +$('addAt').value || 0));
  const empty = (document.querySelector('input[name=addKind]:checked')?.value ?? 'empty') === 'empty';
  const flat = state.flats.get(model) ?? (await restoreFlat(model));
  pushUndo();
  const page = addBlankPage(state.project, model, at, flat, null, empty);
  if (!page) { state.undo.pop(); updateButtons(); return status('Impossible d\'ajouter une page sur ce modèle.'); }
  syncEndPages(state.project);
  replan(); recomputeAll();
  await showPage(pages().indexOf(page));
  status(`Page ${pages().indexOf(page) + 1} ajoutée : touchez une case pour écrire. ↶ pour annuler.`);
  scheduleSave();
}

/** « Ajouter des lignes » : combien (1, 5, 10, 15 ou au choix), en dessous ou au-dessus de la ligne. */
async function rowsDialog(page, line) {
  const html = `<p>Combien de lignes vides ?</p>
    <div class="counts">${[1, 5, 10, 15].map((n) => `<button data-n="${n}" class="${n === 10 ? 'on' : ''}">${n}</button>`).join('')}</div>
    <p><label>Autre nombre : <input type="number" id="rowsN" min="1" max="200" value="10" inputmode="numeric"></label></p>
    <p><label><input type="checkbox" id="rowsAbove"> au-dessus de cette ligne (sinon en dessous)</label></p>
    <p class="muted">Mêmes colonnes, même hauteur et mêmes traits que les lignes du tableau ; la case de lot apparaît quand on écrit (page ajoutée : avec le « + » à gauche). La page garde sa taille : si elle est pleine, la suite part sur une page blanche juste après.</p>`;
  const choice = await dialog('Ajouter des lignes', html, [{ label: 'Annuler', value: null }, { label: 'Ajouter', value: 'ok', className: 'accent' }], (e) => {
    const b = e.target.closest('button[data-n]');
    if (!b) return;
    $('rowsN').value = b.dataset.n;
    for (const x of $('dialogBody').querySelectorAll('button[data-n]')) x.classList.toggle('on', x === b);
  });
  if (choice !== 'ok') return;
  const n = Math.max(1, Math.min(200, Math.trunc(+$('rowsN').value || 0)));
  return addRowsNow(page, line, n, $('rowsAbove').checked && canAddAbove(page, line));
}

/** Ajoute n lignes vides sous (ou au-dessus de) la rangée de la ligne ; la suite part sur une page blanche si la page est pleine. */
async function addRowsNow(page, line, n, above) {
  const flat = page.Added ? null : state.flats.get(page) ?? (await restoreFlat(page));
  pushUndo();
  const res = addRows(state.project, page, line, n, { above, img: flat });
  if (!res.added) { state.undo.pop(); updateButtons(); return status('Impossible d\'ajouter des lignes ici : choisissez une ligne d\'un tableau.'); }
  syncEndPages(state.project);
  replan(); recomputeAll();
  await refreshView();
  if (res.last && pages()[state.index] === page) state.view?.flashLine(res.last);
  const more = res.pages.length ? ` La page était pleine : la suite est sur ${res.pages.length > 1 ? `les pages ${res.pages.map((p) => pages().indexOf(p) + 1).join(', ')}` : `la page ${pages().indexOf(res.pages[0]) + 1}`} (page blanche ajoutée).` : '';
  status(`${res.added} ligne(s) ajoutée(s).${more} ↶ pour annuler.`);
  scheduleSave();
}

/** « + » au-dessus d'une colonne (la couper en deux) ou « × » sur un trait (réunir deux colonnes), page ajoutée. */
async function columnAction(page, table, c, add) {
  pushUndo();
  const ok = add ? addColumn(page, table, c) : removeColumn(page, table, c);
  if (!ok) { state.undo.pop(); updateButtons(); return status(add ? 'Colonne trop étroite pour être coupée en deux.' : 'Ce trait ne peut pas être retiré (il faut garder au moins deux colonnes).'); }
  state.flats.delete(page);
  replan(); recomputeAll();
  await refreshView();
  status(add ? 'Colonne ajoutée : une case vide sur chaque ligne. ↶ pour annuler.' : 'Colonnes réunies (textes mis ensemble). ↶ pour annuler.');
  scheduleSave();
}

async function removePage(page) {
  if (state.busy || !(await confirmDialog('Retirer cette page ?', `« ${page.SourceName} » sera retirée du projet et des exports.`, 'Retirer'))) return;
  const i = pages().indexOf(page);
  if (i < 0) return;
  pushUndo();
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

const SUPPORTED = /\.(jpe?g|png|bmp|tiff?|heic|heif|webp|gif|pdf|txt|csv|xlsx|inventaire)$/i;

/**
 * Dossier ou .zip : on prend tous les fichiers utilisables qu'il contient (sous-dossiers compris),
 * dans l'ordre des noms (« page 2 » avant « page 10 »). Le reste est ignoré.
 */
async function expandFiles(files) {
  const out = [];
  let ignored = 0;
  for (const file of files) {
    const path = repairName(file.webkitRelativePath || file.name || '');
    if (/\.zip$/i.test(path) || file.type === 'application/zip') {
      const entries = unzipSync(new Uint8Array(await file.arrayBuffer()));
      for (const [name, bytes] of Object.entries(entries)) {
        const base = name.split('/').pop();
        if (name.endsWith('/') || name.startsWith('__MACOSX/') || base.startsWith('.')) continue;
        if (!SUPPORTED.test(base)) { ignored++; continue; }
        out.push({ path: path + '/' + repairName(name), file: new File([bytes], repairName(base)) });
      }
      continue;
    }
    const base = path.split('/').pop();
    if (file.webkitRelativePath && (base.startsWith('.') || !SUPPORTED.test(base))) { ignored++; continue; }
    out.push({ path, file });
  }
  out.sort((a, b) => naturalCompare(a.path, b.path));
  return { files: out.map((x) => x.file), ignored };
}

async function importFiles(picked) {
  let added = 0;
  const wasEmpty = pages().length === 0;
  const firstNew = pages().length;
  const blankPages = [];
  const { files: sorted, ignored } = await expandFiles(picked);
  if (sorted.length === 0) {
    await dialog('Rien à importer', '<p>Aucune photo, PDF ou tableau trouvé dans ce que vous avez choisi.</p>', [{ label: 'OK' }]);
    return;
  }
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
        for await (const { index, count, png, blank } of pdfPages(bytes)) {
          if (blank) blankPages.push(`${name} – page ${index}`);
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
  if (added && wasEmpty) state.project.Name = repairName((picked[0].webkitRelativePath || '').split('/')[0] || sorted[0].name || 'Inventaire').replace(/\.[^.]+$/, '');
  if (blankPages.length > 0)
    await dialog('Pages vides', `<p>Ces pages du PDF n'ont rien donné à l'écran (image illisible ou page blanche) :</p><ul>${blankPages.map((n) => `<li>${escapeHtml(n)}</li>`).join('')}</ul><p>Si elles ne sont pas vides sur l'original, retirez-les et importez une photo de la page à la place.</p>`, [{ label: 'OK' }]);
  if (added) {
    // On revient sur la première page importée.
    if (firstNew < pages().length && state.index !== firstNew) await showPage(firstNew);
    await saveNow();
    const n = pages().length - firstNew;
    status(`${n} page(s) ajoutée(s)${ignored ? ` (${ignored} fichier(s) ignoré(s))` : ''}. Touchez une case pour la corriger ; ◀ ▶ en haut pour changer de page.`);
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
  const flat = render(src, page.Crop ?? Quad.full(src.w, src.h), page.Skew, page.Width, baseHeight(page));
  return page.Added ? blankBase(flat, page) : flat; // page ajoutée : tableaux vidés, filets retracés
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
  for (const p of pages()) { p.SourceName = repairName(p.SourceName); upgradePage(p); }
  resyncLots(state.project);
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

/** Page redressée avec toutes les corrections dessinées (colonne des lots, textes, traits, couleurs). */
async function editedOf(page) {
  const base = state.flats.get(page) ?? (await restoreFlat(page));
  return base ? renderEdits(page, stretch(base, page), lotFrameEnabled(state.project)) : null;
}

/**
 * Pages exportées en JPEG (photos et PDF), totaux recalculés avant : chaque page telle qu'à l'écran (corrections, lots,
 * lots 0 recollés, pages « lots de fin », lignes ajoutées). Sert à l'export JPEG et à l'export PDF.
 */
async function renderPages(only = null) {
  const plan = replan();
  recomputeAll();
  const all = pages().map((page, k) => ({ page, k })).filter((x) => x.page.Source === 'Image');
  const targets = only ? all.filter((x) => x.page === only) : all;
  // Lots 0 : leurs morceaux sont pris sur leur page (corrections comprises), puis recollés après le dernier lot.
  const crops = new Map();
  const unitPages = [...new Set(targets.flatMap(({ k }) => plan.pages[k].pieces.filter((pc) => pc.kind === 'unit').map((pc) => pc.unit.p)))].filter((p) => !pages()[p].EndLots);
  for (let i = 0; i < unitPages.length; i++) {
    const page = pages()[unitPages[i]];
    if (page.Source !== 'Image') continue;
    status(`Préparation des lots 0 (${i + 1}/${unitPages.length})…`); await tick();
    const edited = await editedOf(page);
    if (edited) for (const u of plan.units.filter((x) => x.p === unitPages[i])) crops.set(u, unitCrop(u, edited));
  }
  const files = [];
  const frame = lotFrameEnabled(state.project);
  for (let i = 0; i < targets.length; i++) {
    const { page, k } = targets[i];
    progress(i / targets.length);
    status(`Création de l'image ${i + 1} sur ${targets.length}…`); await tick();
    // Page « lots de fin » : composée sur l'image de sa page modèle.
    const tpl = plan.pages[k].template;
    const edited = page.EndLots ? (tpl >= 0 ? await editedOf(pages()[tpl]) : null) : await editedOf(page);
    if (!edited) continue;
    let final = edited;
    const arranged = pageArranged(state.project, plan, k);
    if (arranged) final = composePage(state.project, plan, k, edited, crops);
    // Page qui garde sa forme : la photo d'origine, inchangée sauf dans les cases modifiées.
    if (!arranged && totalExtra(page) === 0 && !(page.Inserts ?? []).length) {
      const src = await orientedSource(page);
      if (!src) continue;
      final = composeIntoSource(src, page, edited, frame);
    }
    const canvas = rasterToCanvas(final);
    const blob = await canvasToBlob(canvas, 'image/jpeg', 0.93);
    freeCanvas(canvas);
    files.push(new File([blob], outputName(page), { type: 'image/jpeg' }));
  }
  progress(null);
  return { plan, files };
}

/** Phrase sur les lots 0 recollés (pour les messages d'export). */
const zeroNote = (plan) => { const z = plan.moving.filter((u) => u.kind === 'zero'); return z.length > 0 ? ` ${z.length} lot(s) 0 placé(s) à la fin (page ${(z[0].endPage ?? plan.dest.p) + 1}).` : ''; };

/** Export PDF : tout le document (ou la page affichée) en un fichier, une page PDF par page. */
async function exportPdf(only = null) {
  const { plan, files } = await renderPages(only);
  if (!files.length) return status('Pas de photo ni de PDF à exporter (les TXT, CSV et XLSX s\'exportent en .txt).');
  status('Création du PDF…'); await tick();
  const jpegs = await Promise.all(files.map(async (f) => new Uint8Array(await f.arrayBuffer())));
  const name = `${state.project.Name}${only ? ` - page ${pages().indexOf(only) + 1}` : ''} - modifié.pdf`;
  const pdf = new File([buildPdf(jpegs, state.project.Name)], name.replace(/[\\/:*?"<>|]/g, '_'), { type: 'application/pdf' });
  const skipped = only ? 0 : pages().filter((p) => p.Source !== 'Image').length;
  status(`PDF prêt : ${files.length} page(s).`);
  await deliver([pdf], 'PDF prêt', `<p>${files.length} page(s), chacune telle qu'à l'écran (corrections${lotsActive(state.project) ? ', numéros de lot' : ''}, totaux recalculés).${zeroNote(plan)}${skipped ? ` ${skipped} page(s) TXT/CSV/XLSX non comprise(s) (elles s'exportent en .txt).` : ''}</p>`);
}

async function exportJpeg(only = null) {
  const { plan, files } = await renderPages(only);
  status(`${files.length} image(s) prête(s).`);
  const zero = zeroNote(plan);
  await deliver(files, files.length > 1 ? 'Images prêtes' : 'Image prête', "<p>Chaque photo d'origine, inchangée sauf dans les cases modifiées" + (lotsActive(state.project) ? ' et la colonne des numéros de lot' : '') + '.' + zero + '</p>');
}

async function exportDialog() {
  await saveNow();
  const images = pages().filter((p) => p.Source === 'Image').length;
  const page = currentPage();
  const choice = await dialog('Exporter', '<p>Choisissez ce que vous voulez obtenir.</p>', [
    ...(images ? [{ label: images > 1 ? `Toutes les images JPEG (${images})` : 'Image JPEG', value: 'all', className: 'accent' }] : []),
    ...(images > 1 && page?.Source === 'Image' ? [{ label: `Image de la page ${state.index + 1} seulement`, value: 'one' }] : []),
    ...(images ? [{ label: images > 1 ? `Document PDF (${images} pages)` : 'Document PDF', value: 'pdf', className: 'accent' }] : []),
    ...(images > 1 && (page?.Source === 'Image' || page?.EndLots) ? [{ label: `PDF de la page ${state.index + 1} seulement`, value: 'pdfone' }] : []),
    { label: 'Fichier texte (.txt)', value: 'txt', className: images ? '' : 'accent' },
    { label: 'Annuler', value: null },
  ]);
  if (choice === 'all') await exportJpeg();
  else if (choice === 'one') await exportJpeg(page);
  else if (choice === 'pdf') await exportPdf();
  else if (choice === 'pdfone') await exportPdf(page);
  else if (choice === 'txt') await exportTxt();
}

// ---------- Lots ----------

/**
 * Bouton « Lots » : la 1re fois, la colonne est créée tout de suite, cases vides (on tape les numéros soi-même).
 * Ensuite : synchroniser (remplir tout d'un coup), renuméroter, vider, enlever.
 */
/**
 * Pages (et tableaux) qui reçoivent les lots : une case à cocher par page, et par tableau quand la page en a plusieurs.
 * Proposé : les tableaux qui contiennent des objets (pas une page de garde, une signature, un récapitulatif).
 * Renvoie l'ensemble des tableaux cochés, ou null (Annuler).
 */
async function chooseLotTables() {
  const list = pages().map((p, i) => ({ i, tables: lotTables(p) }));
  const item = (i, tables) => {
    if (tables.length === 0) return `<li class="off"><label><input type="checkbox" disabled> Page ${i + 1} <small>pas de tableau</small></label></li>`;
    const all = tables.every((x) => defaultLotChoice(state.project, x.table));
    const sub = tables.length > 1 ? `<ul>${tables.map((x) => `<li><label><input type="checkbox" data-p="${i}" data-t="${x.index}" ${defaultLotChoice(state.project, x.table) ? 'checked' : ''}> ${escapeHtml(short(x.name))}${x.objects ? '' : ' <small>(sans objet)</small>'}</label></li>`).join('')}</ul>` : '';
    const one = tables.length === 1 ? `data-t="${tables[0].index}"` : '';
    return `<li><label><input type="checkbox" data-page="${i}" data-p="${i}" ${one} ${all ? 'checked' : ''}> <b>Page ${i + 1}</b> <small>${escapeHtml(short(tables[0].name))}${tables.length > 1 ? ` · ${tables.length} tableaux` : ''}</small></label>${sub}</li>`;
  };
  const html = `<p>Cochez les pages qui reçoivent des <b>numéros de lot</b>. Les autres pages ne changent pas.</p>
    <ul class="list lotpages">${list.map((x) => item(x.i, x.tables)).join('')}</ul>`;
  const body = () => $('dialogBody');
  const shown = dialog('Lots : quelles pages ?', html, [{ label: 'Annuler', value: null }, { label: 'OK', value: 'ok', className: 'accent' }], (e) => {
    const box = e.target.closest?.('input[type=checkbox]');
    if (!box) return;
    const p = box.dataset.p;
    if (box.dataset.page !== undefined) for (const c of body().querySelectorAll(`input[data-p="${p}"][data-t]`)) c.checked = box.checked;
    else { const subs = [...body().querySelectorAll(`ul ul input[data-p="${p}"]`)]; const head = body().querySelector(`input[data-page="${p}"]`); if (head && subs.length) head.checked = subs.some((c) => c.checked); }
  });
  if ((await shown) !== 'ok') return null;
  const chosen = new Set();
  for (const c of body().querySelectorAll('input[data-t]')) {
    if (!c.checked) continue;
    const page = pages()[+c.dataset.p];
    if (c.dataset.page !== undefined && page && lotTables(page).length > 1) continue; // la page : ses tableaux sont cochés un par un
    const t = page?.Tables[+c.dataset.t];
    if (t) chosen.add(t);
  }
  return chosen;
}

/** Aide : les numéros de lot, pas à pas. */
function lotsHelp() {
  return dialog('Numéros de lot : comment ça marche ?', `
    <ol class="help">
      <li><b>Choisir les pages.</b> Bouton <b>Lots</b> : cochez les pages (et tableaux) qui reçoivent des numéros, puis <b>OK</b>. Une page de garde, un récapitulatif ou une signature restent sans numéro. Pour changer : <b>Lots › Pages et tableaux…</b></li>
      <li><b>Numéroter.</b> Touchez une case à gauche d'un objet et tapez son numéro : rien d'autre ne change. Ou <b>Synchroniser…</b> (cette page ou toutes les pages) : les <b>cases vides</b> se remplissent ; les numéros déjà là ne bougent pas, une case vide prend le numéro qui suit et décale seulement les lots qui la suivent directement. Case qui manque sur un objet : clic droit › <b>Ajouter une case de lot</b>. Pour tout refaire dans l'ordre affiché : <b>Renuméroter tout</b>.</li>
      <li><b>Objet absent : à la fin.</b> Tapez <b>0</b> (ou clic droit › <b>Mettre à la fin</b>). La 1re fois, l'app demande où : <b>à la fin de sa partie</b> (juste avant sa ligne TOTAL, ex. « TOTAL MOBILIER ET MATERIEL »), ou sur une <b>page « Lots de fin »</b> placée où vous voulez (les pages suivantes sont renumérotées, une nouvelle page s'ajoute quand elle est pleine). Le message en bas dit exactement où la ligne est partie. Pour la mettre ailleurs : <b>Modifier</b>, puis glissez sa poignée orange ⋮⋮. Pour la remettre : retapez son numéro (ou clic droit › <b>Remettre à sa place</b>).</li>
      <li><b>Objet retiré de la vente.</b> Clic droit › <b>Supprimer cette ligne</b> : elle disparaît (page, fichier texte, totaux), ce qui suit remonte. Pour la retrouver : ↶, ou <b>Retirer… › Remettre les lignes supprimées</b>.</li>
      <li><b>Ajouter ou séparer.</b> Clic droit (ou bouton <b>Ligne</b>) : <b>ligne vide</b> au-dessus ou en dessous, <b>dupliquer</b>, <b>tracer un trait</b> au-dessus ou en dessous pour séparer un gros bloc. Une rangée trop haute (espace vide imprimé) est réduite à la hauteur des autres ; aussi au clic droit : <b>Réduire la rangée</b>.</li>
      <li><b>Déplacer.</b> Touchez <b>Modifier</b> en haut : les poignées <b>⋮⋮</b> apparaissent à gauche de chaque ligne (la poubelle se cache). Glissez-en une vers le haut ou le bas : une rangée, ou une ligne seule d'un bloc (poignée en pointillés), qui devient alors une rangée. Retouchez <b>Modifier</b> pour terminer.</li>
      <li><b>Totaux.</b> Ils suivent tout seuls. Bouton <b>Totaux</b> de la page : recalculer, ou <b>ajouter une ligne TOTAL</b> (lignes au-dessus, la page, des pages au choix ou tout le document ; en gras ou non).</li>
      <li><b>Exporter.</b> Images JPEG, <b>document PDF</b> (tout le document ou la page affichée) ou fichier texte ; les totaux sont recalculés juste avant.</li>
      <li><b>Mise en page.</b> Cochez ou non la <b>loupe « Original »</b> au-dessus de la case et le <b>zoom automatique de l'iPad</b>.</li>
    </ol>
    <p class="muted">Rien ne change sans votre action. ↶ annule la dernière action (jusqu'à 30).</p>`, [{ label: 'Compris', className: 'accent' }]);
}

async function lotsDialog() {
  if (!lotsActive(state.project)) {
    const chosen = await chooseLotTables();
    if (!chosen) return;
    if (chosen.size === 0) return status('Aucune page cochée : pas de numéros de lot.');
    pushUndo();
    setLotTables(state.project, chosen);
    createEmptyLots(state.project);
    const read = await readPrintedLots();
    const n = [...objectLines(state.project)].length;
    const pagesOn = new Set([...objectLines(state.project)].map((o) => pages().indexOf(o.page) + 1));
    await refreshView();
    status(`Colonne des lots créée sur ${pagesOn.size > 1 ? 'les pages' : 'la page'} ${[...pagesOn].join(', ')} : ${n} cases${read ? `, dont ${read} avec le numéro déjà imprimé (gardé)` : ' vides'}. Touchez une case pour taper un numéro (0 = absent : la ligne passe après le dernier lot).`);
    scheduleSave();
    return;
  }
  const objects = [...objectLines(state.project)].map((o) => o.line);
  const filled = objects.filter((l) => l.Lot && !isZeroLot(l)).length, zeros = objects.filter(isZeroLot).length;
  const nums = objects.filter((l) => l.Lot && !isZeroLot(l));
  const first = nums[0]?.Lot ?? '', last = nums[nums.length - 1]?.Lot ?? '';
  const html = `
    <p><b>${objects.length} objets</b> : ${filled} numéroté(s)${filled ? ` (de <b>${escapeHtml(first)}</b> à <b>${escapeHtml(last)}</b>)` : ''}${zeros ? `, ${zeros} au lot 0` : ''}, ${objects.length - filled - zeros} case(s) vide(s).</p>
    <p class="muted">Touchez une case sur la page pour taper un numéro : rien d'autre ne change. <b>0</b> = objet absent : sa ligne passe après le dernier lot ; retapez un numéro pour la remettre à sa place. 🗑 : ce n'est pas un objet. « + » : ajouter une case. ⋮⋮ : glisser pour déplacer une rangée.</p>
    <p class="muted"><b>Synchroniser…</b> remplit les cases vides (cette page ou toutes) sans toucher aux numéros déjà là. <b>Renuméroter tout</b> refait toute la numérotation dans l'ordre affiché.</p>
    <label class="switch"><input id="lotFrame" type="checkbox" ${lotFrameEnabled(state.project) ? 'checked' : ''}> Tracer la colonne dans le tableau (traits et fond comme le tableau imprimé)</label>
    <label>Renuméroter à partir de <input id="lotStart" type="number" inputmode="numeric" min="0" value="${/^\d+$/.test(first) ? first : 1}"></label>`;
  const shown = dialog('Numéros de lot', html, [
    { label: 'Synchroniser…', value: 'sync', className: 'accent' },
    { label: 'Déplacer des lots…', value: 'move' },
    { label: state.project.SortLots ? 'Remettre dans l\'ordre du document' : 'Trier par numéro de lot', value: 'sort' },
    { label: 'Pages et tableaux…', value: 'pages' },
    { label: 'Lots de fin (lot 0)…', value: 'end' },
    { label: 'Comment ça marche ?', value: 'help' },
    { label: 'Renuméroter tout', value: 'number' },
    { label: 'Vider les cases', value: 'empty' },
    { label: 'Enlever la colonne', value: 'clear', className: 'danger' },
    { label: 'Fermer', value: null },
  ]);
  let frameChanged = false;
  $('lotFrame').onchange = (e) => {
    pushUndo();
    state.project.LotFrame = e.target.checked;
    frameChanged = true;
    scheduleSave();
  };
  const choice = await shown;
  if (choice === 'help') return lotsHelp();
  if (choice === 'move') return moveLotsDialog();
  if (choice === 'sort') {
    pushUndo();
    state.project.SortLots = !state.project.SortLots;
    replan(); recomputeAll();
    await refreshView();
    const moved = state.plan.moving.filter((u) => u.kind === 'sort').length;
    status(state.project.SortLots
      ? `Lignes triées par numéro de lot (du plus petit au plus grand, les mêmes numéros ensemble, les codes en lettres comme VGG à la fin) : ${moved} ligne(s) déplacée(s). Lots › Remettre dans l'ordre du document pour revenir.`
      : 'Lignes remises dans l\'ordre du document.');
    return scheduleSave();
  }
  if (choice === 'end') {
    const end = await chooseEndPlace(null, true);
    if (!end) return frameChanged && refreshView();
    pushUndo();
    state.project.EndLots = end;
    replan(); recomputeAll();
    await refreshView();
    const ends = pages().map((p, i) => (p.EndLots ? i + 1 : 0)).filter(Boolean);
    status(end.Mode === 'page'
      ? `Lots 0 sur une page « Lots de fin »${ends.length ? ` (page ${ends.join(', ')})` : ' (créée au premier lot 0)'}.`
      : 'Lots 0 à la fin de leur partie, juste avant la ligne TOTAL (Modifier : les glisser ailleurs).');
    return scheduleSave();
  }
  if (choice === 'pages') {
    const chosen = await chooseLotTables();
    if (!chosen) return frameChanged && refreshView();
    pushUndo();
    setLotTables(state.project, chosen);
    if (!lotsActive(state.project) && chosen.size > 0) { createEmptyLots(state.project); await readPrintedLots(); }
    const pagesOn = new Set([...objectLines(state.project)].map((o) => pages().indexOf(o.page) + 1));
    status(pagesOn.size ? `Lots sur ${pagesOn.size > 1 ? 'les pages' : 'la page'} ${[...pagesOn].join(', ')} (les numéros déjà tapés restent).` : 'Plus aucune page avec des lots.');
  } else if (choice === 'number') {
    const start = parseInt($('lotStart')?.value ?? '1', 10);
    if (!(await confirmDialog('Renuméroter tout ?', 'Tous les numéros (tapés ou synchronisés) sont refaits dans l\'ordre affiché, sauf les 0. Pour seulement remplir les cases vides : Synchroniser… (↶ pour revenir en arrière).', 'Renuméroter'))) return frameChanged && refreshView();
    pushUndo();
    numberLots(state.project, Number.isFinite(start) ? start : 1);
    const objs = [...objectLines(state.project)].filter((o) => !isZeroLot(o.line));
    status(`Lots renumérotés de ${objs[0]?.line.Lot ?? ''} à ${objs[objs.length - 1]?.line.Lot ?? ''}.`);
  } else if (choice === 'empty') {
    if (!(await confirmDialog('Vider les cases ?', 'Tous les numéros seront effacés (les 0 aussi) ; vous les taperez vous-même (bouton ↶ pour revenir en arrière).', 'Vider'))) return frameChanged && refreshView();
    pushUndo();
    emptyLots(state.project);
    status(`Cases vidées : ${objects.length} cases à remplir.`);
  } else if (choice === 'sync') {
    return syncDialog();
  } else if (choice === 'clear') {
    if (!(await confirmDialog('Enlever la colonne des lots ?', 'Tous les numéros seront retirés (texte et images).', 'Enlever'))) return frameChanged && refreshView();
    pushUndo();
    clearLots(state.project);
    status('Colonne des lots enlevée.');
  } else return frameChanged && refreshView();
  recomputeAll();
  await refreshView();
  scheduleSave();
}

/** « Mise en page » : le document (nom, pages), réglages de l'écran ; les outils de la case sélectionnée sont dans la barre du haut. */
async function editDialog() {
  const page = currentPage();
  const shown = dialog('Mise en page', `<p>Le document <b>${escapeHtml(state.project.Name)}</b>${page ? `, page ${state.index + 1} sur ${pages().length}` : ''}.</p>
    <label class="switch"><input id="prefLoupe" type="checkbox" ${prefs.loupe ? 'checked' : ''}> Loupe « Original » au-dessus de la case touchée</label>
    <label class="switch"><input id="prefZoom" type="checkbox" ${prefs.caseZoom ? 'checked' : ''}> Zoom automatique de l'iPad quand on touche une case</label>
    <p class="muted">Pour une case : touchez-la, puis en haut <b>Ligne</b> (dupliquer la ligne, séparer ici ; aussi au clic droit ou à l'appui long sur la case), <b>Barrer</b>, <b>Couleur</b>, <b>Type de ligne</b>, <b>Rétablir</b> (et <b>Recalculer</b> pour un total). Sélectionnez quelques mots pour ne barrer ou colorer qu'eux.</p>`, [
    { label: 'Renommer le document', value: 'rename', className: 'accent' },
    { label: 'Liste des pages', value: 'pages' },
    ...(lotsActive(state.project) ? [{ label: 'Synchroniser les lots', value: 'sync' }] : []),
    ...(page ? [{ label: 'Retirer cette page', value: 'remove', className: 'danger' }] : []),
    { label: 'Fermer', value: null },
  ]);
  $('prefLoupe').onchange = (e) => { prefs.loupe = e.target.checked; savePrefs(); status(prefs.loupe ? 'Loupe activée.' : 'Loupe désactivée : plus d\'agrandissement au-dessus de la case.'); };
  $('prefZoom').onchange = (e) => { prefs.caseZoom = e.target.checked; savePrefs(); status(prefs.caseZoom ? 'Zoom automatique de l\'iPad activé.' : 'Zoom automatique de l\'iPad désactivé.'); };
  const choice = await shown;
  if (choice === 'rename') await renameProject();
  else if (choice === 'pages') await pagesDialog();
  else if (choice === 'sync') await syncDialog();
  else if (choice === 'remove') await removePage(page);
}

async function renameProject() {
  const name = await promptDialog('Renommer le document', 'Nom (utilisé pour les fichiers exportés)', state.project.Name);
  if (!name?.trim() || name.trim() === state.project.Name) return;
  pushUndo();
  state.project.Name = name.trim();
  await saveNow();
  updateButtons();
  status(`Document renommé : ${state.project.Name}`);
}

/** Champ de saisie dans une fenêtre (prompt() n'est pas toujours disponible dans l'app installée). */
function promptDialog(title, label, value) {
  const shown = dialog(title, `<label style="flex-direction:column;align-items:stretch">${escapeHtml(label)}<input id="promptValue" type="text" value="${escapeHtml(value)}" style="min-height:48px;font-size:18px;padding:6px 12px;border:1px solid var(--line);border-radius:10px;-webkit-user-select:text;user-select:text"></label>`, [
    { label: 'Annuler', value: null },
    { label: 'OK', value: 'ok', className: 'accent' },
  ]);
  const input = $('promptValue');
  input.focus();
  input.select();
  input.onkeydown = (e) => { if (e.key === 'Enter') { e.preventDefault(); [...$('dialogButtons').children].pop().click(); } };
  return shown.then((v) => (v === 'ok' ? input.value : null));
}

// ---------- Boutons ----------

function initButtons() {
  $('btnPhoto').onclick = () => $('filePhoto').click();
  $('btnImport').onclick = () => {
    if (state.busy) return;
    dialog('Importer', '<p>Photos, PDF, TXT, CSV, XLSX, projet <b>.inventaire</b> ou dossier compressé <b>.zip</b>. Plusieurs fichiers à la fois : dans Fichiers, « Sélectionner » puis touchez-les tous.</p>', [
      { label: 'Fichiers ou photos', className: 'accent', action: () => { $('fileImport').click(); } },
      { label: 'Un dossier entier', action: () => { $('fileFolder').click(); } },
      { label: 'Annuler' },
    ]);
  };
  for (const id of ['filePhoto', 'fileImport', 'fileFolder'])
    $(id).onchange = (e) => {
      const files = [...e.target.files];
      e.target.value = '';
      if (files.length) run(() => importFiles(files));
    };

  $('pagePrev').onclick = () => goTo(state.index - 1);
  $('pageNext').onclick = () => goTo(state.index + 1);
  $('pageLabel').onclick = () => { if (!state.busy) pagesDialog(); };
  $('btnUndo').onclick = () => run(undo);
  document.addEventListener('keydown', (e) => {
    if (e.target.closest?.('input, textarea') || !$('dialog').hidden || !$('crop').hidden) return;
    if ((e.ctrlKey || e.metaKey) && e.key.toLowerCase() === 'z') { e.preventDefault(); run(undo); return; }
    if (e.key === 'ArrowLeft' || e.key === 'PageUp') goTo(state.index - 1);
    if (e.key === 'ArrowRight' || e.key === 'PageDown') goTo(state.index + 1);
  });

  $('btnMove').onclick = () => {
    state.move = !state.move;
    $('btnMove').setAttribute('aria-pressed', String(state.move));
    state.view?.setMoveMode(state.move);
    status(state.move
      ? 'Modifier : glissez une poignée ⋮⋮ vers le haut ou le bas pour déplacer la rangée (poignée en pointillés : une ligne seule d\'un bloc). Retouchez « Modifier » pour terminer.'
      : 'Modifier terminé.');
  };
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
    pushUndo();
    cycleKind(hit.line);
    resyncLots(state.project);
    replan();
    recomputeAll();
    state.field = f;
    refreshView().then(() => { updateEditBar(); status(`Type de ligne : ${kindLabel(hit.line.Kind)}.`); });
    scheduleSave();
  };

  $('btnLine').onclick = () => {
    if (!$('lineMenu').hidden) return hideLineMenu();
    if (state.field && !state.busy) lineMenu(state.field);
  };
  document.addEventListener('pointerdown', (e) => { if (!e.target.closest?.('#lineMenu, #btnLine')) hideLineMenu(); });
  document.addEventListener('keydown', (e) => { if (e.key === 'Escape') hideLineMenu(); });
  window.addEventListener('resize', hideLineMenu);
  $('viewport').addEventListener('scroll', hideLineMenu, { passive: true });

  // Barrer : sur la sélection (toute la case sans sélection) ; même bouton pour enlever le trait.
  for (const id of ['btnLine', 'btnStrike', 'btnStrikeColor', 'btnColor', 'btnRevert', 'btnKind', 'btnRecalc'])
    $(id).addEventListener('pointerdown', (e) => e.preventDefault()); // garde la case active et sa sélection
  const showColor = () => $('btnStrikeColor').classList.toggle('black', state.strikeColor === BLACK);
  try { state.strikeColor = localStorage.getItem('strikeColor') === BLACK ? BLACK : RED; } catch { state.strikeColor = RED; }
  showColor();
  $('btnStrikeColor').onclick = () => {
    state.strikeColor = state.strikeColor === RED ? BLACK : RED;
    try { localStorage.setItem('strikeColor', state.strikeColor); } catch { /* sans importance */ }
    showColor();
    status(`Couleur du trait : ${state.strikeColor === RED ? 'rouge' : 'noir'}.`);
  };
  $('btnStrike').onclick = () => {
    const f = state.field, view = state.view;
    if (!f || !view) return;
    const sel = view.selectionFor(f);
    const [a, b] = sel.end > sel.start ? [sel.start, sel.end] : [0, f.Text.length];
    const was = isStruck(f, a, b);
    pushUndo();
    toggleStrike(f, a, b, state.strikeColor);
    view.refreshStrikes();
    const part = f.Text.slice(a, b).trim();
    status(`${was ? 'Trait retiré' : 'Barré'} : « ${part.length > 40 ? part.slice(0, 40) + '…' : part} ».`);
    scheduleSave();
  };

  // Couleur du texte : rouge, bleu, vert (noir = enlever) sur la sélection, ou toute la case.
  try { state.textColor = localStorage.getItem('textColor') || 'red'; } catch { state.textColor = 'red'; }
  const showTextColor = () => { $('colorIcon').style.borderBottomColor = TEXT_COLORS[state.textColor] ?? '#000'; };
  showTextColor();
  $('btnColor').onclick = () => {
    if (!$('palette').hidden) return hidePalette();
    const r = $('btnColor').getBoundingClientRect(), top = document.querySelector('.top').getBoundingClientRect();
    Object.assign($('palette').style, { left: Math.max(8, Math.min(r.left, innerWidth - 270)) + 'px', top: r.bottom - top.top + 6 + 'px' });
    $('palette').hidden = false;
  };
  for (const b of $('palette').querySelectorAll('button')) {
    b.addEventListener('pointerdown', (e) => e.preventDefault());
    b.onclick = () => {
      hidePalette();
      const f = state.field, view = state.view;
      if (!f || !view) return;
      const color = b.dataset.color;
      state.textColor = TEXT_COLORS[color] ? color : state.textColor;
      try { localStorage.setItem('textColor', state.textColor); } catch { /* sans importance */ }
      showTextColor();
      const sel = view.selectionFor(f);
      const [a, z] = sel.end > sel.start ? [sel.start, sel.end] : [0, f.Text.length];
      pushUndo();
      const on = setColor(f, a, z, color);
      if (isModified(f)) view.refreshColors(); else redraw(view);
      const part = f.Text.slice(a, z).trim();
      status(`${on ? `En ${b.textContent.toLowerCase()}` : 'Couleur retirée'} : « ${part.length > 40 ? part.slice(0, 40) + '…' : part} ».`);
      scheduleSave();
    };
  }
  document.addEventListener('pointerdown', (e) => { if (!e.target.closest?.('#palette, #btnColor')) hidePalette(); });

  $('btnRevert').onclick = () => {
    const f = state.field;
    if (!f || !state.view) return;
    pushUndo();
    if (isComputedCell(state.view.lineOf.get(f), f)) f.Manual = true; // total : on garde le montant imprimé
    state.view.revert(f);
    afterEdit(state.view, f);
    updateEditBar();
    scheduleSave();
  };

  $('btnRecalc').onclick = () => {
    const f = state.field, view = state.view;
    if (!f || !view) return;
    pushUndo();
    f.Manual = false;
    const changed = recomputeAll();
    view.syncFields([f, ...changed]);
    const input = view.inputs.get(f);
    if (input) { input.value = f.Text; view.layout(input, f); view.visual(input, f, document.activeElement === input); }
    updateEditBar();
    status(`Total recalculé : ${f.Text || '(vide)'}.`);
    scheduleSave();
  };

  $('btnCrop').onclick = () => run(async () => {
    const page = currentPage();
    if (page?.Source !== 'Image') return;
    const src = await orientedSource(page);
    if (!src) return;
    const result = await cropDialog(src, page.Crop);
    if (!result) return status('Recadrage annulé.');
    if ([...allFields(page)].some(isModified) && !(await confirmDialog('Recadrer la page ?', 'Le texte de cette page va être relu. Les numéros de lot et les corrections des lignes retrouvées sont gardés ; les lignes ajoutées sont perdues.'))) return;
    const fresh = newPage({ Id: page.Id, Source: 'Image', SourceName: page.SourceName, SourceEntry: page.SourceEntry, QuarterTurns: (page.QuarterTurns + result.turns) % 4 });
    const { flat } = await processImage(fresh, result.source, result.quad, (f, text) => { progress(f); status(`Recadrage : ${text}`); });
    const i = pages().indexOf(page);
    if (i < 0) return;
    pushUndo();
    const kept = carryOver(page, fresh);
    for (const l of allLines(fresh)) setExtra(fresh, l, lineExtra(l)); // désignations tapées sur plusieurs lignes
    pages()[i] = fresh;
    state.flats.delete(page); state.oriented.delete(page);
    remember(state.oriented, fresh, result.source, 1);
    resyncLots(state.project);
    replan();
    recomputeAll();
    await showPage(i, { flat });
    status(`Page recadrée et relue : ${kept} ligne(s) retrouvée(s) avec leur numéro de lot et leurs corrections. Vérifiez les zones à verrouiller.`);
    scheduleSave();
  });

  $('btnLots').onclick = () => run(lotsDialog);
  $('btnEdit').onclick = () => run(editDialog);
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
    await renameProject();
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

applyPrefs();
window.addEventListener('DOMContentLoaded', start);
window.__app = { state }; // essais automatiques
export { state };
