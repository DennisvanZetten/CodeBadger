# Notification manual

This manual has three chapters: general information about notifications and commands, Slack setup, and Discord setup.

## 1. General

### One active notification channel

CodeBadger supports Slack and Discord, but uses **only one notification channel at a time**. In **Settings → Notifications**, select either Slack or Discord under **Notification channel**. Even if both services are configured, only the selected one is active. **Enable session notifications** turns that selected channel on or off. A channel change takes effect after saving; CodeBadger stops the previous connection first.

### Privacy and security

A notification can contain a summary of an OpenCode answer and project information. Only connect a Slack workspace or Discord server where you are allowed to share that information. Keep bot and app tokens secret. CodeBadger stores tokens securely on this device, not in the regular settings file. As an additional safety check, messages are processed only when they come from the user ID you configured.

### Requirements and behavior

- CodeBadger must be running to monitor OpenCode sessions, send completion notifications, receive questions, and route Slack or Discord replies to OpenCode. Notifications are not monitored for other agent tools. No messages are processed while CodeBadger is fully closed.
- For a monitored session, CodeBadger posts a message in the selected channel and links a thread to it. Reply **in that thread** to continue the exact OpenCode session. A standalone channel message is not linked to a session.
- A session appears in the session-notification list after CodeBadger has linked a thread to it.

### Commands and replies

- Send `!new` as a standalone channel message to start an OpenCode session. CodeBadger creates a thread with a repository list. Reply with a repository number, then send your task as the next thread message. Only active, non-sandboxed OpenCode repositories are listed.
- Send `!todo <text>` to add an item to the CodeBadger list. The first sentence (up to 160 characters) becomes its title; remaining text is the description. Optional type prefixes are `idea:`, `wish:`, `task:`, `bug:`, and `story:`. The default type is `story`.
- Send `!todos` in the channel or a session thread to list open items. If there are more than ten, CodeBadger shows the ten newest by creation date.
- Send `!help` as a standalone channel message to see channel commands, or in a session thread to see session commands. In the `!new` selection thread, `!help` explains repository selection and sending the task.
- Start a session reply with `!build` or `!plan`, followed by a space and your instruction, to switch agents for that turn. Example: `!plan Compare two implementation approaches`. CodeBadger switches the agent first, then sends the text after the command. Future turns keep using that agent until you switch again. Do not send only `!build` or `!plan`; include an instruction.
- Send `!commit` in a session thread to stage and commit all repository changes. If AI commit messages are configured, CodeBadger attempts to generate one. If it cannot, CodeBadger asks for a title; send it as the next thread reply, optionally with the description on the next line.
- Send `!push` in a session thread to push using the same safe remote-branch check as the Git dialog.
- OpenCode permission requests appear in the session thread. Use `!allow` for one-time approval, `!always` to remember approval, or `!deny [reason]` to reject. If several requests are pending, use the displayed number, such as `!allow 2`. The ID shown in the request is also supported.
- OpenCode forms show numbered forms, fields, and options. For one form with one field, reply with `!answer <option number>`. For multiple fields, use `!answer <field number> <option number>`. If multiple forms are pending, prefix the form number. Separate field answers with semicolons. Add your own text after the “Other” option. Multiselect fields accept option numbers separated by `|`. The existing `!answer <id> key=value; key2=value` syntax remains supported.
- Any ordinary thread reply that is not a recognized command is sent as a prompt to the linked OpenCode session.

### Mute notifications

- Open **Settings → Repositories** and enable **Mute session notifications for this repository** to stop future automatic completion notifications for every session in that repository. Turn it off to resume notifications.
- To mute one session, right-click the repository tab and select **Session notifications**. Use **Mute session** or **Unmute session** for each linked session. This option is available only while the selected notification service is connected and the repository is not muted.
- Muting affects automatic completion notifications only. It does not stop the agent, delete existing messages, or block replies in an existing session thread.

### Disable or revoke the integration

Turn off **Enable session notifications** to disable notifications. To revoke access completely, remove the app/bot from the Slack workspace or Discord server and revoke its tokens. Never paste tokens into chat or commit them to Git.

## 2. Slack

### Create a Slack app

