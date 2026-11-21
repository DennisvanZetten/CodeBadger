# Sandbox OpenCode

Le sandbox OpenCode démarre OpenCode pour un dépôt dans un environnement isolé. Au lieu de partager automatiquement tout le dépôt, vous sélectionnez explicitement les fichiers accessibles. Pour un dépôt OpenCode, ouvrez **Permissions** et **Settings** sous **Settings → Repositories**.

## Choisir un modèle et des fichiers

Ouvrez **Settings** dans les options sandbox du dépôt OpenCode.

1. Choisissez le modèle des nouvelles sessions sandbox.
2. Sélectionnez les fichiers et dossiers nécessaires à OpenCode. La sélection d’un dossier inclut les fichiers qu’il contient.
3. Enregistrez la sélection. Les chemins sensibles et les liens ne sont pas proposés.

Les limites générales se trouvent sous **Settings → Sandbox**. Les valeurs par défaut sont de 64 Mo par fichier et 1 024 Mo au total ; les plages configurables sont de 1 à 512 Mo par fichier et de 1 à 2 048 Mo au total. Ces limites s’appliquent aux nouvelles sessions.

## Deux modes d’accès

Au démarrage, CodeBadger mesure les fichiers explicitement sélectionnés :

- **Mode copie/révision :** si la sélection respecte les deux limites de taille, les fichiers choisis sont copiés dans un espace de travail temporaire et isolé. Le sandbox ne dispose pas d’un accès réseau général.
- **Mode direct :** si un fichier ou la sélection totale dépasse une limite, CodeBadger utilise un accès temporaire et restreint aux fichiers réels sélectionnés. Les modifications apparaissent immédiatement dans le dépôt ; la création, la suppression et le renommage de fichiers sont bloqués. Ce mode ne crée pas de copie de révision.

Le mode est choisi automatiquement. Des limites plus élevées peuvent augmenter l’utilisation du disque et de la mémoire. Limitez la sélection aux fichiers nécessaires à la tâche.

## Arrêter la session et examiner les modifications

Après le travail, arrêtez le sandbox avec **Stop sandbox**. En mode copie/révision, les modifications sont capturées pour examen. La vue de révision affiche les fichiers modifiés et leur contenu ; les suppressions peuvent être affichées mais ne peuvent pas être appliquées.

1. Examinez le contenu proposé pour chaque fichier.
2. Sélectionnez uniquement les modifications à appliquer.
3. Choisissez **Apply selected modifications**.

CodeBadger n’applique que les modifications sélectionnées aux fichiers existants et sauvegarde les originaux. Une vérification des conflits est effectuée ; si un fichier original a changé entre-temps, l’application peut être refusée. L’application de plusieurs fichiers n’est pas une transaction atomique. Vérifiez soigneusement les sauvegardes et les résultats. Les changements non appliqués restent disponibles jusqu’à leur application ou leur suppression explicite. La révision doit être terminée ou supprimée avant de démarrer une autre session sandbox.

En mode direct, les modifications sont écrites immédiatement dans les fichiers réels sélectionnés. L’arrêt ne crée pas de copie de révision pour ce mode.

## Autorisations et accès réseau

Les autorisations du sandbox sont propres au dépôt. La fenêtre des autorisations affiche les origines enregistrées. Révoquer une autorisation arrête la session sandbox associée ; les clés API de l’hôte ne sont pas affichées.

L’accès réseau est limité par défaut. Si le sandbox demande un accès nécessitant une approbation, examinez la demande et ne l’autorisez que si l’hôte et la destination sont fiables. N’exposez pas de secrets ni de fichiers du projet qui ne sont pas destinés à la session.

## Limites importantes

- La sélection des fichiers ne signifie pas que leur contenu a été vérifié pour détecter des secrets. Examinez ce que vous partagez ; leur détection n’est pas garantie.
- Les points de réanalyse, liens, chemins non sûrs et sélections trop volumineuses sont refusés.
- Le sandbox est disponible sous Windows et destiné à OpenCode.
- En mode direct, les actions affectent immédiatement les fichiers réels sélectionnés. Vérifiez l’avertissement de mode avant de continuer.
