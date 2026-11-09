"""逆コンパイルした MOD の Config.Bind 呼び出しから、セクション名と項目名を抜き出す。

使い方: python -I extract_binds.py <逆コンパイル先のフォルダ> [除外する MOD フォルダ名...]
出力 (TSV): MOD フォルダ名, 型, セクション, 項目名(文字列でないものは <式> で表示)
"""
import pathlib
import re
import sys

root = pathlib.Path(sys.argv[1])
skip = set(sys.argv[2:])
LIT = re.compile(r'\s*"((?:[^"\\]|\\.)*)"\s*')
OTHER = re.compile(r'\s*([^,()]+(?:\([^()]*\))?)\s*')


def arg(text, pos):
    m = LIT.match(text, pos)
    if m:
        return m.group(1), m.end()
    m = OTHER.match(text, pos)
    return ('<' + m.group(1).strip() + '>', m.end()) if m else (None, pos)


for mod in sorted(p for p in root.iterdir() if p.is_dir()):
    if mod.name in skip:
        continue
    for cs in sorted(mod.rglob('*.cs')):
        text = cs.read_text(encoding='utf-8-sig', errors='replace')
        for m in re.finditer(r'\.Bind(?:<([^>(]+)>)?\(', text):
            sec, p = arg(text, m.end())
            if sec is None or p >= len(text) or text[p] != ',':
                continue
            key, p = arg(text, p + 1)
            print(f'{mod.name}\t{m.group(1) or "?"}\t{sec}\t{key}')
