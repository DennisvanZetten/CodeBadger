# Git-Modul

Das Git-Modul zeigt Branches, Commits und Dateiänderungen eines Repositorys. Öffnen Sie es über **Git overview** im Git-Bereich eines Repository-Tabs. Beim Öffnen ruft CodeBadger den Remote-Status ab.

## Übersicht und Status

Oben wird der aktuelle Branch angezeigt. **Pull** zeigt, wie viele Commits der Branch zurückliegt; **Push** zeigt die lokal vorausliegenden Commits. Der Status unten zeigt geänderte, gestagte und nicht gestagte Dateien sowie den Vorsprung/Rückstand gegenüber dem Upstream-Branch. Mit **Refresh** wird der Remote-Status erneut abgerufen.

Wenn der Branch zurückliegt, prüft Push den Remote-Status und versucht gegebenenfalls zuerst einen Pull – jedoch nur bei sauberem Working Tree. Bei nicht committeten Änderungen wird der Push abgebrochen, damit Sie diese zuerst committen oder stashen können. Ein Branch ohne Upstream kann beim Push einen Upstream erhalten, sofern es eine eindeutige Remote gibt.

## Branches und Commits

Die Ansicht **Branches & commits** zeigt lokale und Remote-Branches sowie die jüngste Commit-Historie.

- Wählen Sie einen Branch aus, um ihn auszuchecken. Der aktuelle Branch ist markiert.
- Branch-Namen mit `/` werden in Gruppen angezeigt.
- Wählen Sie einen Commit aus, um seine geänderten Dateien anzusehen. Wählen Sie eine Datei, um den Diff dieses Commits anzuzeigen.
- Doppelklicken Sie auf einen Commit, um zu einem damit verbundenen Branch zu wechseln.
- Mit einem Rechtsklick auf einen Commit öffnen Sie weitere Aktionen: dort einen Branch erstellen, den Commit detached auschecken, cherry-picken, revertieren, einen geeigneten Branch mergen oder Branch-Namen, Commit-Informationen bzw. SHA kopieren.
- Aktionen sind nur verfügbar, wenn Branch- und Working-Tree-Status dies sicher zulassen. Vor Checkout eines Commits und Revert müssen lokale Änderungen committet oder gestasht sein. Vor dem Cherry-Pick prüft das Modul, ob der Commit bereits auf dem aktuellen Branch liegt.

### Branch erstellen

Wählen Sie **New branch**, um bei `HEAD` zu beginnen, oder klicken Sie mit der rechten Maustaste auf einen Commit und wählen Sie **Create branch here**. Geben Sie einen gültigen Branch-Namen ein. Der neue Branch wird sofort erstellt und ausgecheckt.

### Konflikte

Wenn Git Konflikte meldet, zeigt das Modul die betroffenen Dateien. Mit **Compare in VS Code** können Sie eine Konfliktdatei dort öffnen und lösen. Schließen Sie anschließend den Merge oder Rebase entsprechend Ihrem Git-Ablauf ab.

## Änderungen prüfen und stagen

Wechseln Sie zu **Create commit**, um den Working Tree zu prüfen. Wenn Änderungen vorliegen, kann CodeBadger diese Ansicht beim Öffnen automatisch anzeigen.

- Wählen Sie unter **Unstaged** eine Datei, um ihren Diff anzusehen.
- Mit der Plus-Schaltfläche stagen Sie eine Datei; **Stage all** übernimmt alle nicht gestagten Änderungen.
- Wählen Sie Dateien über die Kontrollkästchen aus und klicken Sie auf **Stage selected**, um nur diese Dateien zu stagen.
- Dateien unter **Staged** werden in den nächsten Commit aufgenommen. Mit der Minus-Schaltfläche nehmen Sie eine Datei wieder aus dem Staging.
- **Undo selected** entfernt ausgewählte, nicht gestagte Änderungen aus dem Working Tree. Die Aktion muss bestätigt werden und ist nicht dasselbe wie Unstaging.

Prüfen Sie vor dem Commit die gestagten Dateien und ihre Diffs. Durch das Staging wird genau festgelegt, welche Änderungen in den Commit gelangen.

## Commit erstellen

1. Stagen Sie die Dateien, die in den Commit gehören.
2. Geben Sie einen Titel mit höchstens 72 Zeichen ein; er ist erforderlich.
3. Fügen Sie optional eine Beschreibung hinzu.
4. **Generate with AI** ist verfügbar, wenn in den Einstellungen ein KI-Anbieter eingerichtet und erreichbar ist. Prüfen Sie den erzeugten Text immer selbst.
5. Wählen Sie **Commit**. Es werden nur gestagte Änderungen committet.

## Push und Pull

- **Pull** führt für den aktuellen Branch `git pull --no-rebase` aus.
- **Push** ruft zuerst Remote-Informationen ab. Liegt der lokale Branch zurück und ist der Working Tree sauber, versucht CodeBadger zuerst einen Pull und danach den Push. Bei lokalen Änderungen wird der Vorgang sicher abgebrochen; committen oder stashen Sie diese und versuchen Sie es erneut.
- Push benötigt einen Upstream oder eine eindeutige Standard-Remote. Andernfalls erklärt CodeBadger, warum nicht gepusht wurde.

Nach einer Git-Operation aktualisiert das Modul den Status. Prüfen Sie bei Fehlern die Git-Meldung; von einem detached HEAD aus ist beispielsweise kein Push möglich.

## Sicher arbeiten

- Prüfen Sie vor dem Commit die gestagten Dateien; nicht gestagte Dateien werden nicht aufgenommen.
- **Undo selected** entfernt Änderungen und kann im Git-Modul nicht rückgängig gemacht werden. Prüfen Sie die Auswahl vor der Bestätigung.
- Checkout eines Commits und Revert sind bei Änderungen im Working Tree gesperrt.
- Bei Pull, Merge, Cherry-Pick oder Revert können Konflikte entstehen. Lösen Sie diese, bevor Sie fortfahren.
