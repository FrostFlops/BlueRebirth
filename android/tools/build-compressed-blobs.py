#!/usr/bin/env python3
# -*- coding: utf-8 -*-
"""为热更资源包生成 `compressed/` 命名空间（客户端热更时会来下载）。

背景（2026-10-11 定位）
------------------------
客户端热更下载分两套 URL：

  ① 普通文件：`GET /windows_android/<rel>_<crc32>`          —— 直接给原文件；
  ② 压缩文件：`GET /windows_android/compressed/<rel>_<MD5>` —— CDN 上存的是
     **gzip 压缩块**，客户端下载后自己解压，再用 MD5 校验解压结果。

哪些文件走 ②，由 assetmap 决定：凡条目形如
    `<rel>,<MD5 32hex 大写>,<原始大小>,<压缩后大小>`
且**压缩后大小 > 0** 的，客户端就只从 `compressed/` 取；我们若没有这一份，
请求会 404，客户端对每个文件重试 10 次后**整轮热更直接失败**
（现象：进度停在 76%、「剩余 750MB」时弹「ダウンロードに失敗しました」，
 日志里 NetworkFailTimes 一路涨）。

实测：`config/*.db`（497 个里我们包里有的那 72 个）压缩后大小与
「gzip, compresslevel=6, mtime=0」的输出**字节数完全一致**；
`wwise/*` 的第 4 字段是 0（不压缩，走 ①），所以只需为 config 生成。

用法
    python build-compressed-blobs.py                 # 生成到 <merged>/compressed/
    python build-compressed-blobs.py --verify-only    # 只校验已有块的大小
"""
import argparse
import gzip
import io
import os
import re
import sys

_HERE = os.path.dirname(os.path.abspath(__file__))
_ROOT = os.path.dirname(os.path.dirname(_HERE))
_WORK2 = os.path.join(_ROOT, 'android', '_work2')

HEX32 = re.compile(r'^[0-9A-F]{32}$')
DEC = re.compile(r'^\d+$')


def log(m):
    print(m, flush=True)


def load_compressed_entries(assetmap_txt):
    """返回 [(rel, 原始大小, 压缩后大小)]，只含压缩后大小 > 0 的条目。"""
    out = []
    with open(assetmap_txt, 'r', encoding='utf-8', errors='replace') as f:
        for line in f:
            p = line.rstrip('\n').split(',')
            if len(p) != 4:
                continue
            rel, md5, raw, comp = p
            if not HEX32.match(md5) or not DEC.match(raw) or not DEC.match(comp):
                continue
            if int(comp) <= 0:
                continue
            out.append((rel, int(raw), int(comp)))
    return out


def gzip_blob(data):
    """与 CDN 一致：gzip、level 6、mtime=0（size 与 assetmap 声明逐字节相等）。"""
    buf = io.BytesIO()
    with gzip.GzipFile(fileobj=buf, mode='wb', compresslevel=6, mtime=0) as g:
        g.write(data)
    return buf.getvalue()


def build(bundle, assetmap_txt):
    entries = load_compressed_entries(assetmap_txt)
    log('assetmap 中「需要 compressed/ 变体」的条目：%d 个' % len(entries))

    made = skipped = bad = 0
    total = 0
    for rel, raw_size, comp_size in entries:
        src = os.path.join(bundle, rel.replace('/', os.sep))
        if not os.path.isfile(src):
            skipped += 1          # 由客户端 APK 基线满足，不必提供
            continue
        data = open(src, 'rb').read()
        if len(data) != raw_size:
            log('  ! 原始文件大小不符（跳过）：%s（本地 %d / assetmap %d）'
                % (rel, len(data), raw_size))
            bad += 1
            continue
        blob = gzip_blob(data)
        if len(blob) != comp_size:
            log('  ! 压缩后大小不符：%s（%d / 期望 %d）' % (rel, len(blob), comp_size))
            bad += 1
            continue
        dst = os.path.join(bundle, 'compressed', rel.replace('/', os.sep))
        os.makedirs(os.path.dirname(dst), exist_ok=True)
        open(dst, 'wb').write(blob)
        made += 1
        total += len(blob)

    log('已生成 %d 个压缩块（%.1f MB）；跳过 %d（包内没有，由 APK 基线满足）；异常 %d'
        % (made, total / 1048576, skipped, bad))
    if bad:
        raise SystemExit('有 %d 个文件压缩后大小与 assetmap 不符，请检查' % bad)
    log('写入目录：' + os.path.join(bundle, 'compressed'))


def verify(bundle, assetmap_txt):
    entries = load_compressed_entries(assetmap_txt)
    ok = missing = badsize = 0
    for rel, _raw, comp in entries:
        p = os.path.join(bundle, 'compressed', rel.replace('/', os.sep))
        if not os.path.isfile(p):
            if os.path.isfile(os.path.join(bundle, rel.replace('/', os.sep))):
                missing += 1        # 本地有原文件却没生成压缩块
            continue
        if os.path.getsize(p) != comp:
            badsize += 1
            log('  ! 压缩块大小不符：%s' % rel)
        else:
            ok += 1
    log('校验：OK %d / 应有但缺失 %d / 大小不符 %d' % (ok, missing, badsize))
    if missing or badsize:
        raise SystemExit('compressed/ 不完整')


def main():
    ap = argparse.ArgumentParser(description='生成/校验热更包的 compressed/ 压缩块')
    ap.add_argument('--bundle', default=os.path.join(_WORK2, 'merged'))
    ap.add_argument('--assetmap', default=os.path.join(_WORK2, 'new_assetmap.txt'))
    ap.add_argument('--verify-only', action='store_true')
    args = ap.parse_args()

    bundle = os.path.abspath(args.bundle)
    assetmap = os.path.abspath(args.assetmap)
    if not os.path.isdir(bundle):
        raise SystemExit('找不到资源包目录：' + bundle)
    if not os.path.isfile(assetmap):
        raise SystemExit('找不到 assetmap 文本：' + assetmap)

    if args.verify_only:
        verify(bundle, assetmap)
    else:
        build(bundle, assetmap)


if __name__ == '__main__':
    sys.exit(main())
