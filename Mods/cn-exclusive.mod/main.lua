-- cn-exclusive.mod：把国服 1.5.120 独有、而日服 1.4.0 客户端里资源齐全却被关掉的内容重新打开。
-- 内容清单、开关与已知限制见同目录 README.md，做法见 docs/MOD_DEVELOPMENT.zh-CN.md 第 12 章。
--
-- 表行、国服文本、语音库与事件 ID 都在 data.lua（tools/export-cn-exclusive.py 生成，不要手改），本文件只放逻辑：
--   * configManager.GetData / GetDataById：每张表第一次返回时按 expect 原地修改日服行（patches）、补入新行
--     （inserts）；GetDataById 先回答注入的 ID，整表尚未缓存时单独取出的行也照样修改。
--   * IllustrateLogic.GetMemoryData：服务端没有登记（memory-chapters.json）的回忆章节不列出。
--   * IllustrateData.GetHeroMemorys / IsHeroMemorysDirty：注入的角色剧情由客户端自己解锁。
--   * SoundHelper.LoadSFX（登录阶段：Wwise 已初始化，舰娘音频库还没加载）：用客户端自带的中文语音库生成改写过
--     ID 的副本，打成 AKPK 文件包写到 cache/ 并挂载，再改 C# 字幕缓存。字幕与誓约文本只对中文语音确实生效的
--     舰娘换成国服文本。原 LoadSFX 无论如何都会照常调用。
-- 钩子不向游戏抛错；应用、跳过与 Wwise 结果码都以 "[cn-exclusive]" 开头写进 BlueOath.Payload.log。

-- 单项开关：改成 false 后重启游戏生效。整个 Mod 的开关是 mod.json 的 "enabled"（启动器的 Mod 页）。
local SETTINGS = {
  fubuki_spring_fashion = true,   -- 吹雪春节时装「爆竹天女」
  oakland_spring_touch = true,    -- 奥克兰春节时装的触摸动作与语音
  spring_festival_story = true,   -- 春节风波（回忆章节 12010，需要服务端登记）
  character_stories = true,       -- 10 艘舰娘的角色剧情
  cn_voice_ninghai = true,        -- 宁海中文语音
  cn_voice_pinghai = true,        -- 平海中文语音
  -- 中文语音的附属项，对宁海、平海两项都生效
  voice = {
    captions = true,      -- 字幕换成国服文本（改 C# 字幕缓存，尽力而为）
    wedding_text = true,  -- 誓约剧情换成国服文本（config_plot_episode_wedding；与字幕一样只在该舰娘的中文语音生效时）
    dorm = true,          -- 宿舍语音（共享库 CV_role_sd_bank）
    letter = true,        -- 情书语音（共享库 CV_role_gaobai_bank）
    probe = true,         -- 挂载后加载一次探测库，确认 Wwise 确实从文件包取库
  },
}

local MOD_FOLDER = "cn-exclusive.mod"
local LOG_PREFIX = "[cn-exclusive] "
local WEDDING_TABLE = "config_plot_episode_wedding"
local STORY_TABLE = "config_building_character_story"
local SHARED_BANK_SWITCHES = {CV_role_sd_bank = "dorm", CV_role_gaobai_bank = "letter"}

-- Wwise 侧的格式事实来自日服 1.4.0 的 AkSoundEngine.dll（Wwise 2018.1，库版本 128）。
local BANK_VERSION = 128
local EVENT_OBJECT = 4
local ACTION_OBJECT = 3
local PLAY_ACTION = 0x0403        -- CAkActionPlay，参数以 u8 淡入曲线 + u32 所属库 ID 结尾
local LANG_SFX_ID = 393239870     -- 日服所有非本地化库 BKHD 里的语言 ID（= FNV-1("sfx")）
local PROJECT_ID = 5378           -- 日服所有库 BKHD 里的工程 ID
local AKPK_VERSION = 1
local BLOCK_SIZE = 16
local PROBE_BANK = "cn_exclusive_probe_bank"
local PACKAGE_FILE = "cn-exclusive.pck"
local STAMP_FILE = "cn-exclusive.stamp"
local CACHE_FORMAT = "cn-exclusive voice cache 1"
local MAX_PATH = 260
local AK_SUCCESS = 1
local AK_INVALID_LANGUAGE = 22
local AK_FILE_NOT_FOUND = 66
local AK_RESULT_NAMES = {
  [0] = "AK_NotImplemented", [1] = "AK_Success", [2] = "AK_Fail", [7] = "AK_InvalidFile",
  [15] = "AK_IDNotFound", [22] = "AK_InvalidLanguage", [31] = "AK_InvalidParameter", [36] = "AK_PathNotFound",
  [52] = "AK_InsufficientMemory", [54] = "AK_UnknownBankID", [56] = "AK_BankReadError",
  [64] = "AK_WrongBankVersion", [66] = "AK_FileNotFound", [69] = "AK_BankAlreadyLoaded",
  [74] = "AK_MemManagerNotInitialized", [75] = "AK_StreamMgrNotInitialized", [92] = "AK_InitBankNotLoaded",
}

-- bootstrap 只保证执行入口顶层时这两个全局指向本 Mod 所在的 Mods 根目录，之后可能被改写或失效，
-- 所以在这里立刻保存，之后只用保存下来的值。
local mod_root = __BLUEOATH_MOD_ROOT
local load_mod_file = __blueoath_loadfile

local function log(message)
  mod.info(LOG_PREFIX .. tostring(message))
end

local function safely(label, action, ...)
  local ok, result = xpcall(action, debug.traceback, ...)
  if not ok then
    log(label .. " failed: " .. tostring(result))
  end
  return ok, result
end

local function assign_with_previous(previous, target, key, value)
  if type(previous) == "function" then
    previous(target, key, value)
  elseif type(previous) == "table" then
    previous[key] = value
  else
    rawset(target, key, value)
  end
end

