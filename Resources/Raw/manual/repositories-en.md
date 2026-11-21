# Repository setup

A repository connects a project folder to an agent in CodeBadger. Active repositories appear as tabs on the home screen.

## Add a repository

1. Open **Settings → Repositories**.
2. Select **Add**.
3. Enter a recognizable name. It is used for the tab and in CodeBadger windows.
4. Choose the project folder with the folder button next to **Path**, or enter the path directly. Choose the Git project folder, not an individual file.
5. Under **Agent tool**, choose the agent to launch for this repository: OpenCode, Claude Code, Codex, or Hermes.
6. Leave the active switch on to show the repository on the home screen.
7. Select **Save** at the bottom.

The project folder must exist. If the selected agent command is not installed or cannot be found, CodeBadger shows a warning and cannot start that agent. A folder can be configured even if CodeBadger has not initialized it as a Git repository; the Git module requires Git to recognize the path as a repository.

## Manage repositories

- Turn a repository off to temporarily hide its tab from the home screen. Its settings and files are retained.
- Update the name or path if a project moves. After changing the path, make sure the folder exists.
- Use the arrow controls to change the order of repository tabs.
- Removing a repository removes it from CodeBadger settings; it does not delete the project folder or Git history.
- Drag repository tabs to reorder them. You can also undock a tab into a separate window and dock it back later.

## Repository-specific options

### Agent configuration

Depending on the selected agent, a settings button may appear beside the agent selection. Use it to manage project configuration. For OpenCode, for example, you can inspect or edit MCP servers and project skills. CodeBadger creates a backup before changing an existing agent configuration. Project configuration is editable only while the relevant agent is available.

### Tickets

You can optionally require tickets and select a ticket system such as Jira or GitHub Issues. When tickets are enabled, select the MCP server configured for this repository in the agent's project configuration. This is used when starting and shaping tasks.

### AI story template

The story template describes the structure the AI should use to shape a task—for example, user story, context, acceptance criteria, technical notes, and out-of-scope items. It is included in the initial prompt when you start a task for this repository.

### OpenCode sandbox

For OpenCode repositories, you can enable the sandbox. It runs OpenCode in an isolated environment with separate settings and permissions. Configure permissions and sandbox options with the **Permissions** and **Settings** buttons. Slack commit and push commands are available only for non-sandboxed OpenCode repositories.

### Slack notifications

When Slack notifications are configured, you can mute automatic completion notifications for an individual OpenCode repository. This stops future automatic completion messages for that repository, but does not stop sessions or block replies in existing Slack threads. See the **Slack notifications** manual for setup and commands.

## Open a repository

Select its tab on the home screen. The workspace shows the agent terminal and, when the path is a valid Git repository, its Git status. The tab includes the current branch, changed-file count, and commits ahead of and behind the remote. Select **Git overview** to open the Git module.

## Troubleshooting

- **Path does not exist:** select the correct project folder again; moved folders are not discovered automatically.
- **Agent command not found:** install the selected agent or choose one that is available.
- **Git status unavailable:** check that Git is installed and the path points to a Git working tree.
- **Repository missing from the home screen:** check in Settings that it is active and has an existing path.
