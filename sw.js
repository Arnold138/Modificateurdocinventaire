// Fonctionnement hors ligne : toute l'application (y compris la lecture du texte) est mise en cache
// à la première ouverture. Ensuite plus besoin d'Internet ; une nouvelle version est prise au lancement suivant.
const VERSION = 'f33b0a18';
const CACHE = `retouche-inventaire-${VERSION}`;
const ASSETS = [
  './',
  'app.css',
  'app.js',
  'core/arrange.js',
  'core/exports.js',
  'core/geometry.js',
  'core/imaging.js',
  'core/importers.js',
  'core/layout.js',
  'core/lotcolumn.js',
  'core/lots.js',
  'core/model.js',
  'core/ocrprep.js',
  'core/rules.js',
  'core/store.js',
  'core/strikes.js',
  'core/totals.js',
  'icons/apple-touch-icon.png',
  'icons/icon-192.png',
  'icons/icon-512.png',
  'index.html',
  'manifest.webmanifest',
  'services/canvas.js',
  'services/db.js',
  'services/decode.js',
  'services/ocr.js',
  'ui/crop.js',
  'ui/pageview.js',
  'vendor/fflate.js',
  'vendor/pdfjs/iccs/CGATS001Compat-v2-micro.icc',
  'vendor/pdfjs/pdf.min.mjs',
  'vendor/pdfjs/pdf.worker.min.mjs',
  'vendor/pdfjs/standard_fonts/FoxitDingbats.pfb',
  'vendor/pdfjs/standard_fonts/FoxitFixed.pfb',
  'vendor/pdfjs/standard_fonts/FoxitFixedBold.pfb',
  'vendor/pdfjs/standard_fonts/FoxitFixedBoldItalic.pfb',
  'vendor/pdfjs/standard_fonts/FoxitFixedItalic.pfb',
  'vendor/pdfjs/standard_fonts/FoxitSerif.pfb',
  'vendor/pdfjs/standard_fonts/FoxitSerifBold.pfb',
  'vendor/pdfjs/standard_fonts/FoxitSerifBoldItalic.pfb',
  'vendor/pdfjs/standard_fonts/FoxitSerifItalic.pfb',
  'vendor/pdfjs/standard_fonts/FoxitSymbol.pfb',
  'vendor/pdfjs/standard_fonts/LiberationSans-Bold.ttf',
  'vendor/pdfjs/standard_fonts/LiberationSans-BoldItalic.ttf',
  'vendor/pdfjs/standard_fonts/LiberationSans-Italic.ttf',
  'vendor/pdfjs/standard_fonts/LiberationSans-Regular.ttf',
  'vendor/pdfjs/wasm/jbig2.wasm',
  'vendor/pdfjs/wasm/jbig2_nowasm_fallback.js',
  'vendor/pdfjs/wasm/openjpeg.wasm',
  'vendor/pdfjs/wasm/openjpeg_nowasm_fallback.js',
  'vendor/pdfjs/wasm/qcms_bg.wasm',
  'vendor/pdfjs/wasm/quickjs-eval.js',
  'vendor/pdfjs/wasm/quickjs-eval.wasm',
  'vendor/tesseract/lang/fra.traineddata.gz',
  'vendor/tesseract/tesseract-core-lstm.wasm.js',
  'vendor/tesseract/tesseract-core-relaxedsimd-lstm.wasm.js',
  'vendor/tesseract/tesseract-core-simd-lstm.wasm.js',
  'vendor/tesseract/tesseract.min.js',
  'vendor/tesseract/worker.min.js',
];

self.addEventListener('install', (e) => {
  e.waitUntil(caches.open(CACHE).then((c) => c.addAll(ASSETS)).then(() => self.skipWaiting()));
});

self.addEventListener('activate', (e) => {
  e.waitUntil(
    caches.keys()
      .then((keys) => Promise.all(keys.filter((k) => k.startsWith('retouche-inventaire-') && k !== CACHE).map((k) => caches.delete(k))))
      .then(() => self.clients.claim()),
  );
});

self.addEventListener('fetch', (e) => {
  if (e.request.method !== 'GET') return;
  e.respondWith(
    caches.match(e.request, { ignoreSearch: true }).then((hit) => hit ?? fetch(e.request)),
  );
});
