#!/usr/bin/env python3
"""
Régénère les mots OCR de test (tests/fixtures/*.words.tsv et *.reread.tsv) avec Tesseract.

Sur la tablette, l'application utilise Windows.Media.Ocr. Hors de Windows, Tesseract
(français, mode « texte épars ») joue ce rôle pour tester toute la chaîne.
La préparation des images est faite par le cœur C# lui-même (commandes de l'exécutable de test).

Usage (depuis la racine du dépôt, après `dotnet build tests/RetoucheInventaire.Core.Tests -c Release`) :
    python3 tools/generer_fixtures.py page1 page2 page5
"""
import csv, glob, gzip, io, os, subprocess, sys, tempfile
from PIL import Image

ROOT = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
FIX = os.path.join(ROOT, "tests", "fixtures")
EXE = os.path.join(ROOT, "tests", "RetoucheInventaire.Core.Tests", "bin", "Release", "net10.0", "RetoucheInventaire.Core.Tests")


def tesseract_words(png, psm):
    tsv = subprocess.run(["tesseract", png, "stdout", "-l", "fra", "--psm", str(psm), "tsv"],
                         capture_output=True, text=True, check=True).stdout
    for r in csv.DictReader(io.StringIO(tsv), delimiter="\t", quoting=csv.QUOTE_NONE):
        t = (r["text"] or "").strip()
        if r["level"] == "5" and t and float(r["conf"]) >= 0:
            yield t, [int(r[k]) for k in ("left", "top", "width", "height")]


def main(names):
    tmp = tempfile.mkdtemp()
    for name in names:
        pgm = os.path.join(FIX, name + ".pgm.gz")
        if not os.path.exists(pgm):
            raw = os.path.join(tmp, name + ".pgm")
            Image.open(os.path.join(FIX, name + ".jpg")).convert("L").save(raw)
            with open(raw, "rb") as src, gzip.open(pgm, "wb") as dst:
                dst.write(src.read())

        # 1) Page entière, préparée par le cœur (filets effacés, seuil, agrandissement).
        prep = os.path.join(tmp, name + ".prep.pgm")
        out = subprocess.run([EXE, "prepare", pgm, prep], capture_output=True, text=True, check=True).stdout
        scale = float(out.strip().split("x")[-1])
        png = os.path.join(tmp, name + ".png")
        Image.open(prep).save(png)
        with open(os.path.join(FIX, name + ".words.tsv"), "w", encoding="utf-8") as f:
            for t, (x, y, w, h) in tesseract_words(png, 11):
                f.write(f"{t}\t{x/scale:.1f}\t{y/scale:.1f}\t{w/scale:.1f}\t{h/scale:.1f}\n")

        # 2) Seconde lecture des cases vides contenant de l'encre.
        cells = os.path.join(tmp, name + "_cells")
        subprocess.run([EXE, "reread-list", name, cells], capture_output=True, text=True, check=True)
        with open(os.path.join(FIX, name + ".reread.tsv"), "w", encoding="utf-8") as f:
            for p in sorted(glob.glob(os.path.join(cells, "*.pgm"))):
                cpng = p[:-4] + ".png"
                Image.open(p).save(cpng)
                fid = os.path.basename(p)[:-4]
                for t, (x, y, w, h) in tesseract_words(cpng, 7):
                    f.write(f"{fid}\t{t}\t{x}\t{y}\t{w}\t{h}\n")
        print(name, "ok")


if __name__ == "__main__":
    main(sys.argv[1:] or ["page1", "page2", "page5"])
