# Flip — guida italiana

## Scarica e installa

Scarica lo ZIP Windows x64 da [Releases](https://github.com/thetinyfoundry/FLIP/releases), estrai l'intero archivio e avvia `Install-FLIP.cmd`. Serve il runtime .NET 10 Windows Desktop x64; non serve compilare il codice. La guida `README.txt` nel pacchetto spiega prerequisiti e motori documentali facoltativi. La versione attuale conserva il numero 0.4.0-beta.3.

[Stato della pubblicazione e verifiche](docs/PUBLICATION-STATUS.md).

Flip è una beta per Windows: apri la ruota con Freccia giù (o il tasto che scegli nelle impostazioni) e rilasci il file su un formato. La copia viene salvata accanto all'originale, senza sovrascriverlo.

## Sul PC di sviluppo
Avvia Flip dall'icona sul Desktop o cercandolo in Start. Rimane nell'area di notifica.
Tasto destro sull'icona vicino all'orologio → **Impostazioni…**: scegli una freccia, Spazio, `<>`, oppure registra un altro tasto singolo. Dal menu puoi anche disattivare l'avvio con Windows, silenziare il tic o provarlo. Il tic della ruota resta attivo per default; i suoni di Windows non vengono modificati.
Sono supportati entrambi gli ordini: trascina e poi premi il tasto, oppure tieni premuto il tasto e inizia a trascinare. Quando appare la ruota puoi rilasciare il tasto. Durante il gesto da Explorer/Desktop il comando non viene passato anche a Explorer. Fuori dal gesto il tasto conserva la funzione normale. Evita Ctrl e Shift: restano modificatori di Windows. Fuori dalla ruota torna il drag/drop normale; premi Esc prima di rilasciare il mouse per annullare.

Per installare da sorgente: `.\scripts\Build.ps1`, poi `.\scripts\Install.ps1`. L'installer crea collegamenti Desktop/Start e abilita l'avvio con Windows per default; `-NoShortcuts` e `-NoStartup` permettono di escluderli. Servono .NET 10 SDK x64 per compilare e Desktop Runtime 10 x64 per eseguire un pacchetto senza SDK. Installazione per utente, senza servizio amministratore. [Guida completa](docs/WINDOWS-INSTALL.md).

La ruota mostra tutte le uscite disponibili. Con Pandoc, un TXT offre PDF, DOCX, EPUB, MD, HTML, ODT, RTF, PNG, JPG, WEBP, FB2 e RST.
Pages non è un'uscita implementata con gli strumenti Windows scelti. Audio/video e OCR non sono inclusi.

## Cosa significa pubblicare la beta
La repo pubblica serve a condividere il codice, ricevere segnalazioni e permettere ad altri di compilarlo. Un archivio dei sorgenti non è un installer.
L'app è offline, ma la prima compilazione e il download facoltativo del motore documenti richiedono Internet.

Prima della pubblicazione:
1. Leggere PUBLIC_RELEASE_REVIEW.md e chiudere i blocchi della release.
2. Verificare file tracciati e storico Git, inclusi autore/email, immagini e audio.
3. Pubblicare soltanto l'esportazione dei file Git approvati. Non comprimere tutta la cartella di lavoro.
4. Per distribuire anche gli eseguibili, preparare licenze/obblighi dei componenti e collaudo su una macchina pulita.

I prototipi precedenti, i backup e le prove sono in _local e non vengono esportati. App, cache NuGet e strumenti scaricati restano esclusi.
I log runtime sono in %LOCALAPPDATA%\Flip\logs e possono contenere percorsi dei documenti: non vanno pubblicati senza ripulirli.
GitHub lascia visibili account, commit e metadati: nessuno può promettere “nessuna traccia” in assoluto.

LibreOffice è stato aggiornato, con autorizzazione esplicita, dalla versione 24.2.5.2 alla 26.8.1. Checksum e firma dell'installer ufficiale sono stati verificati. I test hanno trovato e corretto un problema con i percorsi lunghi dei profili temporanei: 99 controlli di conversione ora passano. Windows richiede ancora un riavvio; non è stato eseguito automaticamente. Prima degli eseguibili pubblici restano il collaudo dopo il riavvio, le prove native e la preparazione delle licenze del pacchetto.

Per build, test, integrazione Windows e rimozione, usare le istruzioni complete in README.md.
