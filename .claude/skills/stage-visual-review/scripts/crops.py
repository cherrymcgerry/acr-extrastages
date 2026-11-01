#!/usr/bin/env python3
"""Cut an in-game screenshot into views for review: an overview and native-resolution tiles.

    crops.py <screenshot.png> [--out DIR]

Writes to DIR (default: <screenshot>-review/):
    overview.jpg          the whole frame, 1720 px wide (composition, colour, big problems)
    tile-r<row>c<col>.jpg 3 x 2 grid at native resolution, 10 % overlap (small artifacts, texture resolution)
    road-near.jpg         the road and its edges in front of the car (bottom middle band)
    road-far.jpg          the road ahead and the start gate area (centre)
Fractions of the frame, so any resolution works; tuned on 21:9 (3440 x 1440) with ACR's chase camera.
"""
import argparse
import os

from PIL import Image

VIEWS = {
    'road-near': (0.12, 0.62, 0.88, 1.00),
    'road-far': (0.30, 0.30, 0.75, 0.75),
}


def main():
    p = argparse.ArgumentParser()
    p.add_argument('shot')
    p.add_argument('--out')
    a = p.parse_args()
    out = a.out or os.path.splitext(a.shot)[0] + '-review'
    os.makedirs(out, exist_ok=True)
    im = Image.open(a.shot).convert('RGB')
    w, h = im.size
    ov = im.copy()
    ov.thumbnail((1720, 1720))
    ov.save(os.path.join(out, 'overview.jpg'), quality=88)
    cols, rows, lap = 3, 2, 0.10
    tw, th = w / cols, h / rows
    for r in range(rows):
        for c in range(cols):
            box = (int(max(0, (c - lap) * tw)), int(max(0, (r - lap) * th)),
                   int(min(w, (c + 1 + lap) * tw)), int(min(h, (r + 1 + lap) * th)))
            im.crop(box).save(os.path.join(out, 'tile-r%dc%d.jpg' % (r, c)), quality=90)
    for name, (x0, y0, x1, y1) in VIEWS.items():
        v = im.crop((int(x0 * w), int(y0 * h), int(x1 * w), int(y1 * h)))
        v.thumbnail((1600, 1600))
        v.save(os.path.join(out, name + '.jpg'), quality=90)
    print(out)


if __name__ == '__main__':
    main()
