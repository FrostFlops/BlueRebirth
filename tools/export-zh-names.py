"""Generate src/BlueOath.Server/item-names-zh.json: Simplified Chinese names for the GM save editor.

The save editor shows the JP client's item and currency names; this table adds a Chinese name in
parentheses. For every bag item (the config_table_index tables ItemCatalogLoader merges) and every
currency of the JP client:

  1. the official name from the CN client's config (same id), if it has one;
  2. otherwise the translation of the JP name in tools/zh-names-extra.json (JP-only items).

Usage (standard library only):

  python tools/export-zh-names.py [--jp-config DIR] [--cn-config DIR] [--extra FILE] [--output FILE]

Defaults follow the repository layout: <repo>/blueoath/blueoath and <repo>/苍蓝誓约/clsy. JP names
without a Chinese name are listed on stderr; add them to zh-names-extra.json and rerun.
"""
import argparse
import json
import os
import sqlite3
import sys

ROOT = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
# Same tables and priority as ItemCatalogLoader.BagGoodsTypes.
BAG_GOODS_TYPES = [1, 6, 7, 8, 9, 10, 11, 12, 13, 14, 15, 23, 25, 28]


def rows(config_dir, file_name):
    """id -> decoded JSON row of a config_*.db (DBObject.jsonbytes XOR 0x55); a missing file is empty."""
    path = os.path.join(config_dir, file_name)
    if not os.path.exists(path):
        return {}
    con = sqlite3.connect('file:' + path.replace('\\', '/') + '?mode=ro', uri=True)
    try:
        out = {}
        for raw_id, data in con.execute('SELECT id, jsonbytes FROM DBObject'):
            try:
                row_id = int(raw_id)
            except (TypeError, ValueError):
                continue
            if row_id == 0 or data is None:
                continue
            if isinstance(data, str):
                data = data.encode('utf-8')
            try:
                out[row_id] = json.loads(bytes(b ^ 0x55 for b in data).decode('utf-8'))
            except (UnicodeDecodeError, json.JSONDecodeError):
                continue
        return out
    finally:
        con.close()


def bag_names(config_dir):
    """id -> name over the bag tables; an id in several tables keeps the first (as ItemCatalogLoader)."""
    index = rows(config_dir, 'config_table_index.db')
    names = {}
    for goods_type in BAG_GOODS_TYPES:
        table = index.get(goods_type)
        if not table or not table.get('file_name'):
            continue
        for row_id, row in rows(config_dir, table['file_name'] + '.db').items():
            name = row.get('name') or ''
            if row_id not in names and name:
                names[row_id] = name
    return names


def currency_names(config_dir):
    return {row_id: row['name'] for row_id, row in rows(config_dir, 'config_currency.db').items() if row.get('name')}


def translate(jp, cn, extra, missing):
    out = {}
    for row_id in sorted(jp):
        zh = cn.get(row_id) or extra.get(jp[row_id])
        if zh is None:
            missing.add(jp[row_id])
        elif zh != jp[row_id]:
            out[str(row_id)] = zh
    return out


def main():
    parser = argparse.ArgumentParser(description=__doc__.splitlines()[0])
    parser.add_argument('--jp-config', default=os.path.join(
        ROOT, 'blueoath', 'blueoath', 'blueoath_Data', 'StreamingAssets', 'config'))
    parser.add_argument('--cn-config', default=os.path.join(
        ROOT, '苍蓝誓约', 'clsy', 'clsy_Data', 'StreamingAssets', 'config'))
    parser.add_argument('--extra', default=os.path.join(ROOT, 'tools', 'zh-names-extra.json'))
    parser.add_argument('--output', default=os.path.join(ROOT, 'src', 'BlueOath.Server', 'item-names-zh.json'))
    args = parser.parse_args()

    for path in (args.jp_config, args.cn_config):
        if not os.path.isfile(os.path.join(path, 'config_table_index.db')):
            sys.exit(f'config directory not found: {path}')
    with open(args.extra, encoding='utf-8') as f:
        extra = json.load(f)

    missing = set()
    result = {
        'currencies': translate(currency_names(args.jp_config), currency_names(args.cn_config), extra, missing),
        'items': translate(bag_names(args.jp_config), bag_names(args.cn_config), extra, missing),
    }
    with open(args.output, 'w', encoding='utf-8', newline='\n') as f:
        json.dump(result, f, ensure_ascii=False, indent=1)
        f.write('\n')
    print(f"wrote {args.output}: {len(result['currencies'])} currencies, {len(result['items'])} items")
    for name in sorted(missing):
        print(f'no Chinese name: {name}', file=sys.stderr)
    return 1 if missing else 0


if __name__ == '__main__':
    sys.exit(main())
