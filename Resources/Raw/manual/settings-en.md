# AI and application settings

Open **Settings** in the application to manage general options, AI commits, notifications, and sandbox limits. Most settings are stored locally on this device.

## AI for commit messages

Under **AI provider**, configure the provider and model used to generate commit messages in the Git module.

1. Choose a provider, such as OpenAI, Azure OpenAI, Anthropic, Google Gemini, Ollama, or Custom URL.
2. Enter the provider URL and API key when required. API keys are stored securely on the device.
3. Test the connection and load the available models.
4. Choose a model and optionally set the maximum input-token budget.

Custom instructions for commit messages are configured separately for each repository in its repository settings. They can specify a commit convention or language. AI generation is optional: you can always write the commit message yourself. Review generated titles and descriptions before committing.

## Notifications

Under **Notifications**, configure sounds for session completion and agent questions, and manage Slack. Slack tokens are stored securely. See the separate **Slack notifications** manual for app scopes, connection setup, and commands.

## Sandbox limits

Under **Sandbox**, set the maximum size per selected file and the maximum total selection size. These limits apply to new sandbox sessions. If the explicit selection exceeds either limit, the sandbox may automatically use Direct mode. In that mode, changes are immediately visible in the selected real files. See **OpenCode sandbox** for details and safety warnings.

## Other settings

- **Task branch prefix:** used when creating branches for tasks.
- **Language:** choose English, Dutch, German, or French. The interface and manuals switch where translations are available.
- **Theme:** choose an available light or dark theme.
- **Toast duration:** set how long status notifications remain visible.
- **Completion sound** and **Question sound:** choose sounds and enable or disable each independently. A preview plays when you select a sound.

Select **Save** to store general settings. API keys remain in the device's secure storage and should not be placed in project files or commits.
