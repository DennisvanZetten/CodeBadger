# Agents et configuration des projets

CodeBadger peut démarrer un terminal d’agent pour chaque dépôt actif. L’agent choisi doit être installé sur l’appareil et accessible via la commande attendue.

## Choisir un agent et vérifier sa disponibilité

Choisissez un agent pour chaque dépôt dans **Settings → Repositories** : OpenCode, Claude Code, Codex ou Hermes. Si CodeBadger ne trouve pas la commande, un avertissement apparaît et la session ne peut pas démarrer. Installez l’agent ou choisissez-en un autre.

Les paramètres généraux comprennent également **Agent management**. Choisissez-y un agent pour afficher son fichier de configuration global et les serveurs MCP détectés. Le chemin et l’état de disponibilité indiquent la configuration utilisée.

## Serveurs MCP

Les serveurs MCP relient un agent à des outils ou sources de données externes. CodeBadger peut afficher les serveurs détectés et, si la fonctionnalité est prise en charge, modifier leur état d’activation.

- **Agent management** gère la configuration globale de l’agent.
- La configuration du dépôt gère les serveurs propres au projet. Le format varie selon l’agent : JSON pour OpenCode et Claude Code, TOML pour Codex, YAML pour Hermes.
- Un dépôt peut choisir un serveur MCP spécifique pour les flux de tickets. Ce serveur doit être disponible et activé dans la configuration de projet correspondante.
- Claude Code stocke les définitions MCP dans `.mcp.json` et leur état d’activation pour ce dépôt dans `.claude/settings.local.json`.
- La configuration de projet Codex est stockée dans `.codex/config.toml`. Codex ne charge les paramètres de projet que pour les projets approuvés.
- OpenCode lit la configuration du projet dans `opencode.json` ou `.opencode/opencode.json`.

Avant toute modification d’un fichier de configuration existant, CodeBadger crée une sauvegarde horodatée. La configuration n’est modifiable que si l’agent est disponible. Vérifiez attentivement le JSON, TOML ou YAML personnalisé avant de l’enregistrer.

## Compétences du projet

Les fenêtres de configuration de projet OpenCode et Codex comportent un onglet de compétences. Vous pouvez sélectionner une compétence, modifier son contenu Markdown, l’enregistrer ou en créer une. Les compétences Codex se trouvent sous `.agents/skills` et celles d’OpenCode sous `.opencode/skills`. Utilisez des minuscules, chiffres et tirets pour les nouveaux identifiants. Les compétences fournissent des instructions de projet réutilisables ; elles ne remplacent pas la configuration générale de l’agent.

## Claude Code et configuration du dépôt

Les paramètres de projet Claude Code sont accessibles pour chaque dépôt via le bouton près du choix de l’agent. Vous pouvez y gérer la configuration MCP et afficher ou modifier la configuration JSON. Si l’agent n’est pas disponible, la configuration est en lecture seule.

## Sécurité et vérification

- Les paramètres globaux d’un agent peuvent affecter plusieurs dépôts ; modifiez-les avec précaution.
- La configuration d’un projet peut être enregistrée dans Git. Vérifiez les paramètres et définitions de serveurs que vous validez.
- Ne placez pas de clés API ni d’autres secrets dans les fichiers de configuration ou les compétences.
- L’activation d’un serveur MCP peut donner à l’agent accès à des actions ou données externes. N’activez que les serveurs de confiance.
