"""逆コンパイルした MOD から、画面に出そうな文字列リテラルを抜き出す(ログ出力の行は除く)。

使い方: python -I extract_literals.py <MOD の逆コンパイル先フォルダ>
出力: ファイル名:行番号<TAB>文字列(重複は最初の 1 回だけ)
"""
import pathlib
import re
import sys

root = pathlib.Path(sys.argv[1])
LIT = re.compile(r'"((?:[^"\\]|\\.)*)"')
SKIP_LINE = re.compile(
    r'Log(Info|Warning|Error|Debug|Message|Fatal)?\s*\(|Debug\.Log|Console\.Write|\.Bind[<(]|'
    r'AccessTools|Harmony|BepInPlugin|BepInDependency|\[assembly|GetField|GetMethod|GetProperty|'
    r'Find\(|FindChild|Resources\.Load|LoadAsset|RPC\(|\.photonView|nameof\(|RaiseEvent|PlayerPrefs|'
    r'ManualLogSource|Exception\(|Shader\.|Animator|GetComponent|SetTrigger|SetBool|SetFloat|SetInteger|Path\.Combine')
seen = set()
for cs in sorted(root.rglob('*.cs')):
    if cs.name.startswith('AssemblyInfo') or 'Properties' in cs.parts:
        continue
    for no, line in enumerate(cs.read_text(encoding='utf-8-sig', errors='replace').splitlines(), 1):
        if SKIP_LINE.search(line):
            continue
        for s in LIT.findall(line):
            # 英字を含まないもの、識別子っぽい 1 語、パスや書式だけのものは除く
            if not re.search(r'[A-Za-z]', s) or s in seen:
                continue
            if re.fullmatch(r'[A-Za-z0-9_.:/\\-]+', s) and not re.search(r'[A-Z][a-z]+', s):
                continue
            seen.add(s)
            print(f'{cs.name}:{no}\t{s}')
