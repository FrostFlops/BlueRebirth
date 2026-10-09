-- 修复基建「生产加速」页（ProductionSpeedUpPage）在加速量足以完成队列、或剩余时间很短时
-- 点「确认」没有任何反应的问题。
--
-- 原版 OnBtnConfirm：所选体力（货币 21，メカニカルメダル）折算的加速时间不超过剩余时间时，
-- 直接调用 DoSpeedup 发送 building.UseStrengthSpeedup；超过剩余时间时只弹出 3200004
-- 「加速时间将被浪费」的二次确认框，请求只在该框的确认回调里发送。这个框由
-- noticeManager:ShowMsgBox 以默认的 UILayer.MAIN 层创建，在本环境里从未产生过请求（推测是
-- 被加速页挡住或未被 C# 侧 UIBoxManager 创建，未实证；同一会话中 UILayer.ATTENTION 层的
-- 确认框工作正常）。而 GetMax 向上取整，所以点「最大」、任何能完成队列的数量，以及剩余不足
-- 一枚体力（配方 2 约 288 秒）时的加速都必然走进这个分支。
--
-- 修法：确认时把数量压到恰好完成队列所需的最小值，然后直接调用 DoSpeedup，绕过这个确认框。
-- 体力照常由服务端扣除（启动器开启「体力」作弊即 --cheat-strength 时才不扣）。原确认框只是
-- 提醒浪费，压缩后多出的加速时间不足一枚体力（另含 2 秒余量），因此不再询问。数量只会往下压：
-- 所选数量不足以完成队列时与原版一样直接部分加速。
local TARGET = "productionspeeduppage"
local TIP_PRODUCE_FINISHED = 3200005
-- ProduceItem 的剩余时间按秒向下取整，客户端估算的服务器时间与服务端结算时刻也可能差一秒
-- 左右；多算 2 秒，避免压缩后差一点完不成队列。
local SAFETY_SECONDS = 2

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

-- 游戏重写的 require 会把 lua_class_path 转成小写点分路径，键为
-- ui.page.building.building2d.productionspeeduppage；这里同样规范化后按末段匹配。
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

-- 与原页面 GetMax/DoCountDown 相同的公式；只读计算，出错时回退到原版确认。
local function plan_speedup(page)
  local remain_time = Logic.buildingLogic:ProduceItem(page.buildingData)
  if type(remain_time) ~= "number" then
    error("ProduceItem returned " .. tostring(remain_time))
  end
  if remain_time <= 0 then
    return remain_time, nil
  end
  local time_per_strength = page.recipeCfg.time / page:GetEnergyCost()
  local need = math.max(1, math.ceil((remain_time + SAFETY_SECONDS) / time_per_strength))
  return remain_time, need
end

local function patch_page(page_class)
  if type(page_class) ~= "table" or patched_classes[page_class] then
    return
  end
  local original_confirm = page_class.OnBtnConfirm
  if type(original_confirm) ~= "function" or
      type(page_class.DoSpeedup) ~= "function" or
      type(page_class.GetEnergyCost) ~= "function" or
      type(page_class.StopCountDownTimer) ~= "function" or
      type(page_class.OnClose) ~= "function" then
    error("ProductionSpeedUpPage API is unavailable")
  end

  -- middleclass 的类表赋值会同步到 __instanceDict；页面每次打开都新建实例，
  -- RegisterAllEvent 绑定按钮时取到的就是这里的新方法。
  page_class.OnBtnConfirm = function(self, ...)
    local ok, remain_time, need = xpcall(plan_speedup, debug.traceback, self)
    if not ok then
      mod.info("speedup plan failed; using the original confirm: " .. tostring(remain_time))
      return original_confirm(self, ...)
    end
    if remain_time <= 0 then
      -- 与原页面 DoCountDown 一致：生产已结束时提示，停止倒计时并关闭加速页。
      noticeManager:ShowTip(UIHelper.GetString(TIP_PRODUCE_FINISHED))
      self:StopCountDownTimer()
      self:OnClose()
      return
    end
    local before = self.costStrength
    if type(before) == "number" and before > need then
      self.costStrength = need
    end
    mod.info("speedup building=" .. tostring(self.buildingData and self.buildingData.Id) ..
      " remain=" .. tostring(remain_time) ..
      " cost=" .. tostring(before) .. "->" .. tostring(self.costStrength) ..
      " need=" .. tostring(need))
    self:DoSpeedup()
  end

  patched_classes[page_class] = true
  mod.info("ProductionSpeedUpPage confirm patched")
end

local function watch_package_loaded()
  local loaded = package.loaded
  local found = false
  for key, value in pairs(loaded) do
    if is_target(key) then
      found = true
      safely("ProductionSpeedUpPage hook", patch_page, value)
    end
  end

  -- 页面模块通常在 bootstrap 之后才第一次 require。__newindex 只在新键写入时触发，
  -- 先按原链路写入（无上游时 rawset），require 回读 package.loaded 不受影响；修补失败
  -- 只记日志，绝不向 require 抛错。监听常驻，模块被重新加载时同样修补新类表。
  local meta = getmetatable(loaded) or {}
  local previous_newindex = meta.__newindex
  meta.__newindex = function(target, key, value)
    assign_with_previous(previous_newindex, target, key, value)
    if is_target(key) then
      safely("ProductionSpeedUpPage hook", patch_page, value)
    end
  end
  setmetatable(loaded, meta)
  if not found then
    mod.info("waiting for ProductionSpeedUpPage")
  end
end

function on_bootstrap()
  -- bootstrap.lua 不保护 on_bootstrap；这里出错会让整个 bootstrap 失败后重跑所有 mod。
  safely("package.loaded watcher", watch_package_loaded)
end
