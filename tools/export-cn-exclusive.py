"""Generate Mods/cn-exclusive.mod/data.lua, memory-chapters.json and decorations.json from the CN 1.5.120 and JP 1.4.0
clients.

Everything cn-exclusive.mod restores is already inside the JP client, only switched off or unreachable; the
mod ships ids and the CN text for a few fields, never game assets. This script derives that data:

  fubuki_spring_fashion   config_fashion 2013014 is_appear (FashionLogic lists only is_appear == 1).
  oakland_spring_touch    config_fashion 1021055 unlock_action / cv_show (read by HomeMainState, GirlInfo and
                          ShowGirlPage through configManager.GetDataById).
  spring_festival_story   CN chapter 12000 (春节剧情) as the new chapter 12010 (JP reused 12000), memory_id 13,
                          and the CN names/descriptions of its nodes config_copy_display 50301-50307.
  character_stories       config_building_character_story rows whose plot_trigger_id no JP row uses; a ship
                          whose CN ids are taken in JP is renumbered as a block above both clients' ids.
                          Plus config_ship_fleet plot_cover where JP has none for those ships.
  cn_voice_<ship>         宁海/平海: event id maps (plus the bank id its Play actions name) that turn the JP
                          client's own Chinese-voice bank c_cl_<ship>_bank into c_cl_<ship>_jp_bank, id swaps in
                          the shared banks that hold both
                          voices, the CN wedding text and the CN captions (read by C# only, so not in patches).
  spring_furniture        CN furniture set 5 (春节家具套装): config_interaction_item_bag_group 5 and its pieces
                          config_interaction_item_bag 250041-250049, unused ids in JP. Each piece points at the JP
                          config_interaction_item with the same three models (36-44; CN numbers them 181236-181244).
                          The CN *_124 icons and the cover uipic_ui_lunarnewyear_cover are not in the JP client: icon
                          and icon_small use the piece's picture (the sprite its JP interaction item shows) and the
                          cover uses the lantern's picture. decorations.json declares the same ids to the server.

Usage (standard library only):

  python tools/export-cn-exclusive.py [--jp-client DIR] [--cn-client DIR] [--output-dir DIR]

Defaults follow the repository layout: <repo>/blueoath/blueoath and <repo>/苍蓝誓约/clsy. The output is
deterministic. Before writing, data.lua is parsed back and every value is checked against the configs and
bank files, and decorations.json against the data.lua rows. A summary (counts per feature, renumbered ids,
warnings) goes to stderr.
"""
import argparse
import json
import math
import os
import re
import sqlite3
import struct
import sys

ROOT = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
FORMAT_VERSION = 1

FUBUKI_FASHION = 2013014
OAKLAND_FASHION = 1021055
CN_SPRING_CHAPTER = 12000
SPRING_CHAPTER = 12010
SPRING_MEMORY_ID = 13
# JP クリスマスシナリオ: a finished event story in the memory list with no period or activity behind it, the
# same role as the new chapter (JP 12000 is tied to its own activity period). Source of JP-only chapter keys.
CHAPTER_TEMPLATE = 10041
VOICE_SHIPS = (('ninghai', 9024011), ('pinghai', 9024021))  # model name part, ship_show / talker id
CAPTION_FIELDS = ('cn_text', 'caption_open', 'caption_end')
# Candidate `expect` fields per patched table. Only those equal in both clients and plain (number, boolean,
# non-empty ASCII string) are emitted: never text, which a translated client may have changed.
EXPECT_FIELDS = {
    'config_fashion': ('belong_to_ship', 'ship_show_id', 'icon'),
    'config_copy_display': ('copy_display_type', 'copy_index', 'copy_thumbnail_before'),
    'config_ship_fleet': ('id', 'valentine_gift'),
    'config_plot_episode_wedding': ('plot_episode_id', 'step', 'talker_id'),
}
FEATURES = ('fubuki_spring_fashion', 'oakland_spring_touch', 'spring_festival_story',
            'character_stories') + tuple('cn_voice_' + ship for ship, _ in VOICE_SHIPS) + ('spring_furniture',)

BAG_TABLE = 'config_interaction_item_bag'
GROUP_TABLE = 'config_interaction_item_bag_group'
ITEM_TABLE = 'config_interaction_item'
SPRING_GROUP = 5                 # CN 春节家具套装; JP uses sets 1 and 3, so the mod keeps the id
BAG_ID_RANGE = (250000, 259999)  # the client finds a decoration's table by id // 10000 = 25
FURNITURE_ITEM = 2               # InteractionItemType.Furniture: shown at home, does nothing when tapped
# JP 聖誕リース①, a piece of the JP Christmas set: source of the bag keys the CN rows lack (reward, show_type).
BAG_TEMPLATE = 255801
GROUP_TEMPLATE = 3
SPRING_COVER_BAG = 250049        # 灯笼: its picture stands in for the CN-only cover uipic_ui_lunarnewyear_cover
MODEL_FIELDS = ('item_name', 'item_name_dawn', 'item_name_night')


def fail(message):
    sys.exit('export-cn-exclusive: ' + message)


def streaming_assets(client):
    """<client>/<name>_Data/StreamingAssets: blueoath_Data for JP, clsy_Data for CN."""
    if os.path.isdir(client):
        for name in sorted(os.listdir(client)):
            path = os.path.join(client, name, 'StreamingAssets')
            if name.endswith('_Data') and os.path.isdir(os.path.join(path, 'config')):
                return path
    fail(f'no <name>_Data/StreamingAssets/config under {client}')


class Config:
    """Decoded config tables of one client: id -> row (DBObject.jsonbytes XOR 0x55), cached per table.

    Integer ids become ints and other ids stay strings; the 'nill' checksum row is skipped.
    """

    def __init__(self, client):
        self.dir = os.path.join(streaming_assets(client), 'config')
        self._tables = {}

    def __getitem__(self, table):
        if table not in self._tables:
            self._tables[table] = self._load(table)
        return self._tables[table]

    def _load(self, table):
        path = os.path.join(self.dir, table + '.db')
        if not os.path.exists(path):
            fail(f'missing {path}')
        con = sqlite3.connect('file:' + path.replace('\\', '/') + '?mode=ro', uri=True)
        try:
            out = {}
            for raw_id, data in con.execute('SELECT id, jsonbytes FROM DBObject'):
                if raw_id is None or raw_id == 'nill' or data is None:
                    continue
                if isinstance(data, str):
                    data = data.encode('utf-8')
                row_id = int(raw_id) if re.fullmatch(r'-?\d+', str(raw_id)) else str(raw_id)
                out[row_id] = json.loads(bytes(b ^ 0x55 for b in data).decode('utf-8'))
            return out
        finally:
            con.close()

    def keys(self, table):
        """Every key a row of the table has (the union, so injected rows never lack one)."""
        keys = set()
        for row in self[table].values():
            keys.update(row)
        return keys


