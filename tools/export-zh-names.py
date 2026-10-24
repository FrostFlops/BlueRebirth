"""Generate src/BlueOath.Server/item-names-zh.json: Simplified Chinese names for the GM save editor.

The save editor shows the JP client's item and currency names; this table adds a Chinese name in
parentheses. For every bag item (the config_table_index tables ItemCatalogLoader merges) and every
currency of the JP client:

  1. the official name from the CN client's config, if the CN row with the same id is the same item;
  2. otherwise the translation of the JP name in tools/zh-names-extra.json.

The two clients use many of the same ids for different items (blind boxes, stamps, event coins,
packs, decorations), so a shared id alone says nothing. The CN row counts as the same item when

  a. it comes from the same bag table (currencies: config_currency);
  b. it has the same icon sprite (icon): of the 729 item ids the hot-updated JP client shares with
     CN 1.5.120, the 132 with different icons are all different items except the six in
     CN_NAME_ALLOW;
  c. a blind box gives the same figure (blind_box_id): the boxes share one generic icon;
  d. the numbers in the two names agree when both have any (NFKC digits, so ① = 1): catches the
     level packs, numbered selection boxes and code-named items whose numbering moved;

and the id is not in CN_NAME_DENY (same icon, but the CN name does not fit the JP item, found by
comparing contents and descriptions by hand; Chinese numerals such as 第七期 escape rule d). Every
shared id was reviewed against this rule; the lists below give the reason for each exception.
Re-review them when the clients change.

Usage (standard library only):

  python tools/export-zh-names.py [--jp-config DIR] [--cn-config DIR] [--extra FILE] [--output FILE]

Defaults follow the repository layout: <repo>/blueoath/blueoath and <repo>/苍蓝誓约/clsy. JP names
without a Chinese name are listed on stderr; add them to zh-names-extra.json and rerun.

Generate it from a JP client with the final hot update (2023-02) applied: its bag tables and
currencies are a superset of the unpatched 1.4.0 ones (same ids and names, 215 more items), so one
table serves both clients. Running it on an unpatched client drops the hot-update items, and the
item catalog test then fails on hot-updated clients.
"""
import argparse
import json
import os
import re
import sqlite3
import sys
import unicodedata

ROOT = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
# Same tables and priority as ItemCatalogLoader.BagGoodsTypes.
BAG_GOODS_TYPES = [1, 6, 7, 8, 9, 10, 11, 12, 13, 14, 15, 23, 25, 28]

# Same table, icon, figure and numbers, but the CN name does not fit the JP item (a different item,
# or one whose CN name names another event, currency or issue number): never use it.
CN_NAME_DENY = {
    'items': {
        10201: 'JP gives the peace stamp, CN (第一期作战嘉奖) the Graf Zeppelin stamp',
        11050: 'JP 限定パック① holds fuel, upper vow stones and soda; CN 元气要满加油包 supply and blue chips',
        11051: 'JP 限定パック② holds fuel, upper vow stones and soda; CN 元气充沛加油包 supply and blue chips',
        11052: 'JP 限定パック③ holds fuel, upper vow stones and soda; CN 元气十足加油包 supply and blue chips',
        11053: 'JP 限定パック④ holds fuel, upper vow stones and soda; CN 元气大满加油包 supply and blue chips',
        17002: 'JP generic event point; CN 遛狗点数 is a CN event',
        17005: 'JP the farmed anglerfish; CN 17005 is the frozen one (CN 17018 is the farmed one)',
        17536: 'JP 和菓子 is the 大願成就 exchange item; CN 整蛊PT is another event point',
        **{row_id: 'JP SKM slogan items; the CN 无用的漂流物 codes are in another order'
           for row_id in range(19500, 19515)},
        80040: 'different weapon lists: JP 53cm quad torpedo, 40.6cm Mk.6 ...; CN 蓝星 120mm, 356mm ...',
        80044: 'JP regular SSR equipment (the CN 80040 list); CN 穆伯武器选择箱',
        251008: 'JP issue 6, CN 第七期 (the CN magazine numbering is one ahead)',
    },
    'currencies': {
        16: 'the real-money currency: JPY on the JP client, RMB on the CN client',
    },
}
# Different icons, but the same item (same contents or the same stamp): use the CN name.
CN_NAME_ALLOW = {
    'items': {
        19716: 'same comeback box (drop 3000096: diamonds x1200, recommendation letters x10, vow box x36)',
        19717: 'same comeback box (drop 3000097: diamonds, blue chips, sapphires, dress tickets x196)',
        19912: 'same twin-sisters stamp; JP uses the localized sprite 408_050_ri',
        19932: 'Zuikaku stamp on both clients; the stamp sprites were renumbered',
        19933: 'Wichita stamp on both clients; the stamp sprites were renumbered',
        19934: 'Jervis stamp on both clients; the stamp sprites were renumbered',
    },
    'currencies': {},
}


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


