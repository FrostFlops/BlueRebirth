#!/usr/bin/env bash
# 构建「安卓端启动器」App（独立服务端；包名 com.blueoath.server，桌面名 BlueRebirthApp）
#
# 用法:
#   android/tools/build-server-app.sh [额外的 dotnet build 参数]
#
# 历史踩坑（详见 .workbuddy/memory/）：
#   1) aapt2 读不了含非 ASCII 字符的路径（-A assets 失败 → APT2265）。
#      工程路径含中文时，本脚本会自动建一个 ASCII 目录联接（junction）再从中构建。
#   2) 必须 clean build。增量重建会产出与程序集不一致的 typemap，安装后抛
#      `UnsatisfiedLinkError: No implementation found for ... n_onCreate()`。
#      脚本先把 obj/bin 挪到临时目录再构建（不用 rm -rf：某些环境安全守卫会拦批量删除）。
#   3) ABI 不要锁定 armeabi-v7a：x86 模拟器的 ARM 转译层下 .NET 运行时会崩溃。
#      保持默认（arm64-v8a + x86_64）。
set -u
export MSYS_NO_PATHCONV=1

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
# 在 Git Bash 下用 `pwd -W` 拿 Windows 风格路径（E:/...），否则是 /e/... 让 PowerShell 认不出。
ROOT="$(cd "$SCRIPT_DIR/../.." && { pwd -W 2>/dev/null || pwd; })"

# 路径含非 ASCII 时，建一个 ASCII 联接用于构建。
ASCII_ROOT="$ROOT"
if ! printf '%s' "$ROOT" | LC_ALL=C grep -q '^[ -~]*$'; then
  DRIVE="$(printf '%s' "$ROOT" | cut -d/ -f1)"
  LINK="$DRIVE/boj"
  if [ ! -f "$LINK/src/BlueOath.Android/BlueOath.Android.csproj" ]; then
    echo "== 创建 ASCII 目录联接 $LINK -> $ROOT =="
    powershell -NoProfile -Command \
      "New-Item -ItemType Junction -Path '$LINK' -Target '$ROOT' -ErrorAction Stop | Out-Null" >/dev/null || {
        echo "创建联接失败，请手动执行：" >&2
        echo "  powershell -c \"New-Item -ItemType Junction -Path $LINK -Target '$ROOT'\"" >&2
        exit 1
      }
  fi
  ASCII_ROOT="$LINK"
fi

PROJ="$ASCII_ROOT/src/BlueOath.Android/BlueOath.Android.csproj"
PRJDIR="$(dirname "$PROJ")"

if [ ! -f "$PROJ" ]; then
  echo "找不到工程：$PROJ" >&2
  exit 1
fi

STALE="${TMPDIR:-/tmp}/bo-stale/$(date +%s)"
mkdir -p "$STALE"

# 必须 clean build：把本项目**以及所有被引用工程**的 obj/bin 挪走。
# 只挪本项目时，引用工程的 obj/*.dll 常被残留的 csc / 杀软（火绒）占用，
# 会报 CS2012「文件正在被另一进程使用」。
move_aside() {
  local dir="$1" label="$2"
  [ -d "$dir" ] || return 0
  mkdir -p "$STALE/$label"
  mv "$dir" "$STALE/$label/" 2>/dev/null && echo "moved $dir -> $STALE/$label/"
}

move_aside "$PRJDIR/obj" "$(basename "$PRJDIR")-obj"
move_aside "$PRJDIR/bin" "$(basename "$PRJDIR")-bin"

# 引用工程（Protocol / Core / Storage / Server 等）
for proj in "$ASCII_ROOT"/src/*/; do
  name="$(basename "$proj")"
  [ "$proj" = "$PRJDIR/" ] && continue
  [ -f "$proj"*.csproj ] || continue
  move_aside "${proj%/}/obj" "$name-obj"
  move_aside "${proj%/}/bin" "$name-bin"
done

# 构建。★ 本机杀软（火绒）会实时扫描新写入的 DLL，偶发 CS2012「文件被占用/拒绝访问」，
#   属于瞬时锁；这里对 CS2012 自动重试若干次，避免每次都要人工重跑。
LOG="$STALE/build.log"
attempt=1
max_attempts=4
while :; do
  echo "== 构建尝试 $attempt/$max_attempts =="
  MSBUILDDISABLENODEREUSE=1 dotnet build "$PROJ" -c Release -p:UseSharedCompilation=false "$@" 2>&1 | tee "$LOG"
  rc=${PIPESTATUS[0]}
  [ $rc -eq 0 ] && break

  if grep -q "CS2012\|MSB3026\|MSB3030" "$LOG" && [ $attempt -lt $max_attempts ]; then
    echo "== 命中文件锁（CS2012/MSB3026），5 秒后重试 =="
    dotnet build-server shutdown >/dev/null 2>&1 || true
    sleep 5
    attempt=$((attempt + 1))
    continue
  fi

  echo "构建失败 (rc=$rc)，完整日志：$LOG" >&2
  exit $rc
done

APK="$ROOT/src/BlueOath.Android/bin/Release/net10.0-android/com.blueoath.server-Signed.apk"
echo ""
echo "== 产物 =="
ls -la "$APK" 2>/dev/null || echo "未找到 APK：$APK"
cat <<'TIP'

== 安装 / 测试 ==
  adb install -r -t "<上面的 APK>"
  adb shell monkey -p com.blueoath.server -c android.intent.category.LAUNCHER 1

★ 若设备上有占用 19090/19191/19192/19193 的 adb reverse 保活脚本，先停掉，
  否则 App 会报 "Address already in use"。
★ 设备全局 HTTP 代理必须清空，否则客户端连 127.0.0.1 也会走代理：
    adb shell settings put global http_proxy :0
TIP
