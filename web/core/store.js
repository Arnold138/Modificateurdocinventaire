// Fichier projet .inventaire (même format que la version Windows : zip avec projet.json + sources/).
import { zipSync, unzipSync, strToU8, strFromU8 } from '../vendor/fflate.js';

export const EXTENSION = '.inventaire';
const JSON_ENTRY = 'projet.json';

/** @param sources {Record<string, Uint8Array>} */
export function packProject(project, sources) {
  const files = { [JSON_ENTRY]: strToU8(JSON.stringify(project, null, 2)) };
  for (const page of project.Pages)
    if (page.SourceEntry && sources[page.SourceEntry] && !files[page.SourceEntry])
      files[page.SourceEntry] = [sources[page.SourceEntry], { level: 0 }];
  return zipSync(files, { level: 6 });
}

export function unpackProject(bytes) {
  const files = unzipSync(bytes);
  if (!files[JSON_ENTRY]) throw new Error('Fichier projet invalide (projet.json absent).');
  const project = JSON.parse(strFromU8(files[JSON_ENTRY]));
  const sources = {};
  for (const [name, data] of Object.entries(files)) if (name !== JSON_ENTRY) sources[name] = data;
  return { project, sources };
}