def bag_rows(config_dir):
    """id -> (table, row) over the bag tables; an id in several tables keeps the first (as ItemCatalogLoader)."""
    index = rows(config_dir, 'config_table_index.db')
    out = {}
    for goods_type in BAG_GOODS_TYPES:
        table = index.get(goods_type)
        if not table or not table.get('file_name'):
            continue
        for row_id, row in rows(config_dir, table['file_name'] + '.db').items():
            if row_id not in out and row.get('name'):
                out[row_id] = (table['file_name'], row)
    return out


def currency_rows(config_dir):
    return {row_id: ('config_currency', row)
            for row_id, row in rows(config_dir, 'config_currency.db').items() if row.get('name')}


def name_numbers(name):
    return set(re.findall(r'[0-9]+', unicodedata.normalize('NFKC', name)))


def same_item(jp, cn):
    """Rules a-d of the module docstring."""
    (jp_table, jp_row), (cn_table, cn_row) = jp, cn
    if jp_table != cn_table or jp_row.get('icon') != cn_row.get('icon'):
        return False
    if (jp_row.get('blind_box_id') or []) != (cn_row.get('blind_box_id') or []):
        return False
    jp_numbers, cn_numbers = name_numbers(jp_row['name']), name_numbers(cn_row['name'])
    return not (jp_numbers and cn_numbers and jp_numbers != cn_numbers)


def translate(kind, jp, cn, extra, missing, stats):
    deny, allow = CN_NAME_DENY[kind], CN_NAME_ALLOW[kind]
    out = {}
    for row_id in sorted(jp):
        name = jp[row_id][1]['name']
        zh = None
        if row_id in cn:
            if row_id in allow or (row_id not in deny and same_item(jp[row_id], cn[row_id])):
                zh = cn[row_id][1]['name'].strip()
                stats['cn'] += 1
            else:
                stats['rejected'] += 1
        if zh is None:
            zh = extra.get(name)
            if zh is not None:
                stats['extra'] += 1
        if zh is None:
            missing.add(name)
        elif zh != name:
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
    stats = {'cn': 0, 'rejected': 0, 'extra': 0}
    result = {
        'currencies': translate('currencies', currency_rows(args.jp_config), currency_rows(args.cn_config),
                                extra, missing, stats),
        'items': translate('items', bag_rows(args.jp_config), bag_rows(args.cn_config), extra, missing, stats),
    }
    with open(args.output, 'w', encoding='utf-8', newline='\n') as f:
        json.dump(result, f, ensure_ascii=False, indent=1)
        f.write('\n')
    print(f"wrote {args.output}: {len(result['currencies'])} currencies, {len(result['items'])} items "
          f"({stats['cn']} CN names, {stats['rejected']} CN names rejected as different items, "
          f"{stats['extra']} translated)")
    for name in sorted(missing):
        print(f'no Chinese name: {name}', file=sys.stderr)
    return 1 if missing else 0


if __name__ == '__main__':
    sys.exit(main())
