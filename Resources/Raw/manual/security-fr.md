# Sécurité et confidentialité

CodeBadger utilise des fichiers de dépôt locaux, la configuration des agents, des fournisseurs d’IA externes, des serveurs MCP et éventuellement Slack. Réfléchissez aux données partagées et aux autorisations activées.

## Secrets et stockage local

- Les clés API des fournisseurs et les jetons Slack sont stockés de manière sécurisée sur l’appareil. Ne les placez pas dans des fichiers du projet, des prompts ou des commits Git.
- Les paramètres généraux et la liste des idées/tâches sont stockés localement. Les identifiants de session Slack et les liens vers les fils sont conservés localement pour acheminer les réponses vers la bonne session.
- Envoyez des notifications Slack uniquement vers un espace de travail où le partage d’informations du projet est autorisé. CodeBadger ne transmet les réponses Slack que depuis le membre configuré.
- Un fournisseur d’IA reçoit le contenu nécessaire à la fonctionnalité demandée, par exemple un diff indexé pour générer un message de commit. N’utilisez qu’un fournisseur avec lequel le partage de ce contenu est autorisé.

## Accès des agents et de MCP

Les agents peuvent utiliser les fichiers et outils autorisés par leur environnement d’exécution. Un serveur MCP peut fournir l’accès à des données ou actions externes. Vérifiez la source et la configuration d’un serveur avant de l’activer. La configuration globale d’un agent peut affecter plusieurs projets ; celle d’un projet peut être validée dans Git.

## Sandbox et révision des fichiers

Ne sélectionnez que les fichiers nécessaires. La sélection des fichiers ne constitue pas une analyse des secrets ; leur détection n’est pas garantie. En mode copie/révision, les modifications du sandbox ne sont appliquées aux fichiers existants qu’après votre sélection, et les originaux sont sauvegardés. Examinez le contenu et les sauvegardes ; l’application de plusieurs fichiers n’est pas une transaction atomique.

Si la sélection dépasse les limites, le mode direct peut être utilisé : les modifications sont immédiatement visibles dans les fichiers réels sélectionnés, sans copie de révision. La création, la suppression et le renommage sont bloqués, mais les fichiers existants sélectionnés peuvent être modifiés directement. Vérifiez l’avertissement de mode et les fichiers choisis avant de démarrer.

Examinez séparément les demandes réseau et les autorisations. N’autorisez que des hôtes et actions de confiance. La révocation d’une autorisation peut arrêter une session sandbox.

## Opérations Git

L’indexation détermine le contenu du commit. Vérifiez la liste indexée et le diff avant de valider. **Undo selected** dans le module Git abandonne les modifications non indexées et ne peut pas être annulé depuis ce module. Un pull, merge, cherry-pick ou revert peut provoquer des conflits ; résolvez-les soigneusement avant de poursuivre.

## Révoquer l’accès

Désactivez les notifications Slack ou révoquez les jetons de l’application Slack pour couper la connexion. Supprimez ou renouvelez les clés du fournisseur d’IA auprès de celui-ci et mettez-les à jour dans CodeBadger. Désactivez les serveurs MCP non fiables. Arrêtez toute session sandbox active et examinez ou supprimez les modifications capturées. Conservez les sauvegardes tant qu’elles peuvent être nécessaires pour restaurer des changements.
