// Une page à l'écran : l'image d'origine et, par-dessus, un champ par case à sa position exacte.
// Case non modifiée : invisible (opacité 0, toujours touchable) → on ne voit que l'original.
// Case modifiée ou en cours d'édition : fond couleur papier + texte noir, réduit pour tenir dans la case.
import { Rect } from '../core/geometry.js';
import { isModified, addLockZone, removeLockZoneAt } from '../core/model.js';
import { paperColor } from '../core/exports.js';
import { lotLabels, canAddLot, hasLotCell } from '../core/lots.js';
import { framedTables } from '../core/lotcolumn.js';
import { zeroRows } from '../core/zerorows.js';
import { rasterToCanvas, freeCanvas, font, lotSize, textLayout, isMultiline, LINE_HEIGHT, strikeSegments } from '../services/canvas.js';
import { adjustStrikes } from '../core/strikes.js';
import { baseBox } from '../core/layout.js';

const BADGES = { Titre: ['T', '#0067c0'], Total: ['Σ', '#107c10'], EnTete: ['E', '#606060'], Ignoree: ['×', '#b42828'] };
const el = (tag, cls, parent) => { const e = document.createElement(tag); if (cls) e.className = cls; parent?.appendChild(e); return e; };
const px = (e, r) => { Object.assign(e.style, { left: r.X + 'px', top: r.Y + 'px', width: r.Width + 'px', height: r.Height + 'px' }); return e; };

export class PageView {
  /**
   * @param page modèle ; @param image raster redressé d'origine (null pour TXT/CSV/XLSX) ;
   * display : image affichée (avec la colonne des lots tracée) ; zoom : fonction qui renvoie le zoom courant ;
   * lotFrame : vrai si la colonne des lots est tracée.
   */
  constructor(page, image, { display, onFocus, onEdited, onTouch, onLocks, onLot, onLotRemove, onLotAdd, lotsActive, lotFrame, zoom, beforeEdit, beforeChange }) {
    this.page = page;
    this.image = image;
    this.cb = { onFocus, onEdited, onTouch, onLocks, onLot, onLotRemove, onLotAdd, lotsActive, lotFrame, zoom, beforeEdit, beforeChange };
    this.lotInputs = new Map();
    this.lineOf = new Map();
    this.inputs = new Map();
    this.base = new Map();
    this.paper = new Map();

    // Colonne des lots à gauche, en dehors de la page : la mise en page du document n'est pas touchée.
    this.row = el('div', 'page-row');
    this.gutter = el('div', 'gutter', this.row);
    this.gutter.style.height = page.Height + 'px';
    this.root = el('div', 'page', this.row);
    Object.assign(this.root.style, { width: page.Width + 'px', height: page.Height + 'px' });
    if (image) {
      this.canvas = rasterToCanvas(display ?? image);
      this.canvas.className = 'bg';
      this.root.appendChild(this.canvas);
    } else this.drawGrid();
    this.fields = el('div', 'layer', this.root);
    this.lots = el('div', 'layer lots', this.root);
    this.strikes = el('div', 'layer strikes', this.root);
    this.selection = null; // dernière sélection dans une case (pour « Barrer »)
    this.overlay = el('div', 'layer overlay', this.root);
    this.lockpad = el('div', 'layer lockpad', this.root);
    this.root.addEventListener('pointerdown', () => onTouch?.(this), true);
    this.initLockPad();
    this.build();
  }

  /** Libère la mémoire de l'image (important sur iPhone/iPad). */
  dispose() {
    freeCanvas(this.canvas);
    this.row.remove();
  }

  /** Remplace l'image affichée (colonne des lots tracée ou non). */
  setDisplay(display) {
    if (!this.canvas || !display) return;
    const c = rasterToCanvas(display);
    c.className = 'bg';
    this.canvas.replaceWith(c);
    freeCanvas(this.canvas);
    this.canvas = c;
  }

