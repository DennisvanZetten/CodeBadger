# Manuel des notifications

Ce manuel comporte trois chapitres : informations générales sur les notifications et commandes, configuration de Slack et configuration de Discord.

## 1. Généralités

### Un seul canal de notification actif

CodeBadger prend en charge Slack et Discord, mais n’utilise **qu’un seul canal de notification à la fois**. Dans **Settings → Notifications**, choisissez Slack ou Discord pour **Notification channel**. Même si les deux services sont configurés, seul le service sélectionné est actif. **Enable session notifications** active ou désactive ce canal. Le changement prend effet après l’enregistrement ; CodeBadger arrête d’abord l’ancienne connexion.

### Confidentialité et sécurité

Une notification peut contenir un résumé d’une réponse OpenCode et des informations sur le projet. Connectez-vous uniquement à un espace Slack ou serveur Discord où leur partage est autorisé. Gardez les jetons du bot et de l’application secrets. CodeBadger stocke les jetons de manière sécurisée sur cet appareil, et non dans le fichier de paramètres normal. Par sécurité supplémentaire, seuls les messages provenant de l’identifiant utilisateur configuré sont traités.

### Conditions et fonctionnement

- CodeBadger doit être en cours d’exécution pour surveiller les sessions OpenCode, envoyer les notifications de fin, recevoir les questions et transmettre les réponses Slack ou Discord à OpenCode. Les autres outils d’agent ne sont pas surveillés. Aucun message n’est traité lorsque CodeBadger est complètement fermé.
- Pour une session surveillée, CodeBadger publie un message dans le canal choisi et y associe un fil. Répondez **dans le fil de la session** pour continuer exactement cette session OpenCode. Un message autonome dans le canal n’est associé à aucune session.
- Une session apparaît dans la liste des notifications de session après qu’un fil lui a été associé par CodeBadger.

### Commandes et réponses

- Envoyez `!new` comme message autonome dans le canal pour démarrer une session OpenCode. CodeBadger crée un fil contenant une liste de dépôts. Répondez avec le numéro d’un dépôt, puis envoyez votre tâche dans le message suivant du fil. Seuls les dépôts OpenCode actifs sans sandbox sont proposés.
- Envoyez `!todo <texte>` pour ajouter un élément à la liste CodeBadger. La première phrase (160 caractères maximum) devient le titre ; le reste devient la description. Les préfixes de type facultatifs sont `idea:`, `wish:`, `task:`, `bug:` et `story:`. Le type par défaut est `story`.
- Envoyez `!todos` dans le canal ou un fil de session pour lister les éléments ouverts. S’il y en a plus de dix, CodeBadger affiche les dix plus récents par date de création.
- Envoyez `!help` comme message autonome dans le canal pour voir les commandes du canal, ou dans un fil de session pour voir les commandes de session. Dans le fil de sélection `!new`, `!help` explique comment choisir un dépôt et envoyer la tâche.
- Commencez une réponse de session par `!build` ou `!plan`, suivi d’un espace et de votre instruction, pour changer d’agent pour ce tour. Exemple : `!plan Compare deux approches d’implémentation`. CodeBadger change d’abord d’agent, puis lui transmet le texte suivant la commande. Les tours suivants continuent avec cet agent jusqu’au prochain changement. N’envoyez pas uniquement `!build` ou `!plan` ; ajoutez une instruction.
- Envoyez `!commit` dans un fil de session pour indexer et valider toutes les modifications du dépôt. Si les messages de commit par IA sont configurés, CodeBadger tente d’en générer un. Sinon, CodeBadger demande un titre ; envoyez-le dans la réponse suivante du fil, avec éventuellement la description à la ligne suivante.
- Envoyez `!push` dans un fil de session pour pousser avec la même vérification sécurisée de la branche distante que dans la fenêtre Git.
- Les demandes d’autorisation OpenCode apparaissent dans le fil de session. Utilisez `!allow` pour autoriser une fois, `!always` pour mémoriser l’autorisation ou `!deny [motif]` pour refuser. S’il y a plusieurs demandes en attente, utilisez le numéro affiché, par exemple `!allow 2`. L’identifiant indiqué dans la demande reste également accepté.
- Les formulaires OpenCode affichent des formulaires, champs et options numérotés. Pour un formulaire à un seul champ, répondez avec `!answer <numéro d’option>`. Pour plusieurs champs, utilisez `!answer <numéro de champ> <numéro d’option>`. S’il y a plusieurs formulaires en attente, indiquez d’abord le numéro du formulaire. Séparez les réponses des champs par des points-virgules. Ajoutez votre propre texte après l’option « Autre ». Les champs à choix multiples acceptent des numéros séparés par `|`. La syntaxe existante `!answer <id> clé=valeur; clé2=valeur` reste prise en charge.
- Toute réponse ordinaire dans un fil qui n’est pas une commande reconnue est envoyée comme prompt à la session OpenCode associée.

