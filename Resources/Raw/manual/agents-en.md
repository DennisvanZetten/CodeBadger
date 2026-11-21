# Agents and project configuration

CodeBadger can start an agent terminal for each active repository. The selected agent must be installed on the device and available through its expected command.

## Choose an agent and check availability

Choose an agent for each repository under **Settings → Repositories**: OpenCode, Claude Code, Codex, or Hermes. If CodeBadger cannot find the command, it shows a warning and the session cannot start. Install the agent or choose another one.

General **Agent management** is also available in Settings. Choose an agent there to view its global configuration file and detected MCP servers. The displayed path and availability status help identify which configuration is being used.

## MCP servers

MCP servers connect an agent to external tools or data sources. CodeBadger can list detected servers and, where supported, change whether they are enabled.

- General **Agent management** controls the global agent configuration.
- Repository configuration controls project-specific servers. Configuration formats vary by agent: OpenCode and Claude Code use JSON configuration; Codex uses TOML; Hermes uses YAML.
- A repository can select a specific MCP server for ticket workflows. That server must be available and enabled in the corresponding project configuration.
- Claude Code stores MCP definitions in `.mcp.json` and this repository's enablement state in `.claude/settings.local.json`.
- Codex project configuration is stored in `.codex/config.toml`. Codex loads project settings only for trusted projects.
- OpenCode project settings are read from `opencode.json` or `.opencode/opencode.json`.

Before changing an existing configuration file, CodeBadger creates a timestamped backup. Configuration is editable only when the agent is available. Carefully validate custom JSON, TOML, or YAML before saving.

## Project skills

The OpenCode and Codex project-configuration dialogs include a project-skills tab. You can select a skill, edit its Markdown content, save it, or create a new one. Codex skills are stored under `.agents/skills`; OpenCode skills are stored under `.opencode/skills`. Use lowercase letters, numbers, and hyphens in new skill IDs. Skills provide reusable project instructions; they do not replace general agent configuration.

## Claude Code and repository configuration

Claude Code project settings are available per repository through the settings button beside the agent selection. There you can manage MCP configuration and view or edit the JSON configuration. If the agent is unavailable, the configuration is read-only.

## Security and review

- Global agent settings can affect more than one repository; change them carefully.
- Project configuration may be committed to Git. Review which settings and server definitions you commit.
- Do not put API keys or other secrets in configuration files or skills.
- Enabling an MCP server may give the agent access to external actions or data. Enable only servers you trust.
