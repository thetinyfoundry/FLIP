[English](README.md) · **Italiano**

# FLIP

[![Versione](https://img.shields.io/badge/versione-v1.0.2-FF7A18?style=flat-square)](https://github.com/thetinyfoundry/FLIP/releases/tag/v1.0.2)
![Piattaforma](https://img.shields.io/badge/Windows-10%20%2F%2011%20x64-1A1C23?style=flat-square)
[![CI](https://img.shields.io/github/actions/workflow/status/thetinyfoundry/FLIP/ci.yml?branch=main&style=flat-square&label=CI)](https://github.com/thetinyfoundry/FLIP/actions/workflows/ci.yml)
[![Download](https://img.shields.io/github/downloads/thetinyfoundry/FLIP/total?style=flat-square&label=download&color=FF7A18)](https://github.com/thetinyfoundry/FLIP/releases)
[![Licenza](https://img.shields.io/badge/licenza-GPL--3.0--only-2ECC71?style=flat-square)](LICENSE)
[![Buy Me a Coffee](https://img.shields.io/badge/Buy%20Me%20a%20Coffee-Support-FFDD00?style=flat-square&logo=buymeacoffee&logoColor=000000)](https://www.buymeacoffee.com/1berimHY9M)

**Trascina, premi un tasto, scegli il formato.** FLIP converte i file sul tuo PC Windows. Trascina uno o più file da Esplora file o dal Desktop, premi **Freccia giù** (o il tasto configurato) e rilascia sul formato nella ruota. La copia convertita appare accanto all'originale.

![Ruota di conversione di FLIP](docs/assets/wheel.png)

[Video dimostrativo](docs/assets/flip-demo.mp4) · [Note di rilascio](https://github.com/thetinyfoundry/FLIP/releases/tag/v1.0.2) · [Registro delle modifiche](CHANGELOG.md) · [Guida Windows](docs/WINDOWS-INSTALL.md) · [Privacy](PRIVACY.md)

## Installa

### Microsoft Store (consigliato)

<a href="https://apps.microsoft.com/detail/9ND9SDDBWW0W"><img src="https://get.microsoft.com/images/it%20dark.svg" width="200" alt="Scarica da Microsoft Store"></a>

Firmato da Microsoft, con .NET, FFmpeg e Whisper inclusi. Si aggiorna dallo Store. Sceglilo se Smart App Control blocca lo zip.

### Zip da GitHub

1. Installa il [.NET 10 Windows Desktop Runtime x64](https://dotnet.microsoft.com/download/dotnet/10.0) su Windows 10 o 11 x64.
2. Scarica [`Flip-1.0.2-win-x64.zip`](https://github.com/thetinyfoundry/FLIP/releases/download/v1.0.2/Flip-1.0.2-win-x64.zip) ed estrai tutto l'archivio.
3. Chiudi FLIP ed esegui `Installa-FLIP.cmd` dalla cartella estratta. Il programma verifica il pacchetto e lo copia in `%LOCALAPPDATA%\Programs\Flip`.

**Usi già FLIP?** Dalla 0.6.3 aggiorna da **Impostazioni → Guida & Scorciatoie → Aggiornamenti**. FLIP contatta GitHub solo quando premi il pulsante. Con una versione precedente esegui `Installa-FLIP.cmd` dal nuovo zip. Per passare allo Store, esegui prima `Disinstalla-FLIP.cmd` dal nuovo zip. Al primo avvio FLIP Converter riprende le tue impostazioni e riusa i motori e i modelli che hai già. Conserva la cartella `%AppData%\FLIP`.

Le due edizioni hanno lo stesso numero di versione ed escono insieme.

## Novità della 1.0

- **FLIP è su Microsoft Store**, con installazione in un clic e voce nel menu di Windows 11.
- **Excel in PDF senza altri programmi.** XLSX, XLSM, XLTX, XLS e ODS, con tutti i fogli visibili, date, valute e celle unite.
- **PDF migliori.** Unione e rimozione dei metadati conservano il testo. Le pagine convertite in immagini mantengono le proporzioni.
- **Dati intatti.** Accenti, CSV con punto e virgola, codici lunghi e date di Excel restano corretti.
- **Video e audio.** MP4 più compatibili, dimensione massima calcolata sulla durata reale, nessun limite di 20 minuti.
- **Stessi motori nelle due edizioni.** Passando allo Store conservi il modello Whisper, quello per rimuovere gli sfondi e Pandoc.
- **Più affidabile.** Impostazioni protette, originali ripristinati da Annulla, cartelle automatiche corrette e meno memoria usata.

[Tutte le correzioni](CHANGELOG.md)

## Cosa puoi fare

- **Ruota e aura.** La ruota segue il cursore tra i monitor. Premi **Tab** o usa la rotellina per passare dai formati agli strumenti, anche quando la ruota è stata aperta con la pressione prolungata. Una selezione mista si può suddividere per tipo di file.
- **Anteprima rapida.** Premi **Spazio** durante il trascinamento. Esplora i modelli 3D con vista orbitale e tavola a quattro viste, consulta la palette delle immagini e visualizza testo e forme d'onda.
- **Apertura con pressione prolungata.** Attiva l'opzione in *Attivazione & Input* e tieni premuto il click su un file selezionato in Esplora File o sul Desktop (default 3 secondi, regolabile) per aprire la ruota senza premere alcun tasto. Dopo mezzo secondo appare un piccolo anello progressivo; per uscire dalla ruota premi il tasto destro del mouse o Esc.
- **Strumenti rapidi.** Ritaglia, ruota, ridimensiona, sfoca aree sensibili, unisci PDF, rimuovi sfondi, estrai testo con OCR e raccogli i risultati in ZIP o, se WinRAR è installato, in RAR. Le cartelle selezionate diventano archivi accanto alla cartella originale.
- **Motori facoltativi.** Se un formato selezionato richiede un motore non installato, FLIP chiede se installarlo prima di avviare la conversione.
- **PDF per l'archiviazione e trascrizione senza connessione.** Converti i file compatibili in PDF/A-2b. **Unisci PDF** conserva dimensione e orientamento originali di ogni pagina; con **A** puoi scegliere Originale, A4 o A3, verticale o orizzontale. Con il motore facoltativo Whisper puoi trascrivere audio e video in TXT o SRT sul tuo PC.
- **Cartelle automatiche, preset e accessibilità.** Dall'Hub puoi configurare la ruota, le destinazioni, i motori facoltativi, le cartelle monitorate e le ricette. Chi usa un dispositivo Tobii può abilitare la selezione con lo sguardo.

## Formati e motori

| Categoria | Esempi | Motore |
| --- | --- | --- |
| Immagini | PNG, JPEG, WebP, BMP, TIFF, GIF, ICO | Integrato |
| PDF e documenti | PDF/A-2b, pagine PDF come immagini, estrazione testo, DOCX, ODT, Markdown | Integrato, Pandoc o LibreOffice secondo la conversione |
| Dati strutturati | JSON, JSONL, YAML, TOML, XML, CSV, TSV, INI | Integrato |
| Fogli di calcolo | XLSX, XLSM, XLTX, XLS, ODS in PDF, CSV, JSON e altri | Integrato (LibreOffice preferito per il PDF se installato) |
| 3D e vettoriale | OBJ, FBX, STL, PLY, GLB, GLTF, 3MF, DAE, SVG, DXF | Integrato |
| Archivi | ZIP, TAR, GZ, TGZ, CBZ, 7Z | Integrato |
| Audio e video | MP3, WAV, FLAC, MP4, MKV, WebM, trascrizioni TXT e SRT | FFmpeg o Whisper, inclusi nell'edizione Store |

I formati di uscita dipendono dai file selezionati e dai motori installati. La conversione avviene sul tuo PC. FLIP elabora i file in una cartella temporanea e salva i risultati nella destinazione configurata.

## Compilazione da sorgente

Installa il [.NET 10 SDK x64](https://dotnet.microsoft.com/download/dotnet/10.0).

```powershell
dotnet build src/Flip/Flip.csproj -c Release -warnaserror
dotnet run --project src/Flip/Flip.csproj -c Release --no-build -- --test-input
dotnet run --project src/Flip/Flip.csproj -c Release --no-build -- --test-conversions
```

[Architettura](ARCHITECTURE.md) · [Come contribuire](CONTRIBUTING.md)

## Contributi

Grazie a [@AlphaGolf-sec](https://github.com/AlphaGolf-sec) per la correzione del secondo avvio nella v0.6.1 e per gli aggiornamenti dall'app nella v0.6.3, e a [@giovamolinaro](https://github.com/giovamolinaro) per la correzione YAML, XML e CSV nella v0.6.2.

## Supporto

<a href="https://www.buymeacoffee.com/1berimHY9M"><img src="https://cdn.buymeacoffee.com/buttons/v2/default-yellow.png" height="44" alt="Offrimi un caffè"></a>

## Licenza

Dalla v0.6.0, il codice originale e le risorse di FLIP sono distribuiti con licenza [GNU GPLv3](LICENSE) (`GPL-3.0-only`). Copyright © 2026 The Tiny Foundry / Raffaele Acanfora. I [componenti di terze parti](THIRD_PARTY_NOTICES.md) conservano le rispettive licenze.
