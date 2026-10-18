# HandBrake Treemap

Een WinDirStat-achtige desktop-tool om **volledige mappen in één keer** om te zetten naar
`.mkv` met HandBrakeCLI, en pas daarna — na jouw controle — de originelen op te ruimen.

## Hoe het werkt

1. **Scan** een mediamap. Je krijgt een mappenboom (zoals WinDirStat) met per map:
   - **Om te zetten** — som van alle video's die nog géén `.mkv` hebben (bestaande `.mkv`'s tellen niet mee)
   - **Aandeel** — balkje: deel van de bovenliggende map
   - **#** — aantal bestanden dat nog moet
   - **Bespaard** — origineel − mkv voor wat al omgezet is
   Mappen waar alles al `.mkv` is, verschijnen niet. Volledig omgezette mappen worden groen.
2. Selecteer een map (of de hoofdmap) → **Zet geselecteerde map om**. Alle bestanden in die
   map *en submappen* worden na elkaar omgezet. Je kunt altijd **Stop** drukken.
   - Het originele bestand blijft **altijd** staan.
   - HandBrake schrijft eerst naar `naam.hbtmp.mkv`; pas bij succes wordt dat `naam.mkv`.
     Een onderbroken omzetting laat dus nooit een half bestand achter dat "klaar" lijkt.
3. **Controleer**:
   - **Controleer omzettingen** vergelijkt de speelduur van origineel en `.mkv` (via `HandBrakeCLI --scan`).
   - Selecteer een bestand rechts → **Speel origineel** / **Speel .mkv** / **Toon in Verkenner**
     (of dubbelklik) om zelf te kijken.
   - **Min. besparing (%)** (standaard 5%): spaart een omzetting minder, dan wordt de nieuwe `.mkv`
     meteen weggegooid en het origineel hernoemd naar `naam.nomkv.ext`. Het origineel blijft dus behouden.
   - Bestanden met **`.nomkv.`** in de naam worden altijd overgeslagen en tellen niet mee als "om te zetten".
4. **Verwijder originelen…** verwijdert de originelen van omgezette bestanden in de geselecteerde map.
   - Standaard alleen bestanden die de duurcontrole doorstaan hebben (**Alleen gecontroleerde**).
   - Op Windows standaard **naar de prullenbak** (let op: op netwerkschijven is er geen prullenbak).
   - Je krijgt eerst een bevestiging met origineel / mkv / bespaard.
   - Omgezette bestanden die (met de huidige drempel) te weinig besparen: daar wordt de `.mkv`
     verwijderd en het origineel behouden als `*.nomkv.*`.
   - Vooraf én vlak voor elk origineel wordt gecontroleerd of de `.mkv` nog bestaat (en niet leeg is).
     Is de `.mkv` verdwenen, dan blijft het origineel staan en komt het terug op "te doen".
5. **Verwijder omgezette (.mkv)…** doet het omgekeerde: verwijdert de `.mkv`-bestanden in de geselecteerde
   map en houdt de originelen, die terug op "te doen" komen (bv. om opnieuw om te zetten met een andere preset).
   Een `.mkv` waarvan het origineel ontbreekt, wordt nooit verwijderd.
6. Onderaan zie je hoeveel GB je deze sessie bespaard hebt, en het totaal voor de hele scan.

De status komt volledig van de schijf: `film.mp4` met een `film.mkv` ernaast = omgezet.
Je kunt de app dus gerust sluiten en later opnieuw scannen om verder te gaan of op te ruimen.
Hebben twee video's dezelfde naam (`film.avi` + `film.mp4`), dan wordt het doel `film.avi.mkv`
en `film.mp4.mkv` om botsingen te vermijden.

## Vereisten

1. **.NET 8 SDK** (Visual Studio 2022 17.8 of nieuwer bevat die)
2. **HandBrakeCLI** (de command-line versie, apart van de GUI):
   - Windows: `winget install HandBrake.HandBrake.CLI` of https://handbrake.fr/downloads2.php
   - Debian/Ubuntu: `sudo apt install handbrake-cli` · macOS: `brew install handbrake`

De tool zoekt `HandBrakeCLI` op `PATH` en de gebruikelijke plaatsen; anders vul je het pad bovenaan in.

## Log en instellingen

- Onderaan staat een **log** met alles wat de tool doet (HandBrakeCLI-aanroepen, fouten, verwijderde
  bestanden, …). Alles wordt ook bewaard in `%LOCALAPPDATA%\HandBrakeTreemap\log.txt`.
- Instellingen (pad naar HandBrakeCLI, gekozen preset, preset-bestand, laatste map, drempel, vinkjes)
  worden bewaard in het register onder `HKEY_CURRENT_USER\Software\HandBrakeTreemap`.
- Eigen presets worden gezocht in `%APPDATA%\HandBrake\presets.json` van de huidige gebruiker én van
  de andere Windows-gebruikers op de pc (o.a. `C:\Users\Backup\...`). Heeft je account geen leesrechten
  op die map, dan staat dat in de log: kopieer het bestand dan, of exporteer de presets in HandBrake.

## Bouwen

Open `HandBrakeTreemap.sln` in Visual Studio 2022 en druk F5, of:

```bash
dotnet run -c Release
dotnet publish -c Release -r win-x64 --self-contained -p:PublishSingleFile=true
```

## Projectstructuur

```
Program.cs, App.axaml(.cs)      startpunt + thema
Models/FolderNode.cs            map in de boom + recursieve sommen
Models/VideoFile.cs             origineel ↔ .mkv, status, controle, formattering
Services/FolderScanner.cs       scannen + koppelen origineel/mkv
Services/HandBrakeRunner.cs     HandBrakeCLI: omzetten, voortgang, duur uitlezen
Services/FileOps.cs             verwijderen (prullenbak), openen in Verkenner/speler
Services/PresetCatalog.cs       ingebouwde presetnamen
ViewModels/MainViewModel.cs     alle logica en state
Views/MainWindow.axaml(.cs)     layout, knoppen, bevestigingsdialoog
```
