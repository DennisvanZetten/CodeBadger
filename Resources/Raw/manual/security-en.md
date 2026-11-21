# Security and privacy

CodeBadger works with local repository files, agent configuration, external AI providers, MCP servers, and optionally Slack. Consider what data you share and which permissions you enable.

## Secrets and local storage

- Provider API keys and Slack tokens are stored securely on the device. Do not put them in project files, prompts, or Git commits.
- General settings and the ideas/tasks list are stored locally. Slack session IDs and thread links are stored locally so replies can be routed to the correct session.
- Send Slack notifications only to a workspace where sharing project information is permitted. CodeBadger forwards Slack replies only from the configured Slack member.
- An AI provider receives the content needed for the requested AI feature, such as a staged diff for a commit message. Use only a provider with which sharing that repository content is permitted.

## Agent and MCP access

Agents can use files and tools with the permissions of their execution environment. An MCP server may provide access to external data or actions. Check a server's source and configuration before enabling it. Global agent configuration may affect multiple projects; project configuration may be committed to Git.

## Sandbox and file review

Select only files needed for the task. File selection is not a scan for secrets; detection is not guaranteed. In Copy/review mode, sandbox changes are applied to existing files only after your selection, and originals are backed up. Review the content and backups; applying multiple files is not one atomic transaction.

If the selection exceeds sandbox limits, Direct mode may be used: changes are immediately visible in the selected real files, without a review copy. Creating, deleting, and renaming files is blocked, but existing selected files can be changed immediately. Check the mode warning and selected files before starting.

Review network requests and permissions individually. Allow only trusted hosts and actions. Revoking a permission can stop a sandbox session.

## Git operations

Staging determines what goes into a commit. Review the staged list and diff before committing. **Undo selected** in the Git module discards unstaged changes and cannot be undone there. Pull, merge, cherry-pick, and revert can produce conflicts; resolve them carefully before continuing.

## Revoke access

Disable Slack notifications or revoke Slack app tokens to end the Slack connection. Remove or rotate AI-provider keys with the provider and update them in CodeBadger. Disable untrusted MCP servers. Stop an active sandbox session and review or discard its captured changes. Keep backups for as long as you may need them to restore changes.
