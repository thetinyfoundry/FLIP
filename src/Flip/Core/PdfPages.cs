using System.IO;
using System.Runtime.InteropServices;
using System.Text;

namespace Flip.Core;

/// <summary>
/// Copia le pagine PDF così come sono (testo, vettori, immagini) con PDFium, la libreria già usata per le anteprime.
/// Disegnarle come immagini farebbe perdere testo selezionabile, qualità e formato pagina.
/// </summary>
internal static class PdfPages
{
    // PDFium non è thread-safe: PDFtoImage protegge ogni chiamata con questa stringa, internata dal runtime.
    private const string PdfiumLock = "e362349b-001d-4cb2-bf55-a71606a3e36f";

    /// <summary>Crea un PDF con tutte le pagine dei documenti indicati, nell'ordine dato.</summary>
    internal static void Combine(IReadOnlyList<byte[]> documents, string outputPath, bool withoutMetadata, CancellationToken token, Action<int>? copied = null)
    {
        byte[] result;
        // Forza il caricamento di PDFium da parte di PDFtoImage prima delle chiamate dirette.
        _ = documents.Count == 0 ? throw new IOException("Nessuna pagina da copiare.") : PDFtoImage.Conversion.GetPageCount(documents[0]);

        lock (PdfiumLock)
        {
            FPDF_InitLibrary();
            nint output = FPDF_CreateNewDocument();
            if (output == 0) throw new IOException("Impossibile creare il PDF.");
            try
            {
                for (int i = 0; i < documents.Count; i++)
                {
                    token.ThrowIfCancellationRequested();
                    var pin = GCHandle.Alloc(documents[i], GCHandleType.Pinned);
                    try
                    {
                        nint input = FPDF_LoadMemDocument64(pin.AddrOfPinnedObject(), (nuint)documents[i].Length, null);
                        if (input == 0)
                            throw new IOException(FPDF_GetLastError() == 4
                                ? Loc.T("Il PDF è protetto da password.", "The PDF is password protected.")
                                : Loc.T("PDF danneggiato o non leggibile.", "The PDF is damaged or unreadable."));
                        try
                        {
                            if (!FPDF_ImportPages(output, input, null, FPDF_GetPageCount(output)))
                                throw new IOException(Loc.T("Copia delle pagine PDF non riuscita.", "Copying the PDF pages failed."));
                        }
                        finally { FPDF_CloseDocument(input); }
                    }
                    finally { pin.Free(); }
                    copied?.Invoke(i + 1);
                }
                result = Save(output);
            }
            finally { FPDF_CloseDocument(output); }
        }

        if (withoutMetadata) BlankMetadata(result);
        using var stream = new FileStream(outputPath, FileMode.CreateNew, FileAccess.Write, FileShare.None);
        stream.Write(result);
        stream.Flush(true);
    }

    /// <summary>
    /// Risoluzione per convertire le pagine in immagini senza deformarle: lato lungo di circa 2400 pixel.
    /// Larghezza e altezza fisse, usate prima, schiacciavano ogni pagina in proporzione 3:4.
    /// </summary>
    internal static PDFtoImage.RenderOptions RenderOptions(byte[] pdf)
    {
        double longest = 1, largest = 1;
#pragma warning disable CA1416
        foreach (var size in PDFtoImage.Conversion.GetPageSizes(pdf))
        {
            longest = Math.Max(longest, Math.Max(size.Width, size.Height));
            largest = Math.Max(largest, (double)size.Width * size.Height);
        }
#pragma warning restore CA1416
        double dpi = Math.Clamp(72 * 2400 / longest, 110, 300);
        // Un manifesto enorme non deve esaurire la memoria.
        double pixels = largest * dpi * dpi / (72 * 72);
        if (pixels > 60_000_000) dpi *= Math.Sqrt(60_000_000 / pixels);
        return new(Dpi: Math.Max(8, (int)dpi));
    }

