# Paramètres IA et de l’application

Ouvrez **Settings** dans l’application pour gérer les options générales, les commits par IA, les notifications et les limites du sandbox. La plupart des paramètres sont enregistrés localement sur cet appareil.

## IA pour les messages de commit

Dans **AI provider**, configurez le fournisseur et le modèle utilisés pour générer les messages de commit dans le module Git.

1. Choisissez un fournisseur, par exemple OpenAI, Azure OpenAI, Anthropic, Google Gemini, Ollama ou Custom URL.
2. Saisissez l’URL du fournisseur et la clé API si nécessaire. Les clés API sont stockées de manière sécurisée sur l’appareil.
3. Testez la connexion et chargez les modèles disponibles.
4. Choisissez un modèle et, si besoin, définissez le budget maximal de jetons d’entrée et des instructions personnalisées.

Les instructions peuvent imposer une convention de commit ou une langue. La génération par IA est facultative : vous pouvez toujours rédiger le message vous-même. Vérifiez les titres et descriptions générés avant de valider.

## Notifications

Dans **Notifications**, configurez les sons de fin de session et de question de l’agent, et gérez Slack. Les jetons Slack sont stockés de manière sécurisée. Consultez le manuel distinct **Notifications Slack** pour les autorisations de l’application, la connexion et les commandes.

## Limites du sandbox

Dans **Sandbox**, définissez la taille maximale de chaque fichier sélectionné et la taille totale maximale de la sélection. Ces limites s’appliquent aux nouvelles sessions sandbox. Si la sélection explicite dépasse l’une des limites, le sandbox peut passer automatiquement en mode direct. Dans ce mode, les modifications sont visibles immédiatement dans les fichiers réels sélectionnés. Consultez **Sandbox OpenCode** pour les détails et les avertissements de sécurité.

## Autres paramètres

- **Task branch prefix :** utilisé pour les branches créées pour les tâches.
- **Language :** choisissez l’anglais, le néerlandais, l’allemand ou le français. L’interface et les manuels changent lorsque les traductions sont disponibles.
- **Theme :** choisissez un thème clair ou sombre disponible.
- **Toast duration :** définit la durée d’affichage des notifications d’état.
- **Completion sound** et **Question sound :** choisissez et activez/désactivez chaque son séparément. Un aperçu est joué lors du choix d’un son.

Sélectionnez **Save** pour enregistrer les paramètres généraux. Les clés API restent dans le stockage sécurisé de l’appareil et ne doivent pas être placées dans les fichiers du projet ou les commits.
