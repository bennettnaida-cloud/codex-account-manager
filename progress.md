# Progress Log

## 2026-07-09 - Task: Prepare privacy-safe GitHub import
### What was done
- Initialized the project as a Git repository for GitHub import.
- Added ignore rules for local account config, token metadata, local Codex config, build outputs, release archives, and IDE files.
- Added `accounts.example.json` so users can see the config shape without exposing real accounts.
- Configured the repository to use a GitHub noreply author email.
### Testing
- `powershell -NoProfile -ExecutionPolicy Bypass -File .\Build-CodexAccountManager.ps1` -> passed
- `powershell -NoProfile -ExecutionPolicy Bypass -File .\Test-CodexAccountSwitcher.ps1` -> passed
- Tracked-file secret scan -> no real token or private key matches
- Tracked-file ignore-boundary check -> no local account config, token metadata, local Codex config, build output, or release archive tracked
### Notes
- `.gitignore`: Protects local/private files from being committed.
- `accounts.example.json`: Provides a non-secret local account config example.
- `progress.md`: Compacted to remove local machine paths and account names before rewriting GitHub history.
- Rollback: restore the previous repository history from a trusted local backup if needed; do not republish the old history because it contained local path/account identifiers.

## 2026-07-09 - Task: Rewrite privacy-safe GitHub repository
### What was done
- Replaced tests that referenced private local account names and user paths with temporary account fixtures.
- Updated the build self-test to use temporary account fixtures instead of requiring a real local `accounts.json`.
- Added `README.md` with usage, privacy boundary, build, and verification instructions.
- Prepared the repository for a clean history rewrite so GitHub will receive only privacy-safe content.
- Reinitialized local Git history and force-pushed the privacy-safe root commit to GitHub.
- Attempted to delete the old GitHub repository, but GitHub rejected the API request because the local CLI token lacks the `delete_repo` scope.
### Testing
- `powershell -NoProfile -ExecutionPolicy Bypass -File .\Build-CodexAccountManager.ps1` -> passed
- `powershell -NoProfile -ExecutionPolicy Bypass -File .\Test-CodexAccountSwitcher.ps1` -> passed
- Worktree privacy scan for local user paths, private account names, GitHub tokens, OpenAI-style keys, and private-key headers -> no matches
- GitHub force push -> remote `main` updated to the privacy-safe root commit
- GitHub delete attempt -> blocked by missing `delete_repo` scope
### Notes
- `Test-CodexAccountSwitcher.ps1`: Uses temporary account directories and names for verification.
- `Build-CodexAccountManager.ps1`: Runs self-test against a temporary account configuration.
- `README.md`: Documents the project and privacy expectations.
- `progress.md`: Records the privacy rewrite without exposing local paths or account names.
- Rollback: restore the working tree from a local backup or clone the previous remote only if the private identifiers are acceptable in that environment; do not restore the old remote history if the private identifiers must stay removed.

## 2026-07-09 - Task: Document Business access token account setup
### What was done
- Expanded `README.md` with a Business access token login section.
- Clarified that the manager does not handle code-receiving flows; it uses a legally obtained Business access token through `codex login --with-access-token`.
- Added screenshots from the existing `artifacts` folder to show the main account manager UI and account card launch entry points.
- Added step-by-step instructions for adding an account by entering an account name, `CODEX_HOME`, and access token.
### Testing
- `git diff --check` -> passed
- README content check for Business token setup instructions -> passed
- Worktree privacy scan for local user paths, private account names, GitHub tokens, OpenAI-style keys, and private-key headers -> no matches
### Notes
- `README.md`: Added Business token login explanation, limitations, screenshots, and add-account steps.
- `progress.md`: Appended this documentation update.
- Rollback: revert `README.md` and remove this progress entry.

## 2026-07-09 - Task: Remove private screenshots from GitHub history
### What was done
- Identified that previously tracked UI screenshots could expose local account names and filesystem paths.
- Removed screenshot references from `README.md`.
- Added an ignore rule for screenshot PNG files under `artifacts`.
- Prepared to rewrite Git history again so leaked screenshot blobs are not present in the current branch history.
### Testing
- `powershell -NoProfile -ExecutionPolicy Bypass -File .\Build-CodexAccountManager.ps1` -> passed
- `powershell -NoProfile -ExecutionPolicy Bypass -File .\Test-CodexAccountSwitcher.ps1` -> passed
- Remote force push -> `origin/main` updated to the screenshot-free root commit
- Tracked tree check for `artifacts/` screenshots -> no tracked screenshots
- HEAD privacy scan for local user paths, private account names, GitHub tokens, OpenAI-style keys, and private-key headers -> no matches
- GitHub repository visibility -> PRIVATE
### Notes
- `README.md`: Removed screenshot embeds and added a privacy note that real UI screenshots should not be committed.
- `.gitignore`: Ignores `artifacts/*.png`.
- `artifacts/*.png`: Removed from version control because screenshots can contain private account/path data.
- `progress.md`: Recorded the screenshot privacy fix.
- Rollback: do not restore the old screenshots unless they are fully sanitized and verified not to contain private data.
