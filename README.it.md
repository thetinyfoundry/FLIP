# Flip — Guida in Italiano

Converti qualsiasi file direttamente da Esplora file. Trascinalo, premi **Freccia giù**, e rilascialo sul formato desiderato nella ruota. Flip salverà una nuova copia convertita accanto all'originale, senza mai sovrascriverla.

Flip è un convertitore locale per Windows x64. La versione attuale è la **0.5.0-beta.1**.

---

## 🌟 Novità della versione 0.5.0-beta.1

- 🎬 **Conversioni Audio & Video (con FFmpeg)**:
  - Formati audio: `MP3`, `WAV`, `FLAC`, `OGG`, `M4A`, `AAC`.
  - Formati video: `MP4`, `MKV`, `WEBM`, `AVI`, `MOV`.
  - **Estrazione rapida della traccia audio**: trascina un file video (es. MP4) sulla ruota e rilascialo su `MP3` o `WAV` per estrarre istantaneamente l'audio.
- 📊 **Dati strutturati nativi (senza dipendenze esterne)**:
  - Conversione bidirezionale tra `JSON`, `YAML`, `XML`, `CSV`.
  - Esportazione diretta in `PDF`, `HTML`, `Markdown` e `TXT`.
- ⏱️ **Barra di caricamento radiale animata**:
  - La ruota rimane aperta durante le conversioni più lunghe (es. video o documenti pesanti), visualizzando un arco di avanzamento circolare, un raggio rotante e la percentuale in tempo reale, con una spunta verde finale al completamento.
  - È possibile annullare la conversione in qualsiasi momento premendo **Esc**.
- 🔔 **Verifica aggiornamenti integrata**:
  - Tasto destro sull'icona di Flip nell'area di notifica $\rightarrow$ *"Verifica aggiornamenti su GitHub…"* per controllare istantaneamente la presenza di nuove release.
- 🔊 **Feedback sonoro tattile migliorato**:
  - Ripristinato il ticchettio secco originale della ruota con riproduzione nativa WASAPI/Media Foundation, compatibile con tutti gli output audio (cuffie Bluetooth, USB, monitor HDMI).

---

## Scarica e installa

**[Scarica FLIP 0.5.0-beta.1 per Windows x64 (ZIP)](https://github.com/thetinyfoundry/FLIP/releases/download/v0.5.0-beta.1/Flip-0.5.0-beta.1-win-x64.zip)** · [Note di rilascio e checksum SHA256](https://github.com/thetinyfoundry/FLIP/releases/tag/v0.5.0-beta.1)

1. Scarica l'archivio ZIP da [GitHub Releases](https://github.com/thetinyfoundry/FLIP/releases) ed estrailo in una cartella.
2. Installa il [.NET 10 Windows Desktop Runtime x64](https://dotnet.microsoft.com/download/dotnet/10.0) se non è già presente sul sistema.
3. Fai doppio clic su `Install-FLIP.cmd` (oppure esegui `.\scripts\Install.ps1`). L'app viene installata in `%LOCALAPPDATA%\Programs\Flip` e si avvia automaticamente.

### Motori aggiuntivi facoltativi:
- **Audio & Video (FFmpeg)**: Flip rileva automaticamente FFmpeg se installato nel sistema (ad esempio tramite `winget install Gyan.FFmpeg`) o inserito in `%LOCALAPPDATA%\Flip\tools\ffmpeg.exe`.
- **Documenti avanzati (Pandoc & LibreOffice)**: chiudi Flip ed esegui `Install-DocumentEngine.ps1` per scaricare automaticamente l'eseguibile ufficiale di Pandoc verificandone l'hash SHA256. LibreOffice può essere installato a parte per il rendering di PDF e formati Office.

---

## Come usare la ruota

1. Seleziona uno o più file in Esplora file o sul Desktop.
2. Inizia a trascinare e premi **Freccia giù** (o il tasto configurato nelle Impostazioni). Quando la ruota compare puoi rilasciare il tasto.
3. Porta il cursore sul settore del formato desiderato e rilascia il pulsante del mouse.
4. Per annullare prima del rilascio, rilascia il file fuori dalla ruota oppure premi **Esc**.

Tasto destro sull'icona vicino all'orologio $\rightarrow$ **Impostazioni…**: puoi personalizzare il tasto di attivazione (frecce, barra spaziatrice o qualsiasi tasto singolo della tastiera).

---

## Tabella formati supportati

| Categoria | Formati in ingresso | Formati in uscita | Motore |
| --- | --- | --- | --- |
| **Immagini** | PNG, JPG/JPEG, WebP, BMP, TIFF, GIF | PNG, JPG, WebP, PDF, BMP, TIFF, GIF | Nativo (SkiaSharp) |
| **Dati strutturati** | JSON, YAML/YML, XML, CSV | JSON, YAML, XML, CSV, TXT, HTML, Markdown, PDF | Nativo incorporato |
| **Audio** | MP3, WAV, FLAC, OGG, M4A, AAC, WMA, OPUS | WAV, MP3, FLAC, OGG, M4A, AAC | FFmpeg locale |
| **Video** | MP4, MKV, WebM, AVI, MOV, WMV, M4V | MP4, MKV, WebM, AVI, MOV, oltre ad estrazione audio MP3/WAV | FFmpeg locale |
| **Testo semplice** | TXT | PDF, PNG, JPG, WebP; con Pandoc anche DOCX, EPUB, Markdown, HTML, ODT, RTF, FB2, RST | Nativo / Pandoc |
| **Documenti** | Markdown, HTML, DOCX, EPUB, ODT, RTF, FB2, RST | Altri formati documento con Pandoc; PDF/immagini richiedono LibreOffice | Pandoc / LibreOffice |
| **PDF** | PDF | Pagine singole in PNG/JPG/WebP e testo estratto in TXT; formati documento con Pandoc | Nativo / Pandoc |

---

## Privacy e sicurezza locale

Tutte le conversioni avvengono **al 100% in locale** sul tuo computer: nessun dato, testo o file viene inviato su server esterni o servizi cloud.
Flip non sovrascrive mai i file esistenti (aggiunge `.converted` e un progressivo numerico se necessario).

[Contribuisci](CONTRIBUTING.md) · [Changelog](CHANGELOG.md) · [Architettura](ARCHITECTURE.md)
