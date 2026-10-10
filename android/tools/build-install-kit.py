#!/usr/bin/env python3
# -*- coding: utf-8 -*-
"""打包「安装套件」(.brk)：把客户端 APK + 热更资源包合成一个文件，供 BlueRebirthApp 内部安装。

设计要点
  · 单文件、无外网依赖、无需随机 seek —— App 侧用 SAF 拿到的 content:// 流顺序读即可。
  · 不用 ZIP：套件 >4GB 需要 Zip64，且 ZIP 要从**尾部**读中央目录，SAF 的流不保证可定位。
  · 逐文件带 md5 —— App 侧边写边校验，且支持「中断后重跑跳过已完成文件」。
  · 头部的 clientVersion 从 assetmap 里自动识别（写死版本号很坑），App 据此自动对齐
    服务端的 tar_version，根治「版本对不上永远停在热更页」。

格式 BRKIT001
    [0:8)               magic  b"BRKIT001"
    [8:16)              uint64 LE  headerLen
    [16:16+headerLen)   header JSON (UTF-8)
    [16+headerLen:)     payload：按 header.entries 顺序拼接
        · kind=bundle 的 payload 是「文件表」，逐条：
              u8   kind      0=文件 1=结束 2=空目录
              u16  pathLen LE
              u32  dataLen LE   (kind=2 时为 0)
              path  (UTF-8, '/' 分隔)
              [16] md5          (kind=2 时省略)
              data
        · kind=apk 的 payload 就是 APK 原始字节

用法
    # 打包
    python build-install-kit.py
    python build-install-kit.py --out D:/BlueRebirth-kit.brk --launcher-apk <BlueRebirthApp.apk>

    # 校验已产出的套件（重算每个文件的 md5）
    python build-install-kit.py --verify-only android/finel_clients/BlueRebirth-kit-1.4.140.brk
"""
import argparse
import hashlib
import json
import os
import re
import struct
import sys
import time

MAGIC = b"BRKIT001"
FORMAT = 1
CHUNK = 1 << 20

_HERE = os.path.dirname(os.path.abspath(__file__))
_ROOT = os.path.dirname(os.path.dirname(_HERE))
_WORK2 = os.path.join(_ROOT, 'android', '_work2')
_FINEL = os.path.join(_ROOT, 'android', 'finel_clients')

KIND_FILE, KIND_END, KIND_DIR = 0, 1, 2


def log(m):
    print('[%s] %s' % (time.strftime('%H:%M:%S'), m), flush=True)


def md5_of(path):
    h = hashlib.md5()
    with open(path, 'rb') as f:
        for chunk in iter(lambda: f.read(CHUNK), b''):
            h.update(chunk)
    return h.digest()


def detect_client_version(assetmap_path):
    """从 assetmap(UnityFS) 里取版本号；该文件里唯一出现的 x.y.z 就是它。"""
    with open(assetmap_path, 'rb') as f:
        data = f.read(1 << 20)
    found = []
    for m in re.finditer(rb'(?<![\d.])(\d{1,2}\.\d{1,2}\.\d{1,3})(?![\d.])', data):
        v = m.group(1).decode()
        if v not in found:
            found.append(v)
    return found


def collect_bundle(bundle_root):
    """返回 (files, dirs)，均为 bundle_root 下的相对路径（'/' 分隔），已排序。"""
    files, dirs = [], []
    for dp, dns, fns in os.walk(bundle_root):
        dns.sort()
        rel_dir = os.path.relpath(dp, bundle_root).replace('\\', '/')
        if rel_dir != '.':
            dirs.append(rel_dir)
        for fn in sorted(fns):
            rel = fn if rel_dir == '.' else rel_dir + '/' + fn
            files.append((rel, os.path.join(dp, fn)))
    return files, sorted(dirs)


def table_length(files, dirs):
    # 结束标记同样是一条完整记录（u8 kind + u16 pathLen + u32 dataLen = 7 字节），
    # App 侧固定按 7 字节读记录头。
    total = 7
    for rel, path in files:
        total += 1 + 2 + 4 + len(rel.encode('utf-8')) + 16 + os.path.getsize(path)
    for rel in dirs:
        total += 1 + 2 + 4 + len(rel.encode('utf-8'))
    return total


def write_bundle_table(out, files, dirs, progress=None):
    """把文件表写进 out；返回 (文件数, 总字节)。"""
    written = 0
    count = 0
    for rel in dirs:
        pb = rel.encode('utf-8')
        out.write(struct.pack('<BHI', KIND_DIR, len(pb), 0))
        out.write(pb)
        written += 1 + 2 + 4 + len(pb)
    for rel, path in files:
        pb = rel.encode('utf-8')
        size = os.path.getsize(path)
        if size > 0xFFFFFFFF:
            raise SystemExit('单个文件超过 4GB，v1 格式不支持：' + rel)
        digest = md5_of(path)
        out.write(struct.pack('<BHI', KIND_FILE, len(pb), size))
        out.write(pb)
        out.write(digest)
        with open(path, 'rb') as src:
            while True:
                chunk = src.read(CHUNK)
                if not chunk:
                    break
                out.write(chunk)
        written += 1 + 2 + 4 + len(pb) + 16 + size
        count += 1
        if progress and count % 500 == 0:
            progress(count, len(files), written)
    out.write(struct.pack('<BHI', KIND_END, 0, 0))
    written += 1 + 2 + 4
    return count, written


