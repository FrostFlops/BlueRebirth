-- 「许愿墙」作弊（启动器作弊选项 / 服务端 --cheat-vow）的界面配套。
--
-- 日服祈愿页在每次祈愿结束后（LuaEvent.ShowGirlEnd，或碎片奖励页关闭时）调用
-- WishPage:_ShowChargeTip，打开 WishCdPage{type="result"} 显示「下次可祈愿时间」。这个时间完全由
-- 客户端公式 GetFinalChargeTime 现算，不读服务端下发的冷却。作弊开启时服务端把冷却写成 0，
-- 并在应答后补发一条 illustrate.IllustrateInfo 把客户端自己设的冷却覆盖回 0，祈愿按钮已经可用，
-- 但这个结果页仍会显示一段并不存在的冷却。
--
-- 修法：打开结果页前先看 Logic.wishLogic:CheckCharge()；服务端说没有冷却时不弹。未开作弊时
-- 冷却照常存在，CheckCharge 为 true，行为与原版完全相同。判断出错时退回原版。
local TARGET = "wishpage"

local patched_classes = setmetatable({}, {__mode = "k"})

local function safely(label, action, value)
  local ok, failure = xpcall(function()
    action(value)
  end, debug.traceback)
  if not ok then
    mod.info(label .. " failed: " .. tostring(failure))
  end
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

-- 游戏重写的 require 会把类路径转成小写点分路径（ui.page.illustrate.wishpage），这里按末段精确匹配，
-- 避免误伤其它以 wishpage 结尾的模块名。
local function is_target(key)
  if type(key) ~= "string" then
    return false
  end
  local path = string.lower(key)
  if not string.find(path, TARGET, 1, true) then
    return false
  end
  path = (string.gsub(path, "[/\\]", "."))
  path = (string.gsub(path, "%.lua$", ""))
  return path == TARGET or string.sub(path, -(#TARGET + 1)) == "." .. TARGET
end

local function patch_page(page_class)
  if type(page_class) ~= "table" or patched_classes[page_class] then
    return
  end
  local original_show = page_class._ShowChargeTip
  if type(original_show) ~= "function" then
    error("WishPage._ShowChargeTip is unavailable")
  end

  -- middleclass 的类表赋值会同步到 __instanceDict；页面打开时 RegisterAllEvent 取到的就是新方法。
  page_class._ShowChargeTip = function(self, ...)
    local ok, charging = xpcall(function()
      return Logic.wishLogic:CheckCharge()
    end, debug.traceback)
    if ok and charging == false then
      mod.info("wish cooldown result page skipped: the server reports no cooldown")
      return
    end
    if not ok then
      mod.info("CheckCharge failed; showing the original page: " .. tostring(charging))
    end
    return original_show(self, ...)
  end

  patched_classes[page_class] = true
  mod.info("WishPage cooldown tip patched")
end

local function watch_package_loaded()
  local loaded = package.loaded
  local found = false
  for key, value in pairs(loaded) do
    if is_target(key) then
      found = true
      safely("WishPage hook", patch_page, value)
    end
  end

  -- 页面模块通常在 bootstrap 之后才第一次 require；先按原链路写入，修补失败只记日志，绝不向 require 抛错。
  local meta = getmetatable(loaded) or {}
  local previous_newindex = meta.__newindex
  meta.__newindex = function(target, key, value)
    assign_with_previous(previous_newindex, target, key, value)
    if is_target(key) then
      safely("WishPage hook", patch_page, value)
    end
  end
  setmetatable(loaded, meta)
  if not found then
    mod.info("waiting for WishPage")
  end
end

function on_bootstrap()
  safely("package.loaded watcher", watch_package_loaded)
end
