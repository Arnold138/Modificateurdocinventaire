// Export PDF : une page par image JPEG (la page telle qu'exportée en JPEG : corrections, lots, totaux).
// Pas de bibliothèque : le JPEG est mis tel quel dans le PDF (DCTDecode), pleine page, largeur A4 (595 pt),
// hauteur au prorata. Portage identique dans Export/PdfWriter.cs.

const A4_WIDTH = 595;

/** Largeur, hauteur et nombre de composantes (1 gris, 3 couleur, 4 CMJN) lus dans l'en-tête du JPEG. */
export function jpegInfo(b) {
  let i = 2;
  while (i + 9 < b.length) {
    if (b[i] !== 0xff) { i++; continue; }
    const m = b[i + 1];
    if (m === 0xd8 || m === 0x01 || (m >= 0xd0 && m <= 0xd7)) { i += 2; continue; }
    const len = (b[i + 2] << 8) | b[i + 3];
    if (m >= 0xc0 && m <= 0xcf && m !== 0xc4 && m !== 0xc8 && m !== 0xcc)
      return { h: (b[i + 5] << 8) | b[i + 6], w: (b[i + 7] << 8) | b[i + 8], comps: b[i + 9] };
    i += 2 + len;
  }
  throw new Error('JPEG illisible');
}

/** Texte PDF : ASCII entre parenthèses, sinon UTF-16 (hexadécimal). */
function pdfText(s) {
  if (/^[\x20-\x7e]*$/.test(s)) return `(${s.replace(/[\\()]/g, (c) => '\\' + c)})`;
  let hex = 'FEFF';
  for (const c of s) {
    const cp = c.codePointAt(0);
    const units = cp > 0xffff ? [0xd800 + ((cp - 0x10000) >> 10), 0xdc00 + ((cp - 0x10000) & 0x3ff)] : [cp];
    for (const u of units) hex += u.toString(16).toUpperCase().padStart(4, '0');
  }
  return `<${hex}>`;
}

const fmt = (v) => (Math.round(v * 100) / 100).toString();

/** PDF d'une page par image JPEG (Uint8Array), dans l'ordre. */
export function buildPdf(jpegs, title = '') {
  const enc = new TextEncoder();
  const chunks = [];
  let size = 0;
  const offsets = [];
  const put = (x) => { const b = typeof x === 'string' ? enc.encode(x) : x; chunks.push(b); size += b.length; };
  const obj = (n, body, stream = null) => {
    offsets[n] = size;
    put(`${n} 0 obj\n${body}\n`);
    if (stream) { put('stream\n'); put(stream); put('\nendstream\n'); }
    put('endobj\n');
  };
  const n = jpegs.length;
  // 1 catalogue, 2 pages, 3 infos, puis par page : page, image, contenu.
  put('%PDF-1.4\n');
  put(new Uint8Array([0x25, 0xe2, 0xe3, 0xcf, 0xd3, 0x0a])); // fichier binaire
  obj(1, '<< /Type /Catalog /Pages 2 0 R >>');
  obj(2, `<< /Type /Pages /Count ${n} /Kids [${jpegs.map((_, i) => `${4 + 3 * i} 0 R`).join(' ')}] >>`);
  obj(3, `<< /Title ${pdfText(title)} /Producer (Retouche d'inventaire) >>`);
  jpegs.forEach((jpeg, i) => {
    const { w, h, comps } = jpegInfo(jpeg);
    const pw = A4_WIDTH, ph = (A4_WIDTH * h) / w;
    const p = 4 + 3 * i;
    const content = enc.encode(`q ${fmt(pw)} 0 0 ${fmt(ph)} 0 0 cm /Im0 Do Q`);
    obj(p, `<< /Type /Page /Parent 2 0 R /MediaBox [0 0 ${fmt(pw)} ${fmt(ph)}] /Resources << /XObject << /Im0 ${p + 1} 0 R >> >> /Contents ${p + 2} 0 R >>`);
    const cs = comps === 1 ? '/DeviceGray' : comps === 4 ? '/DeviceCMYK' : '/DeviceRGB';
    obj(p + 1, `<< /Type /XObject /Subtype /Image /Width ${w} /Height ${h} /ColorSpace ${cs} /BitsPerComponent 8 /Filter /DCTDecode${comps === 4 ? ' /Decode [1 0 1 0 1 0 1 0]' : ''} /Length ${jpeg.length} >>`, jpeg);
    obj(p + 2, `<< /Length ${content.length} >>`, content);
  });
  const xref = size, count = 4 + 3 * n;
  let table = `xref\n0 ${count}\n0000000000 65535 f \n`;
  for (let k = 1; k < count; k++) table += `${String(offsets[k]).padStart(10, '0')} 00000 n \n`;
  put(table);
  put(`trailer\n<< /Size ${count} /Root 1 0 R /Info 3 0 R >>\nstartxref\n${xref}\n%%EOF\n`);
  const out = new Uint8Array(size);
  let o = 0;
  for (const c of chunks) { out.set(c, o); o += c.length; }
  return out;
}
