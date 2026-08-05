#!/bin/bash
set -euo pipefail

APP_DISPLAY_NAME="Codex Account Manager"
SCRIPT_DIR="$(cd "$(dirname "$0")" && pwd -P)"
PAYLOAD_DIR="$SCRIPT_DIR/payload"
ELECTRON_ZIP="$PAYLOAD_DIR/electron-v43.1.1-darwin-arm64.zip"
CODEX_CLI_TGZ="$PAYLOAD_DIR/openai-codex-0.144.1-darwin-arm64.tgz"
APP_ASAR="$PAYLOAD_DIR/app.asar"
APP_ICON="$PAYLOAD_DIR/AppIcon.icns"
APP_VERSION="1.1.5"
if [[ -f "$SCRIPT_DIR/app-version.txt" ]]; then
  APP_VERSION="$(/usr/bin/tr -d '\r\n' < "$SCRIPT_DIR/app-version.txt")"
fi
APP_SHORT_VERSION="$APP_VERSION"
APP_BUILD_VERSION="5"
if [[ "$APP_VERSION" == *.*.*.* ]]; then
  APP_SHORT_VERSION="${APP_VERSION%.*}"
  APP_BUILD_VERSION="${APP_VERSION##*.}"
fi
MAINTENANCE_LOCK_DIR=""
MAINTENANCE_LOCK_NONCE=""

release_maintenance_lock() {
  [[ -n "$MAINTENANCE_LOCK_DIR" && -n "$MAINTENANCE_LOCK_NONCE" ]] || return 0
  local owner_file="$MAINTENANCE_LOCK_DIR/owner"
  if [[ -d "$MAINTENANCE_LOCK_DIR" && ! -L "$MAINTENANCE_LOCK_DIR" && \
        -f "$owner_file" && ! -L "$owner_file" && \
        "$(/usr/bin/sed -n '4p' "$owner_file" 2>/dev/null || true)" == "$MAINTENANCE_LOCK_NONCE" ]]; then
    /bin/rm -f "$owner_file"
    /bin/rmdir "$MAINTENANCE_LOCK_DIR" 2>/dev/null || true
  fi
  MAINTENANCE_LOCK_DIR=""
  MAINTENANCE_LOCK_NONCE=""
}

