# Configuration des dépôts

Un dépôt associe un dossier de projet à un agent dans CodeBadger. Les dépôts actifs apparaissent sous forme d’onglets sur l’écran d’accueil.

## Ajouter un dépôt

1. Ouvrez **Settings → Repositories**.
2. Sélectionnez **Add**.
3. Saisissez un nom reconnaissable. Il est utilisé pour l’onglet et dans les fenêtres CodeBadger.
4. Choisissez le dossier du projet avec le bouton de dossier à côté de **Path**, ou saisissez directement le chemin. Choisissez le dossier du projet Git, pas un fichier individuel.
5. Dans **Agent tool**, choisissez l’agent à lancer pour ce dépôt : OpenCode, Claude Code, Codex ou Hermes.
6. Laissez l’option d’activation en marche pour afficher le dépôt sur l’écran d’accueil.
7. Sélectionnez **Save** en bas.

Le dossier du projet doit exister. Si la commande de l’agent choisi n’est pas installée ou introuvable, CodeBadger affiche un avertissement et ne peut pas lancer cet agent. Un dossier peut être configuré sans que CodeBadger y ait initialisé un dépôt Git ; le module Git nécessite que Git reconnaisse le chemin comme un dépôt.

## Gérer les dépôts

- Désactivez un dépôt pour masquer temporairement son onglet. Ses paramètres et ses fichiers sont conservés.
- Modifiez son nom ou son chemin si le projet est déplacé. Après un changement de chemin, vérifiez que le dossier existe.
- Utilisez les flèches pour modifier l’ordre des onglets.
- La suppression retire le dépôt des paramètres CodeBadger, mais ne supprime ni le dossier du projet ni l’historique Git.
- Faites glisser les onglets pour les réorganiser. Vous pouvez aussi détacher un onglet dans une fenêtre séparée et le réintégrer ensuite.

## Options propres au dépôt

### Configuration de l’agent

Selon l’agent choisi, un bouton de paramètres peut apparaître à côté de la sélection. Il permet de gérer la configuration du projet. Avec OpenCode, par exemple, vous pouvez consulter ou modifier les serveurs MCP et les compétences du projet. CodeBadger crée une sauvegarde avant de modifier une configuration d’agent existante. La configuration du projet n’est modifiable que si l’agent concerné est disponible.

### Tickets

Vous pouvez rendre les tickets facultativement obligatoires et choisir un système comme Jira ou GitHub Issues. Lorsque les tickets sont activés, sélectionnez aussi le serveur MCP configuré pour ce dépôt dans la configuration du projet de l’agent. Cette option est utilisée lors du lancement et de la structuration des tâches.

### Modèle de récit IA

Le modèle décrit la structure à utiliser par l’IA pour détailler une tâche, par exemple récit utilisateur, contexte, critères d’acceptation, notes techniques et éléments hors périmètre. Il est inclus dans le prompt initial lorsque vous démarrez une tâche pour ce dépôt.

### Sandbox OpenCode

Pour les dépôts OpenCode, vous pouvez activer le sandbox. OpenCode s’exécute alors dans un environnement isolé, avec ses propres paramètres et autorisations. Configurez les autorisations et les options du sandbox avec les boutons **Permissions** et **Settings**. Les commandes Slack de commit et de push sont disponibles uniquement pour les dépôts OpenCode sans sandbox.

### Notifications Slack

Lorsque les notifications Slack sont configurées, vous pouvez désactiver les notifications automatiques de fin pour un dépôt OpenCode donné. Cela arrête les futures notifications automatiques pour ce dépôt, sans arrêter les sessions ni bloquer les réponses dans les fils Slack existants. Consultez le manuel **Notifications Slack** pour la configuration et les commandes.

## Ouvrir un dépôt

Sélectionnez son onglet sur l’écran d’accueil. L’espace de travail affiche le terminal de l’agent et, si le chemin correspond à un dépôt Git valide, son état Git. L’onglet indique la branche actuelle, le nombre de fichiers modifiés et les commits en avance ou en retard sur le dépôt distant. Sélectionnez **Git overview** pour ouvrir le module Git.

## Dépannage

- **Path does not exist :** choisissez à nouveau le bon dossier de projet ; les dossiers déplacés ne sont pas détectés automatiquement.
- **Agent command not found :** installez l’agent choisi ou sélectionnez-en un qui est disponible.
- **Git status unavailable :** vérifiez que Git est installé et que le chemin pointe vers un arbre de travail Git.
- **Dépôt absent de l’écran d’accueil :** vérifiez dans les paramètres qu’il est actif et que son chemin existe.
