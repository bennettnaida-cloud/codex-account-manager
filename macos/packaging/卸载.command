#!/bin/bash
set -euo pipefail

APP_NAME="Codex Account Manager.app"
SYSTEM_APP="/Applications/$APP_NAME"
USER_APP="$HOME/Applications/$APP_NAME"
APP_DISPLAY_NAME="${APP_NAME%.app}"
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
  local current_uid current_start current_command lock_mtime now temporary_owner
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
      temporary_owner="$lock_dir/.owner.$$"
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

if [[ "$(uname -s)" != "Darwin" ]]; then
  echo "卸载脚本只能在 macOS 中运行。" >&2
  exit 1
fi

acquire_maintenance_lock
trap release_maintenance_lock EXIT
trap 'exit 130' INT TERM

quit_running_managers() {
  local app_path manager_executable pid owner_uid command attempt
  local caller_uid="$(/usr/bin/id -u)"
  local matched_pids=()
  for app_path in "$USER_APP" "$SYSTEM_APP"; do
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
      echo "Codex Account Manager 仍在运行。请手动退出后重新执行卸载；卸载器不会强制结束主程序。" >&2
      exit 1
    fi
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
          echo "Access Token 网关无法停止，卸载已中止。" >&2
          exit 1
        fi
      fi
    fi
  done
}

require_gateway_port_released() {
  local attempt
  for attempt in {1..50}; do
    if ! /usr/sbin/lsof -nP -iTCP:8317 -sTCP:LISTEN -t >/dev/null 2>&1; then
      return
    fi
    /bin/sleep 0.1
  done
  echo "本地端口 8317 仍被占用。为避免留下后台网关，卸载已中止；请注销当前用户或确认占用程序后重试。" >&2
  exit 1
}

remove_user_app() {
  if [[ -e "$USER_APP" ]]; then
    rm -rf "$USER_APP"
    echo "已删除：$USER_APP"
  fi
}

remove_system_app() {
  if [[ ! -e "$SYSTEM_APP" ]]; then
    return
  fi
  if [[ -w /Applications ]]; then
    rm -rf "$SYSTEM_APP"
  else
    echo "需要管理员密码以删除系统应用目录中的版本。"
    sudo /bin/rm -rf "$SYSTEM_APP"
  fi
  echo "已删除：$SYSTEM_APP"
}

quit_running_managers
stop_gateway_daemon_for_app "$USER_APP"
stop_gateway_daemon_for_app "$SYSTEM_APP"
require_gateway_port_released
remove_user_app
remove_system_app

echo
echo "卸载完成。以下用户数据已保留："
echo "  $HOME/Library/Application Support/Codex Account Manager"
echo "  $HOME/.codex"
echo "  $HOME/.codex-accounts"
echo "如需删除账号或历史，请先确认不再需要后手动处理，卸载脚本不会自动删除。"
read -r -p "按回车键关闭……" _
release_maintenance_lock
trap - EXIT INT TERM
