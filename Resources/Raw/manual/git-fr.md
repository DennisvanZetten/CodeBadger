# Module Git

Le module Git affiche les branches, les commits et les modifications de fichiers d’un dépôt. Ouvrez-le avec **Git overview** dans le panneau Git d’un onglet de dépôt. CodeBadger récupère l’état distant à l’ouverture.

## Vue d’ensemble et état

La branche actuelle apparaît en haut. **Pull** indique le nombre de commits de retard ; **Push**, le nombre de commits locaux en avance. L’état en bas indique les fichiers modifiés, indexés et non indexés, ainsi que l’avance/le retard par rapport à la branche upstream. Utilisez **Refresh** pour récupérer à nouveau l’état distant.

Si la branche est en retard, Push vérifie l’état distant et peut effectuer un pull avant le push, mais uniquement si l’arbre de travail est propre. En présence de modifications non validées, le push est interrompu afin que vous puissiez d’abord les valider ou les mettre de côté (stash). Lors du push, une branche sans upstream peut en recevoir un si une remote non ambiguë est disponible.

## Branches et commits

La vue **Branches & commits** affiche les branches locales et distantes, ainsi que les commits récents.

- Sélectionnez une branche pour l’extraire (checkout). La branche actuelle est marquée.
- Les noms de branche contenant `/` sont regroupés dans des dossiers.
- Sélectionnez un commit pour afficher ses fichiers modifiés. Sélectionnez un fichier pour voir le diff de ce commit.
- Double-cliquez sur un commit pour passer à une branche qui lui est associée.
- Faites un clic droit sur un commit pour afficher les actions : créer une branche à cet endroit, extraire le commit en HEAD détachée, cherry-pick, revert, fusionner une branche admissible ou copier son nom, les informations du commit ou son SHA.
- Certaines actions ne sont activées que si l’état de la branche et de l’arbre de travail le permet sans risque. Avant d’extraire un commit ou de faire un revert, les modifications locales doivent être validées ou mises de côté. Avant un cherry-pick, le module vérifie si le commit figure déjà sur la branche actuelle.

### Créer une branche

Sélectionnez **New branch** pour partir de `HEAD`, ou faites un clic droit sur un commit et choisissez **Create branch here**. Saisissez un nom de branche valide. La nouvelle branche est créée et extraite immédiatement.

### Conflits

Si Git signale des conflits, le module affiche les fichiers concernés. Sélectionnez **Resolve conflict** pour ouvrir l’éditeur intégré : comparez la version actuelle à gauche avec la version entrante à droite, choisissez un côté pour chaque ligne en conflit et modifiez le résultat en dessous. **Take all left/right** reprend un côté entier. **Save and mark resolved** enregistre et indexe le résultat. **Open in VS Code** reste disponible en alternative. Terminez ensuite le merge ou le rebase selon votre procédure Git.

## Examiner et indexer les modifications

Passez à **Create commit** pour examiner l’arbre de travail. Si des changements sont présents, CodeBadger peut ouvrir cette vue automatiquement.

- Sélectionnez un fichier sous **Unstaged** pour examiner son diff.
- Utilisez le bouton plus pour indexer un fichier, ou **Stage all** pour indexer toutes les modifications non indexées.
- Cochez les fichiers voulus puis sélectionnez **Stage selected** pour n’indexer que ceux-ci.
- Les fichiers sous **Staged** seront inclus dans le prochain commit. Le bouton moins désindexe un fichier.
- **Undo selected** supprime de l’arbre de travail les modifications sélectionnées qui ne sont pas indexées. Une confirmation est demandée ; cette action est différente de la désindexation.

Avant de valider, vérifiez les fichiers indexés et leur diff. L’indexation détermine précisément les changements inclus dans le commit.

## Créer un commit

1. Indexez les fichiers à inclure.
2. Saisissez un titre de 72 caractères maximum ; il est obligatoire.
3. Ajoutez éventuellement une description.
4. **Generate with AI** est disponible si un fournisseur d’IA est configuré dans les paramètres et accessible. Vérifiez toujours le texte généré.
5. Sélectionnez **Commit**. Seules les modifications indexées sont validées.

## Push et pull

- **Pull** exécute `git pull --no-rebase` pour la branche actuelle.
- **Push** récupère d’abord les informations distantes. Si des commits locaux sont en retard et que l’arbre de travail est propre, CodeBadger tente un pull avant le push. En présence de modifications locales, l’opération est interrompue sans risque ; validez-les ou mettez-les de côté, puis réessayez.
- Push nécessite un upstream ou une remote par défaut non ambiguë. Sinon, CodeBadger explique pourquoi le push n’a pas été effectué.

Le module actualise l’état après une opération Git. En cas d’échec, consultez le message Git ; par exemple, il est impossible de pousser depuis une HEAD détachée.

## Travailler en toute sécurité

- Vérifiez les fichiers indexés avant le commit ; les fichiers non indexés n’y sont pas inclus.
- **Undo selected** supprime des modifications et ne peut pas être annulé dans le module Git. Vérifiez la sélection avant de confirmer.
- L’extraction d’un commit et le revert sont bloqués si l’arbre de travail contient des modifications.
- Un pull, merge, cherry-pick ou revert peut provoquer des conflits. Résolvez-les avant de continuer.
