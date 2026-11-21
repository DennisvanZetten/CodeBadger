# Agenten en projectconfiguratie

CodeBadger kan een agentterminal starten voor iedere actieve repository. De geselecteerde agent moet op het apparaat zijn geïnstalleerd en beschikbaar zijn via het verwachte commando.

## Agent kiezen en beschikbaarheid controleren

Kies per repository in **Instellingen → Repositories** een agent: OpenCode, Claude Code, Codex of Hermes. Als CodeBadger de opdracht niet vindt, verschijnt een waarschuwing en kan de sessie niet worden gestart. Installeer de agent of kies een andere.

CodeBadger heeft daarnaast **Agent management** in de algemene instellingen. Kies daar een agent om diens globale configuratiebestand en gevonden MCP-servers te bekijken. De weergegeven pad- en beschikbaarheidsstatus helpt vaststellen welke configuratie wordt gebruikt.

## MCP-servers

MCP-servers verbinden een agent met externe tools of gegevensbronnen. CodeBadger kan gevonden servers tonen en, waar ondersteund, hun ingeschakelde status aanpassen.

- In algemene **Agent management** beheer je de globale agentconfiguratie.
- Bij repositoryconfiguratie beheer je projectgebonden servers. De configuratievorm verschilt per agent: OpenCode en Claude Code gebruiken JSON-configuratie; Codex gebruikt TOML; Hermes gebruikt YAML.
- Een repository kan voor ticketworkflows een specifieke MCP-server kiezen. Die server moet in de bijbehorende projectconfiguratie beschikbaar en ingeschakeld zijn.
- Voor Claude Code worden MCP-definities in `.mcp.json` opgeslagen en de ingeschakelde status voor deze repository in `.claude/settings.local.json`.
- Voor Codex staat de projectconfiguratie onder `.codex/config.toml`. Codex laadt projectinstellingen alleen voor vertrouwde projecten.
- Voor OpenCode worden projectinstellingen gelezen uit `opencode.json` of `.opencode/opencode.json`.

Voor wijzigingen aan bestaande configuratiebestanden maakt CodeBadger eerst een tijdgestempelde back-up. Configuratie is alleen bewerkbaar wanneer de agent beschikbaar is. Controleer aangepaste JSON-, TOML- of YAML-inhoud zorgvuldig voordat je opslaat.

## Projectskills

De OpenCode- en Codex-projectconfiguratievensters bieden een tab voor projectskills. Je kunt een skill selecteren, de Markdown-inhoud aanpassen, opslaan of een nieuwe skill maken. Codex-skills staan onder `.agents/skills`; OpenCode-skills onder `.opencode/skills`. Gebruik voor nieuwe skill-ID's kleine letters, cijfers en streepjes. Skills geven de agent herbruikbare projectinstructies; ze vervangen geen algemene agentconfiguratie.

## Claude Code en repositoryconfiguratie

Claude Code-projectinstellingen zijn per repository beschikbaar via de instellingenknop naast de agentkeuze. Daar beheer je de MCP-configuratie en kun je de JSON-configuratie bekijken of aanpassen. Als de agent ontbreekt, wordt de configuratie alleen-lezen.

## Beveiliging en controle

- Globale agentinstellingen kunnen invloed hebben op meer dan één repository; pas ze met beleid aan.
- Projectconfiguratie kan in Git worden opgenomen. Controleer welke instellingen en serverdefinities je commit.
- Deel geen API-sleutels of andere geheimen in configuratiebestanden of skills.
- Een MCP-server inschakelen geeft de agent mogelijk toegang tot externe acties of data. Schakel alleen servers in die je vertrouwt.
