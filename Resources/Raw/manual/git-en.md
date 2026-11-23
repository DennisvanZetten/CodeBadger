# Git module

The Git module shows branches, commits, and file changes for one repository. Open it with **Git overview** in the Git panel of a repository tab. CodeBadger fetches remote status when the module opens.

## Overview and status

The current branch appears at the top. **Pull** shows how many commits the branch is behind; **Push** shows how many commits are ahead locally. The status at the bottom shows changed, staged, and unstaged files, plus the ahead/behind counts relative to the upstream branch. Use **Refresh** to fetch remote status again.

If the branch is behind, Push checks remote status and may pull first, but only when the working tree is clean. If there are uncommitted changes, the push is aborted so you can commit or stash them first. A branch without an upstream can be given one during push when there is an unambiguous remote.

## Branches and commits

The **Branches & commits** view shows local and remote branches alongside recent commit history.

- Select a branch to check it out. The current branch is marked.
- Branch names containing `/` are grouped into folders.
- Select a commit to view its changed files. Select a file to view that commit's diff.
- Double-click a commit to switch to a branch associated with it.
- Right-click a commit for more actions: create a branch there, check out the commit detached, cherry-pick, revert, merge an eligible branch, or copy the branch name, commit information, or SHA.
- Some actions are enabled only when the branch and working-tree state permit them safely. Checking out a commit and reverting require local changes to be committed or stashed first. Before cherry-picking, the module checks whether the commit is already on the current branch.

## Submodules

The **Submodules** view shows submodules recorded by the current commit, including nested submodules when their repository is initialized. Its summary counts matching, differing, and uninitialized checkouts.

- **In sync** means only that the checked-out commit matches the commit recorded by the parent repository. Local modifications and untracked files are reported separately.
- A branch name is informational; **detached HEAD is normal** for a submodule.
- Each entry shows its path, expected and checked-out commits, local changes, conflicts, and errors. Nested status cannot be read until the parent submodule is initialized.
- **Synchronize** initializes and updates all submodules recursively with `git submodule update --init --recursive`. The per-submodule button limits this to the selected submodule and its children.
- **Sync URLs** runs `git submodule sync --recursive`. **Sync URLs and update** runs that first, then the update.
- **Refresh status** reads local status only; it does not fetch.
- Before updating, CodeBadger checks local changes and conflicts, including initialized nested submodules. It stops to preserve changes; it never automatically forces, resets, or stashes. Network, authentication, and missing-commit errors are shown.
- Automatic synchronization after branch changes is a per-repository setting and is off by default. When enabled, CodeBadger warns before the main operation if local submodule changes exist. You can cancel or continue the main operation without submodule synchronization. After a successful branch change, it updates the submodules; a failed update does not undo the branch change.
- Normal synchronization never uses `--remote`: the commit recorded by the parent, not a newer remote commit, is authoritative.

### Create a branch

Select **New branch** to start from `HEAD`, or right-click a commit and select **Create branch here**. Enter a valid branch name. The new branch is created and checked out immediately.

### Conflicts

If Git reports conflicts, the module lists the conflicting files. Select **Resolve conflict** to open the built-in editor: compare the current version on the left with the incoming version on the right, choose a side for each conflicting line, and edit the result below. **Take all left/right** selects an entire side. **Save and mark resolved** saves and stages the result. **Open in VS Code** remains available as an alternative. Then complete the merge or rebase according to your Git workflow.

## Review and stage changes

Switch to **Create commit** to inspect the working tree. If there are changes, CodeBadger may open this view automatically.

- Select a file under **Unstaged** to inspect its diff.
- Use the plus button to stage one file, or **Stage all** to stage all unstaged changes.
- Select files with the checkboxes and choose **Stage selected** to stage only those files.
- Files under **Staged** are included in the next commit. Use the minus button to unstage a file.
- **Undo selected** removes selected unstaged changes from the working tree. It asks for confirmation and is different from unstaging.

Review the staged files and their diff before committing. Staging determines exactly which changes go into the commit.

## Create a commit

1. Stage the files you want in the commit.
2. Enter a title of up to 72 characters; a title is required.
3. Optionally add a description.
4. You can use **Generate with AI** if an AI provider is configured and reachable in Settings. Always review the generated message.
5. Select **Commit**. Only staged changes are committed.

## Push and pull

- **Pull** runs `git pull --no-rebase` for the current branch.
- **Push** fetches remote information first. If local commits are behind and the working tree is clean, CodeBadger tries to pull before pushing. If there are local changes, the operation is safely aborted; commit or stash them and try again.
- Push requires an upstream or an unambiguous default remote. If neither is available, CodeBadger explains why it did not push.

The module refreshes its status after a Git operation. Check the Git message if an operation fails; for example, pushing is not possible from a detached HEAD.

## Work safely

- Review staged files before committing; unstaged files are not included in that commit.
- **Undo selected** removes changes and cannot be undone through the Git module. Verify the selection before confirming.
- Checking out a commit or reverting is blocked while the working tree has changes.
- Pull, merge, cherry-pick, or revert can produce conflicts. Resolve them before continuing.
