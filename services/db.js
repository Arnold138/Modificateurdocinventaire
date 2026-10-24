// Stockage local de l'iPad (IndexedDB) : les projets et leurs photos restent sur l'appareil.
const DB_NAME = 'retouche-inventaire';
let dbPromise = null;

function db() {
  dbPromise ??= new Promise((ok, ko) => {
    const req = indexedDB.open(DB_NAME, 1);
    req.onupgradeneeded = () => {
      const d = req.result;
      d.createObjectStore('projects', { keyPath: 'id' });
      d.createObjectStore('sources');
      d.createObjectStore('meta');
    };
    req.onsuccess = () => ok(req.result);
    req.onerror = () => ko(req.error);
  });
  return dbPromise;
}

async function run(store, mode, fn) {
  const d = await db();
  return new Promise((ok, ko) => {
    const tx = d.transaction(store, mode);
    const result = fn(tx.objectStore(store));
    tx.oncomplete = () => ok(result instanceof IDBRequest ? result.result : result);
    tx.onerror = () => ko(tx.error);
    tx.onabort = () => ko(tx.error ?? new Error('Stockage plein ou indisponible.'));
  });
}

export const saveProject = (id, project) =>
  run('projects', 'readwrite', (s) => s.put({ id, name: project.Name, updated: Date.now(), pages: project.Pages.length, json: JSON.stringify(project) }));

export async function listProjects() {
  const all = await run('projects', 'readonly', (s) => s.getAll());
  return all.map(({ id, name, updated, pages }) => ({ id, name, updated, pages })).sort((a, b) => b.updated - a.updated);
}

export const putSource = (projectId, entry, bytes) => run('sources', 'readwrite', (s) => s.put(bytes, `${projectId}/${entry}`));

export async function loadProject(id) {
  const rec = await run('projects', 'readonly', (s) => s.get(id));
  if (!rec) return null;
  const range = IDBKeyRange.bound(`${id}/`, `${id}/￿`);
  const keys = await run('sources', 'readonly', (s) => s.getAllKeys(range));
  const values = await run('sources', 'readonly', (s) => s.getAll(range));
  const sources = {};
  keys.forEach((k, i) => { sources[k.slice(id.length + 1)] = values[i]; });
  return { project: JSON.parse(rec.json), sources };
}

export async function deleteProject(id) {
  await run('projects', 'readwrite', (s) => s.delete(id));
  await run('sources', 'readwrite', (s) => s.delete(IDBKeyRange.bound(`${id}/`, `${id}/￿`)));
}

export const setMeta = (key, value) => run('meta', 'readwrite', (s) => s.put(value, key));
export const getMeta = (key) => run('meta', 'readonly', (s) => s.get(key));
