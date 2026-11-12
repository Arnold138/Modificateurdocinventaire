// Réparation à l'ouverture des pages lues par une version précédente (portage de PageRepair.cs) : page jamais retouchée dont le
// tableau avait perdu sa colonne des désignations (bord gauche effacé sur le scan) relue ; page ajoutée encore vide qui avait
// perdu cette colonne de son modèle recréée. Rien de ce que l'utilisateur a fait n'est perdu ; une seule fois par page.
import { analyze, allLines, allFields, isModified, newPage } from './model.js';
import { addBlankPage, canBeModel } from './addpage.js';

/** Lecture des filets actuelle (page.GridRev) : 1 = bord gauche coupé et filets penchés repris. */
export const CURRENT_GRID = 1;

const emptyRect = (r) => !r || r.Width <= 0 || r.Height <= 0;
const blankText = (s) => (s ?? '').trim().length === 0;

/** Page importée pas encore vérifiée avec la lecture actuelle des filets. */
export const repairPending = (page) =>
  (page.GridRev ?? 0) < CURRENT_GRID && page.Source === 'Image' && !page.Added && !page.EndLots;

/** Page importée telle que lue : rien de tapé, barré, coloré, ajouté, déplacé, agrandi ou verrouillé à la main. */
export function untouched(page) {
  if ((page.Inserts?.length ?? 0) > 0 || (page.LockZones?.length ?? 0) > 0) return false;
  if (page.Tables.some((t) => t.RowOrder != null || t.Rows.some((r) => r.Inserted != null))) return false;
  for (const l of allLines(page))
    if ((l.Extra ?? 0) !== 0 || (l.Tall ?? 0) !== 0 || l.Copy || l.Deleted || l.KindChosenByUser || l.MoveTo != null || l.TotalScope != null
      || l.EndAt != null || l.NoLot || l.ForceLot || l.LotHere || l.LotManual || (l.Lot ?? '').length > 0) return false;
  for (const f of allFields(page))
    if (isModified(f) || (f.Strikes?.length ?? 0) > 0 || (f.Colors?.length ?? 0) > 0 || f.Manual) return false;
  return true;
}

const grid = (t) => t.HasGrid !== false && (t.ColumnXs?.length ?? 0) >= 2 && (t.RowYs?.length ?? 0) >= 2;

/** Chaque tableau à grille de l'ancienne lecture se retrouve (mêmes filets, au moins autant de colonnes), et un en a plus. */
export function gains(old, fresh) {
  const olds = old.Tables.filter(grid), news = fresh.Tables.filter(grid);
  if (olds.length === 0 || news.length < olds.length) return false;
  let gained = false;
  for (const o of olds) {
    const n = news.find((n) => o.ColumnXs.every((x) => n.ColumnXs.some((y) => Math.abs(x - y) <= 6))
      && Math.abs(n.RowYs.length - o.RowYs.length) <= 1 && Math.abs(n.RowYs[n.RowYs.length - 1] - o.RowYs[o.RowYs.length - 1]) <= 12);
    if (!n) return false;
    if (n.ColumnXs.length > o.ColumnXs.length) gained = true;
  }
  return gained;
}

const overlap = (a, b) => {
  const top = Math.max(a.Bounds.Y, b.Bounds.Y), bottom = Math.min(a.Bounds.Y + a.Bounds.Height, b.Bounds.Y + b.Bounds.Height);
  return bottom - top > 0.5 * Math.min(a.Bounds.Height, b.Bounds.Height);
};

/** Relit la page sur son image redressée avec le texte déjà lu ; vrai si elle a changé. Notée vérifiée dans tous les cas. */
export function regrid(page, image) {
  if (!repairPending(page)) return false;
  page.GridRev = CURRENT_GRID;
  if (!untouched(page) || image.w !== page.Width || image.h !== page.Height) return false;
  const words = [...allFields(page)].filter((f) => !blankText(f.OriginalText))
    .map((f) => ({ Text: f.OriginalText, Box: emptyRect(f.Ink) ? f.Box : f.Ink }));
  const fresh = newPage({ Id: page.Id, Source: page.Source });
  analyze(fresh, image, words);
  if (!gains(page, fresh)) return false;
  for (const t of fresh.Tables) {
    const old = page.Tables.find((o) => overlap(o, t));
    if (old) t.LotsOff = !!old.LotsOff;
  }
  page.Tables = fresh.Tables;
  page.LooseLines = fresh.LooseLines;
  page.Rev = fresh.Rev;
  page.GridRev = CURRENT_GRID;
  return true;
}

/** Page ajoutée encore vide dont un tableau a moins de colonnes que son modèle : son modèle (à recréer vierge à sa place), sinon null. */
export function brokenBlank(project, page) {
  const info = page.Added;
  if (!info || page.EndLots) return null;
  if ([...allFields(page)].some((f) => !blankText(f.Text)) || [...allLines(page)].some((l) => (l.Lot ?? '').length > 0)) return null;
  const lost = page.Tables.some((t, i) => i < (info.Tables?.length ?? 0) && info.Tables[i] && t.ColumnXs.length < info.Tables[i].Orig.length);
  if (!lost) return null;
  const model = project.Pages.find((p) => p.Id === info.From && !p.Added && !p.EndLots);
  return model && canBeModel(model) ? model : null;
}

export { addBlankPage };