local function join_path(...)
  local parts = {}
  for index, part in ipairs({...}) do
    part = string.gsub(part, "/", "\\")
    if index > 1 then
      part = string.gsub(part, "^\\+", "")
    end
    part = string.gsub(part, "\\+$", "")
    parts[#parts + 1] = part
  end
  return table.concat(parts, "\\")
end

local mod_dir = type(mod_root) == "string" and join_path(mod_root, MOD_FOLDER) or nil

local function load_data()
  if type(load_mod_file) ~= "function" then
    return nil, "__blueoath_loadfile is unavailable"
  end
  local chunk, load_error = load_mod_file(MOD_FOLDER .. "/data.lua")
  if not chunk then
    return nil, "cannot load data.lua: " .. tostring(load_error)
  end
  local ok, result = pcall(chunk)
  if not ok then
    return nil, "data.lua failed: " .. tostring(result)
  end
  if type(result) ~= "table" or type(result.patches) ~= "table" or type(result.inserts) ~= "table" or
      type(result.memory_chapters) ~= "table" or type(result.hero_memories) ~= "table" or
      type(result.voice) ~= "table" or type(result.voice.banks) ~= "table" or
      type(result.voice.captions) ~= "table" then
    return nil, "data.lua did not return the expected tables"
  end
  return result
end

local DATA, data_error = load_data()

local function feature_on(feature)
  return SETTINGS[feature] == true
end

local function voice_option(name)
  return type(SETTINGS.voice) == "table" and SETTINGS.voice[name] == true
end

local function deep_clone(value)
  if type(value) ~= "table" then
    return value
  end
  local result = {}
  for key, child in pairs(value) do
    result[key] = deep_clone(child)
  end
  return result
end

local function sorted_keys(map)
  local keys = {}
  for key in pairs(map) do
    keys[#keys + 1] = key
  end
  table.sort(keys, function(a, b)
    return tostring(a) < tostring(b)
  end)
  return keys
end

local function describe(value)
  if type(value) == "string" then
    return string.format("%q", value)
  end
  return tostring(value)
end

-- xLua 的 CS 命名空间对不存在的类型返回带 ".fqn" 的占位表，这里把它当作不存在。
local function cs_type(path)
  local cs = rawget(_G, "CS")
  if cs == nil then
    return nil
  end
  local ok, value = pcall(function()
    local node = cs
    for part in string.gmatch(path, "[^%.]+") do
      node = node[part]
      if node == nil then
        return nil
      end
    end
    return node
  end)
  if not ok or value == nil then
    return nil
  end
  if type(value) == "table" and rawget(value, ".fqn") ~= nil then
    return nil
  end
  return value
end

-- bootstrap 不读 mod.json 的 enabled，这里自己读。CS 出现之前无法读文件，先按启用处理且不缓存结论；
-- 读不到或解析不出时按启用处理。
local mod_enabled = nil
local function enabled()
  if mod_enabled ~= nil then
    return mod_enabled
  end
  local file = cs_type("System.IO.File")
  if file == nil or mod_dir == nil then
    return true
  end
  local ok, text = pcall(function()
    return file.ReadAllText(mod_dir .. "\\mod.json")
  end)
  if not ok or type(text) ~= "string" then
    mod_enabled = true
    log("mod.json is unreadable, treating the mod as enabled: " .. tostring(text))
  elseif string.find(text, '"enabled"%s*:%s*false') then
    mod_enabled = false
    log('mod.json has "enabled": false; cn-exclusive changes nothing')
  else
    mod_enabled = true
  end
  return mod_enabled
end

------------------------------------------------------------------------------------------------------------------
-- 配置：patches / inserts
------------------------------------------------------------------------------------------------------------------

-- name -> {patches = {[id] = {patch...}}, patch_list, inserts = {[id] = insert}, insert_list, inserted = {[id] = true}}
local config_tables = {}
-- 每条 patch / insert 只记一次结论（true 已应用，false 已跳过）；每个功能全部有结论时写一行汇总。
local entry_states = {}
local feature_progress = {}

local function table_index(name)
  local index = config_tables[name]
  if index == nil then
    index = {patches = {}, patch_list = {}, inserts = {}, insert_list = {}, inserted = {}}
    config_tables[name] = index
  end
  return index
end

local function track(entry, kind)
  local progress = feature_progress[entry.feature]
  if progress == nil then
    progress = {total = 0, done = 0, skipped = 0, tables = {}, table_order = {}}
    feature_progress[entry.feature] = progress
  end
  local counts = progress.tables[entry.table]
  if counts == nil then
    counts = {patch = 0, insert = 0, patch_applied = 0, insert_applied = 0}
    progress.tables[entry.table] = counts
    progress.table_order[#progress.table_order + 1] = entry.table
  end
  counts[kind] = counts[kind] + 1
  progress.total = progress.total + 1
end

local function log_feature_summary(feature, progress)
  local parts = {}
  for _, name in ipairs(progress.table_order) do
    local counts = progress.tables[name]
    if counts.patch > 0 then
      parts[#parts + 1] = string.format("%s %d/%d patched", name, counts.patch_applied, counts.patch)
    end
    if counts.insert > 0 then
      parts[#parts + 1] = string.format("%s %d/%d added", name, counts.insert_applied, counts.insert)
    end
  end
  local line = feature .. ": " .. table.concat(parts, ", ")
  if progress.skipped > 0 then
    line = line .. string.format("; %d skipped (see the skip lines above)", progress.skipped)
  end
  log(line)
end

local function settle(entry, kind, applied, reason)
  if entry_states[entry] ~= nil then
    return
  end
  entry_states[entry] = applied
  if not applied then
    log(string.format("skip %s %s %s (%s): %s", kind, entry.table, tostring(entry.id), entry.feature, reason))
  end
  local progress = feature_progress[entry.feature]
  if progress == nil then
    return
  end
  progress.done = progress.done + 1
  if applied then
    local counts = progress.tables[entry.table]
    counts[kind .. "_applied"] = counts[kind .. "_applied"] + 1
  else
    progress.skipped = progress.skipped + 1
  end
  if progress.done == progress.total then
    log_feature_summary(entry.feature, progress)
  end
end

local function patch_wanted(patch)
  if not feature_on(patch.feature) then
    return false
  end
  if patch.table == WEDDING_TABLE then
    return voice_option("wedding_text")
  end
  return true
end

if DATA ~= nil then
  for _, patch in ipairs(DATA.patches) do
    if patch_wanted(patch) then
      local index = table_index(patch.table)
      local list = index.patches[patch.id]
      if list == nil then
        list = {}
        index.patches[patch.id] = list
      end
      list[#list + 1] = patch
      index.patch_list[#index.patch_list + 1] = patch
      track(patch, "patch")
    end
  end
  for _, insert in ipairs(DATA.inserts) do
    if feature_on(insert.feature) then
      local index = table_index(insert.table)
      index.inserts[insert.id] = insert
      index.insert_list[#index.insert_list + 1] = insert
      track(insert, "insert")
    end
  end
end

-- 同一 ID 的行可能有多份对象：整表缓存里一份，整表缓存之前 GetDataById 单独解码的又是另一份。每份都检查并修改一次。
local patched_rows = setmetatable({}, {__mode = "k"})
local prepared_tables = setmetatable({}, {__mode = "k"})

-- 语音的结论（setup_voice 设置）：voice_active 是舰娘自己的语音库已打进包并挂载的功能。字幕和誓约文本只对这些功能
-- 换成国服文本，免得中文文本配日语语音。
local voice_active = {}
local voice_done = false
-- 语音有结论之前读到的誓约行：row -> {[patch] = true}，由 apply_deferred_wedding 在 SoundHelper.LoadSFX 里补做。
-- 游戏只在播放剧情时读这张表，那时登录阶段的 LoadSFX 早已执行，这里通常是空的。
local deferred_wedding = setmetatable({}, {__mode = "k"})
local wedding_deferred_logged = false

local function apply_patch(patch, row)
  if type(row) ~= "table" then
    settle(patch, "patch", false, "the row is missing from the client table")
    return
  end
  local done = patched_rows[row]
  if done == nil then
    done = {}
    patched_rows[row] = done
  end
  if done[patch] then
    return
  end
  if patch.table == WEDDING_TABLE then
    if not voice_done then
      local pending = deferred_wedding[row]
      if pending == nil then
        pending = {}
        deferred_wedding[row] = pending
      end
      pending[patch] = true
      if not wedding_deferred_logged then
        wedding_deferred_logged = true
        log(WEDDING_TABLE .. " was read before the voice setup; its text waits for the voice result")
      end
      return
    end
    if not voice_active[patch.feature] then
      settle(patch, "patch", false, "the Chinese voice is not active")
      return
    end
  end
  done[patch] = true
  for _, key in ipairs(sorted_keys(patch.expect)) do
    local expected = patch.expect[key]
    if row[key] ~= expected then
      settle(patch, "patch", false, string.format("%s is %s, expected %s", key, describe(row[key]),
        describe(expected)))
      return
    end
  end
  for key, value in pairs(patch.set) do
    row[key] = deep_clone(value)
  end
  settle(patch, "patch", true)
end

local function prepare_table(name, data)
  prepared_tables[data] = true
  if not enabled() then
    return
  end
  local index = config_tables[name]
  for _, patch in ipairs(index.patch_list) do
    apply_patch(patch, data[patch.id])
  end
  for _, insert in ipairs(index.insert_list) do
    if data[insert.id] == nil then
      data[insert.id] = deep_clone(insert.row)
      index.inserted[insert.id] = true
      settle(insert, "insert", true)
    elseif not index.inserted[insert.id] then
      settle(insert, "insert", false, "the id already exists in the client table")
    end
  end
end

local function apply_deferred_wedding()
  if not voice_done or next(deferred_wedding) == nil then
    return
  end
  local rows = deferred_wedding
  deferred_wedding = setmetatable({}, {__mode = "k"})
  local index = config_tables[WEDDING_TABLE]
  for _, patch in ipairs(index ~= nil and index.patch_list or {}) do
    for row, pending in pairs(rows) do
      if pending[patch] then
        apply_patch(patch, row)
      end
    end
  end
end

local config_patched = false

local function patch_config_manager(manager)
  if config_patched or type(manager) ~= "table" then
    return
  end
  if next(config_tables) == nil then
    return
  end
  local previous_get_data = manager.GetData
  local previous_get_data_by_id = manager.GetDataById
  if type(previous_get_data) ~= "function" or type(previous_get_data_by_id) ~= "function" then
    error("configManager API is unavailable")
  end

  -- 发布版 isDebug=false，GetData 返回缓存表本身，可以原地修改；Logic 初始化时建的缓存（copyLogic.tabAllCopyId、
  -- buildingLogic.shipFleetPlotMap 等）只建一次，所以必须在每张表第一次返回时就改好。
  manager.GetData = function(name, ...)
    local data = previous_get_data(name, ...)
    if config_tables[name] ~= nil and type(data) == "table" and not prepared_tables[data] then
      safely("config " .. tostring(name), prepare_table, name, data)
    end
    return data
  end

  -- 注入的 ID 先回答，不交给前一层：整表还没缓存时原函数会去 SQLite 按 ID 查，查不到还会写错误日志。
  manager.GetDataById = function(name, id, ...)
    local index = config_tables[name]
    if index == nil then
      return previous_get_data_by_id(name, id, ...)
    end
    local numeric_id = tonumber(id)
    if numeric_id ~= nil and index.inserts[numeric_id] ~= nil and enabled() then
      local data = manager.GetData(name)
      local row = type(data) == "table" and data[numeric_id] or nil
      if row ~= nil then
        return row
      end
    end
    local row = previous_get_data_by_id(name, id, ...)
    local patches = numeric_id ~= nil and index.patches[numeric_id] or nil
    if patches ~= nil and type(row) == "table" and enabled() then
      for _, patch in ipairs(patches) do
        safely("config " .. tostring(name) .. " " .. tostring(id), apply_patch, patch, row)
      end
    end
    return row
  end

  config_patched = true
  local names = sorted_keys(config_tables)
  log("configManager hooks installed for " .. table.concat(names, ", "))
end

------------------------------------------------------------------------------------------------------------------
-- 图鉴「回忆」：回忆章节与角色剧情
------------------------------------------------------------------------------------------------------------------

local memory_chapters = {}
local hero_memories = {}
if DATA ~= nil then
  for _, chapter in ipairs(DATA.memory_chapters) do
    if feature_on(chapter.feature) then
      memory_chapters[chapter.chapter_id] = chapter
    end
  end
  for _, entry in ipairs(DATA.hero_memories) do
    if feature_on(entry.feature) then
      hero_memories[#hero_memories + 1] = entry
    end
  end
end

local function server_memory_index(chapter_id)
  local data = rawget(_G, "Data")
  local illustrate = type(data) == "table" and data.illustrateData or nil
  if type(illustrate) ~= "table" or type(illustrate.GetMemoryIndexByChapterId) ~= "function" then
    return nil
  end
  local ok, index = pcall(illustrate.GetMemoryIndexByChapterId, illustrate, chapter_id)
  if not ok then
    return nil
  end
  return tonumber(index)
end

-- 回忆进度来自服务端 illustrate.Memory。没有登记这一章的服务端（旧版本，或服务端关掉了本 Mod）不会给出进度，
-- 这时不列出它，免得多出一个打不开的 0/N 条目。
local memory_chapter_states = {}
local function filter_memory_chapters(list)
  if type(list) ~= "table" then
    return
  end
  for position = #list, 1, -1 do
    local chapter = list[position]
    local chapter_id = type(chapter) == "table" and tonumber(chapter.id) or nil
    local entry = chapter_id ~= nil and memory_chapters[chapter_id] or nil
    if entry ~= nil then
      local index = server_memory_index(chapter_id)
      local shown = index ~= nil and index > 0
      if not shown then
        table.remove(list, position)
      end
      local state = shown and "shown" or "hidden"
      if memory_chapter_states[chapter_id] ~= state then
        memory_chapter_states[chapter_id] = state
        if shown then
          log(string.format("%s: memory chapter %d listed (server progress %s/%d)", entry.feature, chapter_id,
            tostring(index), #entry.copies))
        else
          log(string.format("%s: memory chapter %d hidden: the server has not registered it (progress %s); " ..
            "it needs a server that reads memory-chapters.json", entry.feature, chapter_id, tostring(index)))
        end
      end
    end
  end
end

-- HeroMemory 由服务端推送；Mod 新增的剧情不在其中（服务端也不应推送，见 README），在客户端并入：
-- heroMemoryList[ship_fleet_id][story_id] = true。只并入确实注入了配置行的剧情，否则回忆页会在
-- #GetHeroPlotCfgs(sfId) 处对 nil 取长度。重复调用不会重复添加。
local hero_memory_logged = false
local hero_memory_empty_logged = false
local function merge_hero_memories(memories)
  if type(memories) ~= "table" then
    return 0
  end
  local index = config_tables[STORY_TABLE]
  local inserted = index ~= nil and index.inserted or {}
  local added, stories, ships = 0, 0, 0
  for _, entry in ipairs(hero_memories) do
    local unlocked = memories[entry.ship_fleet_id]
    local counted = false
    for _, story_id in ipairs(entry.story_ids) do
      if inserted[story_id] then
        if type(unlocked) ~= "table" then
          unlocked = {}
          memories[entry.ship_fleet_id] = unlocked
        end
        if not unlocked[story_id] then
          unlocked[story_id] = true
          added = added + 1
        end
        stories = stories + 1
        counted = true
      end
    end
    if counted then
      ships = ships + 1
    end
  end
  if stories > 0 and not hero_memory_logged then
    hero_memory_logged = true
    log(string.format("character_stories: %d stories of %d ships unlocked in the memories (client side)",
      stories, ships))
  elseif stories == 0 and not hero_memory_empty_logged then
    hero_memory_empty_logged = true
    log("character_stories: no injected story rows yet, nothing unlocked in the memories")
  end
  return added
end

local patched_classes = setmetatable({}, {__mode = "k"})

local function patch_illustrate_logic(class)
  if type(class) ~= "table" or patched_classes[class] or next(memory_chapters) == nil or not enabled() then
    return
  end
  local previous = class.GetMemoryData
  if type(previous) ~= "function" then
    error("IllustrateLogic.GetMemoryData is unavailable")
  end
  class.GetMemoryData = function(self, ...)
    local list = previous(self, ...)
    safely("memory chapter filter", filter_memory_chapters, list)
    return list
  end
  patched_classes[class] = true
  log("IllustrateLogic.GetMemoryData patched (memory chapters " .. table.concat(sorted_keys(memory_chapters), ", ") ..
    " need the server's acknowledgement)")
end

local function patch_illustrate_data(class)
  if type(class) ~= "table" or patched_classes[class] or #hero_memories == 0 or not enabled() then
    return
  end
  local previous_get = class.GetHeroMemorys
  local previous_dirty = class.IsHeroMemorysDirty
  if type(previous_get) ~= "function" or type(previous_dirty) ~= "function" then
    error("IllustrateData.GetHeroMemorys / IsHeroMemorysDirty is unavailable")
  end
  -- ChatLogic 等直接读这份数据；先于 IllustrateLogic 并入时标脏，让后者重建它缓存的列表。
  class.GetHeroMemorys = function(self, ...)
    local memories = previous_get(self, ...)
    local ok, added = safely("hero memory merge", merge_hero_memories, memories)
    if ok and added > 0 and type(self.SetHeroMemorysDirty) == "function" then
      self:SetHeroMemorysDirty(true)
    end
    return memories
  end
  -- IllustrateLogic.GetHeroMemorys 每次先问是否脏，再决定是否用缓存的列表：并入了新内容就回答"脏"。
  class.IsHeroMemorysDirty = function(self, ...)
    local dirty = previous_dirty(self, ...)
    local ok, added = safely("hero memory merge", merge_hero_memories, self.heroMemoryList)
    if ok and added > 0 then
      return true
    end
    return dirty
  end
  patched_classes[class] = true
  log("IllustrateData.GetHeroMemorys / IsHeroMemorysDirty patched")
end

-- 改类表而不是 Logic.illustrateLogic / Data.illustrateData 实例：middleclass 的类表赋值会同步到实例的
-- __instanceDict，加载早晚都能生效；也不必在 Logic 表上挂 __newindex（别的 Mod 在那里装的监听会在取到自己的键后
-- 恢复旧的 __newindex，后装的监听可能被一并拆掉）。游戏重写的 require 把类路径转成小写点分路径
-- （logic.illustratelogic、data.illustratedata），这里规范化后按末段匹配。
local CLASS_PATCHES = {
  illustratelogic = patch_illustrate_logic,
  illustratedata = patch_illustrate_data,
}

local function class_patch_for(key)
  if type(key) ~= "string" then
    return nil
  end
  local path = string.lower(key)
  path = (string.gsub(path, "[/\\]", "."))
  path = (string.gsub(path, "%.lua$", ""))
  local name = string.match(path, "([^%.]+)$")
  return name ~= nil and CLASS_PATCHES[name] or nil
end

local function watch_class_modules()
  local loaded = package.loaded
  for key, value in pairs(loaded) do
    local patch = class_patch_for(key)
    if patch ~= nil then
      safely("class " .. key, patch, value)
    end
  end
  -- 监听常驻且只在新键写入时触发；先按原链路写入，修补失败只记日志，绝不向 require 抛错。
  local meta = getmetatable(loaded) or {}
  local previous_newindex = meta.__newindex
  meta.__newindex = function(target, key, value)
    assign_with_previous(previous_newindex, target, key, value)
    local patch = class_patch_for(key)
    if patch ~= nil then
      safely("class " .. tostring(key), patch, value)
    end
  end
  setmetatable(loaded, meta)
end

------------------------------------------------------------------------------------------------------------------
-- 语音：库 ID 改写与 AKPK 文件包（纯 Lua 5.3）
------------------------------------------------------------------------------------------------------------------

-- AK::SoundEngine::GetIDFromString：UTF-8 字节上的 FNV-1 32 位哈希，只把 A–Z 转成小写。
local function fnv1_32(text)
  local hash = 2166136261
  for position = 1, #text do
    local byte = string.byte(text, position)
    if byte >= 65 and byte <= 90 then
      byte = byte + 32
    end
    hash = (hash * 16777619) & 0xFFFFFFFF
    hash = hash ~ byte
  end
  return hash
end

local function bank_sections(data)
  local sections = {}
  local position = 1
  local length = #data
  while position <= length do
    if position + 7 > length then
      error("truncated section header at " .. (position - 1), 0)
    end
    local tag, size = string.unpack("<c4I4", data, position)
    local body = position + 8
    if body + size - 1 > length then
      error("section " .. tag .. " overruns the file", 0)
    end
    sections[#sections + 1] = {tag = tag, offset = body, size = size}
    position = body + size
  end
  return sections
end

-- HIRC：u32 个数，每个对象 u8 类型 + u32 大小 + u32 ID + 其余内容（大小从 ID 算起）。position 指类型字节。
local function hirc_objects(data, offset, size)
  if size < 4 then
    error("HIRC is too small", 0)
  end
  local count = string.unpack("<I4", data, offset)
  local position = offset + 4
  local finish = offset + size
  local objects = {}
  for _ = 1, count do
    if position + 9 > finish then
      error("truncated HIRC object header", 0)
    end
    local object_type, object_size, object_id = string.unpack("<BI4I4", data, position)
    if object_size < 4 or position + 5 + object_size > finish then
      error("HIRC object overruns the chunk", 0)
    end
    objects[#objects + 1] = {type = object_type, id = object_id, position = position, size = object_size}
    position = position + 5 + object_size
  end
  if position ~= finish then
    error("HIRC object sizes do not add up to the chunk size", 0)
  end
  return objects
end

-- Play 动作末尾 u32 所属库 ID 的位置；其他动作返回 nil。
local function play_bank_id_position(data, object)
  local position = object.position + 9
  local finish = object.position + 5 + object.size
  if string.unpack("<I2", data, position) ~= PLAY_ACTION then
    return nil
  end
  position = position + 2 + 4 + 1                          -- 动作类型、目标 ID、是否总线
  position = position + 1 + string.byte(data, position) * 5  -- 属性：个数、ID、值
  position = position + 1 + string.byte(data, position) * 9  -- 范围属性：个数、ID、(最小, 最大)
  position = position + 1                                  -- 淡入曲线
  if position + 4 ~= finish then
    error("unexpected Play action layout at offset " .. (object.position - 1), 0)
  end
  return position
end

-- 返回改写后的库（大小不变）。全部检查通过才改，任何一项不符就报错，由调用方跳过这个库：
-- 版本 128、首段 BKHD、恰好一个 HIRC、对象大小对得上、BKHD 库 ID 符合；每个要改的 ID 都是只被一个对象使用的事件，
-- 改成的 ID 在库里没有被占用（同一次互换的除外）；库 ID 不同时，每个 Play 动作的所属库 ID 都是源库 ID、个数等于
-- play_actions，且源库 ID 只出现在 BKHD 和这些 Play 动作里。
local function patch_bank(data, spec)
  if spec.duplicate ~= nil then
    error("event id " .. spec.duplicate .. " is listed twice in data.lua", 0)
  end
  local sections = bank_sections(data)
  local header, hirc
  local headers = 0
  for _, section in ipairs(sections) do
    if section.tag == "BKHD" then
      headers = headers + 1
      header = section
    elseif section.tag == "HIRC" then
      if hirc ~= nil then
        error("more than one HIRC chunk", 0)
      end
      hirc = section
    end
  end
  if sections[1] == nil or sections[1].tag ~= "BKHD" or headers ~= 1 or hirc == nil then
    error("the bank must start with BKHD and contain exactly one HIRC", 0)
  end
  if header.size < 20 then
    error("BKHD is shorter than 20 bytes", 0)
  end
  local version, bank_id = string.unpack("<I4I4", data, header.offset)
  if version ~= BANK_VERSION then
    error("bank version " .. version .. ", expected " .. BANK_VERSION, 0)
  end
  if bank_id ~= spec.source_bank_id then
    error("BKHD bank id " .. bank_id .. ", expected " .. spec.source_bank_id, 0)
  end

  local objects = hirc_objects(data, hirc.offset, hirc.size)
  local users = {}
  local events = {}
  for _, object in ipairs(objects) do
    users[object.id] = (users[object.id] or 0) + 1
    if object.type == EVENT_OBJECT then
      if events[object.id] ~= nil then
        error("duplicate event id " .. object.id, 0)
      end
      events[object.id] = object
    end
  end
  local targets = {}
  for from, to in pairs(spec.renames) do
    if events[from] == nil then
      error("event " .. from .. " is not in the bank", 0)
    end
    if users[from] ~= 1 then
      error("id " .. from .. " is used by several objects", 0)
    end
    if users[to] ~= nil and spec.renames[to] == nil then
      error("target id " .. to .. " is already used in the bank", 0)
    end
    if targets[to] then
      error("two events renamed to " .. to, 0)
    end
    targets[to] = true
  end

  local writes = {}
  for from, to in pairs(spec.renames) do
    writes[events[from].position + 5] = to
  end
  if spec.target_bank_id ~= spec.source_bank_id then
    local fields = {}
    for _, object in ipairs(objects) do
      if object.type == ACTION_OBJECT then
        local position = play_bank_id_position(data, object)
        if position ~= nil then
          if string.unpack("<I4", data, position) ~= spec.source_bank_id then
            error("Play action " .. object.id .. " references another bank", 0)
          end
          fields[#fields + 1] = position
        end
      end
    end
    if spec.play_actions ~= nil and #fields ~= spec.play_actions then
      error(#fields .. " Play actions, expected " .. spec.play_actions, 0)
    end
    local known = {[header.offset + 4] = true}
    for _, position in ipairs(fields) do
      known[position] = true
    end
    local needle = string.pack("<I4", spec.source_bank_id)
    local hits = 0
    local start = 1
    while true do
      local found = string.find(data, needle, start, true)
      if found == nil then
        break
      end
      if not known[found] then
        error("the bank id occurs at an unexpected offset " .. (found - 1), 0)
      end
      hits = hits + 1
      start = found + 1
    end
    if hits ~= #fields + 1 then
      error("the bank id occurs " .. hits .. " times, expected " .. (#fields + 1), 0)
    end
    writes[header.offset + 4] = spec.target_bank_id
    for _, position in ipairs(fields) do
      writes[position] = spec.target_bank_id
    end
  end

  local positions = {}
  for position in pairs(writes) do
    positions[#positions + 1] = position
  end
  table.sort(positions)
  local pieces = {}
  local cursor = 1
  for _, position in ipairs(positions) do
    pieces[#pieces + 1] = string.sub(data, cursor, position - 1)
    pieces[#pieces + 1] = string.pack("<I4", writes[position])
    cursor = position + 4
  end
  pieces[#pieces + 1] = string.sub(data, cursor)
  return table.concat(pieces)
end

-- 只有 20 字节 BKHD 的库：读到文件尾即成功。经文件包加载返回 AK_Success，文件包没被查找时返回 AK_FileNotFound。
local function probe_bank()
  return "BKHD" .. string.pack("<I4I4I4I4I4I4", 20, BANK_VERSION, fnv1_32(PROBE_BANK), LANG_SFX_ID, 0, PROJECT_ID)
end

-- 语言表只有一项 {"sfx", 0}：文件包与游戏当前语言无关。u32 个数，{u32 距表头偏移, u32 ID}，UTF-16LE 字符串，补齐到 4。
local function language_map()
  local raw = string.pack("<I4I4I4", 1, 12, 0) .. "s\0f\0x\0\0\0"
  return raw .. string.rep("\0", (-#raw) % 4)
end

-- AKPK：'AKPK'、u32 头大小（这 8 字节之后到最后一张表末尾）、u32 版本、四张表的大小（语言、库、流文件、外部源），
-- 然后是语言表、库表（u32 个数 + 每项 {文件 ID, 块大小, 文件大小, 起始块, 语言 ID}，按文件 ID 升序）、两张空表，
-- 最后是文件本体。文件从第 起始块 × 块大小 字节开始，文件和整个包都补零到块大小。
local function build_package(files)
  local lut = {}
  for _, file in ipairs(files) do
    local id = fnv1_32(file.name)
    if #file.data < 28 or string.sub(file.data, 1, 4) ~= "BKHD" or string.unpack("<I4", file.data, 13) ~= id then
      error(file.name .. " is not a bank with the id of its name", 0)
    end
    lut[#lut + 1] = {id = id, data = file.data}
  end
  table.sort(lut, function(a, b)
    return a.id < b.id
  end)
  for position = 2, #lut do
    if lut[position].id == lut[position - 1].id then
      error("two banks with id " .. lut[position].id, 0)
    end
  end
  local language = language_map()
  local banks_size = 4 + 20 * #lut
  local header_size = 20 + #language + banks_size + 4 + 4
  local position = 8 + header_size
  position = position + (-position) % BLOCK_SIZE
  local data_start = position
  local entries = {}
  local body = {}
  for _, file in ipairs(lut) do
    entries[#entries + 1] = string.pack("<I4I4I4I4I4", file.id, BLOCK_SIZE, #file.data, position // BLOCK_SIZE, 0)
    local pad = (-#file.data) % BLOCK_SIZE
    body[#body + 1] = file.data
    body[#body + 1] = string.rep("\0", pad)
    position = position + #file.data + pad
  end
  local head = "AKPK" .. string.pack("<I4I4I4I4I4I4", header_size, AKPK_VERSION, #language, banks_size, 4, 4) ..
    language .. string.pack("<I4", #lut) .. table.concat(entries) .. string.pack("<I4I4", 0, 0)
  return head .. string.rep("\0", data_start - #head) .. table.concat(body)
end

------------------------------------------------------------------------------------------------------------------
-- 语音：挂载
------------------------------------------------------------------------------------------------------------------

-- 经 xLua 反射调用时 AKRESULT 可能是数字也可能是枚举对象，统一换成数字再比较。
local function result_code(result)
  if type(result) == "number" then
    return result
  end
  if result == nil then
    return nil
  end
  local ok, value = pcall(function()
    return result:GetHashCode()
  end)
  if ok and type(value) == "number" then
    return value
  end
  return tonumber(string.match(tostring(result), "(%-?%d+)"))
end

local function result_text(result)
  local code = result_code(result)
  return string.format("%s(%s)", AK_RESULT_NAMES[code] or tostring(result), tostring(code))
end

local function utf16_length(text)
  if utf8 == nil or utf8.codes == nil then
    return #text
  end
  local ok, count = pcall(function()
    local units = 0
    for _, code in utf8.codes(text) do
      units = units + (code >= 0x10000 and 2 or 1)
    end
    return units
  end)
  return ok and count or #text
end

local function ascii_lower(text)
  return (string.gsub(text, "[A-Z]", function(letter)
    return string.char(string.byte(letter) + 32)
  end))
end

local function split_path(path)
  path = string.gsub(path, "/", "\\")
  local drive, rest = string.match(path, "^(%a:)\\(.*)$")
  if drive == nil then
    return nil
  end
  local parts = {}
  for part in string.gmatch(rest, "[^\\]+") do
    if part == ".." then
      parts[#parts] = nil
    elseif part ~= "." then
      parts[#parts + 1] = part
    end
  end
  return ascii_lower(drive), parts
end

-- 文件包名直接拼在 Wwise 基础路径后面（不支持绝对路径，".." 由 CreateFileW 规范化），所以给出从库目录到文件包的
-- 相对路径；不在同一个盘时返回 nil。
local function relative_path(from_dir, to_file)
  local from_drive, from_parts = split_path(from_dir)
  local to_drive, to_parts = split_path(to_file)
  if from_drive == nil or from_drive ~= to_drive or #to_parts == 0 then
    return nil
  end
  local common = 0
  while common < #from_parts and common < #to_parts - 1 and
      ascii_lower(from_parts[common + 1]) == ascii_lower(to_parts[common + 1]) do
    common = common + 1
  end
  local parts = {}
  for _ = common + 1, #from_parts do
    parts[#parts + 1] = ".."
  end
  for position = common + 1, #to_parts do
    parts[#parts + 1] = to_parts[position]
  end
  return table.concat(parts, "\\")
end

local function directory_path(path)
  path = string.gsub(path, "/", "\\")
  if string.sub(path, -1) ~= "\\" then
    path = path .. "\\"
  end
  return path
end

local function sound_bank_base_path()
  local getter = cs_type("AkBasePathGetter")
  if getter ~= nil then
    local ok, base = pcall(function()
      return getter.GetSoundbankBasePath()
    end)
    if ok and type(base) == "string" and base ~= "" then
      return directory_path(base)
    end
  end
  -- 与 AkBasePathGetter 相同的拼法，只在上面取不到时使用。
  local application = cs_type("UnityEngine.Application")
  local ok, assets = pcall(function()
    return application.streamingAssetsPath
  end)
  if ok and type(assets) == "string" and assets ~= "" then
    log("voice: AkBasePathGetter.GetSoundbankBasePath is unavailable, deriving the bank folder")
    return directory_path(join_path(assets, "Audio", "GeneratedSoundBanks", "Windows"))
  end
  return nil
end

local function package_id_of(engine, name)
  local ok, id = pcall(function()
    return engine.GetIDFromString(name)
  end)
  if ok and type(id) == "number" then
    return id
  end
  return fnv1_32(name)
end

local function mount_package(engine, base, cache_dir)
  local failures = {}
  local relative = relative_path(base, cache_dir .. "\\" .. PACKAGE_FILE)
  if relative ~= nil and utf16_length(base) + utf16_length(relative) < MAX_PATH then
    local ok, result, package_id = pcall(function()
      return engine.LoadFilePackage(relative, -1)
    end)
    local code = ok and result_code(result) or nil
    if code == AK_SUCCESS or code == AK_INVALID_LANGUAGE then
      return true, string.format("relative path %s: %s, package id %s", relative, result_text(result),
        tostring(package_id))
    end
    failures[#failures + 1] = "relative path " .. relative .. ": " .. (ok and result_text(result) or tostring(result))
  else
    failures[#failures + 1] = "no usable relative path from the bank folder (other drive or path too long)"
  end
  -- 改用缓存目录作基础路径挂载，然后无论成败都恢复原来的基础路径。
  local ok, switched, result, package_id = pcall(function()
    local set = engine.SetBasePath(cache_dir .. "\\")
    return set, engine.LoadFilePackage(PACKAGE_FILE, -1)
  end)
  local restored_ok, restored = pcall(function()
    return engine.SetBasePath(base)
  end)
  if not restored_ok or result_code(restored) ~= AK_SUCCESS then
    log("voice: WARNING: restoring the Wwise base path returned " ..
      (restored_ok and result_text(restored) or tostring(restored)))
  end
  if ok then
    local code = result_code(result)
    if code == AK_SUCCESS or code == AK_INVALID_LANGUAGE then
      return true, string.format("base path switch (SetBasePath %s): %s, package id %s", result_text(switched),
        result_text(result), tostring(package_id))
    end
    failures[#failures + 1] = "base path switch (SetBasePath " .. result_text(switched) .. "): " ..
      result_text(result)
  else
    failures[#failures + 1] = "base path switch: " .. tostring(switched)
  end
  return false, table.concat(failures, "; ")
end

local function run_probe(engine)
  local result, bank_id = engine.LoadBank(PROBE_BANK, -1)
  local code = result_code(result)
  local line = string.format("voice: probe LoadBank(%s) = %s, bank id %s", PROBE_BANK, result_text(result),
    tostring(bank_id))
  if code == AK_SUCCESS then
    local pointer = cs_type("System.IntPtr")
    local unloaded = engine.UnloadBank(PROBE_BANK, pointer and pointer.Zero or 0)
    log(line .. "; UnloadBank = " .. result_text(unloaded) .. ": Wwise reads banks from the package")
  elseif code == AK_FILE_NOT_FOUND then
    log(line .. ": Wwise does not look into the package")
  else
    log(line)
  end
  return code
end

------------------------------------------------------------------------------------------------------------------
-- 语音：构建、缓存与报告
------------------------------------------------------------------------------------------------------------------

-- 需要的库及其中启用的事件。舰娘库整库改名；共享库（宿舍、情书）库 ID 不变，只互换中日两组事件 ID。
local function voice_plan()
  local plan = {}
  for _, bank in ipairs(DATA.voice.banks) do
    local switch = SHARED_BANK_SWITCHES[bank.target]
    if switch == nil or voice_option(switch) then
      local events = {}
      for _, event in ipairs(bank.events) do
        if feature_on(event.feature) then
          events[#events + 1] = event
        end
      end
      if #events > 0 then
        plan[#plan + 1] = {bank = bank, events = events, shared = bank.source_bank_id == bank.target_bank_id}
      end
    end
  end
  return plan
end

local function bank_spec(entry, events)
  local renames = {}
  local duplicate = nil
  local function rename(from, to)
    if renames[from] ~= nil then
      duplicate = duplicate or from
    end
    renames[from] = to
  end
  for _, event in ipairs(events) do
    rename(event.from, event.to)
    if event.swap then
      rename(event.to, event.from)
    end
  end
  return {
    renames = renames, duplicate = duplicate, source_bank_id = entry.bank.source_bank_id,
    target_bank_id = entry.bank.target_bank_id, play_actions = entry.bank.play_actions,
  }
end

local function plan_digest(entry)
  local bank = entry.bank
  local parts = {bank.source, bank.target, tostring(bank.source_bank_id), tostring(bank.target_bank_id),
    tostring(bank.play_actions)}
  for _, event in ipairs(entry.events) do
    parts[#parts + 1] = string.format("%s%s%s", tostring(event.from), event.swap and "<>" or ">",
      tostring(event.to))
  end
  return fnv1_32(table.concat(parts, " "))
end

-- 缓存戳：格式、data.lua 版本、每个库的源文件名与实际大小、改写内容的摘要，之后是实际打进包的库（及其中包含的
-- 功能）和包的大小。
local function cache_key(plan)
  local lines = {CACHE_FORMAT, "data " .. tostring(DATA.version)}
  for _, entry in ipairs(plan) do
    lines[#lines + 1] = string.format("bank %s %s -> %s %08x", entry.bank.source, tostring(entry.size),
      entry.bank.target, plan_digest(entry))
  end
  return table.concat(lines, "\n") .. "\n"
end

local function file_length(path)
  local file = cs_type("System.IO.File")
  if not file.Exists(path) then
    return nil
  end
  local info = cs_type("System.IO.FileInfo")
  return tonumber(info(path).Length)
end

local function reuse_cache(key, package_path, stamp_path)
  local file = cs_type("System.IO.File")
  if not file.Exists(stamp_path) then
    return nil
  end
  local stamp = file.ReadAllText(stamp_path)
  if type(stamp) ~= "string" or string.sub(stamp, 1, #key) ~= key then
    return nil
  end
  local listed, size = string.match(string.sub(stamp, #key + 1), "^packaged ([^\n]*)\nsize (%d+)\n$")
  if listed == nil or file_length(package_path) ~= tonumber(size) then
    return nil
  end
  local packaged = {}
  for item in string.gmatch(listed, "[^;]+") do
    local target, features = string.match(item, "^([^=]+)=(.*)$")
    if target == nil then
      return nil
    end
    packaged[target] = {}
    for feature in string.gmatch(features, "[^+]+") do
      packaged[target][feature] = true
    end
  end
  return packaged, tonumber(size)
end

-- 先改舰娘库；某艘舰娘的库缺失或没通过检查时（failed），共享库里也不再互换她的事件，免得只有宿舍、情书是中文。
local function build_cache(plan, key, cache_dir, package_path, stamp_path, failed)
  local file = cs_type("System.IO.File")
  local files = {{name = PROBE_BANK, data = probe_bank()}}
  local packaged = {}
  local listed = {}
  for _, shared_pass in ipairs({false, true}) do
    for _, entry in ipairs(plan) do
      if entry.shared == shared_pass then
        local events = {}
        for _, event in ipairs(entry.events) do
          if not failed[event.feature] then
            events[#events + 1] = event
          end
        end
        local ok, result = false, "the ships' own banks were skipped"
        if #events > 0 then
          -- 读文件也放在 pcall 里：一个库读不出来只跳过这个库。
          ok, result = pcall(function()
            return patch_bank(file.ReadAllBytes(entry.source_path), bank_spec(entry, events))
          end)
        end
        if ok then
          files[#files + 1] = {name = entry.bank.target, data = result}
          local features = {}
          for _, event in ipairs(events) do
            features[event.feature] = true
          end
          packaged[entry.bank.target] = features
          listed[#listed + 1] = entry.bank.target .. "=" .. table.concat(sorted_keys(features), "+")
        else
          log(string.format("voice: skip bank %s -> %s: %s", entry.bank.source, entry.bank.target, tostring(result)))
          if not entry.shared then
            for _, event in ipairs(entry.events) do
              failed[event.feature] = true
            end
          end
        end
      end
    end
  end
  if #listed == 0 then
    return nil
  end
  local package = build_package(files)
  cs_type("System.IO.Directory").CreateDirectory(cache_dir)
  if file.Exists(stamp_path) then
    file.Delete(stamp_path)
  end
  local temporary = package_path .. ".tmp"
  file.WriteAllBytes(temporary, package)
  if file.Exists(package_path) then
    file.Delete(package_path)
  end
  file.Move(temporary, package_path)
  file.WriteAllText(stamp_path, key .. "packaged " .. table.concat(listed, ";") .. "\nsize " .. #package .. "\n")
  log(string.format("voice: package built: %s (%d bytes; %s)", package_path, #package, table.concat(listed, ", ")))
  return packaged
end

local function voice_features()
  local features = {}
  local seen = {}
  for _, bank in ipairs(DATA.voice.banks) do
    for _, event in ipairs(bank.events) do
      if not seen[event.feature] then
        seen[event.feature] = true
        features[#features + 1] = event.feature
      end
    end
  end
  return features
end

-- 每个语音功能一行：包里有哪些库、改了多少事件，或者为什么没有生效。
local function report_voice(plan, packaged, failure)
  for _, feature in ipairs(voice_features()) do
    if feature_on(feature) then
      local parts = {}
      if failure == nil then
        for _, entry in ipairs(plan) do
          local included = packaged[entry.bank.target]
          if included ~= nil and included[feature] then
            local count, swaps = 0, 0
            for _, event in ipairs(entry.events) do
              if event.feature == feature then
                if event.swap then
                  swaps = swaps + 1
                else
                  count = count + 1
                end
              end
            end
            if count > 0 then
              parts[#parts + 1] = string.format("%s (%d events)", entry.bank.target, count)
              if not entry.shared then
                voice_active[feature] = true
              end
            elseif swaps > 0 then
              parts[#parts + 1] = string.format("%s (%d swapped)", entry.bank.target, swaps)
            end
          end
        end
      end
      if #parts > 0 then
        log(feature .. ": Chinese voice served from the package: " .. table.concat(parts, ", "))
      else
        log(feature .. ": Chinese voice is NOT active: " .. tostring(failure or "none of its banks was packaged"))
      end
    end
  end
end

local function setup_voice()
  if voice_done or DATA == nil then
    return
  end
  if not enabled() then
    voice_done = true
    return
  end
  local plan = voice_plan()
  if #plan == 0 then
    voice_done = true
    return
  end
  local engine = cs_type("AkSoundEngine")
  local file = cs_type("System.IO.File")
  if engine == nil or file == nil or mod_dir == nil then
    voice_done = true
    report_voice(plan, {}, "AkSoundEngine or System.IO is unavailable through CS")
    return
  end
  local initialized_ok, initialized = pcall(function()
    return engine.IsInitialized()
  end)
  if not initialized_ok then
    voice_done = true
    report_voice(plan, {}, "AkSoundEngine.IsInitialized raised an error: " .. tostring(initialized))
    return
  end
  if not initialized then
    log("voice: Wwise is not initialized yet; retrying at the next SoundHelper.LoadSFX")
    return
  end
  voice_done = true

  local base = sound_bank_base_path()
  if base == nil then
    report_voice(plan, {}, "the sound bank folder is unknown")
    return
  end
  local cache_dir = join_path(mod_dir, "cache")
  local package_path = cache_dir .. "\\" .. PACKAGE_FILE
  local stamp_path = cache_dir .. "\\" .. STAMP_FILE

  local usable = {}
  local failed = {}
  for _, entry in ipairs(plan) do
    entry.source_path = base .. entry.bank.source .. ".bnk"
    local measured, size = pcall(file_length, entry.source_path)
    entry.size = measured and size or nil
    local problem = nil
    if not measured then
      problem = "cannot be read: " .. tostring(size)
    elseif entry.size == nil then
      problem = "not found at " .. entry.source_path
    elseif entry.size ~= entry.bank.source_size then
      problem = string.format("%d bytes, expected %d", entry.size, entry.bank.source_size)
    end
    if problem == nil then
      usable[#usable + 1] = entry
    else
      log("voice: skip bank " .. entry.bank.source .. ": " .. problem)
      if not entry.shared then
        for _, event in ipairs(entry.events) do
          failed[event.feature] = true
        end
      end
    end
  end
  if #usable == 0 then
    report_voice(plan, {}, "no source bank matched")
    return
  end
  plan = usable

  -- 文件包挂载期间文件一直打开着：先卸下本进程里可能还挂着的旧包（没有时返回 AK_Fail），再改写文件。
  local names = {PACKAGE_FILE}
  local relative = relative_path(base, package_path)
  if relative ~= nil then
    table.insert(names, 1, relative)
  end
  for _, name in ipairs(names) do
    local ok, released = pcall(function()
      return engine.UnloadFilePackage(package_id_of(engine, name))
    end)
    if ok and result_code(released) == AK_SUCCESS then
      log("voice: released a package mounted earlier: " .. name)
    end
  end

  local key = cache_key(plan)
  local checked, packaged, size = pcall(reuse_cache, key, package_path, stamp_path)
  if not checked then
    log("voice: the package cache cannot be checked, rebuilding it: " .. tostring(packaged))
    packaged = nil
  end
  if packaged ~= nil then
    log(string.format("voice: package cache reused: %s (%d bytes)", package_path, size))
  else
    local built, result = pcall(build_cache, plan, key, cache_dir, package_path, stamp_path, failed)
    if not built then
      report_voice(plan, {}, "the package could not be built or written: " .. tostring(result))
      return
    end
    packaged = result
    if packaged == nil then
      report_voice(plan, {}, "every source bank failed its checks")
      return
    end
  end

  local mounted, detail = mount_package(engine, base, cache_dir)
  if not mounted then
    log("voice: LoadFilePackage failed: " .. detail)
    report_voice(plan, packaged, "the package could not be mounted")
    return
  end
  log("voice: package mounted via " .. detail)
  if voice_option("probe") then
    -- 探测库都取不到时，舰娘库同样不会从包里来；探测本身出错（比如反射调用不成）时结论未知，照常报告。
    local ok, code = safely("voice probe", run_probe, engine)
    if ok and code ~= AK_SUCCESS then
      report_voice(plan, packaged, "the probe bank was not served from the package")
      return
    end
  end
  report_voice(plan, packaged)
end

------------------------------------------------------------------------------------------------------------------
-- 字幕：C# 字幕缓存（尽力而为）
------------------------------------------------------------------------------------------------------------------

-- C# 自己读 config_caption_access_list：Dict.Blo.DictCaptionAccessListBlo.GetCaptionAccessList(key) 调
-- DictDataManager.Instance.dictCaptionAccessListDao 的 GetById。Dao（LuaDaoBase`1）把每个键的行数组以 WeakReference
-- 存在私有字典 CachePrimary 里：GetById 命中时返回数组的 [0]，未命中时按 id 读一行并缓存 T[1]。
-- 所以先经 Blo 取行（缓存里就有了这个键），再取出缓存的 T[1] 在 Lua 里持有，数组不会被回收，之后 GetById 一直返回
-- 改过的那一行。数组优先用反射从 CachePrimary 读；反射不可用时用 GetGroupById，它与 GetById 共用这个字典，命中时
-- 返回缓存的数组。GetGroupById 未命中时却是按 indexid 列查（字幕行的 indexid 都是空的），会缓存一个空数组，之后
-- GetById 对这个键抛 IndexOutOfRangeException：绝不能在 Blo 之前调用它，拿到的数组也要核对。
-- 两样都取不到时只改行本身（GC 后行对象可能被重新读出，改动随之失效，下次 LoadSFX 再改）。
-- 每次 LoadSFX 都重新检查一遍（重新登录时缓存可能被清掉），只在有改动时写日志。
local caption_holds = {}
local caption_reported = false
local cache_field = nil  -- LuaDaoBase`1.CachePrimary 的 FieldInfo；反射不可用时为 false

local function wanted_caption_keys()
  local keys = {}
  for _, key in ipairs(sorted_keys(DATA.voice.captions)) do
    if voice_active[DATA.voice.captions[key].feature] then
      keys[#keys + 1] = key
    end
  end
  return keys
end

local function same_object(a, b)
  local object = cs_type("System.Object")
  if object ~= nil then
    local ok, same = pcall(function()
      return object.ReferenceEquals(a, b)
    end)
    if ok then
      return same == true
    end
  end
  return rawequal(a, b)
end

-- BindingFlags.Instance | BindingFlags.NonPublic = 36。枚举参数传数字还是枚举对象，依 xLua 的转换规则而定，依次尝试。
local function private_field(type_object, name)
  local flags = {36}
  local binding_flags = cs_type("System.Reflection.BindingFlags")
  if binding_flags ~= nil then
    local ok, value = pcall(function()
      return binding_flags.__CastFrom(36)
    end)
    if ok and value ~= nil then
      flags[#flags + 1] = value
    end
  end
  for _, flag in ipairs(flags) do
    local ok, field = pcall(function()
      return type_object:GetField(name, flag)
    end)
    if ok and field ~= nil then
      return field
    end
  end
  return nil
end

-- 私有字段只在声明它的类上找得到，沿基类链向上找。
local function find_cache_field(dao)
  local ok, current = pcall(function()
    return dao:GetType()
  end)
  while ok and current ~= nil do
    local field = private_field(current, "CachePrimary")
    if field ~= nil then
      return field
    end
    ok, current = pcall(function()
      return current.BaseType
    end)
  end
  return nil
end

-- CachePrimary 里这个键的 WeakReference 目标；没有这个键或目标已被回收时为 nil。
local function cached_array(dao, key)
  local found, reference = cache_field:GetValue(dao):TryGetValue(key)
  if not found or reference == nil then
    return nil
  end
  return reference.Target
end

local function holds_row(array, row)
  local ok, held = pcall(function()
    return array.Length >= 1 and same_object(array[0], row)
  end)
  return ok and held == true
end

local function lookup_caption(blo, key)
  local ok, row = pcall(function()
    return blo.GetCaptionAccessList(key)
  end)
  if ok then
    return row
  end
  return nil
end

-- 只在 Blo 刚把这个键放进缓存之后调用，这时 GetGroupById 命中并返回缓存的 T[1]。两次调用之间缓存若被 GC 清掉，
-- 它会缓存并返回空数组：这时不返回它（第二个返回值为 true），由调用方放开引用让它被回收。
local function group_array(dao, key, row)
  local ok, group = pcall(function()
    return dao:GetGroupById(key, true)
  end)
  if not ok or group == nil then
    return nil, false
  end
  if holds_row(group, row) then
    return group, false
  end
  local measured, empty = pcall(function()
    return group.Length == 0
  end)
  return nil, measured and empty == true
end

-- 返回：行、要持有的对象、数组的来源（"CachePrimary" / "GetGroupById"，只持有行时为 nil）、是否碰到了空数组。
local function caption_row(dao, blo, key)
  local row = lookup_caption(blo, key)
  if row == nil or dao == nil then
    return row, row, nil, false
  end
  if cache_field then
    for _ = 1, 2 do
      local ok, array = pcall(cached_array, dao, key)
      if not ok then
        cache_field = false
        log("captions: the Dao's CachePrimary cannot be read by reflection (" .. tostring(array) ..
          "); using GetGroupById")
        break
      end
      if array ~= nil and holds_row(array, row) then
        return row, array, "CachePrimary", false
      end
      -- 两次调用之间数组被回收了：再经 Blo 读一次，重新放进缓存。
      row = lookup_caption(blo, key)
      if row == nil then
        return nil
      end
    end
    if cache_field then
      return row, row, nil, false
    end
  end
  local group, emptied = group_array(dao, key, row)
  if group ~= nil then
    return row, group, "GetGroupById", false
  end
  return row, row, nil, emptied
end

-- GetGroupById 缓存了空数组时：Lua 侧不再引用它（xLua 随 userdata 回收放开 C# 对象），C# 再回收一次，WeakReference
-- 就失效，GetById 重新按 id 读行。返回重新读出的行，只改它本身，不持有数组。
local function release_empty_array(blo, key)
  collectgarbage("collect")
  local gc = cs_type("System.GC")
  if gc ~= nil then
    pcall(function()
      gc.Collect()
    end)
  end
  return lookup_caption(blo, key)
end

local function apply_captions()
  if DATA == nil or not voice_option("captions") or not enabled() then
    return
  end
  local keys = wanted_caption_keys()
  if #keys == 0 then
    if voice_done and not caption_reported then
      caption_reported = true
      log("captions: left Japanese because the Chinese voice is not active")
    end
    return
  end
  local blo = cs_type("Dict.Blo.DictCaptionAccessListBlo")
  if blo == nil then
    if not caption_reported then
      caption_reported = true
      log("captions: Dict.Blo.DictCaptionAccessListBlo is unavailable; captions stay Japanese")
    end
    return
  end
  local dao = nil
  local manager = cs_type("Dict.DictDataManager")
  if manager ~= nil then
    local ok, value = pcall(function()
      return manager.Instance.dictCaptionAccessListDao
    end)
    if ok then
      dao = value
    end
  end
  if dao ~= nil and cache_field == nil then
    cache_field = find_cache_field(dao) or false
    if not cache_field then
      log("captions: the Dao's CachePrimary field is not reachable by reflection; using GetGroupById")
    end
  end

  -- 有几行日服文本本来就与国服相同（如 "<%%0><无>"），不算改动。
  local changed, unchanged, missing, held_arrays = 0, 0, 0, 0
  local first_key, first_row = nil, nil
  for _, key in ipairs(keys) do
    local caption = DATA.voice.captions[key]
    local row, holder, source, emptied = caption_row(dao, blo, key)
    if emptied then
      row = release_empty_array(blo, key)
      holder = row
      log(string.format("captions: %s: GetGroupById cached an empty array (the cached row was collected between " ..
        "two lookups); released it, the row reads again: %s", key, row ~= nil and "yes" or "no"))
    end
    if row == nil then
      missing = missing + 1
    else
      caption_holds[key] = holder
      if source ~= nil then
        held_arrays = held_arrays + 1
      end
      local ok, current = pcall(function()
        return row.cn_text
      end)
      if ok and current == caption.cn_text then
        unchanged = unchanged + 1
      else
        local written = pcall(function()
          row.cn_text = caption.cn_text
          row.caption_open = caption.caption_open
          row.caption_end = caption.caption_end
        end)
        if written then
          changed = changed + 1
          if first_key == nil then
            first_key, first_row = key, row
          end
        else
          missing = missing + 1
        end
      end
    end
  end

  if not caption_reported then
    caption_reported = true
    local route = dao == nil and "no Dao" or (cache_field and "CachePrimary" or "GetGroupById")
    local line = string.format("captions: %d/%d C# caption rows carry the Chinese text (%d written, %d already " ..
      "equal; %d held as Dao arrays via %s), %d unavailable", changed + unchanged, #keys, changed, unchanged,
      held_arrays, route, missing)
    if first_key ~= nil then
      local ok, again = pcall(function()
        return blo.GetCaptionAccessList(first_key)
      end)
      local same = ok and again ~= nil and same_object(again, first_row)
      local text_ok, text = pcall(function()
        return again.cn_text
      end)
      local sticks = ok and text_ok and text == DATA.voice.captions[first_key].cn_text
      line = line .. string.format("; re-lookup of %s returns the same object: %s, the new text reads back: %s",
        first_key, same and "yes" or "no", sticks and "yes" or "no")
    end
    log(line)
  elseif changed > 0 then
    log(string.format("captions: %d rows had reverted to Japanese and were set again", changed))
  end
end

------------------------------------------------------------------------------------------------------------------
-- SoundHelper.LoadSFX 钩子
------------------------------------------------------------------------------------------------------------------

local load_sfx_wrapped = false

local function wrap_load_sfx(previous)
  return function(...)
    safely("voice setup", setup_voice)
    safely("wedding text", apply_deferred_wedding)
    safely("caption patch", apply_captions)
    return previous(...)
  end
end

-- util.SoundHelper 先执行 SoundHelper = {}，之后才逐个赋函数，所以等 LoadSFX 这个键写入时再包装。
local function hook_sound_helper(helper)
  if load_sfx_wrapped or type(helper) ~= "table" then
    return
  end
  local current = rawget(helper, "LoadSFX")
  if type(current) == "function" then
    rawset(helper, "LoadSFX", wrap_load_sfx(current))
    load_sfx_wrapped = true
    log("SoundHelper.LoadSFX wrapped")
    return
  end
  local meta = getmetatable(helper) or {}
  local previous_newindex = meta.__newindex
  meta.__newindex = function(target, key, value)
    assign_with_previous(previous_newindex, target, key, value)
    local assigned = rawget(target, key)
    if key == "LoadSFX" and not load_sfx_wrapped and type(assigned) == "function" then
      rawset(target, key, wrap_load_sfx(assigned))
      load_sfx_wrapped = true
      log("SoundHelper.LoadSFX wrapped")
    end
  end
  setmetatable(helper, meta)
end

------------------------------------------------------------------------------------------------------------------
-- 入口
------------------------------------------------------------------------------------------------------------------

local function count_keys(map)
  local count = 0
  for _ in pairs(map) do
    count = count + 1
  end
  return count
end

local function install()
  if DATA == nil then
    log("data.lua is unavailable, nothing is installed: " .. tostring(data_error))
    return
  end
  local on, off = {}, {}
  for _, feature in ipairs({"fubuki_spring_fashion", "oakland_spring_touch", "spring_festival_story",
      "character_stories", "cn_voice_ninghai", "cn_voice_pinghai"}) do
    if feature_on(feature) then
      on[#on + 1] = feature
    else
      off[#off + 1] = feature
    end
  end
  log(string.format("data v%s: %d patches, %d inserts, %d banks, %d captions; on: %s; off: %s",
    tostring(DATA.version), #DATA.patches, #DATA.inserts, #DATA.voice.banks, count_keys(DATA.voice.captions),
    #on > 0 and table.concat(on, ", ") or "none", #off > 0 and table.concat(off, ", ") or "none"))

  if next(config_tables) ~= nil then
    mod.watch_global("configManager", patch_config_manager)
  end
  if next(memory_chapters) ~= nil or #hero_memories > 0 then
    watch_class_modules()
  end
  if feature_on("cn_voice_ninghai") or feature_on("cn_voice_pinghai") then
    mod.watch_global("SoundHelper", hook_sound_helper)
  end
end

function on_bootstrap()
  -- bootstrap.lua 不保护 on_bootstrap；这里出错会让整个 bootstrap 失败后重跑所有 Mod。
  safely("install", install)
end
