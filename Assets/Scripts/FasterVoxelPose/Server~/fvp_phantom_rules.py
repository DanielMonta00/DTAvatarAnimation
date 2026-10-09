#!/usr/bin/env python
"""Tries rules for telling phantoms from real people on the file fvp_phantoms.py wrote, and prints what each one keeps and removes.

    python fvp_phantom_rules.py phantoms_3views.npz [phantoms_4views.npz ...]

A rule 'removes' a skeleton; a good rule removes most phantoms and almost no real people.
"""
import sys

import numpy as np


def features(d):
    sup = d['support'].astype(float)
    valid = sup >= 0.0
    n_valid = valid.sum(1)
    s = np.where(valid, sup, np.nan)
    with np.errstate(all='ignore'):
        f = {
            'score': d['score'].astype(float),
            'min_support': np.where(n_valid > 0, np.nanmin(np.where(valid, sup, np.inf), axis=1), np.nan),
            'mean_support': np.nanmean(s, axis=1),
            'second_lowest': np.where(n_valid >= 2, np.sort(np.where(valid, sup, np.inf), axis=1)[:, 1], np.nan),
        }
    f['views_seen'] = n_valid
    f['views_ok_15'] = (valid & (sup >= 0.15)).sum(1)
    f['views_ok_25'] = (valid & (sup >= 0.25)).sum(1)
    j = d['joints'].astype(float)                       # (n, 15, 3) mm, Z up
    f['neck_z'] = j[:, 0, 2]
    f['hip_z'] = j[:, 2, 2]
    f['height'] = j[:, 0, 2] - 0.5 * (j[:, 8, 2] + j[:, 14, 2])
    f['dist'] = d['dist'].astype(float)
    return f


def report(name, f, real):
    n_real, n_ph = int(real.sum()), int((~real).sum())
    print('\n==== %s: %d real, %d phantom skeletons (%.0f %% phantom)' % (name, n_real, n_ph, 100.0 * n_ph / max(1, n_real + n_ph)))

    def line(label, remove):
        rm_ph = remove[~real].mean() * 100 if n_ph else 0.0
        rm_re = remove[real].mean() * 100 if n_real else 0.0
        print('  %-44s removes %5.1f %% of phantoms, %5.1f %% of real people   (left: %d phantom, %d real)' % (
            label, rm_ph, rm_re, int((~remove & ~real).sum()), int((~remove & real).sum())))

    print(' -- the network\'s own score')
    for t in (0.12, 0.15, 0.18, 0.2):
        line('score < %.2f' % t, f['score'] < t)
    print(' -- 2D support: the weakest view (mean heatmap value at the projected joints)')
    for t in (0.02, 0.05, 0.1, 0.15, 0.2, 0.3):
        line('min support < %.2f' % t, np.nan_to_num(f['min_support'], nan=1.0) < t)
    print(' -- 2D support: mean over the views')
    for t in (0.1, 0.2, 0.3, 0.4):
        line('mean support < %.2f' % t, np.nan_to_num(f['mean_support'], nan=1.0) < t)
    print(' -- views that back it')
    for t, k in ((15, 2), (15, 3), (25, 2), (25, 3)):
        key = 'views_ok_%d' % t
        need = np.minimum(k, np.maximum(f['views_seen'], 1))
        line('backed by < %d views at >= 0.%d' % (k, t), f[key] < need)
    print(' -- combinations')
    for t in (0.1, 0.15):
        for s in (0.13, 0.15):
            rm = (np.nan_to_num(f['min_support'], nan=1.0) < t) & (f['score'] < s)
            line('min support < %.2f AND score < %.2f' % (t, s), rm)
    rm = (np.nan_to_num(f['second_lowest'], nan=1.0) < 0.15)
    line('second weakest view < 0.15', rm)
    print(' -- geometry')
    line('height of the neck above the hips < 0.2 m', f['height'] < 0)  # placeholder to show the column exists
    print('  neck z (m): real median %.2f  phantom median %.2f' % (np.median(f['neck_z'][real]) / 1000, np.median(f['neck_z'][~real]) / 1000 if n_ph else float('nan')))
    if n_ph:
        close = f['dist'][~real] < 1500
        print('  phantoms within 1.5 m of a real person: %.0f %%  (median distance %.2f m)' % (100 * close.mean(), np.median(f['dist'][~real]) / 1000))


def main():
    for path in sys.argv[1:]:
        d = np.load(path)
        report(path, features(d), d['real'].astype(bool))
        print('  ground-truth people nobody was found for: %d of %d' % (int(d['misses']), int(d['n_gt'])))


if __name__ == '__main__':
    main()