def is_plain(value):
    if isinstance(value, (bool, int, float)):
        return True
    return isinstance(value, str) and value != '' and value.isascii()


def is_text(jp_value, cn_value):
    """Natural-language text: a string field with non-ASCII content in either client."""
    return (isinstance(jp_value, str) and isinstance(cn_value, str)
            and not (jp_value.isascii() and cn_value.isascii()))


class Builder:
    def __init__(self, jp, cn, bank_dir):
        self.jp = jp
        self.cn = cn
        self.bank_dir = bank_dir
        self.patches = []
        self.inserts = []
        self.memory_chapters = []
        self.hero_memories = []
        self.banks = []
        self.captions = {}
        self.decorations = {'groups': [], 'bags': []}
        self.renumbered = []
        self.warnings = []
        self.notes = []
        self._events = {}

    def warn(self, message):
        self.warnings.append(message)

    # ---- patches ----

    def patch(self, feature, table, row_id, values):
        """In-place change of an existing JP row; `values` are the CN values of the changed fields."""
        jp_row, cn_row = self.jp[table].get(row_id), self.cn[table].get(row_id)
        if jp_row is None or cn_row is None:
            fail(f'{table} {row_id} is missing in the {"JP" if jp_row is None else "CN"} config')
        values = {field: value for field, value in values.items() if jp_row.get(field) != value}
        if not values:
            self.warn(f'{feature}: {table} {row_id} already has the CN values; no patch')
            return
        expect = {}
        for field in EXPECT_FIELDS[table]:
            value = jp_row.get(field)
            if field not in values and field in cn_row and cn_row[field] == value and is_plain(value):
                expect[field] = value
        if not expect:
            fail(f'{table} {row_id}: no identifying field is equal in both clients')
        self.patches.append({'feature': feature, 'table': table, 'id': row_id,
                             'expect': dict(sorted(expect.items())), 'set': dict(sorted(values.items()))})

    def cn_fields(self, table, row_id, fields):
        row = self.cn[table].get(row_id)
        if row is None:
            fail(f'CN {table} {row_id} is missing')
        return {field: row[field] for field in fields}

    def fashions(self):
        self.patch('fubuki_spring_fashion', 'config_fashion', FUBUKI_FASHION,
                   self.cn_fields('config_fashion', FUBUKI_FASHION, ['is_appear']))
        self.patch('oakland_spring_touch', 'config_fashion', OAKLAND_FASHION,
                   self.cn_fields('config_fashion', OAKLAND_FASHION, ['unlock_action', 'cv_show']))

    # ---- spring festival story ----

    def spring_festival(self):
        table = 'config_chapter'
        source = self.cn[table].get(CN_SPRING_CHAPTER)
        if source is None:
            fail(f'CN {table} {CN_SPRING_CHAPTER} is missing')
        for client, config in (('JP', self.jp), ('CN', self.cn)):
            if SPRING_CHAPTER in config[table]:
                fail(f'{table} {SPRING_CHAPTER} already exists in the {client} config')
        used = sorted({row.get('memory_id', 0) for row in self.jp[table].values()} - {0})
        if SPRING_MEMORY_ID in used:
            fail(f'memory_id {SPRING_MEMORY_ID} is used in the JP config')
        copies = list(source['level_list'])
        for copy_id in copies:
            if copy_id not in self.jp['config_copy_display']:
                fail(f'copy {copy_id} of CN chapter {CN_SPRING_CHAPTER} is missing in JP config_copy_display')
            # The server (MemoryChapterModLoader) skips a memory chapter whose copies another chapter already lists.
            owners = [cid for cid, row in self.jp[table].items()
                      if any(copy_id in (row.get(key) or []) for key in ('level_list', 'running_level_list', 'treaty_copy'))]
            if owners:
                fail(f'JP chapters {owners} also list copy {copy_id}; the server would not register chapter '
                     f'{SPRING_CHAPTER}')

        overrides = {'memory_id': SPRING_MEMORY_ID, 'memory_start': 0, 'chapter_period': 0,
                     'chapter_periodarea': []}
        if 'id' in source:
            overrides['id'] = SPRING_CHAPTER
        row = self.complete_row(table, source, CHAPTER_TEMPLATE, overrides)
        self.inserts.append({'feature': 'spring_festival_story', 'table': table, 'id': SPRING_CHAPTER,
                             'row': row})
        self.memory_chapters.append({'feature': 'spring_festival_story', 'chapter_id': SPRING_CHAPTER,
                                     'copies': copies})
        self.notes.append(f'config_chapter {SPRING_CHAPTER} from CN {CN_SPRING_CHAPTER}, JP-only keys from JP '
                          f'{CHAPTER_TEMPLATE}; memory_id {SPRING_MEMORY_ID} (JP uses {used})')
        for copy_id in copies:
            self.patch('spring_festival_story', 'config_copy_display', copy_id,
                       self.cn_fields('config_copy_display', copy_id, ['name', 'description']))

    def complete_row(self, table, source, template_id, overrides):
        """The CN row reshaped to the JP key set: JP-only keys from the JP template row, CN-only keys dropped."""
        template = self.jp[table].get(template_id)
        if template is None:
            fail(f'JP {table} template {template_id} is missing')
        keys = self.jp.keys(table)
        dropped = sorted(set(source) - keys)
        if dropped:
            self.warn(f'{table}: CN-only keys {dropped} dropped')
        row = {}
        for key in sorted(keys):
            if key in overrides:
                row[key] = overrides[key]
            elif key in source:
                row[key] = source[key]
            elif key in template:
                row[key] = template[key]
            else:
                fail(f'{table}: JP template {template_id} lacks key {key}')
        return row

    # ---- character stories ----

    def character_stories(self):
        table = 'config_building_character_story'
        jp_rows, cn_rows = self.jp[table], self.cn[table]
        used_triggers = {row['plot_trigger_id'] for row in jp_rows.values()}
        ships = {}
        for cn_id in sorted(cn_rows):
            row = cn_rows[cn_id]
            if row['plot_trigger_id'] not in used_triggers:
                ships.setdefault(row['ship_fleet_id'], []).append(cn_id)
        taken = set(jp_rows) | set(cn_rows)
        next_id = (max(taken) // 100 + 1) * 100 + 1
        template_id = min(jp_rows)
        self.check_story_plots([cn_rows[cn_id]['plot_trigger_id'] for ids in ships.values() for cn_id in ids])

        stories = []
        # PlotHeroDetailPage lists a ship's stories by ascending id, so a ship with any id taken in JP moves
        # as a whole block and keeps its order.
        for ship_id, cn_ids in sorted(ships.items(), key=lambda item: item[1][0]):
            if ship_id not in self.jp['config_ship_fleet']:
                fail(f'ship_fleet {ship_id} of the CN stories is missing in JP')
            if any(cn_id in jp_rows for cn_id in cn_ids):
                new_ids = list(range(next_id, next_id + len(cn_ids)))
                next_id += len(cn_ids)
                self.renumbered.append((ship_id, cn_ids, new_ids))
            else:
                new_ids = cn_ids
            for cn_id, new_id in zip(cn_ids, new_ids):
                if new_id in jp_rows or (new_id != cn_id and new_id in cn_rows):
                    fail(f'{table}: id {new_id} for CN {cn_id} is not free')
                source = cn_rows[cn_id]
                overrides = {'id': new_id} if 'id' in source else {}
                stories.append((new_id, ship_id, self.complete_row(table, source, template_id, overrides)))

        for new_id, _, row in sorted(stories, key=lambda story: story[0]):
            self.inserts.append({'feature': 'character_stories', 'table': table, 'id': new_id, 'row': row})
        for ship_id in sorted(ships):
            story_ids = sorted(new_id for new_id, story_ship, _ in stories if story_ship == ship_id)
            self.hero_memories.append({'feature': 'character_stories', 'ship_fleet_id': ship_id,
                                       'story_ids': story_ids})
            jp_cover = self.jp['config_ship_fleet'][ship_id].get('plot_cover')
            cn_cover = self.cn['config_ship_fleet'].get(ship_id, {}).get('plot_cover')
            if not jp_cover and cn_cover:
                self.patch('character_stories', 'config_ship_fleet', ship_id, {'plot_cover': cn_cover})
            elif not jp_cover:
                self.warn(f'config_ship_fleet {ship_id} has no plot_cover in either client')

    def check_story_plots(self, trigger_ids):
        """Every trigger (and branch trigger) exists in JP, and so does every step it lists."""
        triggers = self.jp['config_plot_episode_trigger']
        seen, pending, steps = set(), list(trigger_ids), 0
        while pending:
            trigger_id = pending.pop()
            if trigger_id in seen:
                continue
            seen.add(trigger_id)
            trigger = triggers.get(trigger_id)
            if trigger is None:
                fail(f'plot trigger {trigger_id} is missing in JP config_plot_episode_trigger')
            if trigger['plot_tableName'] != 'plot_episode_character':
                self.warn(f'plot trigger {trigger_id} uses {trigger["plot_tableName"]}')
            step_table = 'config_' + trigger['plot_tableName']
            for step_id in str(trigger['plot_steps']).split(','):
                step = self.jp[step_table].get(int(step_id))
                if step is None:
                    fail(f'step {step_id} of plot trigger {trigger_id} is missing in JP {step_table}')
                steps += 1
                for branch in range(1, 5):
                    parameter = step.get(f'branch{branch}_parameter') or []
                    if len(parameter) > 1 and parameter[0] == 1:  # [1, trigger id]: jump to another trigger
                        pending.append(parameter[1])
        self.notes.append(f'character stories: {len(trigger_ids)} triggers, {len(seen) - len(set(trigger_ids))} '
                          f'branch triggers and {steps} steps, all present in the JP config')

    # ---- spring furniture ----

    def spring_furniture(self):
        feature = 'spring_furniture'
        group = self.cn[GROUP_TABLE].get(SPRING_GROUP)
        if group is None:
            fail(f'CN {GROUP_TABLE} {SPRING_GROUP} is missing')
        if SPRING_GROUP in self.jp[GROUP_TABLE]:
            fail(f'{GROUP_TABLE} {SPRING_GROUP} already exists in the JP config')
        members = list(group.get('interactionitem_bag') or [])
        if not members or len(set(members)) != len(members):
            fail(f'CN {GROUP_TABLE} {SPRING_GROUP} lists no pieces or a piece twice: {members}')
        if SPRING_COVER_BAG not in members:
            fail(f'the cover piece {SPRING_COVER_BAG} is not in CN set {SPRING_GROUP}')
        template = self.jp[BAG_TABLE].get(BAG_TEMPLATE)
        if template is None or template.get('interactionitem_bag_group') == 0:
            fail(f'JP {BAG_TABLE} template {BAG_TEMPLATE} is missing or not a set piece')
        # show_type 0 lists the piece (InteractionBagItemShowType.yes); reward must be a JP config_rewards row.
        if template.get('show_type') != 0 or template.get('reward') not in self.jp['config_rewards']:
            fail(f'JP {BAG_TABLE} template {BAG_TEMPLATE}: show_type {template.get("show_type")!r}, '
                 f'reward {template.get("reward")!r} not usable')
        jp_items = {}
        for item_id, item in self.jp[ITEM_TABLE].items():
            models = tuple(item.get(field) for field in MODEL_FIELDS)
            if all(models):
                jp_items.setdefault(models, []).append(item_id)

        rows, mapped = {}, []
        for bag_id in sorted(members):
            source = self.cn[BAG_TABLE].get(bag_id)
            if source is None:
                fail(f'CN {BAG_TABLE} {bag_id} of set {SPRING_GROUP} is missing')
            if not BAG_ID_RANGE[0] <= bag_id <= BAG_ID_RANGE[1]:
                fail(f'{BAG_TABLE} {bag_id} is outside {BAG_ID_RANGE[0]}-{BAG_ID_RANGE[1]}')
            if bag_id in self.jp[BAG_TABLE]:
                fail(f'{BAG_TABLE} {bag_id} already exists in the JP config')
            if source.get('interactionitem_bag_group') != SPRING_GROUP or source.get('type') != 0:
                fail(f'CN {BAG_TABLE} {bag_id} is not a type-0 piece of set {SPRING_GROUP}')
            cn_item = self.cn[ITEM_TABLE].get(source.get('interactionitem'))
            if cn_item is None:
                fail(f'CN {ITEM_TABLE} {source.get("interactionitem")} of {BAG_TABLE} {bag_id} is missing')
            models = tuple(cn_item.get(field) for field in MODEL_FIELDS)
            matches = jp_items.get(models, [])
            if len(matches) != 1:
                fail(f'{BAG_TABLE} {bag_id}: {len(matches)} JP {ITEM_TABLE} rows have the models {models}')
            item_id = matches[0]
            jp_item = self.jp[ITEM_TABLE][item_id]
            if jp_item.get('interaction_item_type') != FURNITURE_ITEM or \
                    jp_item.get('item_location_coordinate') != cn_item.get('item_location_coordinate'):
                fail(f'JP {ITEM_TABLE} {item_id} is not the same furniture piece as CN {cn_item.get("id")}')
            if tuple(source.get(field) for field in MODEL_FIELDS) != models:
                fail(f'CN {BAG_TABLE} {bag_id} names other models than its interaction item')
            # The JP config shows this sprite for the same item, so it is in the JP client's atlases.
            picture = jp_item.get('interaction_item_pic')
            if not picture or picture != source.get('picture'):
                fail(f'{BAG_TABLE} {bag_id}: picture {source.get("picture")!r} is not the JP item picture {picture!r}')
            overrides = {'id': bag_id, 'interactionitem': item_id, 'icon': picture, 'icon_small': picture}
            rows[bag_id] = self.complete_row(BAG_TABLE, source, BAG_TEMPLATE, overrides)
            mapped.append((bag_id, cn_item.get('id'), item_id))

        cover = rows[SPRING_COVER_BAG]['picture']
        group_overrides = {'id': SPRING_GROUP, 'icon': cover}
        group_row = self.complete_row(GROUP_TABLE, group, GROUP_TEMPLATE, group_overrides)
        for bag_id in sorted(rows):
            self.inserts.append({'feature': feature, 'table': BAG_TABLE, 'id': bag_id, 'row': rows[bag_id]})
        self.inserts.append({'feature': feature, 'table': GROUP_TABLE, 'id': SPRING_GROUP, 'row': group_row})
        self.decorations['groups'].append({'id': SPRING_GROUP, 'bags': members})
        for bag_id in sorted(rows):
            row = rows[bag_id]
            self.decorations['bags'].append({'id': bag_id, 'group': row['interactionitem_bag_group'],
                                             'interactionItem': row['interactionitem'], 'type': row['type'],
                                             'showType': row['show_type']})
        self.notes.append(f'spring furniture: set {SPRING_GROUP} with {len(rows)} pieces; CN interaction items '
                          + ', '.join(f'{cn_id}->{jp_id}' for _, cn_id, jp_id in mapped)
                          + f'; icon/icon_small: the CN sprites -> each piece\'s picture; cover {group.get("icon")} '
                            f'-> {cover}')

    # ---- voice ----

    def voice(self):
        main_banks = {f'c_cl_{ship}{suffix}' for ship, _ in VOICE_SHIPS for suffix in ('_bank', '_jp_bank')}
        if not os.path.isdir(self.bank_dir):
            fail(f'missing {self.bank_dir}')
        listed = sorted(name[:-4] for name in os.listdir(self.bank_dir) if name.endswith('.txt'))
        shared = {}
        for ship, talker_id in VOICE_SHIPS:
            feature = 'cn_voice_' + ship
            self.main_bank(ship, feature, listed)
            self.wedding(feature, talker_id)
            self.ship_captions(ship, feature)
            for bank in listed:
                if bank not in main_banks:
                    shared.setdefault(bank, []).extend(self.swap_pairs(bank, ship, feature))
        for bank in sorted((bank for bank, events in shared.items() if events), key=str.lower):
            bank_id = self.bank_id(bank)
            events = sorted(shared[bank], key=lambda event: event['name'])
            self.check_hirc(bank, [e['from'] for e in events] + [e['to'] for e in events], [])
            self.banks.append({'source': bank, 'source_size': self.bank_size(bank), 'source_bank_id': bank_id,
                               'target': bank, 'target_bank_id': bank_id, 'events': events})

    def bank_path(self, bank, extension='.bnk'):
        path = os.path.join(self.bank_dir, bank + extension)
        if not os.path.isfile(path):
            fail(f'missing {path}')
        return path

    def bank_size(self, bank):
        return os.path.getsize(self.bank_path(bank))

    def bank_id(self, bank):
        """The bank id in the BKHD chunk (version u32, then id u32), checked against FNV of the bank name."""
        with open(self.bank_path(bank), 'rb') as f:
            head = f.read(16)
        if len(head) < 16 or head[:4] != b'BKHD':
            fail(f'{bank}.bnk does not start with BKHD')
        bank_id = struct.unpack_from('<I', head, 12)[0]
        if bank_id != fnv1_32(bank):
            fail(f'{bank}.bnk bank id {bank_id} != fnv({bank}) {fnv1_32(bank)}')
        return bank_id

    def bank_events(self, bank):
        """Event name -> id from the bank's .txt listing, each id checked against FNV of the name."""
        if bank not in self._events:
            self._events[bank] = self._read_bank_events(bank)
        return self._events[bank]

    def _read_bank_events(self, bank):
        events, section = {}, None
        with open(self.bank_path(bank, '.txt'), encoding='utf-8', errors='replace') as f:
            for line in f:
                line = line.rstrip('\r\n')
                if not line:
                    section = None
                elif not line.startswith('\t'):
                    section = line.split('\t', 1)[0]
                elif section == 'Event':
                    parts = line.split('\t')
                    event_id, name = int(parts[1]), parts[2]
                    if event_id != fnv1_32(name):
                        fail(f'{bank}.txt: event {name} has id {event_id}, fnv gives {fnv1_32(name)}')
                    events[name] = event_id
        return events

    def check_hirc(self, bank, unique_events, absent_ids):
        """The ids the mod rewrites occur exactly once as HIRC events; new ids are not in the bank yet."""
        with open(self.bank_path(bank), 'rb') as f:
            try:
                objects = hirc_objects(f.read())
            except (ValueError, struct.error) as error:
                fail(f'{bank}.bnk: {error}')
        if not objects:
            fail(f'{bank}.bnk has no HIRC objects')
        for event_id in unique_events:
            count = sum(1 for kind, object_id in objects if kind == 4 and object_id == event_id)
            if count != 1:
                fail(f'{bank}.bnk: event {event_id} occurs {count} times as a HIRC event')
        ids = {object_id for _, object_id in objects}
        for object_id in absent_ids:
            if object_id in ids:
                fail(f'{bank}.bnk already has a HIRC object {object_id}')

    def main_bank(self, ship, feature, listed):
        source, target = f'c_cl_{ship}_bank', f'c_cl_{ship}_jp_bank'
        source_events, target_events = self.bank_events(source), self.bank_events(target)
        suffix = f'_c_cl_{ship}'
        events = []
        for name in sorted(source_events):
            if not name.startswith('CV_') or not name.endswith(suffix):
                self.warn(f'{source}: event {name} is not a CV event of {ship}; not mapped')
                continue
            target_name = name + '_jp'
            if target_name not in target_events:
                fail(f'{target}.txt lacks {target_name}')
            events.append({'feature': feature, 'from': source_events[name], 'to': target_events[target_name],
                           'name': name})
        mapped = {event['to'] for event in events}
        for name, event_id in sorted(target_events.items()):
            if event_id in mapped:
                continue
            elsewhere = [bank for bank in listed if bank != target and name in self.bank_events(bank)]
            if elsewhere:
                self.notes.append(f'{target}: {name} has no CN event; it is also in {", ".join(elsewhere)}')
            else:
                self.warn(f'{target}: {name} has no CN event and no other bank; the replaced bank loses it')
        self.check_hirc(source, [e['from'] for e in events], [e['to'] for e in events])
        source_bank_id = self.bank_id(source)
        with open(self.bank_path(source), 'rb') as f:
            play_banks = play_action_banks(f.read())
        # Every Play action names the bank that holds its sound; the copy must name the bank it is served as.
        if len(play_banks) != len(events) or any(bank_id != source_bank_id for bank_id in play_banks):
            fail(f'{source}.bnk: expected {len(events)} Play actions naming bank {source_bank_id}, '
                 f'found {len(play_banks)} ({sorted(set(play_banks))})')
        self.banks.append({'source': source, 'source_size': self.bank_size(source),
                           'source_bank_id': source_bank_id, 'target': target,
                           'target_bank_id': self.bank_id(target), 'play_actions': len(play_banks),
                           'events': events})

    def swap_pairs(self, bank, ship, feature):
        """Voice events of the JP model (c_cl_<ship>_jp) whose CN counterpart sits in the same bank."""
        jp_model = re.compile(rf'c_cl_{ship}_jp(?=_|$)')
        cn_model = re.compile(rf'c_cl_{ship}(?=_|$)')
        events = self.bank_events(bank)
        pairs = []
        for name in sorted(events):
            if jp_model.search(name):
                cn_name = jp_model.sub(f'c_cl_{ship}', name, count=1)
                if cn_name not in events:
                    continue
                if not name.startswith('CV_'):
                    self.notes.append(f'{bank}: {name} / {cn_name} left alone (not a voice event)')
                    continue
                pairs.append({'feature': feature, 'swap': True, 'from': events[name], 'to': events[cn_name],
                              'name': name})
            elif cn_model.search(name) and name.startswith('CV_'):
                if cn_model.sub(f'c_cl_{ship}_jp', name, count=1) not in events:
                    self.warn(f'{bank}: {name} has no _jp counterpart in the same bank')
        return pairs

    def wedding(self, feature, talker_id):
        table = 'config_plot_episode_wedding'
        ids = sorted(row_id for row_id, row in self.jp[table].items() if row.get('talker_id') == talker_id)
        if not ids:
            fail(f'no JP {table} rows for talker {talker_id}')
        for row_id in ids:
            jp_row, cn_row = self.jp[table][row_id], self.cn[table].get(row_id)
            if cn_row is None:
                self.warn(f'{table} {row_id} is missing in the CN config')
                continue
            values = {field: cn_row[field] for field in sorted(set(jp_row) & set(cn_row))
                      if jp_row[field] != cn_row[field] and is_text(jp_row[field], cn_row[field])}
            other = sorted(field for field in set(jp_row) & set(cn_row)
                           if jp_row[field] != cn_row[field] and field not in values)
            if other:
                self.notes.append(f'{table} {row_id}: non-text differences left as JP: {", ".join(other)}')
            if values:
                self.patch(feature, table, row_id, values)

    def ship_captions(self, ship, feature):
        jp_rows, cn_rows = self.jp['config_caption_access_list'], self.cn['config_caption_access_list']
        suffix = f'_c_cl_{ship}_jp'
        keys = sorted(key for key in jp_rows
                      if isinstance(key, str) and key.startswith('zm_') and key.endswith(suffix))
        if not keys:
            fail(f'no JP caption rows zm_*{suffix}')
        for key in keys:
            cn_row = cn_rows.get(key[:-3])
            if cn_row is None:
                self.warn(f'caption {key}: CN row {key[:-3]} is missing')
                continue
            self.captions[key] = {'feature': feature, **{field: cn_row[field] for field in CAPTION_FIELDS}}


def fnv1_32(name):
    """Wwise short id: FNV-1 32 of the lower-cased name."""
    value = 2166136261
    for byte in name.lower().encode('utf-8'):
        value = (value * 16777619) & 0xFFFFFFFF
        value ^= byte
    return value


def hirc_objects(data):
    """(type, id) of every object in the bank's HIRC chunk (each object: u8 type, u32 size, u32 id, ...)."""
    pos = 0
    while pos + 8 <= len(data):
        tag, size = data[pos:pos + 4], struct.unpack_from('<I', data, pos + 4)[0]
        if tag == b'HIRC':
            count, objects, cursor = struct.unpack_from('<I', data, pos + 8)[0], [], pos + 12
            for _ in range(count):
                kind, length, object_id = struct.unpack_from('<BII', data, cursor)
                objects.append((kind, object_id))
                cursor += 5 + length
            if cursor != pos + 8 + size:
                raise ValueError('HIRC objects do not fill the chunk')
            return objects
        pos += 8 + size
    return []


PLAY_ACTION = 0x0403


def play_action_banks(data):
    """Bank id in the last 4 bytes of every Play action (HIRC type 3 whose action type is 0x0403)."""
    pos = 0
    while pos + 8 <= len(data):
        tag, size = data[pos:pos + 4], struct.unpack_from('<I', data, pos + 4)[0]
        if tag == b'HIRC':
            banks, cursor = [], pos + 12
            for _ in range(struct.unpack_from('<I', data, pos + 8)[0]):
                kind, length = struct.unpack_from('<BI', data, cursor)
                if kind == 3 and struct.unpack_from('<H', data, cursor + 9)[0] == PLAY_ACTION:
                    banks.append(struct.unpack_from('<I', data, cursor + 5 + length - 4)[0])
                cursor += 5 + length
            return banks
        pos += 8 + size
    return []


# ---- Lua output ----

LUA_KEYWORDS = frozenset(('and break do else elseif end false for function goto if in local nil not or repeat '
                          'return then true until while').split())
LINE_WIDTH = 116


class DataKeys(dict):
    """A table keyed by data (caption keys) rather than field names: always written as ["key"]."""


def lua_string(text):
    out = ['"']
    for char in text:
        code = ord(char)
        if char in '\\"':
            out.append('\\' + char)
        elif char == '\n':
            out.append('\\n')
        elif char == '\r':
            out.append('\\r')
        elif char == '\t':
            out.append('\\t')
        elif code < 0x20 or code == 0x7F:
            out.append('\\%03d' % code)
        else:
            out.append(char)
    out.append('"')
    return ''.join(out)


def lua_key(key, bracket=False):
    if isinstance(key, bool) or not isinstance(key, (int, str)):
        raise TypeError(f'unsupported key {key!r}')
    if isinstance(key, int):
        return f'[{key}]'
    if not bracket and re.fullmatch(r'[A-Za-z_][A-Za-z0-9_]*', key) and key not in LUA_KEYWORDS:
        return key
    return f'[{lua_string(key)}]'


def lua_scalar(value):
    if isinstance(value, bool):
        return 'true' if value else 'false'
    if isinstance(value, int):
        return str(value)
    if isinstance(value, float):
        if not math.isfinite(value):
            raise ValueError(f'unsupported number {value!r}')
        return repr(value)  # always has '.' or 'e', so Lua reads a float
    if isinstance(value, str):
        return lua_string(value)
    raise TypeError(f'unsupported value {value!r}')


def lua_items(value):
    if isinstance(value, dict):
        bracket = isinstance(value, DataKeys)
        return [lua_key(key, bracket) + ' = ' for key in value], list(value.values())
    return [''] * len(value), list(value)


def lua_inline(value):
    if not isinstance(value, (dict, list)):
        return lua_scalar(value)
    prefixes, values = lua_items(value)
    return '{' + ', '.join(prefix + lua_inline(item) for prefix, item in zip(prefixes, values)) + '}'


def lua_value(value, column, level):
    """Lua constructor for value; a table that does not fit on the line gets one entry per line."""
    inline = lua_inline(value)
    if not isinstance(value, (dict, list)) or not value or column + len(inline) <= LINE_WIDTH:
        return inline
    prefixes, values = lua_items(value)
    pad = '  ' * (level + 1)
    lines = ['{']
    for prefix, item in zip(prefixes, values):
        lines.append(pad + prefix + lua_value(item, len(pad) + len(prefix), level + 1) + ',')
    lines.append('  ' * level + '}')
    return '\n'.join(lines)


def render_lua(data):
    return ('-- Generated by tools/export-cn-exclusive.py from the CN 1.5.120 and JP 1.4.0 configs. '
            'Do not edit by hand.\n' + 'return ' + lua_value(data, len('return '), 0) + '\n')


class LuaParser:
    """Reads back the table constructors render_lua writes (Lua 5.3 literal syntax)."""

    TOKEN = re.compile(r'\s+|--[^\n]*|(?P<number>-?(?:\d+\.\d*|\.\d+|\d+)(?:[eE][+-]?\d+)?)'
                       r'|(?P<name>[A-Za-z_]\w*)|(?P<string>")|(?P<op>[{}\[\]=,;])')
    ESCAPES = {'n': '\n', 'r': '\r', 't': '\t', 'a': '\a', 'b': '\b', 'f': '\f', 'v': '\v', '\\': '\\',
               '"': '"', "'": "'", '\n': '\n'}

    def __init__(self, text):
        self.text, self.pos = text, 0

    def parse(self):
        if self.next() != ('name', 'return'):
            raise ValueError('expected return')
        value = self.value(self.next())
        if self.next() is not None:
            raise ValueError(f'trailing text at {self.pos}')
        return value

    def next(self):
        while self.pos < len(self.text):
            match = self.TOKEN.match(self.text, self.pos)
            if not match:
                raise ValueError(f'unexpected {self.text[self.pos:self.pos + 20]!r} at {self.pos}')
            self.pos = match.end()
            if match.lastgroup == 'string':
                return 'string', self.string()
            if match.lastgroup:
                return match.lastgroup, match.group(match.lastgroup)
        return None

    def string(self):
        out = []
        while True:
            char = self.text[self.pos]
            self.pos += 1
            if char == '"':
                return ''.join(out)
            if char == '\n':
                raise ValueError('newline in string')
            if char != '\\':
                out.append(char)
                continue
            char = self.text[self.pos]
            if char.isdigit():
                digits = re.match(r'\d{1,3}', self.text[self.pos:]).group()
                self.pos += len(digits)
                out.append(chr(int(digits)))
            elif char in self.ESCAPES:
                self.pos += 1
                out.append(self.ESCAPES[char])
            else:
                raise ValueError(f'unsupported escape \\{char}')

    def value(self, token):
        kind, text = token
        if kind == 'number':
            return float(text) if re.search(r'[.eE]', text) else int(text)
        if kind == 'string':
            return text
        if kind == 'name' and text in ('true', 'false'):
            return text == 'true'
        if token == ('op', '{'):
            return self.table()
        raise ValueError(f'unexpected {text!r}')

    def table(self):
        entries, index = {}, 0
        token = self.next()
        while token != ('op', '}'):
            if token == ('op', '['):
                key = self.value(self.next())
                if self.next() != ('op', ']') or self.next() != ('op', '='):
                    raise ValueError('bad [key] =')
                entries[key] = self.value(self.next())
            elif token[0] == 'name' and token[1] not in ('true', 'false'):
                if self.next() != ('op', '='):
                    raise ValueError('bad key =')
                entries[token[1]] = self.value(self.next())
            else:
                index += 1
                entries[index] = self.value(token)
            token = self.next()
            if token in (('op', ','), ('op', ';')):
                token = self.next()
            elif token != ('op', '}'):
                raise ValueError(f'expected , or }} at {self.pos}')
        return entries


def normalize(value):
    """Lua tables and JSON values in one shape: sequences as lists, the empty table as an empty list."""
    if isinstance(value, dict):
        if list(value) == list(range(1, len(value) + 1)):
            return [normalize(item) for item in value.values()]
        return {key: normalize(item) for key, item in value.items()}
    if isinstance(value, (list, tuple)):
        return [normalize(item) for item in value]
    return value


def same(a, b):
    """Equal values of equal types (1 and 1.0 differ, as Lua integers and floats do)."""
    a, b = normalize(a), normalize(b)
    if type(a) is not type(b):
        return False
    if isinstance(a, dict):
        return a.keys() == b.keys() and all(same(a[key], b[key]) for key in a)
    if isinstance(a, list):
        return len(a) == len(b) and all(same(x, y) for x, y in zip(a, b))
    return a == b


def verify(parsed, builder, decorations):
    """data.lua as parsed back, checked value by value against the configs and the bank files; decorations.json
    (as parsed back) against the data.lua rows."""
    jp, cn, errors = builder.jp, builder.cn, []

    def check(condition, message):
        if not condition:
            errors.append(message)

    check(parsed.get('version') == FORMAT_VERSION, 'version')
    for patch in normalize(parsed['patches']):
        table, row_id = patch['table'], patch['id']
        jp_row, cn_row = jp[table].get(row_id), cn[table].get(row_id)
        if jp_row is None or cn_row is None:
            errors.append(f'patch {table} {row_id}: row missing')
            continue
        for field, value in patch['set'].items():
            check(same(value, cn_row.get(field)), f'patch {table} {row_id}.{field} != CN value')
            check(not same(value, jp_row.get(field)), f'patch {table} {row_id}.{field} is already the JP value')
        for field, value in patch['expect'].items():
            check(field not in patch['set'] and is_plain(value) and same(value, jp_row.get(field))
                  and same(value, cn_row.get(field)), f'expect {table} {row_id}.{field}')

    inserted = {}
    for insert in normalize(parsed['inserts']):
        table, row_id, row = insert['table'], insert['id'], insert['row']
        inserted.setdefault(table, {})[row_id] = row
        check(row_id not in jp[table], f'insert {table} {row_id} exists in JP')
        check(set(row) == jp.keys(table), f'insert {table} {row_id}: key set differs from JP')
        check(row.get('id', row_id) == row_id, f'insert {table} {row_id}: id field')
        if table == 'config_chapter':
            source, template = cn[table][CN_SPRING_CHAPTER], jp[table][CHAPTER_TEMPLATE]
            fixed = {'id': row_id, 'memory_id': SPRING_MEMORY_ID, 'memory_start': 0, 'chapter_period': 0,
                     'chapter_periodarea': []}
            for key, value in row.items():
                expected = fixed[key] if key in fixed else source[key] if key in source else template[key]
                check(same(value, expected), f'insert {table} {row_id}.{key}')
            check(row_id not in cn[table], f'insert {table} {row_id} exists in CN')
        elif table == 'config_building_character_story':
            sources = [r for r in cn[table].values() if r['plot_trigger_id'] == row['plot_trigger_id']]
            check(len(sources) == 1, f'insert {table} {row_id}: CN source row')
            for key, value in row.items():
                if key != 'id' and sources:
                    check(same(value, sources[0][key]), f'insert {table} {row_id}.{key} != CN value')
        elif table == BAG_TABLE:
            source, template = cn[table].get(row_id), jp[table][BAG_TEMPLATE]
            item = jp[ITEM_TABLE].get(row.get('interactionitem'))
            cn_item = cn[ITEM_TABLE].get(source.get('interactionitem')) if source is not None else None
            check(BAG_ID_RANGE[0] <= row_id <= BAG_ID_RANGE[1] and row.get('interactionitem_bag_group') == SPRING_GROUP
                  and row.get('type') == 0 and row.get('show_type') == 0, f'insert {table} {row_id}: id, set or type')
            if source is None or item is None or cn_item is None:
                errors.append(f'insert {table} {row_id}: CN row, its interaction item or the JP interaction item missing')
                continue
            check(item.get('interaction_item_type') == FURNITURE_ITEM and
                  all(item.get(f) == cn_item.get(f) == row.get(f) for f in MODEL_FIELDS),
                  f'insert {table} {row_id}: JP interaction item {row.get("interactionitem")} has other models')
            check(row.get('icon') == row.get('icon_small') == row.get('picture') == item.get('interaction_item_pic'),
                  f'insert {table} {row_id}: icons are not the JP item picture')
            for key, value in row.items():
                if key not in ('id', 'interactionitem', 'icon', 'icon_small'):
                    expected = source[key] if key in source else template[key]
                    check(same(value, expected), f'insert {table} {row_id}.{key}')
        elif table == GROUP_TABLE:
            source = cn[table].get(row_id)
            check(row_id == SPRING_GROUP and source is not None, f'insert {table} {row_id}: CN source row')
            for key, value in row.items():
                if key not in ('id', 'icon') and source is not None:
                    check(same(value, source[key]), f'insert {table} {row_id}.{key} != CN value')
        else:
            errors.append(f'insert {table} {row_id}: no check for this table')

    bags, groups = inserted.get(BAG_TABLE, {}), inserted.get(GROUP_TABLE, {})
    for group_id, group in groups.items():
        members = group.get('interactionitem_bag', [])
        check(members and all(bags.get(m, {}).get('interactionitem_bag_group') == group_id for m in members),
              f'insert {GROUP_TABLE} {group_id}: a listed piece is not inserted with this set')
        check(group.get('icon') == bags.get(SPRING_COVER_BAG, {}).get('picture'), f'insert {GROUP_TABLE} {group_id}: cover')
    check(all(row_id in groups.get(row.get('interactionitem_bag_group'), {}).get('interactionitem_bag', [])
              for row_id, row in bags.items()), f'an inserted {BAG_TABLE} row is not listed by its set')
    # decorations.json tells the server the same rows (the server-side loader rejects anything else).
    declared = {bag['id']: bag for bag in decorations['bags']}
    check(len(declared) == len(decorations['bags']) and sorted(declared) == sorted(bags),
          'decorations.json bags != data.lua config_interaction_item_bag inserts')
    for bag_id, bag in declared.items():
        row = bags.get(bag_id, {})
        check(bag == {'id': bag_id, 'group': row.get('interactionitem_bag_group'),
                      'interactionItem': row.get('interactionitem'), 'type': row.get('type'),
                      'showType': row.get('show_type')} and bag['interactionItem'] in jp[ITEM_TABLE],
              f'decorations.json bag {bag_id} != data.lua row')
    check(sorted(group['id'] for group in decorations['groups']) == sorted(groups),
          'decorations.json groups != data.lua config_interaction_item_bag_group inserts')
    for group in decorations['groups']:
        check(group['id'] not in jp[GROUP_TABLE] and
              group['bags'] == groups.get(group['id'], {}).get('interactionitem_bag'),
              f'decorations.json group {group["id"]} != data.lua row')

    chapters = inserted.get('config_chapter', {})
    for chapter in normalize(parsed['memory_chapters']):
        row = chapters.get(chapter['chapter_id'])
        check(row is not None and same(chapter['copies'], row['level_list']), 'memory_chapters')
    stories = inserted.get('config_building_character_story', {})
    listed = []
    for memory in normalize(parsed['hero_memories']):
        ids = memory['story_ids']
        listed += ids
        check(ids == sorted(ids) and all(stories.get(i, {}).get('ship_fleet_id') == memory['ship_fleet_id']
                                         for i in ids), f'hero_memories {memory["ship_fleet_id"]}')
    check(sorted(listed) == sorted(stories), 'hero_memories do not cover the inserted stories')

    for bank in normalize(parsed['voice']['banks']):
        source, target = bank['source'], bank['target']
        check(bank['source_size'] == builder.bank_size(source), f'bank {source} size')
        check(bank['source_bank_id'] == builder.bank_id(source), f'bank {source} id')
        check(bank['target_bank_id'] == builder.bank_id(target), f'bank {target} id')
        source_events, target_events = builder.bank_events(source), builder.bank_events(target)
        if source != target:
            with open(builder.bank_path(source), 'rb') as f:
                check(bank.get('play_actions') == len(play_action_banks(f.read())), f'bank {source} play actions')
        for event in bank['events']:
            name = event['name']
            if event.get('swap'):
                partner = re.sub(r'(c_cl_[a-z]+)_jp(?=_|$)', r'\1', name, count=1)
                check(source_events.get(name) == event['from'] and source_events.get(partner) == event['to'],
                      f'bank {source} swap {name}')
            else:
                check(source_events.get(name) == event['from'] == fnv1_32(name)
                      and target_events.get(name + '_jp') == event['to'] == fnv1_32(name + '_jp'),
                      f'bank {source} event {name}')

    captions_jp, captions_cn = jp['config_caption_access_list'], cn['config_caption_access_list']
    for key, caption in parsed['voice']['captions'].items():
        cn_row = captions_cn.get(key[:-3]) if key.endswith('_jp') else None
        check(key in captions_jp and cn_row is not None, f'caption {key}: rows')
        for field in CAPTION_FIELDS:
            check(cn_row is not None and same(caption[field], cn_row[field]), f'caption {key}.{field}')
    return errors


def memory_chapters_json(chapters):
    items = [json.dumps({'chapterId': c['chapter_id'], 'copies': c['copies']}) for c in chapters]
    body = ',\n'.join('    ' + item for item in items)
    return '{\n  "memoryChapters": [\n' + body + '\n  ]\n}\n'


def decorations_json(decorations):
    """The server-side declaration of the injected decorations (format: DecorModLoader)."""
    parts = []
    for key in ('groups', 'bags'):
        items = ',\n'.join('    ' + json.dumps(item) for item in decorations[key])
        parts.append(f'  "{key}": [\n{items}\n  ]' if items else f'  "{key}": []')
    return '{\n' + ',\n'.join(parts) + '\n}\n'


def summary(builder):
    lines = ['cn-exclusive.mod data:']
    for feature in FEATURES:
        patches = [p for p in builder.patches if p['feature'] == feature]
        inserts = [i for i in builder.inserts if i['feature'] == feature]
        events = [e for b in builder.banks for e in b['events'] if e['feature'] == feature]
        captions = [c for c in builder.captions.values() if c['feature'] == feature]
        parts = []
        for kind, items in (('patches', patches), ('inserts', inserts)):
            if items:
                parts.append(f'{len(items)} {kind} ({", ".join(sorted({item["table"] for item in items}))})')
        if feature == 'spring_festival_story':
            parts += [f'memory chapter {chapter["chapter_id"]} with {len(chapter["copies"])} copies'
                      for chapter in builder.memory_chapters]
        if feature == 'character_stories':
            parts.append(f'{len(builder.hero_memories)} ships')
        if feature == 'spring_furniture':
            parts.append(f'decorations.json: {len(builder.decorations["bags"])} pieces, '
                         f'sets {[group["id"] for group in builder.decorations["groups"]]}')
        if events:
            swaps = sum(1 for e in events if e.get('swap'))
            parts.append(f'{len(events) - swaps} bank events + {swaps} swaps')
        if captions:
            parts.append(f'{len(captions)} captions')
        lines.append(f'  {feature}: {"; ".join(parts) or "nothing"}')
    lines.append('renumbered config_building_character_story ids (CN -> mod):')
    for ship_id, cn_ids, new_ids in builder.renumbered:
        lines.append(f'  ship_fleet {ship_id}: {cn_ids[0]}-{cn_ids[-1]} -> {new_ids[0]}-{new_ids[-1]}')
    if not builder.renumbered:
        lines.append('  none')
    lines += ['note: ' + note for note in builder.notes]
    lines += ['warning: ' + warning for warning in builder.warnings]
    return '\n'.join(lines)


def main():
    parser = argparse.ArgumentParser(description=__doc__.splitlines()[0])
    parser.add_argument('--jp-client', default=os.path.join(ROOT, 'blueoath', 'blueoath'))
    parser.add_argument('--cn-client', default=os.path.join(ROOT, '苍蓝誓约', 'clsy'))
    parser.add_argument('--output-dir', default=os.path.join(ROOT, 'Mods', 'cn-exclusive.mod'))
    args = parser.parse_args()

    jp, cn = Config(args.jp_client), Config(args.cn_client)
    bank_dir = os.path.join(streaming_assets(args.jp_client), 'Audio', 'GeneratedSoundBanks', 'Windows')
    builder = Builder(jp, cn, bank_dir)
    builder.fashions()
    builder.spring_festival()
    builder.character_stories()
    builder.spring_furniture()
    builder.voice()

    data = {
        'version': FORMAT_VERSION,
        'patches': builder.patches,
        'inserts': builder.inserts,
        'memory_chapters': builder.memory_chapters,
        'hero_memories': builder.hero_memories,
        'voice': {'banks': builder.banks, 'captions': DataKeys(sorted(builder.captions.items()))},
    }
    text = render_lua(data)
    parsed = LuaParser(text).parse()
    if not same(parsed, data):
        fail('data.lua does not parse back to the generated data')
    decorations = decorations_json(builder.decorations)
    if json.loads(decorations) != builder.decorations:
        fail('decorations.json does not parse back to the generated data')
    errors = verify(parsed, builder, json.loads(decorations))
    if errors:
        fail('data.lua check failed:\n  ' + '\n  '.join(errors))

    os.makedirs(args.output_dir, exist_ok=True)
    outputs = (('data.lua', text), ('memory-chapters.json', memory_chapters_json(builder.memory_chapters)),
               ('decorations.json', decorations))
    for name, content in outputs:
        path = os.path.join(args.output_dir, name)
        with open(path, 'w', encoding='utf-8', newline='\n') as f:
            f.write(content)
        print(f'wrote {path}')
    print(summary(builder), file=sys.stderr)
    return 0


if __name__ == '__main__':
    sys.exit(main())
