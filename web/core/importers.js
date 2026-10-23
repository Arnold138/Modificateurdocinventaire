// Imports TXT, CSV, XLSX → page « tableau ». Portage de TabularReaders.cs et DataPageBuilder.cs.
import { unzipSync, strFromU8 } from '../vendor/fflate.js';
import { Rect } from './geometry.js';
import { newPage, classify, assignIds } from './model.js';

/** UTF-8 si possible (avec ou sans BOM), sinon Windows-1252. */
export function decodeText(bytes) {
  if (bytes[0] === 0xef && bytes[1] === 0xbb && bytes[2] === 0xbf) return new TextDecoder('utf-8').decode(bytes.subarray(3));
  if (bytes[0] === 0xff && bytes[1] === 0xfe) return new TextDecoder('utf-16le').decode(bytes.subarray(2));
  try { return new TextDecoder('utf-8', { fatal: true }).decode(bytes); }
  catch { return new TextDecoder('windows-1252').decode(bytes); }
}

export function parseDelimited(text, ext = '.txt') {
  const sample = text.split('\n').slice(0, 20).join('\n');
  const sep = sample.includes('\t') ? '\t' : sample.includes(';') ? ';' : ext.toLowerCase() === '.csv' && sample.includes(',') ? ',' : null;
  let rows = [];
  if (sep === null) {
    for (const l of text.replace(/\r\n/g, '\n').split('\n')) rows.push([l.replace(/\r+$/, '')]);
  } else {
    let row = [], cell = '', quoted = false;
    for (let i = 0; i < text.length; i++) {
      const c = text[i];
      if (quoted) {
        if (c === '"' && text[i + 1] === '"') { cell += '"'; i++; }
        else if (c === '"') quoted = false;
        else cell += c;
      } else if (c === '"' && cell.length === 0) quoted = true;
      else if (c === sep) { row.push(cell); cell = ''; }
      else if (c === '\n') { row.push(cell.replace(/\r+$/, '')); cell = ''; rows.push(row); row = []; }
      else cell += c;
    }
    if (cell.length > 0 || row.length > 0) { row.push(cell.replace(/\r+$/, '')); rows.push(row); }
  }
  while (rows.length > 0 && rows[rows.length - 1].every((c) => c.trim().length === 0)) rows.pop();
  return rows;
}

// ---------- XLSX : un zip de fichiers XML ----------

const NS_MAIN = 'http://schemas.openxmlformats.org/spreadsheetml/2006/main';
const NS_REL = 'http://schemas.openxmlformats.org/officeDocument/2006/relationships';

function xml(files, name) {
  const f = files[name];
  return f ? new DOMParser().parseFromString(strFromU8(f), 'application/xml') : null;
}

function colIndex(ref) {
  let n = 0;
  for (const ch of ref) { if (!/[A-Za-z]/.test(ch)) break; n = n * 26 + (ch.toUpperCase().charCodeAt(0) - 64); }
  return n - 1;
}

function cellValue(c, shared) {
  const type = c.getAttribute('t') ?? 'n';
  const v = c.getElementsByTagNameNS(NS_MAIN, 'v')[0]?.textContent ?? '';
  if (type === 's') return shared[parseInt(v, 10)] ?? '';
  if (type === 'inlineStr') return [...c.getElementsByTagNameNS(NS_MAIN, 't')].map((t) => t.textContent).join('');
  if (type === 'b') return v === '1' ? 'VRAI' : 'FAUX';
  if (type === 'str' || type === 'e') return v;
  const d = Number(v);
  if (v === '' || Number.isNaN(d)) return v;
  return Number.isInteger(d) && Math.abs(d) < 1e15 ? String(d) : String(Number(d.toPrecision(15))).replace('.', ',');
}