1. Open [api.slack.com/apps](https://api.slack.com/apps) and select **Create New App** → **From scratch**.
2. Name it, for example **CodeBadger**, and choose the workspace where you want notifications.
3. Open **OAuth & Permissions**. Under **Bot Token Scopes**, add:
   - `chat:write` to post notifications and thread replies.
   - `channels:read` and `channels:history` to list public channels and receive their messages.
   - `groups:read` and `groups:history` to list private channels and receive their messages, if needed.
4. Open **Event Subscriptions**, enable events, and subscribe to bot events `message.channels` and `message.groups` (the latter is for private channels).
5. Open **Socket Mode** and enable it. CodeBadger uses Socket Mode, so no public Request URL is needed.
6. Under **Basic Information → App-Level Tokens**, create an app-level token with `connections:write`. It starts with `xapp-`.
7. Return to **OAuth & Permissions** and click **Install to Workspace** (or **Reinstall to Workspace** if you added scopes after installation). Approve the request and copy the **Bot User OAuth Token**, which starts with `xoxb-`.
8. Invite the bot to the channel you want to use, for example with `/invite @CodeBadger`. The bot can post only in channels it has joined.

If an administrator must approve or install the app, ask the Slack workspace administrator to approve it and the permissions above.

### Find your Slack member ID

In the Slack desktop app, open your profile, select **More**, then **Copy member ID**. The ID usually starts with `U`. CodeBadger uses it as a safety check: only messages from this Slack user are forwarded to OpenCode.

### Configure CodeBadger for Slack

1. Open **Settings → Notifications**.
2. Select **Slack** as **Notification channel**. If Discord is also configured, this selection ensures only Slack is active.
3. Paste the `xoxb-` token into **Slack bot token** and the `xapp-` token into **Slack app-level token**. They are stored securely on the device, not in the regular settings file.
4. Enter your Slack member ID.
5. Select **Load Slack channels** and choose a channel where the bot is a member. Channels the bot has not joined cannot be selected.
6. Select **Test Slack connection**, enable **Send OpenCode completion notifications to Slack**, and save.

The status in this panel reports whether CodeBadger is connected to Slack using Socket Mode. CodeBadger must be running to receive replies and OpenCode questions and monitor sessions.

## 3. Discord

### Create and invite a Discord bot

1. Open the [Discord Developer Portal](https://discord.com/developers/applications) and select **New Application**. Give it a name, such as **CodeBadger**.
2. Open **Bot** and create the bot if one was not created automatically. Copy its token using **Reset Token** or **View Token**. Treat this token like a password; anyone who has it can control the bot.
3. Under **Bot → Privileged Gateway Intents**, enable **Message Content Intent** and save. CodeBadger requires this privileged intent to receive server message text.
4. Open **OAuth2 → URL Generator**. Select the `bot` scope and grant these **Bot Permissions**:
   - **View Channels** and **Read Message History** to access the chosen channel and its context.
   - **Send Messages** to post notifications.
   - **Create Public Threads** and **Send Messages in Threads** to create per-session threads and reply in them.
5. Open the generated URL, select your server, and authorize the bot. You must have permission to add bots to that server.
6. Check the server and channel permission overrides to ensure the bot actually has these permissions. Channel overrides may restrict server permissions.

### Find your Discord IDs

In the Discord client, enable **User Settings → Advanced → Developer Mode**. Right-click the server where the bot was invited and select **Copy Server ID**. Also copy your own user ID from your profile/context menu using **Copy User ID**. CodeBadger uses your user ID as a safety check and processes messages only from you.

### Configure CodeBadger for Discord

1. Open **Settings → Notifications**.
2. Select **Discord** as **Notification channel**. If Slack is also configured, only Discord will be active.
3. Paste the bot token into **Discord bot token**.
4. Enter the copied server ID in **Discord server (guild) ID**.
5. Select **Load Discord channels** and choose a text or announcement channel the bot can access.
6. Enter your Discord user ID in **Your Discord user ID**.
7. Select **Test Discord connection**, enable **Enable session notifications**, and save.

The status shows whether CodeBadger is connected to the Discord Gateway. If loading or testing fails, check the bot token, server ID, channel permissions, and **Message Content Intent**. CodeBadger must remain running to process messages and replies. When both services are configured, **Notification channel** determines which integration is active.
