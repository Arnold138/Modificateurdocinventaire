// Lecture des photos et des PDF en images (dans le navigateur, rien n'est envoyé).
import { raster } from '../core/imaging.js';
import { roundHalfEven } from '../core/geometry.js';
import { makeCanvas, canvasToRaster, canvasToBlob } from './canvas.js';

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
    return canvasToRaster(c);
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

/** Chaque page du PDF → image PNG (environ 200 ppp pour une page A4). */
export async function renderPdf(bytes) {
  const lib = await loadPdfJs();
  let doc;
  try {
    doc = await lib.getDocument({ data: bytes.slice() }).promise;
  } catch (e) {
    if (e?.name === 'PasswordException') throw new Error('Ce PDF est protégé par un mot de passe.');
    throw e;
  }
  const pages = [];
  for (let i = 1; i <= doc.numPages; i++) {
    const page = await doc.getPage(i);
    const scale = 1700 / page.getViewport({ scale: 1 }).width;
    const vp = page.getViewport({ scale });
    const c = makeCanvas(Math.round(vp.width), Math.round(vp.height));
    const ctx = c.getContext('2d', { willReadFrequently: true });
    ctx.fillStyle = '#fff';
    ctx.fillRect(0, 0, c.width, c.height);
    await page.render({ canvasContext: ctx, viewport: vp, canvas: c }).promise;
    const png = new Uint8Array(await (await canvasToBlob(c, 'image/png')).arrayBuffer());
    pages.push(png);
  }
  return pages;
}

export const isImageName = (n) => /\.(jpe?g|png|bmp|tiff?|heic|heif|webp)$/i.test(n);
export const mimeOf = (n) => (/\.png$/i.test(n) ? 'image/png' : /\.(heic|heif)$/i.test(n) ? 'image/heic' : /\.webp$/i.test(n) ? 'image/webp' : 'image/jpeg');
export { raster };
