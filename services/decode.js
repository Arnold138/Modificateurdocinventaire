// Lecture des photos et des PDF en images (dans le navigateur, rien n'est envoyé).
import { raster } from '../core/imaging.js';
import { roundHalfEven } from '../core/geometry.js';
import { makeCanvas, canvasToRaster, canvasToBlob, freeCanvas } from './canvas.js';

const MAX_SIDE = 4000;

/** Photo (JPEG, PNG, HEIC selon l'appareil) → raster RGBA, orientation de l'appareil photo respectée. */
export async function decodeImage(bytes, type = '') {
  const url = URL.createObjectURL(new Blob([bytes], { type }));
  try {
    const img = new Image();
    img.src = url;
    await img.decode();
    const k = Math.min(1, MAX_SIDE / Math.max(img.naturalWidth, img.naturalHeight));
    const w = Math.max(1, roundHalfEven(img.naturalWidth * k)), h = Math.max(1, roundHalfEven(img.naturalHeight * k));
    const c = makeCanvas(w, h);
    const ctx = c.getContext('2d', { willReadFrequently: true });
    ctx.fillStyle = '#fff';
    ctx.fillRect(0, 0, w, h);
    ctx.imageSmoothingQuality = 'high';
    ctx.drawImage(img, 0, 0, w, h);
    const r = canvasToRaster(c);
    freeCanvas(c);
    return r;
  } finally {
    URL.revokeObjectURL(url);
  }
}

let pdfjs = null;
async function loadPdfJs() {
  if (!pdfjs) {
    pdfjs = await import('../vendor/pdfjs/pdf.min.mjs');
    pdfjs.GlobalWorkerOptions.workerSrc = new URL('../vendor/pdfjs/pdf.worker.min.mjs', import.meta.url).href;
  }
  return pdfjs;
}

/**
 * Pages du PDF une par une → image PNG (environ 200 ppp pour une page A4).
 * Une seule page en mémoire à la fois : indispensable sur iPhone/iPad pour les longs inventaires.
 */
export async function* pdfPages(bytes) {
  const lib = await loadPdfJs();
  const base = new URL('../vendor/pdfjs/', import.meta.url).href;
  const task = lib.getDocument({
    data: bytes.slice(),
    // Décodeurs des images de scanner (JPEG 2000, JBIG2) et polices standard : livrés avec l'app, hors ligne.
    wasmUrl: base + 'wasm/',
    standardFontDataUrl: base + 'standard_fonts/',
    iccUrl: base + 'iccs/',
    // Chemins les plus sûrs sur iPhone/iPad (pas d'OffscreenCanvas ni de décodeur d'images du système).
    isOffscreenCanvasSupported: false,
    isImageDecoderSupported: false,
  });
  let doc;
  try {
    doc = await task.promise;
  } catch (e) {
    if (e?.name === 'PasswordException') throw new Error('Ce PDF est protégé par un mot de passe.');
    throw e;
  }
  try {
  for (let i = 1; i <= doc.numPages; i++) {
    const page = await doc.getPage(i);
    const scale = 1700 / page.getViewport({ scale: 1 }).width;
    const vp = page.getViewport({ scale });
    const c = makeCanvas(Math.round(vp.width), Math.round(vp.height));
    const ctx = c.getContext('2d', { willReadFrequently: true });
    ctx.fillStyle = '#fff';
    ctx.fillRect(0, 0, c.width, c.height);
    await page.render({ canvasContext: ctx, viewport: vp, canvas: c }).promise;
    const blank = isBlank(ctx, c.width, c.height);
    const png = new Uint8Array(await (await canvasToBlob(c, 'image/png')).arrayBuffer());
    freeCanvas(c);
    page.cleanup();
    yield { index: i, count: doc.numPages, png, blank };
  }
  } finally {
    await task.destroy();
  }
}

/** Page restée blanche (image du PDF illisible) : presque aucun pixel foncé. */
function isBlank(ctx, w, h) {
  const d = ctx.getImageData(0, 0, w, h).data;
  let dark = 0;
  for (let i = 0; i < d.length; i += 4 * 7) if (d[i] + d[i + 1] + d[i + 2] < 600) dark++;
  return dark < (w * h) / 7 / 2000;
}

export const isImageName = (n) => /\.(jpe?g|png|bmp|tiff?|heic|heif|webp|gif)$/i.test(n);
export const mimeOf = (n) => (/\.png$/i.test(n) ? 'image/png' : /\.(heic|heif)$/i.test(n) ? 'image/heic' : /\.webp$/i.test(n) ? 'image/webp' : 'image/jpeg');
export { raster };