/** @returns {{sheet:string, rows:string[][]}[]} */
export function readXlsx(bytes) {
  const files = unzipSync(bytes);
  const shared = [];
  const sst = xml(files, 'xl/sharedStrings.xml');
  if (sst) for (const si of sst.getElementsByTagNameNS(NS_MAIN, 'si'))
    shared.push([...si.getElementsByTagNameNS(NS_MAIN, 't')].map((t) => t.textContent).join(''));
  const rels = {};
  for (const r of xml(files, 'xl/_rels/workbook.xml.rels').getElementsByTagName('Relationship')) rels[r.getAttribute('Id')] = r.getAttribute('Target');
  const result = [];
  for (const sheet of xml(files, 'xl/workbook.xml').getElementsByTagNameNS(NS_MAIN, 'sheet')) {
    let target = rels[sheet.getAttributeNS(NS_REL, 'id')].replace(/^\//, '');
    const doc = xml(files, target.startsWith('xl/') ? target : 'xl/' + target);
    if (!doc) continue;
    const rows = new Map();
    let autoRow = 0;
    for (const r of doc.getElementsByTagNameNS(NS_MAIN, 'row')) {
      const ri = r.getAttribute('r') ? parseInt(r.getAttribute('r'), 10) - 1 : autoRow;
      autoRow = ri + 1;
      const cells = new Map();
      let autoCol = 0;
      for (const c of r.getElementsByTagNameNS(NS_MAIN, 'c')) {
        const col = c.getAttribute('r') ? colIndex(c.getAttribute('r')) : autoCol;
        autoCol = col + 1;
        cells.set(col, cellValue(c, shared));
      }
      rows.set(ri, cells);
    }
    if (rows.size === 0) continue;
    const width = Math.max(1, ...[...rows.values()].filter((c) => c.size > 0).map((c) => Math.max(...c.keys()) + 1));
    const list = [];
    for (let i = 0; i <= Math.max(...rows.keys()); i++)
      list.push(Array.from({ length: width }, (_, j) => rows.get(i)?.get(j) ?? ''));
    while (list.length > 0 && list[list.length - 1].every((c) => c.trim().length === 0)) list.pop();
    result.push({ sheet: sheet.getAttribute('name') ?? 'Feuille', rows: list });
  }
  return result;
}

// ---------- Page « tableau » ----------

const PAGE_WIDTH = 1240, MARGIN = 40, ROW_HEIGHT = 36, FONT_SIZE = 18, CHAR_WIDTH = 9.5;

export function buildDataPage(sourceName, rows) {
  const page = newPage({ Source: 'Data', SourceName: sourceName });
  const nCols = Math.max(1, rows.length === 0 ? 1 : Math.max(...rows.map((r) => r.length)));
  const want = Array.from({ length: nCols }, (_, c) => {
    const len = Math.max(0, ...rows.map((r) => (c < r.length ? r[c].length : 0)));
    return Math.min(60, Math.max(c === 0 ? 30 : 8, len)) * CHAR_WIDTH + 16;
  });
  const k = (PAGE_WIDTH - 2 * MARGIN) / want.reduce((s, v) => s + v, 0);
  const xs = [MARGIN];
  for (const w of want) xs.push(xs[xs.length - 1] + w * k);
  const ys = [MARGIN];
  for (let i = 0; i < Math.max(1, rows.length); i++) ys.push(ys[ys.length - 1] + ROW_HEIGHT);
  const table = { Bounds: Rect.fromEdges(xs[0], ys[0], xs[xs.length - 1], ys[ys.length - 1]), HasGrid: true, ColumnXs: xs, RowYs: ys, Rows: [] };
  rows.forEach((row, r) => {
    const line = { Kind: 'Article', KindChosenByUser: false, OutsideTable: false, Cells: [] };
    for (let c = 0; c < nCols; c++) {
      const text = c < row.length ? row[c].trim() : '';
      const numeric = text.length > 0 && [...text].every((ch) => /\p{Nd}/u.test(ch) || ' ,.-+ '.includes(ch));
      line.Cells.push({
        Id: '', Column: c, OriginalText: text, Text: text, Ink: Rect.empty(),
        Box: Rect.fromEdges(xs[c] + 4, ys[r] + 4, xs[c + 1] - 4, ys[r + 1] - 4),
        Align: c > 0 && numeric ? 'Right' : 'Left', Indent: 4, FontSize: FONT_SIZE, Locked: false,
      });
    }
    table.Rows.push({ Bounds: Rect.fromEdges(xs[0], ys[r], xs[xs.length - 1], ys[r + 1]), Lines: [line] });
  });
  classify(table);
  page.Tables.push(table);
  page.Width = PAGE_WIDTH;
  page.Height = Math.trunc(ys[ys.length - 1] + MARGIN);
  assignIds(page);
  return page;
}