  /**
   * Numéros de lot écrits sur la page, dans la colonne (modifiables au doigt), et colonne de gauche :
   * type de ligne (T, Σ…), poubelle sur les lignes numérotées, « + » sur les autres.
   */
  refreshLots() {
    const focused = [...this.lotInputs].find(([, i]) => i === document.activeElement)?.[0];
    this.lots.replaceChildren();
    this.lotInputs.clear();
    const framed = this.cb.lotFrame?.() ? framedTables(this.page) : [];
    for (const { line, table, box } of lotLabels(this.page)) {
      const input = el('input', 'lotfield', this.lots);
      input.type = 'text';
      input.inputMode = 'numeric';
      input.autocomplete = 'off';
      input.value = line.Lot;
      input.setAttribute('aria-label', 'Numéro de lot');
      px(input, box);
      input.style.font = font(lotSize(line, box));
      input.style.lineHeight = box.Height + 'px';
      input.style.background = framed.includes(table) || !this.image ? 'transparent' : this.paperOfBox(box);
      // La suite se recale quand on quitte la case (Entrée ou toucher ailleurs), pas pendant la frappe.
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

  /** Rangées au lot 0 : voilées, avec la mention « déplacée en fin d'inventaire » (fait à l'export). */
  refreshZeroRows() {
    for (const z of this.overlay.querySelectorAll('.zerorow')) z.remove();
    for (const { table: t, row: r } of zeroRows(this.page)) {
      const left = t.ColumnXs?.length ? t.ColumnXs[0] : t.Bounds.X, right = t.ColumnXs?.length ? t.ColumnXs[t.ColumnXs.length - 1] : Rect.right(t.Bounds);
      const d = px(el('div', 'zerorow', this.overlay), Rect.fromEdges(left, t.RowYs[r], right, t.RowYs[r + 1]));
      el('span', '', d).textContent = 'Lot 0 → fin d\'inventaire';
    }
  }

  /** Après un recalcul : met les nouveaux numéros en place sans fermer le clavier si on est en train de taper. */
  updateLots() {
    const labels = lotLabels(this.page);
    const same = labels.length === this.lotInputs.size && labels.every(({ line, box }) => {
      const i = this.lotInputs.get(line);
      return i && Math.abs(parseFloat(i.style.left) - box.X) < 0.5 && Math.abs(parseFloat(i.style.width) - box.Width) < 0.5;
    });
    if (!same) return this.refreshLots();
    for (const { line, box } of labels) {
      const i = this.lotInputs.get(line);
      px(i, box);
      i.style.lineHeight = box.Height + 'px';
      if (i !== document.activeElement) i.value = line.Lot;
      i.style.font = font(lotSize(line, box));
    }
    this.refreshGutter();
  }

  refreshGutter() {
    this.refreshZeroRows();
    this.gutter.replaceChildren();
    const active = this.cb.lotsActive?.() ?? false;
    for (const t of this.page.Tables)
      for (const line of t.Rows.flatMap((r) => r.Lines)) {
        if (line.OutsideTable || line.Cells.length === 0) continue;
        const lb = line.Cells.reduce((r, f) => Rect.union(r, f.Box), Rect.empty());
        const b = { ...lb, Height: lb.Height - (line.Extra ?? 0) };
        const badge = BADGES[line.Kind];
        if (badge) {
          const d = el('div', 'badge', this.gutter);
          d.textContent = badge[0];
          d.style.background = badge[1];
          d.style.top = Rect.cy(b) - 12 + 'px';
        }
        if (!active) continue;
        const has = hasLotCell(line);
        if (!has && !canAddLot(line)) continue;
        const btn = el('button', has ? 'gbtn trash' : 'gbtn add', this.gutter);
        btn.innerHTML = `<svg><use href="#${has ? 'i-trash' : 'i-plus'}"/></svg>`;
        btn.setAttribute('aria-label', has ? `Retirer le lot ${line.Lot}` : 'Ajouter un numéro de lot');
        btn.title = has ? 'Pas un objet : retirer ce numéro (les suivants se décalent)' : 'Ajouter un numéro de lot sur cette ligne';
        const h = Math.max(26, Math.min(40, b.Height + 6));
        Object.assign(btn.style, { top: Rect.cy(b) - h / 2 + 'px', height: h + 'px' });
        btn.onclick = () => (has ? this.cb.onLotRemove : this.cb.onLotAdd)?.(this, line);
      }
  }

  paperOfBox(box) {
    const [r, g, b] = paperColor(this.image, box);
    return `rgb(${r},${g},${b})`;
  }

  refresh() {
    this.fields.replaceChildren();
    this.overlay.replaceChildren();
    this.inputs.clear();
    this.build();
  }

  setLockMode(on) { this.lockpad.classList.toggle('active', on); }

  revert(f) {
    adjustStrikes(f, f.Text, f.OriginalText);
    f.Text = f.OriginalText;
    const input = this.inputs.get(f);
    if (input) { input.value = f.Text; this.layout(input, f); this.visual(input, f, document.activeElement === input); }
  }

  focus(f) { this.inputs.get(f)?.focus({ preventScroll: true }); }

  build() {
    for (const z of this.page.LockZones) {
      const d = px(el('div', 'lockzone', this.overlay), z);
      d.textContent = '🔒';
    }
    for (const t of this.page.Tables)
      for (const row of t.Rows)
        for (const line of row.Lines) {
          for (const f of line.Cells) {
            this.lineOf.set(f, line);
            if (!f.Locked) this.addField(f);
          }
        }
    this.loupe = el('div', 'loupe', this.overlay);
    this.refreshLots();
    this.refreshStrikes();
  }

  /** Traits des morceaux barrés, posés par-dessus le texte (scan ou texte retapé). */
  refreshStrikes() {
    this.strikes.replaceChildren();
    for (const [f, line] of this.lineOf)
      for (const g of strikeSegments(line, f)) {
        const d = el('div', 'strike', this.strikes);
        Object.assign(d.style, { left: g.x0 + 'px', width: g.x1 - g.x0 + 'px', top: g.y - g.thick / 2 + 'px', height: g.thick + 'px', background: g.color });
      }
  }

  /** Morceau sélectionné dans la case (vide : toute la case). */
  selectionFor(f) {
    return this.selection?.f === f ? this.selection : { f, start: 0, end: 0 };
  }

  addField(f) {
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
    input.style.textAlign = f.Align === 'Right' ? 'right' : f.Align === 'Center' ? 'center' : 'left';
    this.layout(input, f);
    this.visual(input, f, false);

    input.addEventListener('focus', () => { this.visual(input, f, true); this.showLoupe(f); this.cb.onFocus?.(this, f); });
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
    this.inputs.set(f, input);
  }

  /** Place le champ sur sa case : même taille d'écriture que l'imprimé, 1re ligne à la place du texte d'origine. */
  layout(input, f) {
    const line = this.lineOf.get(f);
    const t = textLayout(line, f, input.value);
    px(input, f.Box);
    input.style.font = font(t.size);
    const lh = LINE_HEIGHT * t.size;
    input.style.lineHeight = lh + 'px';
    input.style.paddingTop = Math.max(0, t.top - f.Box.Y) + 'px';
    input.style.paddingLeft = f.Align === 'Left' ? f.Indent + 'px' : '0';
    input.style.paddingRight = f.Align === 'Right' ? f.Indent + 'px' : isMultiline(f) ? '3px' : '0';
  }

  /** La page a changé de hauteur (texte sur plusieurs lignes) : tout est replacé sans fermer le clavier. */
  relayout(image, display) {
    this.image = image;
    Object.assign(this.root.style, { height: this.page.Height + 'px' });
    this.gutter.style.height = this.page.Height + 'px';
    if (image) this.setDisplay(display ?? image);
    else { this.root.querySelector('svg.grid')?.remove(); this.drawGrid(); }
    this.paper.clear();
    for (const [f, input] of this.inputs) {
      this.layout(input, f);
      this.visual(input, f, input === document.activeElement);
    }
    for (const z of this.overlay.querySelectorAll('.lockzone')) z.remove();
    for (const z of this.page.LockZones) {
      const d = px(el('div', 'lockzone', this.overlay), z);
      d.textContent = '🔒';
      this.overlay.prepend(d);
    }
    this.updateLots();
    this.refreshStrikes();
  }

  visual(input, f, focused) {
    const onImage = !!this.image;
    const show = focused || isModified(f) || !onImage;
    input.style.opacity = show ? '1' : '0';
    input.style.background = onImage ? this.paperOf(f) : 'transparent';
  }

  paperOf(f) {
    if (!this.paper.has(f)) {
      const [r, g, b] = paperColor(this.image, f.Box);
      this.paper.set(f, `rgb(${r},${g},${b})`);
    }
    return this.paper.get(f);
  }

  /** Pendant l'édition, l'original de la case est montré en loupe juste au-dessus. */
  showLoupe(f) {
    if (!this.canvas) return;
    const b = Rect.fromEdges(Math.max(0, f.Box.X - 4), Math.max(0, f.Box.Y - 2), Math.min(this.page.Width, Rect.right(f.Box) + 4), Math.min(this.page.Height, Rect.bottom(f.Box) + 2));
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
    Object.assign(this.loupe.style, { display: 'block', left: Math.max(0, Math.min(b.X, this.page.Width - c.width - 6)) + 'px', top: top + 'px' });
  }

  focusNextInColumn(f) {
    const col = this.page.Tables.flatMap((t) => t.Rows).flatMap((r) => r.Lines).flatMap((l) => l.Cells).filter((c) => c.Column === f.Column && this.inputs.has(c));
    const i = col.indexOf(f);
    if (i >= 0 && i + 1 < col.length) this.inputs.get(col[i + 1]).focus();
  }

  drawGrid() {
    const ns = 'http://www.w3.org/2000/svg';
    const svg = document.createElementNS(ns, 'svg');
    svg.setAttribute('class', 'grid');
    svg.setAttribute('width', this.page.Width);
    svg.setAttribute('height', this.page.Height);
    const line = (x1, y1, x2, y2) => {
      const l = document.createElementNS(ns, 'line');
      Object.entries({ x1, y1, x2, y2, stroke: '#000', 'stroke-width': 1.5 }).forEach(([k, v]) => l.setAttribute(k, v));
      svg.appendChild(l);
    };
    for (const t of this.page.Tables) {
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
      const zone = Rect.fromEdges(Math.min(a.x, p.x), Math.min(a.y, p.y), Math.max(a.x, p.x), Math.max(a.y, p.y));
      this.cb.beforeChange?.();
      const changed = zone.Width < 8 && zone.Height < 8 ? removeLockZoneAt(this.page, p.x, p.y) : addLockZone(this.page, zone);
      if (changed) { this.refresh(); this.cb.onLocks?.(this); }
    };
    this.lockpad.addEventListener('pointerup', end);
    this.lockpad.addEventListener('pointercancel', () => { start = null; rubber?.remove(); });
  }
}
