# AI- en applicatie-instellingen

Open **Instellingen** vanuit de applicatie om algemene opties, AI-commits, meldingen en sandboxlimieten te beheren. De meeste instellingen worden lokaal op dit apparaat bewaard.

## AI voor commitberichten

Onder **AI provider** stel je de provider en het model in voor het genereren van commitberichten vanuit de Git-module.

1. Kies een provider, bijvoorbeeld OpenAI, Azure OpenAI, Anthropic, Google Gemini, Ollama of Custom URL.
2. Vul waar nodig de provider-URL en API-sleutel in. API-sleutels worden veilig op het apparaat opgeslagen.
3. Test de verbinding en laad de beschikbare modellen.
4. Kies een model en stel desgewenst het maximale invoertokenbudget en aangepaste instructies in.

Voorbeelden van aangepaste instructies zijn een vaste commitconventie of taal. AI-generatie is optioneel: je kunt altijd zelf een commitbericht schrijven. Controleer een gegenereerde titel en beschrijving voordat je commit.

## Meldingen

Onder **Notifications** kun je geluiden bij sessievoltooiing en agentvragen instellen en Slack-configuratie beheren. Slack-tokens worden veilig opgeslagen. Zie de aparte handleiding **Slack-meldingen** voor de apprechten, verbinding en commando's.

## Sandboxlimieten

Onder **Sandbox** stel je de maximale grootte per geselecteerd bestand en de maximale totale selectie in. Deze limieten gelden voor nieuwe sandboxsessies. Overschrijdt de expliciete bestandsselectie een van de limieten, dan kan de sandbox automatisch Direct-modus gebruiken. In die modus zijn wijzigingen meteen zichtbaar op de echte geselecteerde bestanden. Zie **OpenCode-sandbox** voor uitleg en veiligheidswaarschuwingen.

## Overige instellingen

- **Task branch prefix:** wordt gebruikt voor branches die bij taken worden aangemaakt.
- **Language:** kies Engels, Nederlands, Duits of Frans. Handleidingen en interface wisselen mee waar vertalingen beschikbaar zijn.
- **Theme:** kies een beschikbaar licht of donker thema.
- **Toast duration:** stel in hoelang statusmeldingen zichtbaar blijven.
- **Completion sound** en **Question sound:** kies geluiden en schakel ze afzonderlijk in of uit. Voorvertoningen worden afgespeeld wanneer je een geluid kiest.

Klik op **Save** om algemene instellingen op te slaan. API-sleutels blijven in de beveiligde opslag van het apparaat en horen niet in projectbestanden of commits.
