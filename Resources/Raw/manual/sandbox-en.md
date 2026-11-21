# OpenCode sandbox

The OpenCode sandbox starts OpenCode for a repository in an isolated environment. Instead of sharing the whole repository automatically, you explicitly select which files are available. Open **Permissions** and **Settings** under **Settings → Repositories** for an OpenCode repository to configure it.

## Choose a model and files

Open **Settings** in the sandbox options for an OpenCode repository.

1. Choose the model for new sandbox sessions.
2. Select the files and folders OpenCode needs. Selecting a folder includes its descendant files.
3. Save the selection. Sensitive paths and links are not shown as selectable entries.

General sandbox limits are under **Settings → Sandbox**. The defaults are 64 MB per file and 1,024 MB total; configurable ranges are 1–512 MB per file and 1–2,048 MB total. These limits apply to new sessions.

## Two access modes

CodeBadger measures the explicitly selected files when starting:

- **Copy/review mode:** if the selection fits within both size limits, selected files are copied to a temporary, isolated working copy. The sandbox does not receive general network access.
- **Direct mode:** if any file or the total selection exceeds a limit, CodeBadger uses temporary, restricted access to the selected real files. Changes are immediately visible in the repository; creating, deleting, and renaming files is blocked. There is no review copy in this mode.

The mode is selected automatically. Increasing limits can increase memory and disk use. Keep the selection limited to what the task needs.

## Stop the session and review changes

When finished, stop the sandbox with **Stop sandbox**. In copy/review mode, changes are captured for review. The review shows changed files and their content; deletions may be shown but cannot be applied.

1. Review each file's proposed content.
2. Select only the changes you want to apply.
3. Choose **Apply selected modifications**.

CodeBadger applies only selected modifications to existing files and backs up the originals. A conflict check is performed; if an original file has changed in the meantime, applying its modification may be refused. Applying multiple files is not one atomic transaction. Check the backups and results carefully. Unapplied review changes remain available until you apply or explicitly discard them. You must finish or discard the review before starting another sandbox session.

Direct mode writes changes to the selected real files immediately. Stopping does not create a review copy for that mode.

## Permissions and network access

Sandbox permissions are repository-specific. The permissions dialog shows recorded origins. Revoking a permission stops the associated repository sandbox session; host API keys are not displayed.

Network access is restricted by default. If a sandbox requests access that requires approval, review the request and allow it only when you trust the host and destination. Do not expose secrets or project files that are not intended for the session.

## Important limitations

- File selection does not mean file contents have been checked for secrets; review what you share. Secret detection is not guaranteed.
- Reparse points, links, unsafe paths, and excessively large selections are rejected.
- The sandbox is available on Windows and is intended for OpenCode.
- Direct-mode actions affect the selected real files immediately. Check the mode warning before continuing.
