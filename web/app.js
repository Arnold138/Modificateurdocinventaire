// Retouche d'inventaire — version iPhone / iPad (application web installable, hors ligne).
// Même logique que la version Windows : importer → corriger sur la page → exporter JPEG ou .txt.
// Une seule page est affichée à la fois (navigation « 3 / 10 ») : les longs inventaires restent légers en mémoire.
import { Quad } from './core/geometry.js';
import { normalize, render, rotate90 } from './core/imaging.js';
import { detectGrids } from './core/rules.js';
import { prepare, mapBack, emptyFieldsWithInk, prepareCell, applyCellWords } from './core/ocrprep.js';
import { newPage, newProject, analyze, reclassify, allFields, isModified, cycleKind, kindLabel, findLine, firstText, upgradePage, carryOver, allLines } from './core/model.js';
import { buildTxt, txtBlob, composeIntoSource, outputName } from './core/exports.js';
import { decodeText, parseDelimited, readXlsx, buildDataPage } from './core/importers.js';
import { packProject, unpackProject, EXTENSION } from './core/store.js';
import { objectLines, lotsActive, numberLots, clearLots, setLot, resyncLots, removeLot, addLot, repairName, naturalCompare, createEmptyLots, fillLotsAutomatically, emptyLots, isZeroLot, rowOrder, lotTables, defaultLotChoice, setLotTables } from './core/lots.js';
import { unzipSync } from './vendor/fflate.js';
import { stretch, setExtra, baseHeight, totalExtra } from './core/layout.js';
import { toggleStrike, isStruck, setColor, RED, BLACK, TEXT_COLORS } from './core/strikes.js';
import { planArrangement, viewOf, composePage, unitCrop, pageArranged, mapY, unmapY } from './core/arrange.js';
import { recomputeTotals, isComputedCell, computedValue } from './core/totals.js';
import { frameSignature, lotFrameEnabled } from './core/lotcolumn.js';
import { canCopyLine, canSplit, addedRowOf, copyLine, addBlankLine, locate, splitAt, splitTypedText, removeAddedRow } from './core/rows.js';
import { decodeImage, pdfPages, isImageName, mimeOf } from './services/decode.js';
import { recognize, warmUp } from './services/ocr.js';
import { rasterToCanvas, canvasToBlob, renderEdits, freeCanvas, lineExtra, pageRaster } from './services/canvas.js';
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
  plan: null,           // mise en page : lots 0 recollés après le dernier lot, rangées déplacées
  crops: new Map(),     // morceaux d'image des lots 0 (pour recomposer la page où ils arrivent)
  undo: [],             // états précédents du projet (bouton ↶)
  editSession: null,    // case en cours de frappe (un seul retour en arrière pour toute la saisie)
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
  document.querySelector('.page-head .sync')?.toggleAttribute('hidden', !lotsActive(state.project));
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

const hidePalette = () => { $('palette').hidden = true; };

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

// ---------- Retour en arrière ----------

/** Mémorise l'état du projet avant une action (30 derniers états). */
function pushUndo() {
  state.undo.push(JSON.stringify(state.project));
  if (state.undo.length > 30) state.undo.shift();
  updateButtons();
}

/** Clé de l'image d'une page : si elle n'a pas changé, l'image en mémoire reste valable. */
const imageKey = (p) => JSON.stringify([p.Crop, p.Skew, p.QuarterTurns, p.Width, baseHeight(p), p.SourceEntry]);

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

