# Manuel des notifications Slack

Ce manuel explique comment configurer les notifications Slack, créer l’application Slack et utiliser les réponses, le changement d’agent et la mise en sourdine.

> **Confidentialité :** une notification Slack contient un résumé d’une réponse OpenCode. Connectez uniquement un espace de travail Slack dans lequel le partage des informations du projet est autorisé. Gardez les deux jetons Slack secrets.

## 1. Créer une application Slack

1. Ouvrez [api.slack.com/apps](https://api.slack.com/apps) et choisissez **Create New App** → **From scratch**.
2. Donnez un nom à l’application, par exemple **CodeBadger**, et choisissez l’espace de travail où vous souhaitez recevoir les notifications.
3. Ouvrez **OAuth & Permissions**. Sous **Bot Token Scopes**, ajoutez :
   - `chat:write` — publier les notifications et les réponses dans les fils.
   - `channels:read` et `channels:history` — lister les canaux publics et recevoir leurs messages.
   - `groups:read` et `groups:history` — lister les canaux privés et recevoir leurs messages, si vous souhaitez en utiliser un.
4. Ouvrez **Event Subscriptions**, activez les événements et abonnez le bot à `message.channels` et `message.groups` (ce dernier concerne les canaux privés).
5. Ouvrez **Socket Mode** et activez-le. CodeBadger utilise Socket Mode ; aucune URL Request publique n’est donc nécessaire.
6. Dans **Basic Information → App-Level Tokens**, créez un jeton d’application avec l’autorisation `connections:write`. Il commence par `xapp-`.
7. Revenez à **OAuth & Permissions** et cliquez sur **Install to Workspace** (ou **Reinstall to Workspace** si vous avez ajouté des autorisations après l’installation). Autorisez l’application et copiez le **Bot User OAuth Token**, qui commence par `xoxb-`.
8. Invitez le bot dans le canal Slack souhaité. Dans ce canal, saisissez par exemple `/invite @CodeBadger`. Le bot ne peut publier que dans les canaux qu’il a rejoints.

Si l’approbation ou l’installation de l’application nécessite un administrateur, demandez à l’administrateur Slack d’approuver l’application et les autorisations indiquées.

## 2. Trouver votre identifiant de membre Slack

Dans l’application Slack pour ordinateur, ouvrez votre profil, sélectionnez **More**, puis **Copy member ID**. L’identifiant commence généralement par `U`. CodeBadger l’utilise comme contrôle de sécurité : seules les réponses de ce membre Slack sont transmises à OpenCode.

## 3. Configurer CodeBadger

1. Ouvrez **Settings → Notifications**.
2. Laissez **Slack** sélectionné comme canal de notification.
3. Collez le jeton `xoxb-` dans **Slack bot token** et le jeton `xapp-` dans **Slack app-level token**. Ils sont stockés de manière sécurisée sur l’appareil, et non dans le fichier normal des paramètres.
4. Saisissez votre identifiant de membre Slack.
5. Cliquez sur **Load Slack channels**, puis choisissez un canal auquel le bot a été ajouté. Les canaux que le bot n’a pas rejoints ne sont pas sélectionnables.
6. Cliquez sur **Test Slack connection**. Activez **Send OpenCode completion notifications to Slack**, puis enregistrez les paramètres.

L’état affiché dans ce panneau indique si CodeBadger est connecté à Slack via Socket Mode.

## 4. Utiliser les notifications et les réponses

- Lorsqu’une session OpenCode surveillée se termine, CodeBadger publie un résumé concis dans le canal Slack choisi. Le message indique l’agent et la session.
- Répondez **dans le fil du message CodeBadger** pour continuer exactement cette session OpenCode. Un nouveau message dans le canal n’est associé à aucune session.
- Pour changer d’agent pour une réponse, commencez votre réponse au fil par `!build` ou `!plan`, suivi d’un espace et de votre instruction. Exemple : `!plan Compare deux approches d’implémentation`. L’agent est d’abord changé, puis reçoit le texte après la commande. Les tours suivants continuent avec cet agent jusqu’au prochain changement.
- Utilisez `!build` pour le travail d’implémentation et `!plan` pour la planification et l’analyse. N’envoyez pas uniquement `!build` ou `!plan` : ajoutez une instruction.

La réponse Slack doit provenir de l’identifiant de membre configuré dans CodeBadger. CodeBadger doit être ouvert pour recevoir les réponses Slack et détecter la fin des sessions.

## 5. Mettre en sourdine un dépôt ou une session

Lorsque les notifications Slack sont connectées, ouvrez **Settings → Repositories** pour gérer le paramètre de tout le dépôt :

- Activez **Mute Slack notifications for this repository** pour arrêter les futures notifications automatiques de fin pour toutes les sessions de ce dépôt. Désactivez cette option pour les reprendre.
- Pour mettre une seule session en sourdine, faites un clic droit sur l’onglet du dépôt et choisissez **Session notifications**. Cette option n’est disponible que si Slack est connecté et que le dépôt n’est pas en sourdine. La boîte de dialogue liste les sessions liées à Slack et propose **Mute session** ou **Unmute session** pour chacune.

La mise en sourdine ne concerne que les notifications automatiques de fin. Elle n’arrête pas l’agent, ne supprime aucun message Slack et ne bloque pas les réponses dans un fil de session existant.

## Remarques et limites

- La première version prend en charge les notifications Slack pour les sessions OpenCode ; les autres outils d’agent ne sont pas surveillés pour ces événements de fin.
- CodeBadger doit rester ouvert pour détecter la fin d’une session et recevoir les réponses Slack. Aucune notification n’est envoyée lorsque l’application est complètement fermée.
- Une session apparaît dans la liste de mise en sourdine après avoir été associée par CodeBadger à un fil Slack.
- Pour révoquer l’accès, désactivez l’option de notification, supprimez l’application de l’espace de travail Slack ou révoquez ses jetons. Ne collez jamais de jetons dans une conversation et ne les validez pas dans Git.