### Mettre les notifications en sourdine

- Dans **Settings → Repositories**, activez **Mute session notifications for this repository** pour arrêter les futures notifications automatiques de fin pour toutes les sessions de ce dépôt. Désactivez l’option pour les reprendre.
- Pour mettre une seule session en sourdine, faites un clic droit sur l’onglet du dépôt et choisissez **Session notifications**. Utilisez **Mute session** ou **Unmute session** pour chaque session liée. Cette option n’est disponible que si le service de notification sélectionné est connecté et que le dépôt n’est pas déjà en sourdine.
- La mise en sourdine concerne uniquement les notifications automatiques de fin. Elle n’arrête pas l’agent, ne supprime pas les messages existants et ne bloque pas les réponses dans un fil existant.

### Désactiver l’intégration ou révoquer l’accès

Désactivez **Enable session notifications** pour couper les notifications. Pour révoquer complètement l’accès, supprimez l’application/le bot de l’espace Slack ou du serveur Discord et révoquez ses jetons. Ne collez jamais de jetons dans une conversation et ne les validez pas dans Git.

## 2. Slack

### Créer une application Slack

1. Ouvrez [api.slack.com/apps](https://api.slack.com/apps) et choisissez **Create New App** → **From scratch**.
2. Donnez-lui un nom, par exemple **CodeBadger**, et choisissez l’espace de travail où recevoir les notifications.
3. Ouvrez **OAuth & Permissions**. Sous **Bot Token Scopes**, ajoutez :
   - `chat:write` pour publier les notifications et réponses dans les fils.
   - `channels:read` et `channels:history` pour lister les canaux publics et recevoir leurs messages.
   - `groups:read` et `groups:history` pour lister les canaux privés et recevoir leurs messages, si nécessaire.
4. Ouvrez **Event Subscriptions**, activez les événements et abonnez le bot à `message.channels` et `message.groups` (ce dernier concerne les canaux privés).
5. Ouvrez **Socket Mode** et activez-le. CodeBadger utilise Socket Mode ; aucune URL Request publique n’est nécessaire.
6. Dans **Basic Information → App-Level Tokens**, créez un jeton d’application avec `connections:write`. Il commence par `xapp-`.
7. Revenez à **OAuth & Permissions** et cliquez sur **Install to Workspace** (ou **Reinstall to Workspace** si des scopes ont été ajoutés après l’installation). Autorisez l’application et copiez le **Bot User OAuth Token**, qui commence par `xoxb-`.
8. Invitez le bot dans le canal voulu, par exemple avec `/invite @CodeBadger`. Le bot ne peut publier que dans les canaux qu’il a rejoints.

Si un administrateur doit approuver ou installer l’application, demandez-lui d’approuver l’application et les autorisations indiquées.

### Trouver votre identifiant de membre Slack

Dans l’application Slack pour ordinateur, ouvrez votre profil, sélectionnez **More**, puis **Copy member ID**. L’identifiant commence généralement par `U`. CodeBadger l’utilise comme contrôle de sécurité : seuls les messages de cet utilisateur Slack sont transmis à OpenCode.

### Configurer CodeBadger pour Slack

1. Ouvrez **Settings → Notifications**.
2. Choisissez **Slack** comme **Notification channel**. Si Discord est également configuré, cette sélection garantit que seul Slack est actif.
3. Collez le jeton `xoxb-` dans **Slack bot token** et le jeton `xapp-` dans **Slack app-level token**. Ils sont stockés de manière sécurisée sur l’appareil, et non dans le fichier normal des paramètres.
4. Saisissez votre identifiant de membre Slack.
5. Cliquez sur **Load Slack channels** et choisissez un canal auquel le bot a été ajouté. Les canaux qu’il n’a pas rejoints ne peuvent pas être sélectionnés.
6. Cliquez sur **Test Slack connection**, activez **Send OpenCode completion notifications to Slack**, puis enregistrez.

L’état indique si CodeBadger est connecté à Slack via Socket Mode. CodeBadger doit être en cours d’exécution pour recevoir les réponses et questions OpenCode et surveiller les sessions.

## 3. Discord

### Créer et inviter un bot Discord

1. Ouvrez le [Discord Developer Portal](https://discord.com/developers/applications) et choisissez **New Application**. Donnez-lui un nom, par exemple **CodeBadger**.
2. Ouvrez la section **Bot** et créez le bot si cela n’a pas été fait automatiquement. Copiez son jeton avec **Reset Token** ou **View Token**. Traitez-le comme un mot de passe : toute personne qui le possède peut contrôler le bot.
3. Sous **Bot → Privileged Gateway Intents**, activez **Message Content Intent** et enregistrez. CodeBadger a besoin de cet intent privilégié pour recevoir le texte des messages du serveur.
4. Ouvrez **OAuth2 → URL Generator**. Sélectionnez le scope `bot` et accordez les permissions de bot suivantes :
   - **View Channels** et **Read Message History** pour accéder au canal choisi et lire son contexte.
   - **Send Messages** pour publier les notifications.
   - **Create Public Threads** et **Send Messages in Threads** pour créer des fils par session et y répondre.
5. Ouvrez l’URL générée, choisissez votre serveur et autorisez le bot. Vous devez avoir le droit d’ajouter des bots au serveur.
6. Vérifiez dans les paramètres du serveur et du canal que le bot dispose effectivement de ces permissions. Les autorisations propres au canal peuvent restreindre celles du serveur.

### Trouver vos identifiants Discord

Dans le client Discord, activez **User Settings → Advanced → Developer Mode**. Faites un clic droit sur le serveur où le bot a été invité et choisissez **Copy Server ID**. Copiez également votre propre identifiant depuis votre profil/menu contextuel avec **Copy User ID**. CodeBadger utilise votre identifiant comme contrôle de sécurité et ne traite que vos messages.

### Configurer CodeBadger pour Discord

1. Ouvrez **Settings → Notifications**.
2. Choisissez **Discord** comme **Notification channel**. Même si Slack est également configuré, seul Discord sera actif.
3. Collez le jeton dans **Discord bot token**.
4. Saisissez l’identifiant du serveur copié dans **Discord server (guild) ID**.
5. Cliquez sur **Load Discord channels** et choisissez un canal textuel ou d’annonces auquel le bot a accès.
6. Saisissez votre identifiant utilisateur Discord dans **Your Discord user ID**.
7. Cliquez sur **Test Discord connection**, activez **Enable session notifications**, puis enregistrez.

L’état indique si CodeBadger est connecté à la passerelle Discord. Si le chargement ou le test échoue, vérifiez le jeton du bot, l’identifiant du serveur, les permissions du canal et **Message Content Intent**. CodeBadger doit rester ouvert pour traiter les messages et réponses. Si les deux services sont configurés, **Notification channel** détermine l’intégration active.