/** Recalcule la mise en page (lots 0, rangées déplacées). */
function replan() {
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
  const own = await ownDisplay(page);
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
  title.textContent = `Page ${state.index + 1} sur ${list.length} · ${page.SourceName}`;
  const remove = document.createElement('button');
  remove.textContent = 'Retirer la page';
  remove.onclick = () => removePage(page);
  const sync = document.createElement('button');
  sync.className = 'sync';
  sync.innerHTML = '<svg><use href="#i-sync"/></svg>Synchroniser automatiquement';
  sync.onclick = () => { pushUndo(); syncLots(); };
  head.append(title, sync, remove);

  const pp = () => state.plan.pages[k];
  const view = new PageView(viewOf(state.project, state.plan, k), display, {
    lotsActive: () => lotsActive(state.project),
    zoom: () => state.zoom,
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
    onLot: (v, line, value) => {
      pushUndo();
      const wasZero = isZeroLot(line), sig = frameSignature(v.page, lotFrameEnabled(state.project));
      setLot(state.project, line, value);
      const zero = isZeroLot(line);
      replan();
      recomputeAll();
      if (zero !== wasZero || sig !== frameSignature(v.page, lotFrameEnabled(state.project))) refreshView();
      else v.updateLots();
      if (zero && !wasZero) {
        const d = state.plan.dest;
        status(d ? `Lot 0 : « ${firstText(line)} » est passé après le dernier lot (page ${d.p + 1}). Retapez un numéro pour le remettre à sa place.` : 'Lot 0 : aucun autre lot numéroté, la ligne reste en place.');
      } else if (wasZero && !zero) status(`Lot ${line.Lot} : « ${firstText(line)} » est revenu à sa place.`);
      else status(line.Lot ? `Lot ${line.Lot} enregistré.` : 'Case de lot vidée.');
      scheduleSave();
    },
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
      addLot(state.project, line);
      refreshView();
      status('Case de lot ajoutée : tapez le numéro.');
      scheduleSave();
    },
    onLineMenu: (v, f, at) => { if (!state.busy) lineMenu(f, at); },
    onMoveRow: (v, table, from, before) => {
      pushUndo();
      const order = rowOrder(table).filter((r) => r !== from);
      order.splice(before === null ? order.length : order.indexOf(before), 0, from);
      table.RowOrder = order;
      refreshView();
      status('Rangée déplacée. ↶ pour annuler.');
      scheduleSave();
    },
  });
  view.baseFlat = state.flats.get(page) ?? null;
  view.setLockMode(state.lock);
  state.view = view;
  $('pages').append(head, view.row);
  if (scroll) { applyZoom(state.zoom); vp.scrollLeft = scroll.x; vp.scrollTop = scroll.y; }
  else { vp.scrollTop = 0; if (fit) requestAnimationFrame(fitWidth); else applyZoom(state.zoom); }
  if (focusedField && view.inputs.has(focusedField)) { state.field = focusedField; view.focus(focusedField); }
  if (!keepScroll)
    status(page.Source === 'Image'
      ? 'Touchez une case pour la corriger ; l\'original s\'affiche en loupe au-dessus.'
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
  if (canCopyLine(page, line)) {
    items.push({ act: 'blankBelow', icon: 'i-addbelow', label: 'Ajouter une ligne vide en dessous', hint: 'Rangée complète (case de lot, désignation, prix) sous cette rangée. La suite descend.' });
    if (locate(page, line)?.r > 0)
      items.push({ act: 'blankAbove', icon: 'i-addabove', label: 'Ajouter une ligne vide au-dessus', hint: 'Rangée complète (case de lot, désignation, prix) au-dessus de cette rangée.' });
    items.push({ act: 'copy', icon: 'i-copy', label: 'Dupliquer la ligne', hint: 'Copie juste en dessous (texte et prix), à corriger. La suite descend.' });
  }
  if (typedRest && canCopyLine(page, line))
    items.push({ act: 'typed', from: typed, icon: 'i-split', label: 'Séparer ici', hint: `« ${short(typedRest)} » passe dans sa propre ligne, juste en dessous.` });
  else if (canSplit(page, line))
    items.push({ act: 'split', icon: 'i-split', label: 'Séparer ici', hint: 'Cette ligne devient une rangée à part, avec sa case de lot et ses prix.' });
  const added = addedRowOf(page, line);
  if (added?.band.Kind === 'Split' && added.row.Lines[0] === line)
    items.push({ act: 'merge', icon: 'i-merge', label: 'Remettre dans la rangée du dessus', hint: 'Annule « Séparer ici » : le trait ajouté disparaît.' });
  if (added?.band.Kind === 'Copy')
    items.push({ act: 'remove', icon: 'i-trash', label: 'Supprimer cette ligne ajoutée', hint: 'La ligne copiée disparaît, la suite remonte.', danger: true });
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
  const name = short(firstText(line));
  pushUndo();
  let show = null, msg = '';
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
  if (['copy', 'typed', 'split', 'blankBelow', 'blankAbove'].includes(item.act) && !show) {
    state.undo.pop();
    updateButtons();
    status('Impossible ici : choisissez une ligne d\'article d\'un tableau.');
    return;
  }
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

/** « Synchroniser automatiquement » : le logiciel remplit toutes les cases (les numéros tapés restent des repères). */
function syncLots() {
  fillLotsAutomatically(state.project);
  recomputeAll();
  refreshView();
  const objs = [...objectLines(state.project)].filter((o) => !isZeroLot(o.line));
  status(objs.length ? `Lots synchronisés : ${objs.length} objets, de ${objs[0].line.Lot} à ${objs[objs.length - 1].line.Lot}.` : 'Aucun objet à numéroter.');
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
    const before = [...objectLines(state.project)].length;
    resyncLots(state.project);
    lots = [...objectLines(state.project)].length !== before;
  }
  if (grew || lots) redraw(view);
  else if (changed.length) view.syncFields(changed);
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
  return render(src, page.Crop ?? Quad.full(src.w, src.h), page.Skew, page.Width, baseHeight(page));
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

async function exportJpeg(only = null) {
  const plan = replan();
  recomputeAll();
  const all = pages().map((page, k) => ({ page, k })).filter((x) => x.page.Source === 'Image');
  const targets = only ? all.filter((x) => x.page === only) : all;
  // Lots 0 : leurs morceaux sont pris sur leur page (corrections comprises), puis recollés après le dernier lot.
  const crops = new Map();
  const unitPages = [...new Set(targets.flatMap(({ k }) => plan.pages[k].pieces.filter((pc) => pc.kind === 'unit').map((pc) => pc.unit.p)))];
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
    const edited = await editedOf(page);
    if (!edited) continue;
    let final = edited;
    const arranged = pageArranged(state.project, plan, k);
    if (arranged) final = composePage(state.project, plan, k, edited, crops);
    // Page qui garde sa forme : la photo d'origine, inchangée sauf dans les cases modifiées.
    if (!arranged && totalExtra(page) === 0) {
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
  status(`${files.length} image(s) prête(s).`);
  const zero = plan.units.length > 0 ? ` ${plan.units.length} lot(s) 0 placé(s) après le dernier lot (page ${plan.dest.p + 1}).` : '';
  await deliver(files, files.length > 1 ? 'Images prêtes' : 'Image prête', "<p>Chaque photo d'origine, inchangée sauf dans les cases modifiées" + (lotsActive(state.project) ? ' et la colonne des numéros de lot' : '') + '.' + zero + '</p>');
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

async function lotsDialog() {
  if (!lotsActive(state.project)) {
    const chosen = await chooseLotTables();
    if (!chosen) return;
    if (chosen.size === 0) return status('Aucune page cochée : pas de numéros de lot.');
    pushUndo();
    setLotTables(state.project, chosen);
    createEmptyLots(state.project);
    const n = [...objectLines(state.project)].length;
    const pagesOn = new Set([...objectLines(state.project)].map((o) => pages().indexOf(o.page) + 1));
    await refreshView();
    status(`Colonne des lots créée sur ${pagesOn.size > 1 ? 'les pages' : 'la page'} ${[...pagesOn].join(', ')} : ${n} cases vides. Touchez une case pour taper un numéro (0 = absent : la ligne passe après le dernier lot).`);
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
    <p class="muted"><b>Synchroniser automatiquement</b> remplit toutes les cases d'un coup (les numéros tapés restent, la suite repart d'eux).</p>
    <label class="switch"><input id="lotFrame" type="checkbox" ${lotFrameEnabled(state.project) ? 'checked' : ''}> Tracer la colonne dans le tableau (traits et fond comme le tableau imprimé)</label>
    <label>Renuméroter à partir de <input id="lotStart" type="number" inputmode="numeric" min="0" value="${/^\d+$/.test(first) ? first : 1}"></label>`;
  const shown = dialog('Numéros de lot', html, [
    { label: 'Synchroniser automatiquement', value: 'sync', className: 'accent' },
    { label: 'Pages et tableaux…', value: 'pages' },
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
  if (choice === 'pages') {
    const chosen = await chooseLotTables();
    if (!chosen) return frameChanged && refreshView();
    pushUndo();
    setLotTables(state.project, chosen);
    if (!lotsActive(state.project) && chosen.size > 0) createEmptyLots(state.project);
    const pagesOn = new Set([...objectLines(state.project)].map((o) => pages().indexOf(o.page) + 1));
    status(pagesOn.size ? `Lots sur ${pagesOn.size > 1 ? 'les pages' : 'la page'} ${[...pagesOn].join(', ')} (les numéros déjà tapés restent).` : 'Plus aucune page avec des lots.');
  } else if (choice === 'number') {
    pushUndo();
    const start = parseInt($('lotStart')?.value ?? '1', 10);
    numberLots(state.project, Number.isFinite(start) ? start : 1);
    const objs = [...objectLines(state.project)].filter((o) => !isZeroLot(o.line));
    status(`Lots renumérotés de ${objs[0]?.line.Lot ?? ''} à ${objs[objs.length - 1]?.line.Lot ?? ''}.`);
  } else if (choice === 'empty') {
    if (!(await confirmDialog('Vider les cases ?', 'Tous les numéros seront effacés (les 0 aussi) ; vous les taperez vous-même (bouton ↶ pour revenir en arrière).', 'Vider'))) return frameChanged && refreshView();
    pushUndo();
    emptyLots(state.project);
    status(`Cases vidées : ${objects.length} cases à remplir.`);
  } else if (choice === 'sync') {
    pushUndo();
    return syncLots();
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

/** « Modifier » : le document (nom, pages) ; les outils de la case sélectionnée sont dans la barre du haut. */
async function editDialog() {
  const page = currentPage();
  const choice = await dialog('Modifier', `<p>Le document <b>${escapeHtml(state.project.Name)}</b>${page ? `, page ${state.index + 1} sur ${pages().length}` : ''}.</p>
    <p class="muted">Pour une case : touchez-la, puis en haut <b>Ligne</b> (dupliquer la ligne, séparer ici ; aussi au clic droit ou à l'appui long sur la case), <b>Barrer</b>, <b>Couleur</b>, <b>Type de ligne</b>, <b>Rétablir</b> (et <b>Recalculer</b> pour un total). Sélectionnez quelques mots pour ne barrer ou colorer qu'eux.</p>`, [
    { label: 'Renommer le document', value: 'rename', className: 'accent' },
    { label: 'Liste des pages', value: 'pages' },
    ...(lotsActive(state.project) ? [{ label: 'Synchroniser les lots', value: 'sync' }] : []),
    ...(page ? [{ label: 'Retirer cette page', value: 'remove', className: 'danger' }] : []),
    { label: 'Fermer', value: null },
  ]);
  if (choice === 'rename') await renameProject();
  else if (choice === 'pages') await pagesDialog();
  else if (choice === 'sync') { pushUndo(); syncLots(); }
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

window.addEventListener('DOMContentLoaded', start);
window.__app = { state }; // essais automatiques
export { state };