    private static byte[] Save(nint document)
    {
        using var buffer = new MemoryStream();
        WriteBlock write = (_, data, size) =>
        {
            try
            {
                byte[] block = new byte[checked((int)size)];
                Marshal.Copy(data, block, 0, block.Length);
                buffer.Write(block);
                return 1;
            }
            catch (Exception ex) when (ex is OverflowException or OutOfMemoryException or IOException) { return 0; }
        };
        var writer = new FileWrite { Version = 1, Write = Marshal.GetFunctionPointerForDelegate(write) };
        bool saved = FPDF_SaveAsCopy(document, ref writer, 2);
        GC.KeepAlive(write);
        if (!saved || buffer.Length == 0) throw new IOException(Loc.T("Salvataggio del PDF non riuscito.", "Saving the PDF failed."));
        return buffer.ToArray();
    }

    /// <summary>
    /// Il documento nuovo non eredita autore e titolo; restano solo data e programma scritti da PDFium
    /// e gli eventuali blocchi XMP delle singole pagine. Si svuotano a lunghezza invariata, così gli indici del file restano validi.
    /// </summary>
    private static void BlankMetadata(byte[] pdf)
    {
        BlankBetween(pdf, "<?xpacket begin", "<?xpacket end");
        BlankBetween(pdf, "<x:xmpmeta", "</x:xmpmeta>");
        BlankBetween(pdf, "<rdf:RDF", "</rdf:RDF>");
        foreach (string key in new[] { "/Creator", "/Producer", "/CreationDate", "/ModDate", "/LastModified" }) BlankString(pdf, key);
    }

    private static void BlankBetween(byte[] pdf, string start, string end)
    {
        byte[] open = Encoding.ASCII.GetBytes(start), close = Encoding.ASCII.GetBytes(end);
        int position = 0;
        while (Find(pdf, open, position) is >= 0 and var from)
        {
            int to = Find(pdf, close, from);
            if (to < 0) return;
            to += close.Length;
            pdf.AsSpan(from, to - from).Fill((byte)' ');
            position = to;
        }
    }

    private static void BlankString(byte[] pdf, string key)
    {
        byte[] name = Encoding.ASCII.GetBytes(key);
        int position = 0;
        while (Find(pdf, name, position) is >= 0 and var from)
        {
            int i = from + name.Length;
            position = i;
            while (i < pdf.Length && pdf[i] is (byte)' ' or (byte)'\r' or (byte)'\n') i++;
            if (i >= pdf.Length || pdf[i] is not ((byte)'(' or (byte)'<') || (pdf[i] == '<' && i + 1 < pdf.Length && pdf[i + 1] == '<')) continue;
            byte close = pdf[i] == '(' ? (byte)')' : (byte)'>';
            for (int j = i + 1; j < pdf.Length && j - i < 512; j++)
            {
                if (pdf[j] == '\\') { j++; continue; }
                if (pdf[j] == close)
                {
                    pdf.AsSpan(i + 1, j - i - 1).Fill(close == ')' ? (byte)' ' : (byte)'0');
                    break;
                }
            }
        }
    }

    private static int Find(byte[] data, byte[] value, int start)
    {
        int index = data.AsSpan(start).IndexOf(value);
        return index < 0 ? -1 : start + index;
    }

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate int WriteBlock(nint self, nint data, uint size);

    [StructLayout(LayoutKind.Sequential)]
    private struct FileWrite
    {
        internal int Version;
        internal nint Write;
    }

    [DllImport("pdfium")] private static extern void FPDF_InitLibrary();
    [DllImport("pdfium")] private static extern nint FPDF_CreateNewDocument();
    [DllImport("pdfium")] private static extern nint FPDF_LoadMemDocument64(nint data, nuint size, [MarshalAs(UnmanagedType.LPStr)] string? password);
    [DllImport("pdfium")] private static extern void FPDF_CloseDocument(nint document);
    [DllImport("pdfium")] private static extern int FPDF_GetPageCount(nint document);
    [DllImport("pdfium")] private static extern uint FPDF_GetLastError();
    [DllImport("pdfium")] [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool FPDF_ImportPages(nint destination, nint source, [MarshalAs(UnmanagedType.LPStr)] string? range, int index);
    [DllImport("pdfium")] [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool FPDF_SaveAsCopy(nint document, ref FileWrite writer, uint flags);
}
