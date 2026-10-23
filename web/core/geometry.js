// Géométrie : mêmes structures (et mêmes noms en JSON) que la version Windows,
// pour qu'un projet .inventaire s'ouvre sur l'iPad comme sur la Surface.

export const Rect = {
  make: (X, Y, Width, Height) => ({ X, Y, Width, Height }),
  empty: () => ({ X: 0, Y: 0, Width: 0, Height: 0 }),
  fromEdges: (l, t, r, b) => ({ X: l, Y: t, Width: Math.max(0, r - l), Height: Math.max(0, b - t) }),
  left: (r) => r.X,
  top: (r) => r.Y,
  right: (r) => r.X + r.Width,
  bottom: (r) => r.Y + r.Height,
  cx: (r) => r.X + r.Width / 2,
  cy: (r) => r.Y + r.Height / 2,
  isEmpty: (r) => !r || r.Width <= 0 || r.Height <= 0,
  union(a, b) {
    if (Rect.isEmpty(a)) return b;
    if (Rect.isEmpty(b)) return a;
    return Rect.fromEdges(Math.min(a.X, b.X), Math.min(a.Y, b.Y), Math.max(Rect.right(a), Rect.right(b)), Math.max(Rect.bottom(a), Rect.bottom(b)));
  },
  contains: (r, x, y) => x >= r.X && x <= r.X + r.Width && y >= r.Y && y <= r.Y + r.Height,
  inflate: (r, dx, dy) => Rect.fromEdges(r.X - dx, r.Y - dy, r.X + r.Width + dx, r.Y + r.Height + dy),
  vOverlap: (a, b) => Math.max(0, Math.min(Rect.bottom(a), Rect.bottom(b)) - Math.max(a.Y, b.Y)),
};

export const pt = (X, Y) => ({ X, Y });
export const dist = (a, b) => Math.hypot(a.X - b.X, a.Y - b.Y);

export const Quad = {
  full: (w, h) => ({ TopLeft: pt(0, 0), TopRight: pt(w - 1, 0), BottomRight: pt(w - 1, h - 1), BottomLeft: pt(0, h - 1) }),
  points: (q) => [q.TopLeft, q.TopRight, q.BottomRight, q.BottomLeft],
  fromPoints: (p) => ({ TopLeft: p[0], TopRight: p[1], BottomRight: p[2], BottomLeft: p[3] }),
  equals: (a, b) => Quad.points(a).every((p, i) => p.X === Quad.points(b)[i].X && p.Y === Quad.points(b)[i].Y),
  naturalSize(q) {
    const w = Math.max(dist(q.TopLeft, q.TopRight), dist(q.BottomLeft, q.BottomRight));
    const h = Math.max(dist(q.TopLeft, q.BottomLeft), dist(q.TopRight, q.BottomRight));
    return [roundHalfEven(w) + 1, roundHalfEven(h) + 1];
  },
  area(q) {
    const p = Quad.points(q);
    let a = 0;
    for (let i = 0; i < 4; i++) { const n = p[(i + 1) % 4]; a += p[i].X * n.Y - n.X * p[i].Y; }
    return Math.abs(a) / 2;
  },
};

/** Arrondi « bancaire », comme Math.Round de .NET : mêmes tailles d'image que sur Windows. */
export function roundHalfEven(x) {
  const r = Math.round(x);
  return Math.abs(x % 1) === 0.5 && r % 2 !== 0 ? r - 1 : r;
}
