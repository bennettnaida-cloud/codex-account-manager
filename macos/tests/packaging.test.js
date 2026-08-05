const assert = require('node:assert/strict');
const fs = require('node:fs');
const path = require('node:path');
const test = require('node:test');

const packagingRoot = path.join(__dirname, '..', 'packaging');
const installer = fs.readFileSync(path.join(packagingRoot, '一键安装.command'), 'utf8');
const uninstaller = fs.readFileSync(path.join(packagingRoot, '卸载.command'), 'utf8');
const releaseVerifier = fs.readFileSync(path.join(__dirname, '..', 'scripts', 'verify-release.mjs'), 'utf8');
const releaseBuilder = fs.readFileSync(path.join(__dirname, '..', 'scripts', 'build-macos.mjs'), 'utf8');
const rendererSource = fs.readFileSync(path.join(__dirname, '..', 'src', 'renderer.js'), 'utf8');

test('installer quits the manager and precisely stops the daemon before replacing the app', () => {
  assert.match(installer, /tell application id \"com\.codexaccountmanager\.desktop\" to quit/);
  assert.match(installer, /expected_command="\$daemon_executable --local-pat-gateway-daemon"/);
  assert.match(installer, /lsof -nP -iTCP:8317 -sTCP:LISTEN/);
  assert.match(installer, /process_matches_current_user_command/);
  assert.match(installer, /ps -ww/);
  assert.match(installer, /getconf DARWIN_USER_TEMP_DIR/);
  assert.match(installer, /acquire_maintenance_lock/);
  assert.match(installer, /release_maintenance_lock/);
  assert.match(installer, /INSTALL_COMMITTED=1/);
  assert.doesNotMatch(installer, /\b(?:pkill|killall)\b/);
  assert.ok(installer.indexOf('quit_running_managers') < installer.indexOf('stop_gateway_daemon_for_app'));
  const installActions = installer.slice(installer.lastIndexOf('/usr/bin/ditto "$CUSTOM_APP" "$TEMP_APP"'));
  assert.ok(installActions.indexOf('quit_running_managers') < installActions.indexOf('stop_gateway_daemon_for_app'));
  assert.ok(installActions.indexOf('stop_gateway_daemon_for_app') < installActions.indexOf('require_gateway_port_available'));
  assert.ok(installActions.indexOf('require_gateway_port_available') < installActions.indexOf('mv "$TARGET_APP" "$BACKUP_APP"'));
  assert.ok(installActions.indexOf('BACKUP_CREATED=1') < installActions.indexOf('mv "$TARGET_APP" "$BACKUP_APP"'));
  assert.ok(installActions.indexOf('TARGET_REPLACED=1') < installActions.indexOf('mv "$TEMP_APP" "$TARGET_APP"'));
  assert.ok(installActions.indexOf('INSTALL_COMMITTED=1') < installActions.indexOf('rm -rf "$BACKUP_APP"'));
  assert.match(installer, /app-version\.txt/);
  assert.match(installer, /CFBundleShortVersionString \$APP_SHORT_VERSION/);
  assert.match(installer, /CFBundleVersion \$APP_BUILD_VERSION/);
});

test('uninstaller stops only this bundle manager and daemon before deleting either app', () => {
  assert.match(uninstaller, /tell application id \"com\.codexaccountmanager\.desktop\" to quit/);
  assert.match(uninstaller, /expected_command="\$daemon_executable --local-pat-gateway-daemon"/);
  assert.match(uninstaller, /process_matches_current_user_command/);
  assert.match(uninstaller, /ps -ww/);
  assert.match(uninstaller, /getconf DARWIN_USER_TEMP_DIR/);
  assert.match(uninstaller, /acquire_maintenance_lock/);
  assert.match(uninstaller, /release_maintenance_lock/);
  assert.doesNotMatch(uninstaller, /\b(?:pkill|killall)\b/);
  const actions = uninstaller.slice(uninstaller.lastIndexOf('quit_running_managers'));
  assert.ok(actions.indexOf('quit_running_managers') < actions.indexOf('stop_gateway_daemon_for_app'));
  assert.ok(actions.indexOf('stop_gateway_daemon_for_app') < actions.indexOf('require_gateway_port_released'));
  assert.ok(actions.indexOf('require_gateway_port_released') < actions.indexOf('remove_user_app'));
});

test('release verification compares the packaged gateway controller with source', () => {
  assert.match(releaseVerifier, /listPackage\(appAsar\)/);
  assert.match(releaseVerifier, /collectTree\(sourceRoot\)/);
  assert.match(releaseVerifier, /发布 ZIP 与当前 packaging 文件不一致/);
  assert.match(releaseVerifier, /packageJson\?\.version !== appVersion/);
});

test('quota snapshots are treated as private runtime data and labelled in the UI', () => {
  assert.match(releaseBuilder, /release-privacy\.mjs/);
  assert.match(releaseBuilder, /isForbiddenPrivateFile\(file\)/);
  assert.match(rendererSource, /cache:\s*"本地快照"/);
});

test('release privacy scan covers loose text files and local user paths', () => {
  assert.match(releaseBuilder, /scanSensitiveTextFile\(file, '发布目录文件'\)/);
  assert.match(releaseBuilder, /scanReleasePrivacy\(releaseRoot\)/);
});
