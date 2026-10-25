// Une page à l'écran : l'image (recomposée si des rangées ont bougé) et, par-dessus, un champ par case à sa place.
// Case non modifiée : invisible (opacité 0, toujours touchable) → on ne voit que l'original.
// Case modifiée ou en cours d'édition : fond couleur papier + texte noir (ou en couleur), même taille que l'imprimé.
// Les cases sont placées d'après la mise en page (arrange.js) : un lot 0 recollé en fin d'inventaire s'édite là où il est affiché.
import { Rect } from '../core/geometry.js';
import { isModified, addLockZone, removeLockZoneAt, looksLikeTotal } from '../core/model.js';
import { paperColor } from '../core/exports.js';
import { canAddLot, hasLotCell, isZeroLot } from '../core/lots.js';
import { font, lotSize, textLayout, isMultiline, LINE_HEIGHT, strikeSegments, rasterToCanvas, freeCanvas, cleanPatch } from '../services/canvas.js';
import { adjustStrikes, colorRuns, hasColors, textColor } from '../core/strikes.js';
import { baseBox } from '../core/layout.js';
import { isComputedCell } from '../core/totals.js';

const BADGES = { Titre: ['T', '#0067c0'], Total: ['Σ', '#107c10'], EnTete: ['E', '#606060'], Ignoree: ['×', '#b42828'] };
const el = (tag, cls, parent) => { const e = document.createElement(tag); if (cls) e.className = cls; parent?.appendChild(e); return e; };
const px = (e, r) => { Object.assign(e.style, { left: r.X + 'px', top: r.Y + 'px', width: r.Width + 'px', height: r.Height + 'px' }); return e; };
const escapeHtml = (s) => String(s).replace(/[&<>"]/g, (c) => ({ '&': '&amp;', '<': '&lt;', '>': '&gt;', '"': '&quot;' })[c]);

export class PageView {
  /**
   * @param view ce qu'affiche la page (viewOf) ; @param display image affichée (null pour TXT/CSV/XLSX) ;
   * cb : rappels (onFocus, onEdited, onLot, onLotRemove, onLotAdd, onMoveRow, onLocks, onLineMenu, beforeEdit, beforeChange),
   * lotsActive, zoom, mapY / unmapY (zones verrouillées : page d'origine ↔ écran).
   */
  constructor(view, display, cb) {
    this.cb = cb;
    this.inputs = new Map();
    this.lotInputs = new Map();
    this.colorBoxes = new Map();
    this.paper = new Map();
    this.selection = null; // dernière sélection dans une case (pour « Barrer » et « Couleur »)
    this.setView(view);
    this.image = display;

    // Colonne de gauche, en dehors de la page : poignées, type de ligne, poubelle / « + » des lots.
    this.row = el('div', 'page-row');
    this.gutter = el('div', 'gutter', this.row);
    this.root = el('div', 'page', this.row);
    this.size();
    if (display) {
      this.canvas = rasterToCanvas(display);
      this.canvas.className = 'bg';
      this.root.appendChild(this.canvas);
    } else this.drawGrid();
    this.fields = el('div', 'layer', this.root);
    this.colors = el('div', 'layer colors', this.root);
    this.lots = el('div', 'layer lots', this.root);
    this.strikes = el('div', 'layer strikes', this.root);
    this.overlay = el('div', 'layer overlay', this.root);
    this.lockpad = el('div', 'layer lockpad', this.root);
    this.initLockPad();
    // Clic droit : la case qui vient de prendre le focus fait apparaître la barre de la case et la loupe, qui peuvent
    // se trouver sous le pointeur au moment du menu ; on retient donc la case réellement appuyée (bouton droit).
    this.onContext = (e) => {
      const press = this.rightPress && performance.now() - this.rightPress.t < 1500 ? this.rightPress : null;
      this.rightPress = null;
      const direct = [...this.inputs].find(([, i]) => i === e.target)?.[0] ?? null;
      const target = press?.f ?? direct;
      if (!target || press?.selected) return; // texte déjà sélectionné : menu habituel (copier, coller)
      e.preventDefault();
      this.rememberSel?.get(target)?.();
      this.cb.onLineMenu?.(this, target, { x: e.clientX, y: e.clientY });
    };
    document.addEventListener('contextmenu', this.onContext);
    this.build();
  }

  get page() { return this.view.page; }

  setView(view) {
    this.view = view;
    this.place = new Map();
    this.lineOf = new Map();
    for (const entry of view.lines)
      for (const pl of entry.cells) { this.place.set(pl.f, { ...pl, entry }); this.lineOf.set(pl.f, entry.line); }
  }

  size() {
    Object.assign(this.root.style, { width: this.view.width + 'px', height: this.view.height + 'px' });
    this.gutter.style.height = this.view.height + 'px';
  }

  /** Libère la mémoire de l'image (important sur iPhone/iPad). */
  dispose() {
    document.removeEventListener('contextmenu', this.onContext);
    freeCanvas(this.canvas);
    this.row.remove();
  }

  setDisplay(display) {
    this.image = display;
    this.paper.clear();
    if (!this.canvas || !display) return;
    const c = rasterToCanvas(display);
    c.className = 'bg';
    this.canvas.replaceWith(c);
    freeCanvas(this.canvas);
    this.canvas = c;
  }

  build() {
    this.drawLockZones();
    for (const [f, pl] of this.place) if (!f.Locked) this.addField(f, pl);
    this.loupe = el('div', 'loupe', this.overlay);
    this.refreshLots();
    this.refreshStrikes();
    this.refreshColors();
  }

  refresh() {
    this.fields.replaceChildren();
    this.overlay.replaceChildren();
    this.colors.replaceChildren();
    this.inputs.clear();
    this.colorBoxes.clear();
    this.build();
  }

  /**
   * Nouvelle mise en page (rangée agrandie, total recalculé…) : tout est replacé sans fermer le clavier.
   * Si d'autres cases apparaissent ou disparaissent, la page est reconstruite.
   */
  relayout(view, display) {
    const before = [...this.place.keys()];
    this.setView(view);
    const same = before.length === this.place.size && before.every((f) => this.place.has(f));
    this.size();
    if (display) this.setDisplay(display);
    else if (!this.canvas) { this.root.querySelector('svg.grid')?.remove(); this.drawGrid(); }
    this.paper.clear();
    if (!same) return this.refresh();
    for (const [f, input] of this.inputs) {
      this.layout(input, f);
      this.visual(input, f, input === document.activeElement);
    }
    this.drawLockZones();
    this.updateLots();
    this.refreshStrikes();
    this.refreshColors();
  }

  setLockMode(on) { this.lockpad.classList.toggle('active', on); }

  /** Mode « Modifier » : poignées ⋮⋮ affichées tout de suite ; la poubelle et le « + » des lots sont masqués pendant ce temps. */
  setMoveMode(on) {
    this.moveMode = !!on;
    this.gutter.classList.toggle('moving', this.moveMode);
    this.refreshGutter();
  }

  /** Textes changés ailleurs (totaux recalculés) : les cases affichées suivent. */
  syncFields(fields) {
    for (const f of fields) {
      const input = this.inputs.get(f);
      if (!input || input === document.activeElement) continue;
      input.value = f.Text;
      this.layout(input, f);
      this.visual(input, f, false);
    }
    this.refreshStrikes();
    this.refreshColors();
  }

  revert(f) {
    adjustStrikes(f, f.Text, f.OriginalText);
    f.Text = f.OriginalText;
    const input = this.inputs.get(f);
    if (input) { input.value = f.Text; this.layout(input, f); this.visual(input, f, document.activeElement === input); }
    this.refreshStrikes();
    this.refreshColors();
  }

  focus(f) { this.inputs.get(f)?.focus({ preventScroll: true }); }
  entryOf(f) { return this.place.get(f)?.entry ?? null; }

  // ---------- Zones verrouillées ----------

  drawLockZones() {
    for (const z of this.overlay.querySelectorAll('.lockzone')) z.remove();
    for (const z of this.page.LockZones) {
      const y = this.cb.mapY?.(z.Y) ?? z.Y;
      if (y === null) continue;
      const d = px(el('div', 'lockzone', this.overlay), { ...z, Y: y });
      d.textContent = '🔒';
      this.overlay.prepend(d);
    }
  }

  // ---------- Numéros de lot ----------

  /** Numéros écrits sur la page, dans la colonne (modifiables au doigt), puis la colonne de gauche. */
  refreshLots() {
    const focused = [...this.lotInputs].find(([, i]) => i === document.activeElement)?.[0];
    this.lots.replaceChildren();
    this.lotInputs.clear();
    for (const entry of this.view.lines) {
      if (!entry.lotBox) continue;
      const { line, lotBox: box } = entry;
      const input = el('input', 'lotfield', this.lots);
      input.type = 'text';
      input.inputMode = 'numeric';
      input.autocomplete = 'off';
      input.value = line.Lot;
      input.setAttribute('aria-label', 'Numéro de lot');
      px(input, box);
      input.style.font = font(lotSize(line, box));
      input.style.lineHeight = box.Height + 'px';
      input.style.background = 'transparent';
      // Le numéro est enregistré quand on quitte la case (Entrée ou toucher ailleurs) ; rien d'autre ne change.
      const commit = () => {
        if (input.value.trim() !== line.Lot) this.cb.onLot?.(this, line, input.value);
      };
      input.addEventListener('change', commit);
      input.addEventListener('blur', commit);
      input.addEventListener('keydown', (e) => { if (e.key === 'Enter') { e.preventDefault(); input.blur(); } });
      this.lotInputs.set(line, input);
    }
    if (focused && this.lotInputs.has(focused)) this.lotInputs.get(focused).focus({ preventScroll: true });
    this.refreshGutter();
  }

  updateLots() {
    const boxes = this.view.lines.filter((e) => e.lotBox);
    const same = boxes.length === this.lotInputs.size && boxes.every((e) => this.lotInputs.has(e.line));
    if (!same) return this.refreshLots();
    for (const { line, lotBox: box } of boxes) {
      const i = this.lotInputs.get(line);
      px(i, box);
      i.style.lineHeight = box.Height + 'px';
      if (i !== document.activeElement) i.value = line.Lot;
      i.style.font = font(lotSize(line, box));
    }
    this.refreshGutter();
  }

  /** Repère de hauteur d'une ligne à l'écran : sa 1re ligne de texte. */
  lineBand(entry) {
    const c = entry.cells[0];
    if (!c) return null;
    const b = baseBox(entry.line, c.f);
    return { ...b, X: b.X + c.dx, Y: b.Y + c.dy };
  }

  refreshGutter() {
    this.gutter.replaceChildren();
    const active = (this.cb.lotsActive?.() ?? false) && !this.moveMode;
    for (const entry of this.view.lines) {
      const { line } = entry;
      if (line.OutsideTable || entry.carriedOnly || line.Cells.length === 0) continue;
      const b = this.lineBand(entry);
      if (!b) continue;
      const badge = BADGES[line.Kind];
      if (badge) {
        const d = el('div', 'badge', this.gutter);
        d.textContent = badge[0];
        d.style.background = badge[1];
        d.style.top = Rect.cy(b) - 12 + 'px';
      }
      if (!active || entry.table.LotsOff) continue;
      const has = hasLotCell(line);
      if (!has && !canAddLot(line, entry.table.Rows?.find((r) => r.Lines.includes(line)))) continue;
      const btn = el('button', has ? 'gbtn trash' : 'gbtn add', this.gutter);
      btn.innerHTML = `<svg><use href="#${has ? 'i-trash' : 'i-plus'}"/></svg>`;
      btn.setAttribute('aria-label', has ? `Retirer la case de lot ${line.Lot}` : 'Ajouter une case de lot');
      btn.title = has ? 'Pas un objet : retirer cette case' : 'Ajouter une case de lot sur cette ligne';
      const h = Math.max(26, Math.min(40, b.Height + 6));
      Object.assign(btn.style, { top: Rect.cy(b) - h / 2 + 'px', height: h + 'px' });
      btn.onclick = () => (has ? this.cb.onLotRemove : this.cb.onLotAdd)?.(this, line);
    }
    this.drawHandles();
  }

  // ---------- Déplacer une rangée (poignée ⋮⋮ à gauche) ----------

  drawHandles() {
    if (!this.moveMode || !this.cb.onMoveRow) return;
    for (const vt of this.view.tables) {
      // Lots 0 recollés ici (fin de partie ou endroit choisi) : leur poignée les replace où on veut dans ce tableau.
      for (const u of vt.units ?? []) {
        const g = el('div', 'grip unit', this.gutter);
        g.innerHTML = '<svg><use href="#i-grip"/></svg>';
        g.title = 'Lot 0 : glisser pour le placer ailleurs dans ce tableau';
        g.setAttribute('aria-label', 'Déplacer ce lot 0');
        const gh = Math.max(24, Math.min(40, u.bottom - u.top - 4));
        Object.assign(g.style, { top: (u.top + u.bottom) / 2 - gh / 2 + 'px', height: gh + 'px' });
        this.initDrag(g, vt, { r: -1, top: u.top, bottom: u.bottom }, null, u.unit);
      }
      if (vt.strips.length < 2) continue;
      for (const s of vt.strips) {
        const row = vt.table.Rows[s.r];
        if (!row || row.Lines.length === 0 || row.Lines.every((l) => l.Kind === 'EnTete') || row.Lines.some(looksLikeTotal)) continue; // ni en-tête ni total
        if (row.Lines.every((l) => l.Cells.every((c) => !c.Text.trim()))) continue;
        // Bloc de plusieurs lignes : la poignée de la 1re ligne déplace le bloc, celle de chaque autre ligne déplace cette ligne seule.
        const bands = row.Lines.filter((l) => !l.Deleted && l.Cells.some((c) => c.Text.trim()))
          .map((l) => ({ l, e: this.view.lines.find((x) => x.line === l && !x.carriedOnly) })).filter((x) => x.e)
          .map((x) => ({ l: x.l, b: this.lineBand(x.e) })).filter((x) => x.b && Rect.cy(x.b) > s.top && Rect.cy(x.b) < s.bottom);
        const block = bands.length > 1;
        const h = el('div', 'grip', this.gutter);
        h.innerHTML = '<svg><use href="#i-grip"/></svg>';
        h.title = block ? 'Glisser pour déplacer le bloc' : 'Glisser pour déplacer la rangée';
        h.setAttribute('aria-label', h.title);
        const hh = block ? Math.max(24, Math.min(36, bands[0].b.Height + 6)) : Math.max(24, Math.min(40, s.bottom - s.top - 4));
        const cy = block ? Rect.cy(bands[0].b) : (s.top + s.bottom) / 2;
        Object.assign(h.style, { top: cy - hh / 2 + 'px', height: hh + 'px' });
        this.initDrag(h, vt, s);
        for (const { l, b } of bands.slice(1)) {
          const g = el('div', 'grip line', this.gutter);
          g.innerHTML = '<svg><use href="#i-grip"/></svg>';
          g.title = 'Glisser pour déplacer cette ligne seule (elle devient une rangée)';
          g.setAttribute('aria-label', 'Déplacer cette ligne');
          const gh = Math.max(24, Math.min(36, b.Height + 6));
          Object.assign(g.style, { top: Rect.cy(b) - gh / 2 + 'px', height: gh + 'px' });
          this.initDrag(g, vt, { r: s.r, top: Math.max(s.top, b.Y - 4), bottom: Math.min(s.bottom, Rect.bottom(b) + 4) }, l);
        }
      }
    }
  }

  /** Glisser une poignée : rangée entière, ou une ligne seule (`line`) qui devient une rangée à l'endroit choisi. */
  initDrag(handle, vt, strip, line = null, unit = null) {
    let marker = null, ghost = null, target = null;
    const yOf = (e) => (e.clientY - this.root.getBoundingClientRect().top) / this.cb.zoom();
    handle.addEventListener('pointerdown', (e) => {
      e.preventDefault();
      handle.setPointerCapture(e.pointerId);
      ghost = px(el('div', 'dragghost', this.overlay), Rect.fromEdges(0, strip.top, this.view.width, strip.bottom));
      marker = el('div', 'dropline', this.overlay);
      Object.assign(marker.style, { left: '0px', width: this.view.width + 'px', display: 'none' });
    });
    handle.addEventListener('pointermove', (e) => {
      if (!marker) return;
      const y = yOf(e);
      ghost.style.top = y - (strip.bottom - strip.top) / 2 + 'px';
      // Point d'insertion : avant la première rangée dont le milieu est sous le doigt.
      const i = vt.strips.findIndex((s) => y < (s.top + s.bottom) / 2);
      target = i < 0 ? { before: null } : { before: vt.strips[i].r };
      const at = i < 0 ? vt.strips[vt.strips.length - 1].bottom : vt.strips[i].top;
      Object.assign(marker.style, { top: at - 2 + 'px', display: 'block' });
    });
    const end = (ok) => {
      marker?.remove(); ghost?.remove();
      marker = ghost = null;
      const t = target;
      target = null;
      if (!ok || !t) return;
      if (unit) return this.cb.onMoveUnit?.(this, vt.table, unit, t.before);
      if (line) return this.cb.onMoveLine?.(this, vt.table, line, t.before);
      if (t.before === strip.r) return;
      const next = vt.strips[vt.strips.findIndex((s) => s.r === strip.r) + 1];
      if (next && t.before === next.r) return; // même place
      this.cb.onMoveRow?.(this, vt.table, strip.r, t.before);
    };
    handle.addEventListener('pointerup', () => end(true));
    handle.addEventListener('pointercancel', () => end(false));
  }

  // ---------- Cases ----------

  paperOfBox(box) {
    if (!this.image) return 'transparent';
    const [r, g, b] = paperColor(this.image, box);
    return `rgb(${r},${g},${b})`;
  }

  /** Traits des morceaux barrés, posés par-dessus le texte (scan ou texte retapé). */
  refreshStrikes() {
    this.strikes.replaceChildren();
    for (const [f, pl] of this.place)
      for (const g of strikeSegments(this.lineOf.get(f), f)) {
        const d = el('div', 'strike', this.strikes);
        Object.assign(d.style, { left: g.x0 + pl.dx + 'px', width: g.x1 - g.x0 + 'px', top: g.y + pl.dy - g.thick / 2 + 'px', height: g.thick + 'px', background: g.color });
      }
  }

  /** Texte retapé avec des couleurs : écrit par-dessus la case (une zone de saisie ne sait écrire qu'en noir). */
  refreshColors() {
    for (const [f, input] of this.inputs) this.drawColors(f, input, input === document.activeElement);
  }

  drawColors(f, input, focused) {
    const show = !focused && isModified(f) && hasColors(f) && f.Text.length > 0;
    let box = this.colorBoxes.get(f);
    input.style.color = show ? 'transparent' : '#000';
    if (!show) { box?.remove(); this.colorBoxes.delete(f); return; }
    if (!box) { box = el('div', 'colortext', this.colors); this.colorBoxes.set(f, box); }
    for (const k of ['left', 'top', 'width', 'height', 'font', 'lineHeight', 'paddingTop', 'paddingLeft', 'paddingRight', 'textAlign', 'whiteSpace'])
      box.style[k] = input.style[k] || getComputedStyle(input)[k];
    box.classList.toggle('multi', isMultiline(f));
    box.innerHTML = colorRuns(f).map((r) => `<span style="color:${textColor(r.color)}">${escapeHtml(f.Text.slice(r.start, r.end))}</span>`).join('');
  }

  /** Morceau sélectionné dans la case (vide : toute la case). */
  selectionFor(f) {
    return this.selection?.f === f ? this.selection : { f, start: 0, end: 0 };
  }

  addField(f, pl) {
    const line = this.lineOf.get(f);
    // Désignation : zone de texte sur plusieurs lignes (Entrée = nouvelle ligne dans la case).
    const multi = isMultiline(f);
    const input = el(multi ? 'textarea' : 'input', multi ? 'field multi' : 'field', this.fields);
    if (!multi) input.type = 'text';
    input.value = f.Text;
    input.autocomplete = 'off';
    input.setAttribute('autocorrect', 'off');
    input.setAttribute('autocapitalize', 'off');
    // Désignation : correcteur de l'appareil (fautes soulignées, propositions, outils d'écriture), sans correction automatique.
    input.spellcheck = multi;
    input.lang = 'fr';
    if (f.Column > 0 && (f.OriginalText.length === 0 || /\d/.test(f.OriginalText))) input.inputMode = 'decimal';
    if (isComputedCell(line, f)) input.classList.add('computed');
    input.style.textAlign = f.Align === 'Right' ? 'right' : f.Align === 'Center' ? 'center' : 'left';
    this.inputs.set(f, input);
    this.layout(input, f);
    this.visual(input, f, false);

    input.addEventListener('focus', () => { this.visual(input, f, true); if (this.cb.loupe?.() !== false) this.showLoupe(f); this.cb.onFocus?.(this, f); });
    input.addEventListener('blur', () => { this.visual(input, f, false); this.loupe.style.display = 'none'; });
    const remember = () => { this.selection = { f, start: input.selectionStart ?? 0, end: input.selectionEnd ?? 0 }; };
    for (const ev of ['select', 'keyup', 'pointerup', 'focus']) input.addEventListener(ev, remember);
    input.addEventListener('input', () => {
      this.cb.beforeEdit?.(this, f);
      adjustStrikes(f, f.Text, input.value);
      f.Text = input.value;
      remember();
      this.layout(input, f);
      this.visual(input, f, true);
      this.cb.onEdited?.(this, f);
      this.refreshStrikes();
    });
    if (!multi)
      input.addEventListener('keydown', (e) => {
        if (e.key !== 'Enter') return;
        e.preventDefault();
        this.focusNextInColumn(f);
      });
    this.initLineMenu(input, f, remember);
  }

  /**
   * Menu « Ligne » : clic droit (souris, pavé tactile) ou appui long au doigt sur une case.
   * Avec du texte sélectionné dans la case, le clic droit garde le menu habituel (copier, coller).
   */
  initLineMenu(input, f, remember) {
    this.rememberSel ??= new Map();
    this.rememberSel.set(f, remember);
    let timer = null, start = null;
    const cancel = () => { clearTimeout(timer); timer = null; start = null; };
    input.addEventListener('pointerdown', (e) => {
      if (e.button === 2)
        this.rightPress = { f, t: performance.now(), selected: document.activeElement === input && (input.selectionEnd ?? 0) > (input.selectionStart ?? 0) };
      if (e.pointerType === 'mouse' || document.activeElement === input) return; // case en cours de frappe : sélection du texte
      start = { x: e.clientX, y: e.clientY };
      timer = setTimeout(() => {
        const at = start;
        cancel();
        this.cb.onLineMenu?.(this, f, at);
      }, 550);
    });
    input.addEventListener('pointermove', (e) => { if (start && Math.hypot(e.clientX - start.x, e.clientY - start.y) > 10) cancel(); });
    for (const ev of ['pointerup', 'pointercancel', 'pointerleave']) input.addEventListener(ev, cancel);
  }

  /** Repère bref sur une ligne (rangée ajoutée ou séparée). */
  flashLine(line) {
    const entry = this.view.lines.find((e) => e.line === line);
    if (!entry || entry.cells.length === 0) return;
    const box = entry.cells.reduce((r, c) => Rect.union(r, c.box), Rect.empty());
    const d = px(el('div', 'rowflash', this.overlay), Rect.inflate(box, 2, 2));
    setTimeout(() => d.remove(), 1900);
  }

  /** Place le champ sur sa case : même taille d'écriture que l'imprimé, 1re ligne à la place du texte d'origine. */
  layout(input, f) {
    const pl = this.place.get(f);
    if (!pl) return;
    const line = this.lineOf.get(f);
    const t = textLayout(line, f, input.value);
    px(input, pl.box);
    input.style.font = font(t.size, t.bold);
    const lh = LINE_HEIGHT * t.size;
    input.style.lineHeight = lh + 'px';
    input.style.paddingTop = Math.max(0, t.top + pl.dy - pl.box.Y) + 'px';
    input.style.paddingLeft = f.Align === 'Left' ? Math.max(0, f.Box.X + pl.dx + f.Indent - pl.box.X) + 'px' : '0';
    input.style.paddingRight = f.Align === 'Right' ? Math.max(0, Rect.right(pl.box) - (Rect.right(f.Box) + pl.dx - f.Indent)) + 'px' : isMultiline(f) ? '3px' : '0';
  }

  visual(input, f, focused) {
    const onImage = !!this.image;
    const show = focused || isModified(f) || !onImage;
    input.style.opacity = show ? '1' : '0';
    input.style.background = onImage ? this.paperOf(f) : 'transparent';
    this.drawColors(f, input, focused);
  }

  /** Fond de la case : couleur du papier, ou motif tramé de la rangée (totaux, en-têtes sur fond gris). */
  paperOf(f) {
    if (!this.paper.has(f)) {
      const pl = this.place.get(f);
      let bg = this.paperOfBox(pl?.box ?? f.Box);
      if (this.image && this.canvas && pl && pl.dx === 0) {
        const patch = cleanPatch(this.image, pl.entry.page, this.lineOf.get(f), f, pl.dy);
        if (patch) {
          const c = document.createElement('canvas');
          c.width = patch.Width; c.height = patch.Height;
          c.getContext('2d').drawImage(this.canvas, patch.X, patch.Y, patch.Width, patch.Height, 0, 0, patch.Width, patch.Height);
          bg = `${bg} url(${c.toDataURL()}) repeat-x 0 0 / ${patch.Width}px ${patch.Height}px`;
        }
      }
      this.paper.set(f, bg);
    }
    return this.paper.get(f);
  }

  /** Pendant l'édition, l'original de la case est montré en loupe juste au-dessus. */
  showLoupe(f) {
    if (!this.canvas) return;
    const box = this.place.get(f)?.box ?? f.Box;
    const b = Rect.fromEdges(Math.max(0, box.X - 4), Math.max(0, box.Y - 2), Math.min(this.view.width, Rect.right(box) + 4), Math.min(this.view.height, Rect.bottom(box) + 2));
    const scale = 1.4;
    const c = document.createElement('canvas');
    c.width = Math.ceil(b.Width * scale); c.height = Math.ceil(b.Height * scale);
    c.getContext('2d').drawImage(this.canvas, b.X, b.Y, b.Width, b.Height, 0, 0, c.width, c.height);
    this.loupe.replaceChildren();
    el('div', 'loupe-label', this.loupe).textContent = 'Original';
    this.loupe.appendChild(c);
    const h = c.height + 22;
    let top = b.Y - h - 6;
    if (top < 0) top = Rect.bottom(b) + 6;
    Object.assign(this.loupe.style, { display: 'block', left: Math.max(0, Math.min(b.X, this.view.width - c.width - 6)) + 'px', top: top + 'px' });
  }

  focusNextInColumn(f) {
    const col = [...this.place.values()].filter((p) => p.f.Column === f.Column && this.inputs.has(p.f)).sort((a, b) => a.box.Y - b.box.Y);
    const i = col.findIndex((p) => p.f === f);
    if (i >= 0 && i + 1 < col.length) this.inputs.get(col[i + 1].f).focus();
  }

  /** Pages TXT/CSV/XLSX : le quadrillage du tableau, tel qu'affiché. */
  drawGrid() {
    const ns = 'http://www.w3.org/2000/svg';
    const svg = document.createElementNS(ns, 'svg');
    svg.setAttribute('class', 'grid');
    svg.setAttribute('width', this.view.width);
    svg.setAttribute('height', this.view.height);
    const line = (x1, y1, x2, y2) => {
      const l = document.createElementNS(ns, 'line');
      Object.entries({ x1, y1, x2, y2, stroke: '#000', 'stroke-width': 1.5 }).forEach(([k, v]) => l.setAttribute(k, v));
      svg.appendChild(l);
    };
    for (const t of this.view.tables) {
      if (!t.RowYs?.length || !t.ColumnXs?.length) continue;
      const top = t.RowYs[0], bottom = t.RowYs[t.RowYs.length - 1], left = t.ColumnXs[0], right = t.ColumnXs[t.ColumnXs.length - 1];
      for (const x of t.ColumnXs) line(x, top, x, bottom);
      for (const y of t.RowYs) line(left, y, right, y);
    }
    this.root.prepend(svg);
  }

  // ---------- Verrouillage : tracer une zone au doigt, toucher une zone pour la retirer ----------

  initLockPad() {
    let start = null, rubber = null;
    const local = (e) => {
      const r = this.lockpad.getBoundingClientRect(), z = this.cb.zoom();
      return { x: (e.clientX - r.left) / z, y: (e.clientY - r.top) / z };
    };
    const toPage = (y) => this.cb.unmapY?.(y) ?? y;
    this.lockpad.addEventListener('pointerdown', (e) => {
      start = local(e);
      this.lockpad.setPointerCapture(e.pointerId);
      rubber = el('div', 'lockzone rubber', this.overlay);
      e.preventDefault();
    });
    this.lockpad.addEventListener('pointermove', (e) => {
      if (!start) return;
      const p = local(e);
      px(rubber, Rect.fromEdges(Math.min(start.x, p.x), Math.min(start.y, p.y), Math.max(start.x, p.x), Math.max(start.y, p.y)));
    });
    const end = (e) => {
      if (!start) return;
      const p = local(e), a = start;
      start = null;
      rubber?.remove();
      const y0 = toPage(Math.min(a.y, p.y)), y1 = toPage(Math.max(a.y, p.y));
      if (y0 === null || y1 === null) return;
      const zone = Rect.fromEdges(Math.min(a.x, p.x), y0, Math.max(a.x, p.x), y1);
      this.cb.beforeChange?.();
      const changed = zone.Width < 8 && zone.Height < 8 ? removeLockZoneAt(this.page, p.x, toPage(p.y) ?? -1) : addLockZone(this.page, zone);
      if (changed) { this.refresh(); this.cb.onLocks?.(this); }
    };
    this.lockpad.addEventListener('pointerup', end);
    this.lockpad.addEventListener('pointercancel', () => { start = null; rubber?.remove(); });
  }
}

export { isZeroLot };
