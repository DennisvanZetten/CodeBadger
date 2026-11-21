# Terminaux d’agent et sessions

Un onglet de dépôt regroupe le dossier du projet, un terminal pour l’agent choisi et l’état Git. À la première ouverture du terminal, CodeBadger démarre l’agent dans le dossier du dépôt. Le terminal intégré est disponible sous Windows.

## Démarrer et utiliser un agent

1. Activez un dépôt et vérifiez que son chemin existe et que l’agent choisi est disponible.
2. Ouvrez l’onglet du dépôt. CodeBadger démarre l’agent configuré dans le dossier du projet.
3. Saisissez votre texte dans le terminal et envoyez des prompts comme avec l’agent choisi. Le terminal accepte les entrées interactives et affiche la sortie du processus.
4. Sélectionnez un autre onglet de dépôt pour passer d’un terminal de projet à l’autre.

Si la commande de l’agent est introuvable, CodeBadger affiche un avertissement. Installez l’agent ou choisissez-en un autre dans **Settings → Repositories**. Si un terminal précédemment ouvert est fermé, son processus peut être terminé ; il faudra alors redémarrer l’agent.

## Reprendre des sessions OpenCode

Pour OpenCode sans sandbox, CodeBadger suit la session sélectionnée. Une session OpenCode démarrée depuis la liste des tâches ou Slack peut être reprise dans le terminal du dépôt associé. Le terminal lance OpenCode avec l’identifiant de session ; l’historique est géré par OpenCode.

Une tâche démarrée avec **Start in new session** crée une session OpenCode liée à l’élément. Une session démarrée avec `!new` dans Slack est liée à son fil Slack. L’ouverture d’une session depuis un lien de tâche sélectionne le dépôt et la session correspondants dans CodeBadger.

Le terminal du dépôt est un terminal interactif, pas un navigateur de toutes les sessions. Utilisez les commandes propres à l’agent dans le terminal pour consulter ou ouvrir d’autres sessions. CodeBadger ne restaure pas nécessairement la sortie du terminal si son processus est terminé.

## Activité et questions

Pour les sessions OpenCode prises en charge et non autonomes, CodeBadger affiche l’activité sur l’onglet du dépôt :

- Un indicateur animé signifie qu’une session travaille.
- Une icône de question signifie qu’OpenCode attend une autorisation ou une réponse.
- Si Slack est activé, les sessions prises en charge peuvent recevoir une notification de fin. Les questions en attente sont traitées d’abord ; la réponse terminée peut ensuite être publiée.

La détection d’activité dépend du service OpenCode géré sous Windows. OpenCode en mode autonome, les serveurs sélectionnés explicitement et les autres agents ne sont pas surveillés de la même façon.

## Prérequis du terminal et du suivi OpenCode

Le terminal lance la commande choisie avec le dépôt comme répertoire de travail. Pour afficher l’activité OpenCode, CodeBadger interroge séparément le service OpenCode géré via la commande `opencode api`. Les processus OpenCode lancés avec `--standalone` ou `--server` ne sont donc pas inclus dans ce suivi.

## Conseils pour le terminal

- Gardez l’onglet du dépôt disponible pendant l’utilisation du terminal. Lorsqu’une fenêtre est fermée ou détachée, CodeBadger gère le terminal selon la session associée au dépôt.
- Ne supprimez et ne déplacez pas le dossier du projet pendant qu’un terminal ou un agent y travaille.
- La sortie de l’agent peut contenir du contenu du projet ou des résultats d’outils. Ne partagez la sortie du terminal que par des canaux approuvés.
- Les actions Git sont disponibles via **Git overview** dans l’espace du dépôt ; consultez le manuel du module Git.

## Terminaux sandbox

Si le sandbox OpenCode est activé pour un dépôt, CodeBadger utilise le processus sandbox plutôt que le terminal d’agent habituel. Sélectionnez d’abord les fichiers et lisez l’avertissement du mode d’accès. Consultez **Sandbox OpenCode** avant de démarrer.
