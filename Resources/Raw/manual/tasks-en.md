# Ideas and tasks

The **Ideas and tasks** list is a local place to keep work you want to revisit. Open it from the ideas/tasks menu in CodeBadger. The list is stored on this device.

## Add an item

1. Open **Ideas and tasks** and select **New**.
2. Enter a title of up to 160 characters.
3. Choose a type: **Idea**, **Wish**, **Task**, or **Bug**.
4. Optionally add a description (up to 2,000 characters), then save.

You can also add items from Slack with `!todo <title>`. An optional type prefix is `idea:`, `wish:`, `task:`, or `bug:`. Use `!todos` to list open items in Slack; if there are more than ten, CodeBadger shows the ten newest.

## Status and cleanup

Each item has a status: **Open**, **In progress**, or **Done**. Use the status selector on the item to update it. Items are grouped by status and ordered newest first within each group. Deleting an item permanently removes it from the local list.

## Start a task

Only items of type **Task** can be started as a new OpenCode session directly from this list.

1. Choose an eligible active, existing, non-sandboxed OpenCode repository.
2. Select **Start in new session**.
3. Choose the branch to use as the starting point. If a `development` branch is available, you can use it or the current branch.
4. If tickets are required for the repository, choose a ticket option:
   - use an existing ticket and create a branch;
   - create a ticket through the configured MCP server and create a branch;
   - use an existing ticket and an existing local branch.
5. CodeBadger checks out the existing branch or creates a branch, starts an OpenCode session in **Plan** mode, and sends the task with the repository's story template as context.

The branch prefix can be configured under **Settings → Other**. When possible, a new branch name includes the prefix, ticket number, and a slug based on the task title.

After the task starts successfully, its status becomes **In progress** and it is linked to the repository, branch, ticket, and session. Open the linked session using the session link on the task item. The task cannot be started again from the list while it remains linked to a session.

## Requirements and notes

- Starting requires an active OpenCode repository with an existing path and a suitable Git branch for branch operations.
- This workflow does not create a commit or push; use the Git module for those operations.
- Creating a ticket requires the ticket system and repository MCP server to be configured and available.
- Completing work in OpenCode does not automatically change the task status. Update it in the list yourself.
