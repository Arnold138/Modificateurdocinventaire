// Une page à l'écran : l'image d'origine et, par-dessus, un champ par case à sa position exacte.
// Case non modifiée : invisible (opacité 0, toujours touchable) → on ne voit que l'original.
// Case modifiée ou en cours d'édition : fond couleur papier + texte noir, réduit pour tenir dans la case.
import { Rect } from '../core/geometry.js';
import { isModified, addLockZone, removeLockZoneAt } from '../core/model.js';
import { paperColor } from '../core/exports.js';
import { lotLabels } from '../core/lots.js';
import { rasterToCanvas, baseSize, fitSize, font } from '../services/canvas.js';

const BADGES = { Titre: ['T', '#0067c0'], Total: ['Σ', '#107c10'], EnTete: ['E', '#606060'], Ignoree: ['×', '#b42828'] };
const el = (tag, cls, parent) => { const e = document.createElement(tag); if (cls) e.className = cls; parent?.appendChild(e); return e; };
const px = (e, r) => Object.assign(e.style, { left: r.X + 'px', top: r.Y + 'px', width: r.Width + 'px', height: r.Height + 'px' });

export class PageView {
  /** @param page modèle ; @param image raster redressé (null pour TXT/CSV/XLSX) ; @param zoom fonction qui renvoie le zoom courant */
  constructor(page, image, { onFocus, onEdited, onTouch, onLocks, onLot, zoom }) {
    this.page = page;
    this.image = image;
    this.cb = { onFocus, onEdited, onTouch, onLocks, onLot, zoom };
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
      this.canvas = rasterToCanvas(image);
      this.canvas.className = 'bg';
      this.root.appendChild(this.canvas);
    } else this.drawGrid();
    this.fields = el('div', 'layer', this.root);
    this.overlay = el('div', 'layer overlay', this.root);
    this.lockpad = el('div', 'layer lockpad', this.root);
    this.root.addEventListener('pointerdown', () => onTouch?.(this), true);
    this.initLockPad();
    this.build();
  }

  /** Libère la mémoire de l'image (important sur iPhone/iPad). */
  dispose() {
    if (this.canvas) { this.canvas.width = 0; this.canvas.height = 0; }
    this.row.remove();
  }

  /** Champs « Lot » de la colonne de gauche, à la hauteur de chaque objet. */
  refreshLots() {
    this.gutter.replaceChildren();
    for (const { line, box } of lotLabels(this.page)) {
      const input = el('input', 'lot', this.gutter);
      input.type = 'text';
      input.inputMode = 'numeric';
      input.value = line.Lot;
      input.setAttribute('aria-label', 'Numéro de lot');
      const h = Math.max(28, Math.min(40, box.Height + 8));
      Object.assign(input.style, { top: Rect.cy(box) - h / 2 + 'px', height: h + 'px' });
      const commit = () => { if (input.value.trim() !== line.Lot) this.cb.onLot?.(this, line, input.value); };
      input.addEventListener('change', commit);
      input.addEventListener('keydown', (e) => { if (e.key === 'Enter') { e.preventDefault(); input.blur(); } });
    }
  }

  refresh() {
    this.fields.replaceChildren();
    this.overlay.replaceChildren();
    this.inputs.clear();
    this.build();
  }

  setLockMode(on) { this.lockpad.classList.toggle('active', on); }

  revert(f) {
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
          this.badge(t, line);
          for (const f of line.Cells) if (!f.Locked) this.addField(f);
        }
    this.loupe = el('div', 'loupe', this.overlay);
    this.refreshLots();
  }

  addField(f) {
    const input = el('input', 'field', this.fields);
    input.type = 'text';
    input.value = f.Text;
    input.autocomplete = 'off';
    input.setAttribute('autocorrect', 'off');
    input.setAttribute('autocapitalize', 'off');
    input.spellcheck = false;
    if (f.Column > 0 && (f.OriginalText.length === 0 || /\d/.test(f.OriginalText))) input.inputMode = 'decimal';
    px(input, f.Box);
    input.style.textAlign = f.Align === 'Right' ? 'right' : f.Align === 'Center' ? 'center' : 'left';
    this.base.set(f, baseSize(f));
    this.layout(input, f);
    this.visual(input, f, false);

    input.addEventListener('focus', () => { this.visual(input, f, true); this.showLoupe(f); this.cb.onFocus?.(this, f); });
    input.addEventListener('blur', () => { this.visual(input, f, false); this.loupe.style.display = 'none'; });
    input.addEventListener('input', () => {
      f.Text = input.value;
      this.layout(input, f);
      this.visual(input, f, true);
      this.cb.onEdited?.(this, f);
    });
    input.addEventListener('keydown', (e) => {
      if (e.key !== 'Enter') return;
      e.preventDefault();
      this.focusNextInColumn(f);
    });
    this.inputs.set(f, input);
  }

  layout(input, f) {
    const size = fitSize(f, input.value, this.base.get(f));
    input.style.font = font(size);
    input.style.lineHeight = f.Box.Height + 'px';
    input.style.paddingLeft = f.Align === 'Left' ? f.Indent + 'px' : '0';
    input.style.paddingRight = f.Align === 'Right' ? f.Indent + 'px' : '0';
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

  badge(table, line) {
    const b = BADGES[line.Kind];
    if (!b) return;
    const d = el('div', 'badge', this.overlay);
    d.textContent = b[0];
    d.style.background = b[1];
    const bounds = line.Cells.reduce((r, f) => Rect.union(r, f.Box), Rect.empty());
    d.style.left = Math.max(2, table.Bounds.X - 30) + 'px';
    d.style.top = Rect.cy(bounds) - 12 + 'px';
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
    this.root.appendChild(svg);
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
      const changed = zone.Width < 8 && zone.Height < 8 ? removeLockZoneAt(this.page, p.x, p.y) : addLockZone(this.page, zone);
      if (changed) { this.refresh(); this.cb.onLocks?.(this); }
    };
    this.lockpad.addEventListener('pointerup', end);
    this.lockpad.addEventListener('pointercancel', () => { start = null; rubber?.remove(); });
  }
}
