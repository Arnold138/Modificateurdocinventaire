// Reconnaissance du texte hors ligne : Tesseract (moteur et données françaises embarqués dans l'app).
import { rasterToCanvas, freeCanvas } from './canvas.js';

let workerPromise = null;

function getWorker() {
  workerPromise ??= (async () => {
    const base = new URL('../vendor/tesseract/', import.meta.url).href;
    return window.Tesseract.createWorker('fra', 1, {
      workerPath: base + 'worker.min.js',
      corePath: base,
      langPath: base + 'lang',
      gzip: true,
      cacheMethod: 'none',
      workerBlobURL: false,
    });
  })();
  return workerPromise;
}

/** Démarre le moteur en avance (le premier chargement prend quelques secondes). */
export const warmUp = () => getWorker().then(() => true, () => false);

/**
 * @param psm 11 = texte épars (page entière), 7 = une seule ligne (case)
 * @returns {{Text:string, Box:{X,Y,Width,Height}}[]}
 */
export async function recognize(image, psm, whitelist = '') {
  const worker = await getWorker();
  await worker.setParameters({ tessedit_pageseg_mode: String(psm), tessedit_char_whitelist: whitelist });
  const canvas = rasterToCanvas(image);
  let data;
  try { ({ data } = await worker.recognize(canvas, {}, { blocks: true, text: false })); }
  finally { freeCanvas(canvas); }
  const words = [];
  for (const b of data.blocks ?? [])
    for (const p of b.paragraphs ?? [])
      for (const l of p.lines ?? [])
        for (const w of l.words ?? []) {
          const t = (w.text ?? '').trim();
          if (!t || w.confidence < 0) continue;
          words.push({ Text: t, Box: { X: w.bbox.x0, Y: w.bbox.y0, Width: w.bbox.x1 - w.bbox.x0, Height: w.bbox.y1 - w.bbox.y0 } });
        }
  return words;
}
