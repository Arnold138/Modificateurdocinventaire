// Compare la sortie du banc d'essai iPad et celle de Windows : le .txt et chaque page composée doivent être identiques.
//   node web/tests/stress-compare.mjs <dossier iPad> <dossier Windows>
import { readFileSync, readdirSync, existsSync } from 'node:fs';
import { join } from 'node:path';
const [a, b] = process.argv.slice(2);
const txt = (d) => readFileSync(join(d, 'export.txt'), 'utf8').replace(/^﻿/, '');
let bad = 0;
if (txt(a) !== txt(b)) { bad++; console.log('export.txt DIFFÉRENT'); }
const pages = readdirSync(a).filter((f) => f.endsWith('.pgm')).sort();
for (const f of pages) if (!existsSync(join(b, f)) || !readFileSync(join(a, f)).equals(readFileSync(join(b, f)))) { bad++; console.log(`${f} DIFFÉRENT`); }
console.log(`txt ${txt(a) === txt(b) ? 'identique' : 'différent'}, pages identiques ${pages.length - (bad - (txt(a) !== txt(b)))}/${pages.length}`);
process.exit(bad || !pages.length ? 1 : 0);
