# Agent terminals and sessions

A repository tab combines the project folder, a terminal for the selected agent, and Git status. CodeBadger starts the agent in the repository folder when the terminal first opens. The built-in terminal is available on Windows.

## Start and use an agent

1. Activate a repository and check that its path exists and the selected agent is available.
2. Open the repository tab. CodeBadger starts the configured agent in the project folder.
3. Type in the terminal and submit prompts as you normally would with that agent. The terminal supports interactive input and displays output from the agent process.
4. Select another repository tab to switch between project terminals.

If the agent command is not found, CodeBadger shows a warning. Install the agent or choose another under **Settings → Repositories**. If a previously opened terminal has closed, its process may have ended, and you may need to start the agent again.

## Resume OpenCode sessions

For non-sandboxed OpenCode, CodeBadger tracks the selected session. An OpenCode session started from the task list or Slack can be resumed in the associated repository terminal. The terminal starts OpenCode with the session ID; session history is managed by OpenCode.

A task started with **Start in new session** creates a new OpenCode session linked to the task item. A session started with Slack `!new` is linked to its Slack thread. Opening a session from a task link selects the corresponding repository and session in CodeBadger.

The repository terminal is an interactive agent terminal, not an all-sessions browser. Use the selected agent's own commands to inspect or open other sessions from the terminal. CodeBadger does not necessarily restore terminal output after the terminal process has exited.

## Activity and questions

For supported, non-standalone OpenCode sessions, CodeBadger shows activity on the repository tab:

- A spinner means a session is working.
- A question icon means OpenCode is waiting for permission or input.
- If Slack is enabled, supported sessions may receive a completion message. Any pending question is handled first; the completed response can be reported afterward.

Activity detection relies on the managed OpenCode service on Windows. Standalone OpenCode, explicitly selected servers, and other agent tools are not monitored in the same way.

## Agent terminal and OpenCode activity requirements

The terminal runs the selected agent command with the repository as its working directory. For OpenCode activity indicators, CodeBadger separately queries the managed OpenCode service through the `opencode api` command. Therefore, OpenCode processes started with `--standalone` or `--server` are not included in this activity monitoring.

## Terminal tips

- Keep the repository tab available while using its terminal. When windows are closed or detached, CodeBadger manages the terminal according to the associated repository session.
- Do not remove or move the project folder while a terminal or agent is working in it.
- Agent output may include project content or tool results. Share terminal output only through approved channels.
- Git actions are available through **Git overview** in the repository workspace; see the separate Git module manual.

## Sandbox terminals

If the OpenCode sandbox is enabled for a repository, CodeBadger uses the sandbox workflow instead of the regular agent terminal. Select files first and review the mode warning. Read **OpenCode sandbox** before starting a session.
