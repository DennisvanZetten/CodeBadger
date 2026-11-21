# Idées et tâches

La liste **Ideas and tasks** est un espace local pour conserver le travail à reprendre plus tard. Ouvrez-la depuis le menu des idées/tâches de CodeBadger. La liste est enregistrée sur cet appareil.

## Ajouter un élément

1. Ouvrez **Ideas and tasks** et sélectionnez **New**.
2. Saisissez un titre de 160 caractères maximum.
3. Choisissez un type : **Idea**, **Wish**, **Task** ou **Bug**.
4. Ajoutez éventuellement une description (2 000 caractères maximum), puis enregistrez.

Vous pouvez également ajouter un élément depuis Slack avec `!todo <title>`. Le préfixe facultatif est `idea:`, `wish:`, `task:` ou `bug:`. Utilisez `!todos` pour lister les éléments ouverts dans Slack ; s’il y en a plus de dix, CodeBadger affiche les dix plus récents.

## Statut et suppression

Chaque élément a un statut : **Open**, **In progress** ou **Done**. Modifiez-le avec le sélecteur de statut de l’élément. Les éléments sont regroupés par statut et triés du plus récent au plus ancien dans chaque groupe. La suppression retire définitivement l’élément de la liste locale.

## Démarrer une tâche

Seuls les éléments de type **Task** peuvent être démarrés directement depuis cette liste dans une nouvelle session OpenCode.

1. Choisissez un dépôt OpenCode actif, existant et sans sandbox.
2. Sélectionnez **Start in new session**.
3. Choisissez la branche de départ. Si une branche `development` existe, vous pouvez la choisir ou conserver la branche actuelle.
4. Si les tickets sont obligatoires pour le dépôt, choisissez une option :
   - utiliser un ticket existant et créer une branche ;
   - créer un ticket via le serveur MCP configuré et créer une branche ;
   - utiliser un ticket existant et une branche locale existante.
5. CodeBadger extrait la branche existante ou en crée une, démarre une session OpenCode en mode **Plan** et envoie la tâche avec le modèle de récit du dépôt comme contexte.

Le préfixe des branches se configure dans **Settings → Other**. Dans la mesure du possible, le nom d’une nouvelle branche comprend le préfixe, le numéro du ticket et un slug du titre de la tâche.

Après le démarrage, le statut passe à **In progress** et l’élément est associé au dépôt, à la branche, au ticket et à la session. Ouvrez la session associée via le lien sur l’élément. Tant qu’une session y est associée, la tâche ne peut pas être redémarrée depuis la liste.

## Conditions et remarques

- Le démarrage nécessite un dépôt OpenCode actif avec un chemin existant et une branche Git adaptée aux opérations de branche.
- Ce processus ne crée ni commit ni push ; utilisez le module Git pour cela.
- La création d’un ticket nécessite un système de tickets et un serveur MCP configurés et disponibles pour le dépôt.
- Terminer le travail dans OpenCode ne modifie pas automatiquement le statut. Mettez-le à jour dans la liste.
