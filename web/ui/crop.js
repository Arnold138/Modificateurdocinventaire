// Recadrage à la main : 4 poignées à placer au doigt sur les coins de la feuille.
import { Quad, pt } from '../core/geometry.js';
import { detectPage, rotate90, resize } from '../core/imaging.js';
import { rasterToCanvas } from '../services/canvas.js';

const $ = (id) => document.getElementById(id);

/** @returns {Promise<{quad, turns, source} | null>} */
export function cropDialog(source, quad) {
  return new Promise((resolve) => {
    const overlay = $('crop'), stage = $('cropStage'), svg = $('cropSvg'), poly = $('cropPoly');
    let src = source, turns = 0, corners = Quad.points(quad ?? Quad.full(src.w, src.h));
    let scale = 1, canvas = null;
    const handles = [0, 1, 2, 3].map(() => document.createElement('div'));

    function show() {
      const k = Math.min(1, 1600 / Math.max(src.w, src.h));
      const preview = k < 1 ? resize(src, Math.round(src.w * k), Math.round(src.h * k)) : src;
      canvas?.remove();
      canvas = rasterToCanvas(preview);
      stage.prepend(canvas);
      layout();
    }

    function layout() {
      const area = overlay.querySelector('.crop-area').getBoundingClientRect();
      scale = Math.min((area.width - 32) / src.w, (area.height - 32) / src.h);
      const w = src.w * scale, h = src.h * scale;
      Object.assign(stage.style, { width: w + 'px', height: h + 'px' });
      Object.assign(canvas.style, { width: w + 'px', height: h + 'px' });
      svg.setAttribute('width', w); svg.setAttribute('height', h);
      draw();
    }

    function draw() {
      poly.setAttribute('points', corners.map((p) => `${p.X * scale},${p.Y * scale}`).join(' '));
      handles.forEach((hd, i) => Object.assign(hd.style, { left: corners[i].X * scale + 'px', top: corners[i].Y * scale + 'px' }));
    }

    handles.forEach((hd, i) => {
      hd.className = 'handle';
      stage.appendChild(hd);
      hd.addEventListener('pointerdown', (e) => { hd.setPointerCapture(e.pointerId); e.preventDefault(); });
      hd.addEventListener('pointermove', (e) => {
        if (!hd.hasPointerCapture(e.pointerId)) return;
        const r = stage.getBoundingClientRect();
        corners[i] = pt(Math.min(src.w - 1, Math.max(0, (e.clientX - r.left) / scale)), Math.min(src.h - 1, Math.max(0, (e.clientY - r.top) / scale)));
        draw();
      });
    });

    const close = (result) => {
      overlay.hidden = true;
      handles.forEach((h) => h.remove());
      canvas?.remove();
      window.removeEventListener('resize', layout);
      for (const id of ['cropRotate', 'cropAuto', 'cropFull', 'cropCancel', 'cropApply']) $(id).onclick = null;
      resolve(result);
    };
    $('cropRotate').onclick = () => { src = rotate90(src, 1); turns = (turns + 1) % 4; corners = Quad.points(detectPage(src)); show(); };
    $('cropAuto').onclick = () => { corners = Quad.points(detectPage(src)); draw(); };
    $('cropFull').onclick = () => { corners = Quad.points(Quad.full(src.w, src.h)); draw(); };
    $('cropCancel').onclick = () => close(null);
    $('cropApply').onclick = () => close({ quad: Quad.fromPoints(corners), turns, source: src });

    overlay.hidden = false;
    window.addEventListener('resize', layout);
    requestAnimationFrame(show);
  });
}
