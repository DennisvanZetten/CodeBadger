# CodeBadger notification manual

This manual explains how to configure Slack notifications, create the Slack app, and use session replies, agent switching, and mute controls.

> **Privacy:** A Slack notification contains a summary of an OpenCode answer. Only connect a workspace where sharing that project information is permitted. Keep both Slack tokens secret.

## 1. Create a Slack app

1. Open [api.slack.com/apps](https://api.slack.com/apps) and select **Create New App** → **From scratch**.
2. Give it a name such as **CodeBadger** and choose the workspace where you want the notifications.
3. Open **OAuth & Permissions**. Under **Bot Token Scopes**, add:
   - `chat:write` — post completion summaries and thread replies.
   - `channels:read` and `channels:history` — list public channels and receive public-channel messages.
   - `groups:read` and `groups:history` — list private channels and receive private-channel messages, if you plan to use one.
4. Open **Event Subscriptions**, enable events, and subscribe to the bot events `message.channels` and `message.groups` (the latter is for private channels).
5. Open **Socket Mode** and enable it. CodeBadger uses Socket Mode, so you do not need to expose a public Request URL.
6. In **Basic Information → App-Level Tokens**, generate an app-level token with `connections:write`. It starts with `xapp-`.
7. Return to **OAuth & Permissions** and click **Install to Workspace** (or **Reinstall to Workspace** if you added scopes after installation). Approve the request. Copy the **Bot User OAuth Token**, which starts with `xoxb-`.
8. In Slack, invite the bot to your chosen channel. For example, use `/invite @CodeBadger` in that channel. The app can post only to channels it has joined.

If Slack requires an administrator to approve or install the app, ask the workspace admin to approve it and the listed permissions.

## 2. Find your Slack member ID

In the Slack desktop app, open your profile, select **More**, then **Copy member ID**. The ID usually starts with `U`. CodeBadger uses it as a safety check: only replies from this Slack member are forwarded to OpenCode.

## 3. Configure CodeBadger

1. Open **Settings → Notifications**.
2. Keep **Slack** selected as the notification channel.
3. Paste the `xoxb-` token into **Slack bot token** and the `xapp-` token into **Slack app-level token**. They are stored in the device's secure storage, not in the regular settings JSON.
4. Enter your Slack member ID.
5. Select **Load Slack channels**, then choose a channel where the bot is a member. Channels that the bot has not joined are unavailable.
6. Select **Test Slack connection**. Enable **Send OpenCode completion notifications to Slack** and save the settings.

The status shown in this panel reports whether CodeBadger is connected to Slack with Socket Mode.

## 4. Use notifications and replies

- When a monitored OpenCode session finishes, CodeBadger posts a concise summary to the selected Slack channel. The message identifies the agent and session.
- Reply **in the thread on the CodeBadger message** to continue that exact OpenCode session. A new channel message is not routed to a session.
- To switch agent for a reply, start the thread reply with `!build` or `!plan`, followed by a space and your instruction. For example: `!plan Compare two implementation approaches`. The agent switches first, then receives the text after the command. Future turns keep using the selected agent until you switch again.
- Use `!build` for implementation work and `!plan` for planning and analysis. Do not send only `!build` or `!plan`; include an instruction after it.

The channel reply must come from the Slack member ID configured in CodeBadger. CodeBadger must be running to receive Slack replies and monitor session completion.

## 5. Mute a repository or session

When Slack notifications are connected, open **Settings → Repositories** to manage the repository-wide setting:

- Enable **Mute Slack notifications for this repository** to stop future automatic Slack completion notifications for every session in that repository. Turn it off to resume them.
- To mute one session, right-click the repository tab and choose **Session notifications**. This option is available only while Slack notifications are connected and the repository is not muted. The dialog lists Slack-linked sessions; use **Mute session** or **Unmute session** for the selected session.

Muting affects automatic completion notifications only. It does not stop the agent, delete Slack messages, or block replies in an existing session thread.

## Notes and limitations

- The first version supports Slack notifications for OpenCode sessions; other agent tools are not monitored for these completion events.
- CodeBadger must be running to notice a session finishing and to receive Slack replies. It does not deliver notifications while fully closed.
- A session appears in the mute list after CodeBadger has linked it to a Slack notification thread.
- To revoke access, disable the notification switch, remove the app from the Slack workspace, or revoke its tokens. Never paste tokens into chat or commit them to source control.
