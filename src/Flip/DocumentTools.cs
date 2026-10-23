using System.Diagnostics;
using System.IO;
using System.Net;
using System.Text;
using System.Text.RegularExpressions;
namespace Flip;

internal static class DocumentTools
{
    static string? FindPandoc()
    {
        var local = Path.Combine(AppContext.BaseDirectory, "tools", "pandoc.exe"); if (File.Exists(local)) return local;
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir != null; dir = dir.Parent)
        {
            if (!File.Exists(Path.Combine(dir.FullName, "Flip.csproj"))) continue;
            string? root = dir.Parent?.Parent?.FullName;
            if (root == null) return null;
            var candidate = Path.Combine(root, "tools", "pandoc", "pandoc-3.12.1", "pandoc.exe");
            return File.Exists(candidate) ? candidate : null;
        }
        return null;
    }
    internal static readonly string? Pandoc = FindPandoc();
    internal static readonly string? Office = new[] { Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86) }
        .Select(p => Path.Combine(p, "LibreOffice", "program", "soffice.com")).FirstOrDefault(File.Exists);
    internal static bool HasPandoc => Pandoc != null;
    internal static bool HasOffice => Office != null;
    internal static async Task Run(string exe, IEnumerable<string> args, CancellationToken token)
    {
        var start = new ProcessStartInfo(exe) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardError = true, RedirectStandardOutput = true, WorkingDirectory = Path.GetDirectoryName(exe)! };
        foreach (var arg in args) start.ArgumentList.Add(arg);
        using var process = Process.Start(start) ?? throw new IOException("Impossibile avviare il convertitore locale.");
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token); timeout.CancelAfter(TimeSpan.FromSeconds(120));
        var stdout = process.StandardOutput.ReadToEndAsync(); var stderr = process.StandardError.ReadToEndAsync();
        try { await process.WaitForExitAsync(timeout.Token); }
        catch (OperationCanceledException)
        {
            try { if (!process.HasExited) process.Kill(true); } catch (InvalidOperationException) { }
            await process.WaitForExitAsync();
            await Task.WhenAll(stdout, stderr);
            if (!token.IsCancellationRequested) throw new IOException("Il convertitore ha superato il limite di 120 secondi.");
            throw;
        }
        string output = await stdout, error = await stderr;
        if (process.ExitCode != 0) throw new IOException(Path.GetFileName(exe) + " (exit " + process.ExitCode + ", 0x" + process.ExitCode.ToString("X8") + "): " + (error + output).Trim()[..Math.Min(1200, (error + output).Trim().Length)]);
        if (error.Contains("Could not fetch", StringComparison.OrdinalIgnoreCase) || error.Contains("Could not convert image", StringComparison.OrdinalIgnoreCase) || error.Contains("Could not find image", StringComparison.OrdinalIgnoreCase))
            throw new IOException("Il documento contiene risorse non disponibili offline: " + error[..Math.Min(700, error.Length)]);
    }
    internal static string ReadText(string path)
    {
        if (new FileInfo(path).Length > 2 * 1024 * 1024) throw new IOException("Testo troppo grande: limite 2 MiB.");
        using var reader = new StreamReader(path, new UTF8Encoding(false, true), true); return reader.ReadToEnd();
    }
    internal static string HtmlText(string text) => "<!doctype html><html><head><meta charset=\"utf-8\"><title>Documento Flip</title></head><body><pre>" + WebUtility.HtmlEncode(text) + "</pre></body></html>";
    internal static async Task Convert(string source, string target, string staged, string job, CancellationToken token)
    {
        string ext = Path.GetExtension(source).ToLowerInvariant(), input = source;
        if (ext == ".pdf")
        {
            using var pdf = UglyToad.PdfPig.PdfDocument.Open(source);
            if (pdf.NumberOfPages > 200) throw new IOException("PDF oltre il limite di 200 pagine.");
            var text = new StringBuilder();
            foreach (var page in pdf.GetPages()) { token.ThrowIfCancellationRequested(); text.AppendLine(UglyToad.PdfPig.DocumentLayoutAnalysis.TextExtractor.ContentOrderTextExtractor.GetText(page)); text.AppendLine(); }
            if (string.IsNullOrWhiteSpace(text.ToString())) throw new IOException("Il PDF non contiene testo estraibile. Per le scansioni serve OCR; usa PNG/JPG.");
            if (target == "TXT") { File.WriteAllText(staged, text.ToString(), new UTF8Encoding(false)); return; }
            input = Path.Combine(job, "extracted.html"); File.WriteAllText(input, HtmlText(text.ToString()), new UTF8Encoding(false)); ext = ".html";
        }
        else if (ext == ".txt") { input = Path.Combine(job, "text.html"); File.WriteAllText(input, HtmlText(ReadText(source)), new UTF8Encoding(false)); ext = ".html"; }
        if (Pandoc == null) throw new IOException("Modulo documenti Pandoc non disponibile.");
        string from = ext switch { ".md" or ".markdown" => "markdown", ".htm" or ".html" => "html", _ => ext.TrimStart('.') };
        if (target == "PDF")
        {
            if (Office == null) throw new IOException("Per i PDF dei documenti serve LibreOffice locale.");
            string docx = Path.Combine(job, "prepared.docx");
            await Run(Pandoc, ["--sandbox", "--standalone", "--from", from, "--to", "docx", "--output", docx, input], token);
            await OfficePdf(docx, staged, job, token); return;
        }
        string to = target switch { "TXT" => "plain", "MD" => "gfm", "HTML" => "html5", _ => target.ToLowerInvariant() };
        var args = new List<string> { "--sandbox", "--standalone", "--from", from, "--to", to, "--output", staged, "--metadata", "title=Documento Flip" };
        if (to == "fb2")
        {
            // FB2 has no code-block element; preserve code/plain-text lines as paragraphs.
            string filter = Path.Combine(job, "fb2-paragraphs.lua");
            File.WriteAllText(filter, "function Plain(b) return pandoc.Para(b.content) end\nfunction CodeBlock(b)\n local out = pandoc.List:new()\n for line in (b.text .. '\\n'):gmatch('(.-)\\n') do\n  out:insert(pandoc.Para({pandoc.Str(line)}))\n end\n return out\nend\n", new UTF8Encoding(false));
            args.Add("--lua-filter"); args.Add(filter);
        }
        if (to == "html5") args.Add("--embed-resources");
        args.Add(input);
        await Run(Pandoc, args, token);
        if (!File.Exists(staged) || new FileInfo(staged).Length == 0) throw new IOException("Il modulo non ha prodotto un file valido.");
    }
    static async Task OfficePdf(string input, string staged, string job, CancellationToken token)
    {
        // LibreOffice creates deep paths inside its profile; keep these independent of the source folder depth.
        string profile = Directory.CreateTempSubdirectory("Flip-LO-").FullName, output = Path.Combine(job, "lo-output");
        try
        {
            Directory.CreateDirectory(output);
            string config = "<?xml version=\"1.0\"?><oor:items xmlns:oor=\"http://openoffice.org/2001/registry\"><item oor:path=\"/org.openoffice.Office.Common/Security/Scripting\"><prop oor:name=\"MacroSecurityLevel\" oor:op=\"fuse\"><value>3</value></prop></item></oor:items>";
            Directory.CreateDirectory(Path.Combine(profile, "user")); File.WriteAllText(Path.Combine(profile, "user", "registrymodifications.xcu"), config);
            await Run(Office!, ["-env:UserInstallation=" + new Uri(profile + Path.DirectorySeparatorChar).AbsoluteUri, "--headless", "--nologo", "--nodefault", "--norestore", "--convert-to", "pdf:writer_pdf_Export", "--outdir", output, input], token);
            var result = Path.Combine(output, Path.GetFileNameWithoutExtension(input) + ".pdf");
            if (!File.Exists(result)) throw new IOException("LibreOffice non ha prodotto il PDF.");
            File.Move(result, staged, false);
        }
        finally
        {
            try { Directory.Delete(profile, true); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { Evidence.Log("office.profile.cleanup.failed", new { profile, error = ex.Message }); }
        }
    }
}
