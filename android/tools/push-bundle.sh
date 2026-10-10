#!/usr/bin/env bash
# 把热更 bundle（默认 android/_work2/merged，~3.5GB）推送到设备上的目标目录。
#
# 用法:
#   android/tools/push-bundle.sh [目标目录]
#   默认目标：/sdcard/Android/data/com.blueoath.server/files/bundle   （独立服务端 App）
#   推给游戏客户端时传：/sdcard/Android/data/com.zephyrus.clsy.gp/files/bundles
#
# 环境变量：
#   ADB      adb 可执行文件（默认 PATH 里的 adb；BlueStacks 自带可设
#            "C:/Program Files/BlueStacks_nxt_cn/HD-Adb.exe"）
#   SERIAL   设备序列号（默认空，使用 adb 默认设备）
#   MERGED   源目录（默认 <repo>/android/_work2/merged）
#   SKIP_ENTRIES  空格分隔的顶层条目名，跳过不推（例如 "assetmap"）
#
# ★ 为什么要这么绕：在 /sdcard/Android/data/... 下 adb push **无法创建嵌套目录**
#   （secure_mkdirs failed / Operation not permitted），但 adb shell mkdir -p 可以。
#   所以先由 shell 按清单建好全部目录，再按顶层条目逐个 push。
set -u
export MSYS_NO_PATHCONV=1

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
ROOT="$(cd "$SCRIPT_DIR/../.." && pwd)"

ADB_BIN="${ADB:-adb}"
MERGED="${MERGED:-$ROOT/android/_work2/merged}"
DST="${1:-/sdcard/Android/data/com.blueoath.server/files/bundle}"

ADB_CMD=("$ADB_BIN")
[ -n "${SERIAL:-}" ] && ADB_CMD+=(-s "$SERIAL")

if [ ! -d "$MERGED" ]; then
  echo "找不到源 bundle 目录：$MERGED" >&2
  exit 1
fi

echo "== 源：$MERGED"
echo "== 目标：$DST"
"${ADB_CMD[@]}" wait-for-device
"${ADB_CMD[@]}" shell "mkdir -p '$DST'" >/dev/null 2>&1

echo "== 生成目录清单并在设备上创建 =="
cd "$MERGED" || exit 1
# 清单写到当前目录（adb push 需要 Windows 可见的相对路径，不能用 /tmp/...）
LIST=".bo_dirs.txt"
find . -type d | sed 's|^\./||' | grep -v '^\.$' > "$LIST"
echo "   目录数：$(wc -l < "$LIST")"
"${ADB_CMD[@]}" push "$LIST" "$DST/.bo_dirs.txt" 2>&1 | tail -1
"${ADB_CMD[@]}" shell "cd '$DST' && while IFS= read -r d; do mkdir -p \"\$d\"; done < .bo_dirs.txt && echo DIRSFILE_DONE" 2>&1 | tail -1
"${ADB_CMD[@]}" shell "rm -f '$DST/.bo_dirs.txt'" >/dev/null 2>&1
rm -f "$LIST"

echo "== 推送顶层条目 =="
SKIP_ENTRIES="${SKIP_ENTRIES:-}"
for e in *; do
  if [ -n "$SKIP_ENTRIES" ] && [[ " $SKIP_ENTRIES " == *" $e "* ]]; then
    printf '   %-28s SKIP\n' "$e"
    continue
  fi
  printf '   %-28s ' "$e"
  "${ADB_CMD[@]}" push "$e" "$DST/" 2>&1 | tail -1
done

echo "== 校验：文件数对比 =="
local_count=$(find . -type f | wc -l)
remote_count=$("${ADB_CMD[@]}" shell "find '$DST' -type f | wc -l" 2>/dev/null | tr -d '\r')
echo "   本地 $local_count / 设备 $remote_count"
