// Fonctionnement hors ligne : toute l'application (y compris la lecture du texte) est mise en cache
// à la première ouverture. Ensuite plus besoin d'Internet ; une nouvelle version est prise au lancement suivant.
const VERSION = '__VERSION__';
const CACHE = `retouche-inventaire-${VERSION}`;
const ASSETS = [
  './', 'index.html', 'app.css', 'app.js', 'manifest.webmanifest',
  'icons/icon-192.png', 'icons/icon-512.png', 'icons/apple-touch-icon.png',
  'core/geometry.js', 'core/imaging.js', 'core/rules.js', 'core/ocrprep.js', 'core/model.js',
  'core/exports.js', 'core/importers.js', 'core/store.js',
  'services/canvas.js', 'services/decode.js', 'services/ocr.js', 'services/db.js',
  'ui/pageview.js', 'ui/crop.js',
  'vendor/fflate.js', 'vendor/pdfjs/pdf.min.mjs', 'vendor/pdfjs/pdf.worker.min.mjs',
  'vendor/tesseract/tesseract.min.js', 'vendor/tesseract/worker.min.js',
  'vendor/tesseract/tesseract-core-lstm.wasm.js', 'vendor/tesseract/tesseract-core-simd-lstm.wasm.js',
  'vendor/tesseract/tesseract-core-relaxedsimd-lstm.wasm.js', 'vendor/tesseract/lang/fra.traineddata.gz',
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