def build(args):
    bundle_root = os.path.abspath(args.bundle)
    client_apk = os.path.abspath(args.client_apk)
    launcher_apk = os.path.abspath(args.launcher_apk) if args.launcher_apk else None

    if not os.path.isdir(bundle_root):
        raise SystemExit('找不到资源包目录：' + bundle_root)
    if not os.path.isfile(client_apk):
        raise SystemExit('找不到客户端 APK：' + client_apk)

    assetmap = os.path.join(bundle_root, 'assetmap')
    if not os.path.isfile(assetmap):
        raise SystemExit('资源包里没有 assetmap：' + assetmap)

    version = args.client_version
    if not version:
        cands = detect_client_version(assetmap)
        if len(cands) != 1:
            raise SystemExit('无法从 assetmap 唯一确定版本号，候选=%s；请用 --client-version 指定' % cands)
        version = cands[0]
    log('客户端版本（assetmap）= %s' % version)

    files, dirs = collect_bundle(bundle_root)
    log('资源包：%d 个文件 / %d 个目录' % (len(files), len(dirs)))

    bundle_table_len = table_length(files, dirs)
    entries = [{
        'kind': 'bundle', 'offset': 0, 'length': bundle_table_len,
        'fileCount': len(files), 'clientVersion': version,
    }]

    apks = [('client', client_apk, 'com.zephyrus.clsy.gp')]
    if launcher_apk:
        apks.append(('launcher', launcher_apk, 'com.blueoath.server'))

    offset = bundle_table_len
    log('预计算 APK md5…')
    for role, path, pkg in apks:
        size = os.path.getsize(path)
        digest = hashlib.md5(open(path, 'rb').read()).hexdigest()
        log('  %-8s %-46s %.1f MB  md5=%s' % (role, os.path.basename(path), size / 1048576, digest))
        entries.append({
            'kind': 'apk', 'role': role, 'offset': offset, 'length': size,
            'package': pkg, 'md5': digest, 'fileName': os.path.basename(path),
        })
        offset += size

    header = {
        'format': FORMAT,
        'clientVersion': version,
        'createdUtc': time.strftime('%Y-%m-%dT%H:%M:%SZ', time.gmtime()),
        'payloadLength': offset,
        'entries': entries,
    }
    hb = json.dumps(header, separators=(',', ':')).encode('utf-8')

    out_path = os.path.abspath(args.out or os.path.join(
        _FINEL, 'BlueRebirth-kit-%s.brk' % version))
    os.makedirs(os.path.dirname(out_path), exist_ok=True)

    log('写出 %s' % out_path)
    t0 = time.time()
    with open(out_path, 'wb') as out:
        out.write(MAGIC)
        out.write(struct.pack('<Q', len(hb)))
        out.write(hb)

        def prog(done, total, written):
            pct = written * 100.0 / max(header['payloadLength'], 1)
            log('  资源 %d/%d  (%.1f%%)' % (done, total, pct))

        n, written = write_bundle_table(out, files, dirs, prog)
        log('  资源文件表完成：%d 个文件，%d 字节' % (n, written))
        if written != bundle_table_len:
            raise SystemExit('内部错误：文件表长度 %d != 预算 %d' % (written, bundle_table_len))

        for role, path, pkg in apks:
            log('  写入 APK：%s' % os.path.basename(path))
            with open(path, 'rb') as src:
                while True:
                    chunk = src.read(CHUNK)
                    if not chunk:
                        break
                    out.write(chunk)

    size = os.path.getsize(out_path)
    log('完成：%s（%.2f GB，用时 %.0fs）' % (out_path, size / 1073741824, time.time() - t0))
    log('头部 %d 字节；payload 预算 %d，实际 %d' % (len(hb), header['payloadLength'], size - 16 - len(hb)))


