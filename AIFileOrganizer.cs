// ---------- AIFileOrganizer.cs ----------
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

// OpenXML & PdfPig
using DocumentFormat.OpenXml.Packaging;
using PdfDocument = UglyToad.PdfPig.PdfDocument;
using PdfPage = UglyToad.PdfPig.Content.Page;
using WordText = DocumentFormat.OpenXml.Wordprocessing.Text;

// DI / Logging
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

// Mscc-GenerativeAI
using Mscc.GenerativeAI;

namespace AI_bestandsorganizer
{
    // ------- Settings-POCO (appsettings.json → "AIOrganizer") -------


    // ----------------------------------------------------------------
    //  AIFileOrganizer  –  organiseert bestanden m.b.v. Gemini-API
    // ----------------------------------------------------------------
    public class AIFileOrganizer
    {
        private readonly AIOrganizerSettings _settings;
        private readonly ILogger<AIFileOrganizer> _logger;
        private readonly HashSet<string> _supported;
        private readonly GoogleAI _google;

        public AIFileOrganizer(
            IOptions<AIOrganizerSettings> settings,
            GoogleAI google,
            ILogger<AIFileOrganizer> logger)
        {
            _settings  = settings.Value ?? throw new ArgumentNullException(nameof(settings));
            _google    = google       ?? throw new ArgumentNullException(nameof(google));
            _logger    = logger       ?? throw new ArgumentNullException(nameof(logger));

            _supported = new HashSet<string>(
                (_settings.SupportedExtensions ?? new()).Select(e => e.ToLowerInvariant()),
                StringComparer.OrdinalIgnoreCase);
        }

        // ---------------- PUBLIC ----------------
        public async Task<(int processed, int moved)> OrganizeAsync(
            string srcDir,
            string dstDir,
            IProgress<string>? progress = null,
            CancellationToken cancellationToken = default)
        {
            srcDir = Path.GetFullPath(srcDir);
            dstDir = Path.GetFullPath(dstDir);

            var src = new DirectoryInfo(srcDir);
            if (!src.Exists) throw new DirectoryNotFoundException(src.FullName);

            Directory.CreateDirectory(dstDir);

            int processed = 0, moved = 0;

            foreach (var fileInfo in src.EnumerateFiles())
            {
                cancellationToken.ThrowIfCancellationRequested();

                if (!_supported.Contains(fileInfo.Extension.ToLowerInvariant()))
                    continue;

                processed++;
                progress?.Report($"📄 {fileInfo.Name} lezen …");

                string category = _settings.FallbackCategory;
                string targetLabel = $"0. {_settings.FallbackCategory}";

                try
                {
                    string text = await ExtractTextAsync(fileInfo).ConfigureAwait(false);
                    if (!string.IsNullOrWhiteSpace(text))
                        category = await ClassifyAsync(text, cancellationToken).ConfigureAwait(false);

                    targetLabel = _settings.Categories.TryGetValue(category, out var mapped)
                                  ? mapped
                                  : $"0. {_settings.FallbackCategory}";
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Classificatie mislukt voor {File}", fileInfo.Name);
                }

                string targetDir = Path.Combine(dstDir, targetLabel);
                Directory.CreateDirectory(targetDir);

                string dest = Path.Combine(targetDir, fileInfo.Name);
                int c = 1;
                while (File.Exists(dest))
                    dest = Path.Combine(targetDir,
                          $"{Path.GetFileNameWithoutExtension(fileInfo.Name)}_{c++}{fileInfo.Extension}");

                try
                {
                    fileInfo.MoveTo(dest);
                    moved++;
                    progress?.Report($"✅ {fileInfo.Name} → {targetLabel}");
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Verplaatsen mislukt voor {File}", fileInfo.Name);
                    progress?.Report($"❌ Fout bij {fileInfo.Name}: {ex.Message}");
                }
            }

            progress?.Report($"Organisatie klaar – {processed} verwerkt, {moved} verplaatst.");
            return (processed, moved);
        }

        // ---------------- GEMINI ----------------
        private async Task<string> ClassifyAsync(string text, CancellationToken ct)
        {
            string catList = string.Join('\n', _settings.Categories.Keys);
            string prompt =
                $"Classificeer dit document in één categorie:\n{catList}\n- {_settings.FallbackCategory} (fallback)\n\n" +
                text[..Math.Min(text.Length, _settings.MaxPromptChars)];

            var model = _google.GenerativeModel(_settings.ModelName);
            var result = await model.GenerateContent(prompt, cancellationToken: ct);
            string? ans = result.Text?.Trim();

            return !string.IsNullOrEmpty(ans) && _settings.Categories.ContainsKey(ans)
                 ? ans
                 : _settings.FallbackCategory;
        }

        // ---------------- TEXT EXTRACT ----------------
        private static async Task<string> ExtractTextAsync(FileInfo fi)
        {
            string ext = fi.Extension.ToLowerInvariant();
            if (ext is ".txt" or ".md")
                return await File.ReadAllTextAsync(fi.FullName).ConfigureAwait(false);

            if (ext == ".docx")
            {
                var sb = new StringBuilder();
                using var doc = WordprocessingDocument.Open(fi.FullName, false);
                if (doc.MainDocumentPart?.Document?.Body != null)
                {
                    foreach (WordText t in doc.MainDocumentPart.Document.Body.Descendants<WordText>())
                        sb.Append(t.Text).Append(' ');
                }
                return sb.ToString();
            }

            if (ext == ".pdf")
            {
                var sb = new StringBuilder();
                using PdfDocument pdf = PdfDocument.Open(fi.FullName);
                foreach (PdfPage p in pdf.GetPages())
                    sb.Append(p.Text).Append(' ');
                return sb.ToString();
            }

            return string.Empty;
        }
    }
}
