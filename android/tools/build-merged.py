#!/usr/bin/env python3
# -*- coding: utf-8 -*-
"""构建「热更资源合并目录」merged/。

  基准 assetmap = 1.4.140（new_assetmap.txt）
  优先级: new_bundles(1.4.140 整包) > apk_bundles(1.4.90 基线) > overlay(旧缓存)
  产出: merged/ = 可直接 push 到设备 <game>/files/bundles/ 或服务端 App 的 bundle/

用法:
    python build-merged.py
默认目录（相对 <repo>/android）:
    _work2/new_bundles, _work2/new_assetmap.txt, _work2/merged
    _work/apk_bundles, _work/overlay
"""
import os
import json
import shutil
import time

_HERE = os.path.dirname(os.path.abspath(__file__))
_ROOT = os.path.dirname(os.path.dirname(_HERE))          # <repo>
AW = os.path.join(_ROOT, 'android', '_work')
W2 = os.path.join(_ROOT, 'android', '_work2')
NEW = os.path.join(W2, 'new_bundles')
APK = os.path.join(AW, 'apk_bundles')
OVL = os.path.join(AW, 'overlay')
MERGED = os.path.join(W2, 'merged')


def load_am(path):
    out = {}
    with open(path, 'r', encoding='utf-8', errors='replace') as f:
        for n, line in enumerate(f):
            if n == 0:
                continue
            p = line.rstrip('\n').split(',')
            if len(p) == 4 and p[1].isdigit() and p[3].isdigit():
                out[p[0]] = (int(p[1]), p[2], int(p[3]))
    return out


def log(m):
    print('[%s] %s' % (time.strftime('%H:%M:%S'), m), flush=True)


def main():
    am = load_am(os.path.join(W2, 'new_assetmap.txt'))
    log('目标 assetmap: 1.4.140, %d 条' % len(am))

    if os.path.isdir(MERGED):
        log('清理旧 merged/ ...')
        shutil.rmtree(MERGED)
    os.makedirs(MERGED)

    # 1) 先整体拷贝 new_bundles（含 assetmap / bundles / external_files_list.txt / first_use_flag.txt）
    log('拷贝 new_bundles -> merged ...')
    n = 0
    for dp, _, fs in os.walk(NEW):
        rel_dir = os.path.relpath(dp, NEW)
        out_dir = MERGED if rel_dir == '.' else os.path.join(MERGED, rel_dir)
        os.makedirs(out_dir, exist_ok=True)
        for fn in fs:
            shutil.copy2(os.path.join(dp, fn), os.path.join(out_dir, fn))
            n += 1
    log('  拷贝 %d 个文件' % n)

    # 2) 填充缺口
    srcs = [('apk', APK), ('overlay', OVL)]
    filled = 0
    still_missing = []
    for rel, (crc, md5, size) in am.items():
        dst = os.path.join(MERGED, rel.replace('/', os.sep))
        if os.path.isfile(dst) and os.path.getsize(dst) == size:
            continue
        got = False
        for name, src in srcs:
            sp = os.path.join(src, rel.replace('/', os.sep))
            if os.path.isfile(sp) and os.path.getsize(sp) == size:
                os.makedirs(os.path.dirname(dst), exist_ok=True)
                shutil.copy2(sp, dst)
                filled += 1
                got = True
                break
        if not got:
            still_missing.append(rel)
    log('  填充 %d 个, 仍缺 %d 个' % (filled, len(still_missing)))

    # 3) 重新生成 external_files_list.txt
    #    ★ 排除 compressed/：那是给客户端热更用的 gzip 变体，不属于「资源清单」，
    #      列进去可能让客户端的清理/校验逻辑困惑。
    log('重新生成 external_files_list.txt ...')
    entries = []
    for dp, _, fs in os.walk(MERGED):
        for fn in fs:
            p = os.path.join(dp, fn)
            rel = os.path.relpath(p, MERGED).replace('\\', '/')
            if rel in ('external_files_list.txt', 'first_use_flag.txt'):
                continue
            if rel == 'compressed' or rel.startswith('compressed/'):
                continue
            entries.append(rel)
    entries.sort()
    with open(os.path.join(MERGED, 'external_files_list.txt'), 'w', encoding='utf-8', newline='\n') as f:
        f.write('\n'.join(entries) + '\n')
    log('  写入 %d 条' % len(entries))

    # 4) 校验
    total = sum(len(fs) for _, _, fs in os.walk(MERGED))
    log('merged 文件总数: %d' % total)
    log('最终覆盖: %d / %d, 缺 %d' % (len(am) - len(still_missing), len(am), len(still_missing)))
    if still_missing:
        from collections import Counter
        log('缺失分布: %s' % dict(Counter(r.split('/')[0] for r in still_missing)))
        json.dump(still_missing, open(os.path.join(W2, 'final_missing.json'), 'w'), indent=0)

    # 5) 生成 compressed/ 压缩块（客户端热更会来下载；缺了会导致整轮热更失败）
    _build_compressed_blobs()


def _build_compressed_blobs():
    """调用同目录的 build-compressed-blobs.py（客户端热更的 compressed/ 变体）。"""
    log('生成 compressed/ 压缩块 ...')
    try:
        import importlib.util
        path = os.path.join(_HERE, 'build-compressed-blobs.py')
        spec = importlib.util.spec_from_file_location('build_compressed_blobs', path)
        if spec is None or spec.loader is None:
            raise RuntimeError('无法加载 ' + path)
        mod = importlib.util.module_from_spec(spec)
        spec.loader.exec_module(mod)
        mod.build(MERGED, os.path.join(W2, 'new_assetmap.txt'))
    except (Exception, SystemExit) as ex:
        log('  ! compressed/ 生成失败：%s' % ex)
        log('  ! 客户端热更会 404；可稍后单独跑 android/tools/build-compressed-blobs.py')


if __name__ == '__main__':
    main()