def verify(kit_path):
    log('校验 %s' % kit_path)
    with open(kit_path, 'rb') as f:
        if f.read(8) != MAGIC:
            raise SystemExit('magic 不匹配，不是 BRKIT001 套件')
        header_len = struct.unpack('<Q', f.read(8))[0]
        payload_start = 16 + header_len
        header = json.loads(f.read(header_len).decode('utf-8'))
        log('格式 v%s，客户端版本 %s，payload %d 字节'
            % (header['format'], header['clientVersion'], header['payloadLength']))

        bad = 0
        for e in header['entries']:
            if e['kind'] == 'apk':
                h = hashlib.md5()
                left = e['length']
                while left > 0:
                    chunk = f.read(min(CHUNK, left))
                    if not chunk:
                        raise SystemExit('APK 数据提前结束')
                    h.update(chunk)
                    left -= len(chunk)
                ok = h.hexdigest() == e['md5']
                log('  apk %-8s %s  %s' % (e['role'], 'OK' if ok else 'MISMATCH', e['md5']))
                bad += 0 if ok else 1
                continue
            # bundle 文件表
            end = e['offset'] + e['length']
            start = f.tell()
            n = 0
            while f.tell() - start < e['length']:
                kind = f.read(1)[0]
                if kind == KIND_END:
                    break
                path_len, data_len = struct.unpack('<HI', f.read(6))
                path = f.read(path_len).decode('utf-8')
                if kind == KIND_DIR:
                    continue
                digest = f.read(16)
                h = hashlib.md5()
                left = data_len
                while left > 0:
                    chunk = f.read(min(CHUNK, left))
                    if not chunk:
                        raise SystemExit('bundle 数据提前结束')
                    h.update(chunk)
                    left -= len(chunk)
                if h.digest() != digest:
                    bad += 1
                    if bad < 10:
                        log('  MD5 MISMATCH %s' % path)
                n += 1
            log('  bundle %d 个文件，%s' % (n, 'OK' if bad == 0 else '%d 个不符' % bad))
            f.seek(payload_start + end)

    if bad:
        raise SystemExit('校验失败：%d 处不符' % bad)
    log('校验通过')


def extract(kit_path, out_dir):
    """按格式把套件解出来（用于校验与排障；App 侧是等价的流式实现）。"""
    log('解包 %s -> %s' % (kit_path, out_dir))
    with open(kit_path, 'rb') as f:
        if f.read(8) != MAGIC:
            raise SystemExit('magic 不匹配')
        header_len = struct.unpack('<Q', f.read(8))[0]
        payload_start = 16 + header_len
        header = json.loads(f.read(header_len).decode('utf-8'))
        log('客户端版本 %s' % header['clientVersion'])

        for e in header['entries']:
            if e['kind'] != 'bundle':
                continue
            base = os.path.join(out_dir, 'bundle')
            os.makedirs(base, exist_ok=True)
            start = f.tell()
            n = 0
            while f.tell() - start < e['length']:
                kind = f.read(1)[0]
                if kind == KIND_END:
                    break
                path_len, data_len = struct.unpack('<HI', f.read(6))
                rel = f.read(path_len).decode('utf-8')
                target = os.path.join(base, *rel.split('/'))
                if kind == KIND_DIR:
                    os.makedirs(target, exist_ok=True)
                    continue
                digest = f.read(16)
                os.makedirs(os.path.dirname(target), exist_ok=True)
                h = hashlib.md5()
                with open(target, 'wb') as o:
                    left = data_len
                    while left > 0:
                        chunk = f.read(min(CHUNK, left))
                        if not chunk:
                            raise SystemExit('数据提前结束')
                        h.update(chunk)
                        o.write(chunk)
                        left -= len(chunk)
                if h.digest() != digest:
                    raise SystemExit('md5 不符：' + rel)
                n += 1
            log('  bundle：%d 个文件' % n)
            f.seek(payload_start + e['offset'] + e['length'])

        for e in header['entries']:
            if e['kind'] != 'apk':
                continue
            f.seek(payload_start + e['offset'])
            name = e.get('fileName') or (e.get('role', 'apk') + '.apk')
            target = os.path.join(out_dir, name)
            os.makedirs(os.path.dirname(target), exist_ok=True)
            h = hashlib.md5()
            with open(target, 'wb') as o:
                left = e['length']
                while left > 0:
                    chunk = f.read(min(CHUNK, left))
                    if not chunk:
                        raise SystemExit('APK 数据提前结束')
                    h.update(chunk)
                    o.write(chunk)
                    left -= len(chunk)
            ok = h.hexdigest() == e['md5']
            log('  apk %-8s -> %s  %s' % (e.get('role'), name, 'OK' if ok else 'MISMATCH'))
            if not ok:
                raise SystemExit('APK md5 不符')
    log('解包完成')


def main():
    ap = argparse.ArgumentParser(description='BlueRebirth 安装套件打包/校验/解包')
    ap.add_argument('--bundle', default=os.path.join(_WORK2, 'merged'), help='热更资源包目录')
    ap.add_argument('--client-apk', default=os.path.join(_FINEL, 'BlueRebirth-client-1.4.140-planL.apk'))
    ap.add_argument('--launcher-apk', default=None, help='可选：把启动器 APK 也放进套件')
    ap.add_argument('--client-version', default=None, help='默认从 assetmap 自动识别')
    ap.add_argument('--out', default=None)
    ap.add_argument('--verify-only', default=None, help='只校验已产出的套件')
    ap.add_argument('--extract-to', nargs=2, metavar=('KIT', 'DIR'), default=None,
                    help='把套件解到目录（校验/排障用）')
    args = ap.parse_args()

    if args.verify_only:
        verify(os.path.abspath(args.verify_only))
    elif args.extract_to:
        extract(os.path.abspath(args.extract_to[0]), os.path.abspath(args.extract_to[1]))
    else:
        build(args)


if __name__ == '__main__':
    main()