acquire_maintenance_lock() {
  local caller_uid lock_root lock_root_uid lock_root_mode lock_dir owner_file attempt owner_uid owner_mode owner_file_uid
  local stored_pid stored_uid stored_start stored_nonce stored_command
  local current_uid current_start current_command lock_mtime now
  caller_uid="$(/usr/bin/id -u)"
  lock_root="$(/usr/bin/getconf DARWIN_USER_TEMP_DIR 2>/dev/null || true)"
  lock_root="${lock_root%/}"
  if [[ -z "$lock_root" || ! -d "$lock_root" || -L "$lock_root" ]]; then
    echo "无法取得安全的当前用户临时目录，操作已中止。" >&2
    exit 1
  fi
  lock_root="$(cd "$lock_root" 2>/dev/null && pwd -P || true)"
  lock_root_uid="$(/usr/bin/stat -f '%u' "$lock_root" 2>/dev/null || true)"
  lock_root_mode="$(/usr/bin/stat -f '%Lp' "$lock_root" 2>/dev/null || true)"
  if [[ -z "$lock_root" || "$lock_root_uid" != "$caller_uid" || ! "$lock_root_mode" =~ ^[0-7]{3,4}$ ]] ||
     (( (8#$lock_root_mode & 8#022) != 0 )); then
    echo "当前用户临时目录的所有者或权限异常，操作已中止。" >&2
    exit 1
  fi
  lock_dir="$lock_root/com.codexaccountmanager.desktop.maintenance.lock"
  owner_file="$lock_dir/owner"
  for attempt in 1 2 3; do
    if /bin/mkdir -m 700 "$lock_dir" 2>/dev/null; then
      MAINTENANCE_LOCK_DIR="$lock_dir"
      MAINTENANCE_LOCK_NONCE="$(/usr/bin/uuidgen | /usr/bin/tr '[:upper:]' '[:lower:]')"
      current_start="$(/bin/ps -p $$ -o lstart= 2>/dev/null || true)"
      current_command="$(/bin/ps -ww -p $$ -o command= 2>/dev/null || true)"
      local temporary_owner="$lock_dir/.owner.$$"
      if ! /usr/bin/printf '%s\n' "$$" "$caller_uid" "$current_start" "$MAINTENANCE_LOCK_NONCE" "$current_command" > "$temporary_owner" ||
         ! /bin/chmod 600 "$temporary_owner" ||
         ! /bin/mv "$temporary_owner" "$owner_file"; then
        /bin/rm -f "$temporary_owner" "$owner_file" 2>/dev/null || true
        /bin/rmdir "$lock_dir" 2>/dev/null || true
        MAINTENANCE_LOCK_DIR=""
        MAINTENANCE_LOCK_NONCE=""
        echo "无法安全写入安装维护锁，操作已中止。" >&2
        exit 1
      fi
      return 0
    fi
    if [[ ! -d "$lock_dir" || -L "$lock_dir" ]]; then
      echo "安装维护锁路径不安全，操作已中止：$lock_dir" >&2
      exit 1
    fi
    owner_uid="$(/usr/bin/stat -f '%u' "$lock_dir" 2>/dev/null || true)"
    owner_mode="$(/usr/bin/stat -f '%Lp' "$lock_dir" 2>/dev/null || true)"
    if [[ "$owner_uid" != "$caller_uid" || "$owner_mode" != "700" ]]; then
      echo "安装维护锁不属于当前用户或权限异常，操作已中止。" >&2
      exit 1
    fi
    for _ in {1..20}; do
      [[ -e "$owner_file" ]] && break
      /bin/sleep 0.05
    done
    if [[ ! -e "$owner_file" ]]; then
      lock_mtime="$(/usr/bin/stat -f '%m' "$lock_dir" 2>/dev/null || true)"
      now="$(/bin/date +%s)"
      if [[ "$lock_mtime" =~ ^[0-9]+$ && $((now - lock_mtime)) -ge 10 ]] && /bin/rmdir "$lock_dir" 2>/dev/null; then
        continue
      fi
      echo "另一个安装或卸载操作正在启动，请稍后重试。" >&2
      exit 1
    fi
    owner_file_uid="$(/usr/bin/stat -f '%u' "$owner_file" 2>/dev/null || true)"
    if [[ ! -f "$owner_file" || -L "$owner_file" || "$owner_file_uid" != "$caller_uid" ||
          "$(/usr/bin/stat -f '%Lp' "$owner_file" 2>/dev/null || true)" != "600" ]]; then
      echo "安装维护锁记录异常，操作已中止。" >&2
      exit 1
    fi
    stored_pid="$(/usr/bin/sed -n '1p' "$owner_file")"
    stored_uid="$(/usr/bin/sed -n '2p' "$owner_file")"
    stored_start="$(/usr/bin/sed -n '3p' "$owner_file")"
    stored_nonce="$(/usr/bin/sed -n '4p' "$owner_file")"
    stored_command="$(/usr/bin/sed -n '5p' "$owner_file")"
    current_uid=""
    current_start=""
    current_command=""
    if [[ "$stored_pid" =~ ^[0-9]+$ ]]; then
      current_uid="$(/bin/ps -p "$stored_pid" -o uid= 2>/dev/null | /usr/bin/tr -d '[:space:]' || true)"
      current_start="$(/bin/ps -p "$stored_pid" -o lstart= 2>/dev/null || true)"
      current_command="$(/bin/ps -ww -p "$stored_pid" -o command= 2>/dev/null || true)"
    fi
    if [[ "$stored_pid" =~ ^[0-9]+$ && "$stored_uid" == "$caller_uid" && \
          "$stored_nonce" =~ ^[a-f0-9-]{36}$ && "$current_uid" == "$stored_uid" && \
          "$current_start" == "$stored_start" && "$current_command" == "$stored_command" ]]; then
      echo "另一个 Codex Account Manager 安装或卸载正在运行，请等待它完成。" >&2
      exit 1
    fi
    /bin/rm -f "$owner_file"
    if ! /bin/rmdir "$lock_dir" 2>/dev/null; then
      echo "无法安全回收上次中断留下的维护锁，请注销当前用户后重试。" >&2
      exit 1
    fi
  done
  echo "无法取得安装维护锁，请稍后重试。" >&2
  exit 1
}

pause_on_error() {
  local status=$?
  release_maintenance_lock
  if [[ $status -ne 0 ]]; then
    echo
    echo "安装失败，原应用和用户数据均未删除。" >&2
    read -r -p "按回车键退出……" _ || true
  fi
  exit $status
}
trap pause_on_error EXIT

if [[ "$(uname -s)" != "Darwin" || "$(uname -m)" != "arm64" ]]; then
  echo "这个安装包仅支持 Apple Silicon Mac (arm64)。" >&2
  exit 1
fi

acquire_maintenance_lock

for required in "$ELECTRON_ZIP" "$CODEX_CLI_TGZ" "$APP_ASAR" "$APP_ICON" "$SCRIPT_DIR/SHA256SUMS.txt"; do
  if [[ ! -f "$required" ]]; then
    echo "安装包不完整：缺少 $required" >&2
    exit 1
  fi
done

echo "正在校验安装包完整性……"
(cd "$SCRIPT_DIR" && /usr/bin/shasum -a 256 -c SHA256SUMS.txt)

TMP_BASE="${TMPDIR:-/tmp}"
WORK_ROOT="$(mktemp -d "$TMP_BASE/cam-install.XXXXXX")"
RAW_ROOT="$WORK_ROOT/raw"
CLI_ROOT="$WORK_ROOT/cli"
CUSTOM_APP="$WORK_ROOT/$APP_DISPLAY_NAME.app"
mkdir -p "$RAW_ROOT" "$CLI_ROOT"

INSTALL_ROOT="/Applications"
if [[ ! -w "$INSTALL_ROOT" ]]; then
  INSTALL_ROOT="$HOME/Applications"
  mkdir -p "$INSTALL_ROOT"
fi
TARGET_APP="$INSTALL_ROOT/$APP_DISPLAY_NAME.app"
TEMP_APP="$INSTALL_ROOT/.codex-account-manager-install-$$.app"
BACKUP_APP="$INSTALL_ROOT/.codex-account-manager-backup-$$.app"
TARGET_REPLACED=0
BACKUP_CREATED=0
INSTALL_COMMITTED=0
DAEMON_STOPPED=0
MANAGER_STOPPED=0

quit_running_managers() {
  local app_path manager_executable pid owner_uid command attempt
  local caller_uid="$(/usr/bin/id -u)"
  local matched_pids=()
  for app_path in "/Applications/$APP_DISPLAY_NAME.app" "$HOME/Applications/$APP_DISPLAY_NAME.app"; do
    manager_executable="$app_path/Contents/MacOS/$APP_DISPLAY_NAME"
    while read -r pid owner_uid command; do
      if [[ "$pid" =~ ^[0-9]+$ && "$owner_uid" == "$caller_uid" && \
            ( "$command" == "$manager_executable" || "$command" == "$manager_executable "* ) && \
            "$command" != *"--local-pat-gateway-daemon"* ]]; then
        matched_pids+=("$pid")
      fi
    done < <(/bin/ps -ww -axo pid=,uid=,command=)
  done
  if [[ ${#matched_pids[@]} -eq 0 ]]; then
    return
  fi
  /usr/bin/osascript -e 'tell application id "com.codexaccountmanager.desktop" to quit' >/dev/null 2>&1 || true
  for pid in "${matched_pids[@]}"; do
    for attempt in {1..300}; do
      /bin/kill -0 "$pid" 2>/dev/null || break
      /bin/sleep 0.1
    done
    if /bin/kill -0 "$pid" 2>/dev/null; then
      echo "旧版 Codex Account Manager 仍在运行。请手动退出后重新安装；安装器不会强制结束主程序。" >&2
      exit 1
    fi
    MANAGER_STOPPED=1
  done
}

process_matches_current_user_command() {
  local pid="$1"
  local expected_command="$2"
  local caller_uid="$3"
  local record
  record="$(/bin/ps -ww -p "$pid" -o uid=,command= 2>/dev/null || true)"
  record="${record#"${record%%[![:space:]]*}"}"
  [[ "$record" =~ ^([0-9]+)[[:space:]]+(.+)$ && \
     "${BASH_REMATCH[1]}" == "$caller_uid" && \
     "${BASH_REMATCH[2]}" == "$expected_command" ]]
}

stop_gateway_daemon_for_app() {
  local app_path="$1"
  local daemon_executable="$app_path/Contents/MacOS/$APP_DISPLAY_NAME"
  local expected_command="$daemon_executable --local-pat-gateway-daemon"
  local pid owner_uid command current attempt
  local caller_uid="$(/usr/bin/id -u)"
  local matched_pids=()
  while read -r pid owner_uid command; do
    if [[ "$pid" =~ ^[0-9]+$ && "$owner_uid" == "$caller_uid" && "$command" == "$expected_command" ]]; then
      matched_pids+=("$pid")
    fi
  done < <(/bin/ps -ww -axo pid=,uid=,command=)
  for pid in "${matched_pids[@]}"; do
    if process_matches_current_user_command "$pid" "$expected_command" "$caller_uid"; then
      /bin/kill -TERM "$pid" 2>/dev/null || true
    fi
  done
  for pid in "${matched_pids[@]}"; do
    for attempt in {1..50}; do
      /bin/kill -0 "$pid" 2>/dev/null || break
      /bin/sleep 0.1
    done
    if /bin/kill -0 "$pid" 2>/dev/null; then
      if process_matches_current_user_command "$pid" "$expected_command" "$caller_uid"; then
        /bin/kill -KILL "$pid" 2>/dev/null || true
        for attempt in {1..20}; do
          /bin/kill -0 "$pid" 2>/dev/null || break
          /bin/sleep 0.1
        done
        if process_matches_current_user_command "$pid" "$expected_command" "$caller_uid"; then
          echo "旧版 Access Token 网关无法停止，安装已中止。" >&2
          exit 1
        fi
      fi
    fi
    DAEMON_STOPPED=1
  done
}

require_gateway_port_available() {
  local attempt
  for attempt in {1..50}; do
    if ! /usr/sbin/lsof -nP -iTCP:8317 -sTCP:LISTEN -t >/dev/null 2>&1; then
      return
    fi
    /bin/sleep 0.1
  done
  echo "本地端口 8317 仍被占用。请先完全退出旧版 Codex Account Manager 后重新安装；安装器不会结束其它程序。" >&2
  exit 1
}

assert_safe_app_path() {
  case "$1" in
    "/Applications/$APP_DISPLAY_NAME.app"|"$HOME/Applications/$APP_DISPLAY_NAME.app"|\
    "/Applications/.codex-account-manager-install-"*.app|"$HOME/Applications/.codex-account-manager-install-"*.app|\
    "/Applications/.codex-account-manager-backup-"*.app|"$HOME/Applications/.codex-account-manager-backup-"*.app)
      ;;
    *)
      echo "安全检查未通过，拒绝操作路径：$1" >&2
      exit 1
      ;;
  esac
}
assert_safe_app_path "$TARGET_APP"
assert_safe_app_path "$TEMP_APP"
assert_safe_app_path "$BACKUP_APP"

rollback() {
  local status=$?
  trap - EXIT INT TERM
  # 回滚中的某一步即使失败，也必须继续尝试恢复旧应用并释放维护锁。
  set +e
  if [[ $status -ne 0 && $INSTALL_COMMITTED -eq 0 ]]; then
    [[ -e "$TEMP_APP" ]] && rm -rf "$TEMP_APP"
    if [[ $TARGET_REPLACED -eq 1 && -e "$TARGET_APP" ]]; then
      rm -rf "$TARGET_APP"
    fi
    if [[ $BACKUP_CREATED -eq 1 && -e "$BACKUP_APP" ]]; then
      mv "$BACKUP_APP" "$TARGET_APP"
    fi
    if [[ ( $DAEMON_STOPPED -eq 1 || $MANAGER_STOPPED -eq 1 ) && -d "$TARGET_APP" ]]; then
      /usr/bin/open "$TARGET_APP" >/dev/null 2>&1 || true
    fi
  elif [[ $status -ne 0 && $INSTALL_COMMITTED -eq 1 ]]; then
    [[ -e "$BACKUP_APP" ]] && rm -rf "$BACKUP_APP"
  fi
  [[ -n "${WORK_ROOT:-}" && -d "$WORK_ROOT" ]] && rm -rf "$WORK_ROOT"
  release_maintenance_lock
  if [[ $status -ne 0 ]]; then
    echo
    if [[ $INSTALL_COMMITTED -eq 1 ]]; then
      echo "新应用已安全安装，但启动或收尾步骤被中断；已保留可用的新版本。" >&2
    else
      echo "安装失败，原应用和用户数据均未删除。" >&2
    fi
    read -r -p "按回车键退出……" _ || true
  fi
  exit $status
}
trap rollback EXIT
trap 'exit 130' INT TERM

echo "正在由 macOS 原生组装应用……"
/usr/bin/ditto -x -k "$ELECTRON_ZIP" "$RAW_ROOT"
if [[ ! -d "$RAW_ROOT/Electron.app" ]]; then
  echo "Electron payload 结构异常。" >&2
  exit 1
fi
mv "$RAW_ROOT/Electron.app" "$CUSTOM_APP"
mv "$CUSTOM_APP/Contents/MacOS/Electron" "$CUSTOM_APP/Contents/MacOS/$APP_DISPLAY_NAME"
rm -f "$CUSTOM_APP/Contents/Resources/default_app.asar"
/usr/bin/ditto "$APP_ASAR" "$CUSTOM_APP/Contents/Resources/app.asar"
/usr/bin/ditto "$APP_ICON" "$CUSTOM_APP/Contents/Resources/electron.icns"

echo "正在安装内置 Codex CLI 0.144.1……"
/usr/bin/tar -xzf "$CODEX_CLI_TGZ" -C "$CLI_ROOT"
CLI_PAYLOAD="$CLI_ROOT/package/vendor/aarch64-apple-darwin"
for executable in \
  "$CLI_PAYLOAD/bin/codex" \
  "$CLI_PAYLOAD/bin/codex-code-mode-host" \
  "$CLI_PAYLOAD/codex-path/rg" \
  "$CLI_PAYLOAD/codex-resources/zsh/bin/zsh"; do
  if [[ ! -f "$executable" || -L "$executable" ]]; then
    echo "Codex CLI payload 结构异常：$executable" >&2
    exit 1
  fi
done
if ! /usr/bin/lipo -archs "$CLI_PAYLOAD/bin/codex" | /usr/bin/grep -qw arm64; then
  echo "内置 Codex CLI 不是 Apple Silicon 可执行文件。" >&2
  exit 1
fi
/usr/bin/ditto "$CLI_PAYLOAD" "$CUSTOM_APP/Contents/Resources/codex-cli"
chmod 755 \
  "$CUSTOM_APP/Contents/Resources/codex-cli/bin/codex" \
  "$CUSTOM_APP/Contents/Resources/codex-cli/bin/codex-code-mode-host" \
  "$CUSTOM_APP/Contents/Resources/codex-cli/codex-path/rg" \
  "$CUSTOM_APP/Contents/Resources/codex-cli/codex-resources/zsh/bin/zsh"

INFO_PLIST="$CUSTOM_APP/Contents/Info.plist"
PLIST_BUDDY="/usr/libexec/PlistBuddy"
"$PLIST_BUDDY" -c "Set :CFBundleDisplayName $APP_DISPLAY_NAME" "$INFO_PLIST"
"$PLIST_BUDDY" -c "Set :CFBundleExecutable $APP_DISPLAY_NAME" "$INFO_PLIST"
"$PLIST_BUDDY" -c "Set :CFBundleName $APP_DISPLAY_NAME" "$INFO_PLIST"
"$PLIST_BUDDY" -c "Set :CFBundleIdentifier com.codexaccountmanager.desktop" "$INFO_PLIST"
"$PLIST_BUDDY" -c "Set :CFBundleShortVersionString $APP_SHORT_VERSION" "$INFO_PLIST"
"$PLIST_BUDDY" -c "Set :CFBundleVersion $APP_BUILD_VERSION" "$INFO_PLIST"
"$PLIST_BUDDY" -c "Set :CFBundleIconFile electron.icns" "$INFO_PLIST"
"$PLIST_BUDDY" -c "Set :LSMinimumSystemVersion 12.0" "$INFO_PLIST"
"$PLIST_BUDDY" -c "Delete :ElectronAsarIntegrity" "$INFO_PLIST" 2>/dev/null || true

chmod +x "$CUSTOM_APP/Contents/MacOS/$APP_DISPLAY_NAME"
/usr/bin/xattr -dr com.apple.quarantine "$CUSTOM_APP" 2>/dev/null || true
echo "正在执行本机 ad-hoc 签名……"
/usr/bin/codesign --force --deep --sign - --timestamp=none "$CUSTOM_APP"
/usr/bin/codesign --verify --deep --strict "$CUSTOM_APP"

rm -rf "$TEMP_APP" "$BACKUP_APP"
/usr/bin/ditto "$CUSTOM_APP" "$TEMP_APP"
quit_running_managers
stop_gateway_daemon_for_app "/Applications/$APP_DISPLAY_NAME.app"
stop_gateway_daemon_for_app "$HOME/Applications/$APP_DISPLAY_NAME.app"
require_gateway_port_available
if [[ -e "$TARGET_APP" ]]; then
  BACKUP_CREATED=1
  mv "$TARGET_APP" "$BACKUP_APP"
fi
TARGET_REPLACED=1
mv "$TEMP_APP" "$TARGET_APP"
/usr/bin/codesign --verify --deep --strict "$TARGET_APP"
INSTALL_COMMITTED=1
rm -rf "$BACKUP_APP"
BACKUP_CREATED=0

echo "安装完成：$TARGET_APP"
echo "账号、会话与 ~/.codex 数据均未被修改。"
rm -rf "$WORK_ROOT"
/usr/bin/open "$TARGET_APP"
release_maintenance_lock
trap - EXIT INT TERM
