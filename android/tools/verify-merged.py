#!/usr/bin/env python3
"""用 1.4.140 的 assetmap（new_assetmap.txt）全量校验 merged/ 是否与期望 md5/大小一致。

new_assetmap.txt 行格式（普通文件）：`<rel>, <md5 32hex>, <size>, <compressed_size>`
（atlases 等目录条目的第 2 字段是十进制数字，不是 md5 —— 自动跳过。）

用法:
    python verify-merged.py [merged_dir] [assetmap_txt]
默认:
    merged_dir  = <repo>/android/_work2/merged
    assetmap    = <repo>/android/_work2/new_assetmap.txt
"""
import hashlib
import os
import re
import sys

_HERE = os.path.dirname(os.path.abspath(__file__))
_ROOT = os.path.dirname(os.path.dirname(_HERE))          # <repo>
_WORK2 = os.path.join(_ROOT, 'android', '_work2')

MERGED = sys.argv[1] if len(sys.argv) > 1 else os.path.join(_WORK2, 'merged')
TXT = sys.argv[2] if len(sys.argv) > 2 else os.path.join(_WORK2, 'new_assetmap.txt')

HEX32 = re.compile(r'^[0-9A-Fa-f]{32}$')


def md5_of(path):
    h = hashlib.md5()
    with open(path, 'rb') as f:
        for chunk in iter(lambda: f.read(1 << 20), b''):
            h.update(chunk)
    return h.hexdigest()


def main():
    entries = []
    with open(TXT, 'r', encoding='utf-8', errors='replace') as f:
        for line in f:
            line = line.rstrip('\n')
            if not line or ',' not in line:
                continue
            parts = line.split(',')
            if len(parts) < 3:
                continue
            rel, h, size = parts[0], parts[1], parts[2]
            if not HEX32.match(h):
                continue
            try:
                size = int(size)
            except ValueError:
                continue
            entries.append((rel, h.lower(), size))

    print('assetmap 普通文件条目:', len(entries))
    missing, bad_size, bad_md5, ok = [], [], [], 0
    for rel, h, size in entries:
        p = os.path.join(MERGED, rel.replace('/', os.sep))
        if not os.path.exists(p):
            missing.append(rel)
            continue
        actual = os.path.getsize(p)
        if actual != size:
            bad_size.append((rel, actual, size))
            continue
        if md5_of(p).lower() != h:
            bad_md5.append(rel)
            continue
        ok += 1

    print('OK:', ok)
    print('缺失:', len(missing))
    for x in missing[:20]:
        print('   MISS ', x)
    print('大小不符:', len(bad_size))
    for rel, a, e in bad_size[:20]:
        print('   SIZE %s  actual=%d expect=%d' % (rel, a, e))
    print('md5 不符:', len(bad_md5))
    for x in bad_md5[:40]:
        print('   MD5  ', x)

    # 非 0 退出码便于 CI / 脚本判断
    sys.exit(0 if not (missing or bad_size or bad_md5) else 1)


if __name__ == '__main__':
    main()
