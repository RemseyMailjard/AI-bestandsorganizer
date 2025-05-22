// ---------- AIFileOrganizer.cs (improved) ----------
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
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
    /// <summary>
    /// Delegate used to let a UI‑layer confirm or override a suggested filename.
    /// </summary>
    public delegate Task<string> FilenameConfirmationHandler(string originalFilenameBase,
                                                             string suggestedFilenameBase,
                                                             IProgress<string>? progress);

    //---------------------------------------------------------------------
    //  AIFileOrganizer  –  organizes files with help of Gemini‑API
    //---------------------------------------------------------------------
    public partial class AIFileOrganizer
    {
        private readonly AIOrganizerSettings _settings;
        private readonly ILogger<AIFileOrganizer> _logger;
        private readonly HashSet<string> _supported;
        private readonly GoogleAI _google;

        // Heuristic keyword→category map (very light weight, only used when AI fails)
        private static readonly (Regex regex, string category)[] _keywordMap =
        {
            (new Regex(@"\b(invoice|bank|statement|rekening|factuur)\b", RegexOptions.IgnoreCase), "Financiën"),
            (new Regex(@"\b(belasting|tax|aangifte)\b",               RegexOptions.IgnoreCase), "Belastingen"),
            (new Regex(@"\b(polis|verzekering|premium)\b",            RegexOptions.IgnoreCase), "Verzekeringen"),
            (new Regex(@"\b(hypotheek|huurcontract|notaris)\b",        RegexOptions.IgnoreCase), "Woning"),
            (new Regex(@"\b(medisch|recept|dokter|gezondheid)\b",      RegexOptions.IgnoreCase), "Gezondheid en Medisch"),
            (new Regex(@"\b(autoverzekering|kenteken|apk)\b",          RegexOptions.IgnoreCase), "Voertuigen")
        };

        //-----------------------------------------------------------------
        public AIFileOrganizer(IOptions<AIOrganizerSettings> options,
                               GoogleAI google,
                               ILogger<AIFileOrganizer> logger)
        {
            _settings = options?.Value ?? throw new ArgumentNullException(nameof(options));
            _google   = google           ?? throw new ArgumentNullException(nameof(google));
            _logger   = logger           ?? throw new ArgumentNullException(nameof(logger));

            _supported = new HashSet<string>(
                (_settings.SupportedExtensions ?? new()).Select(e => e.ToLowerInvariant()),
                StringComparer.OrdinalIgnoreCase);
        }

        //-----------------------------------------------------------------
        public async Task<(int processed, int moved)> OrganizeAsync(
            string srcDir,
            string dstDir,
            FilenameConfirmationHandler? confirmFilename = null,
            IProgress<string>? progress = null,
            CancellationToken cancellationToken = default)
        {
            srcDir = Path.GetFullPath(srcDir);
            dstDir = Path.GetFullPath(dstDir);
            var src = new DirectoryInfo(srcDir);
            if (!src.Exists) throw new DirectoryNotFoundException(src.FullName);
            Directory.CreateDirectory(dstDir);

            int processed = 0, moved = 0;

            foreach (var fi in src.EnumerateFiles("*", SearchOption.AllDirectories))
            {
                cancellationToken.ThrowIfCancellationRequested();

                if (!_supported.Contains(fi.Extension.ToLowerInvariant()))
                {
                    progress?.Report($"⏭️  Skipping {fi.Name} (unsupported)");
                    continue;
                }

                processed++;
                progress?.Report($"📄 Lezen: {fi.FullName}");

                string category = _settings.FallbackCategory;
                string targetLabel = $"0. {_settings.FallbackCategory}";
                string finalFilenameBase = Path.GetFileNameWithoutExtension(fi.Name);

                try
                {
                    string text = await ExtractTextAsync(fi).ConfigureAwait(false);

                    if (!string.IsNullOrWhiteSpace(text))
                    {
                        category = await ClassifyAsync(text, cancellationToken).ConfigureAwait(false);

                        // Optional descriptive filename
                        if (_settings.EnableDescriptiveFilenames && confirmFilename != null)
                        {
                            progress?.Report($"📝 Generating filename for '{fi.Name}' → {category}");
                            string suggestion = await GenerateFilenameAsync(text, fi.Name, category, cancellationToken)
                                                      .ConfigureAwait(false);
                            finalFilenameBase = await confirmFilename(finalFilenameBase, suggestion, progress);
                            finalFilenameBase = SanitizeFilename(finalFilenameBase);
                            if (string.IsNullOrWhiteSpace(finalFilenameBase))
                                finalFilenameBase = Path.GetFileNameWithoutExtension(fi.Name);
                        }
                    }
                    else
                    {
                        _logger.LogWarning("Geen tekst gevonden in {File}.", fi.FullName);
                    }

                    targetLabel = _settings.Categories.TryGetValue(category, out var mapped)
                                  ? mapped
                                  : $"0. {_settings.FallbackCategory}";
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Classificatie/naamgeneratie mislukt voor {File}", fi.FullName);
                    progress?.Report($"❌ Fout bij verwerken van {fi.Name}: {ex.Message}");
                }

                string targetDir = Path.Combine(dstDir, targetLabel);
                Directory.CreateDirectory(targetDir);

                string dest = Path.Combine(targetDir, finalFilenameBase + fi.Extension);
                int c = 1;
                while (File.Exists(dest))
                    dest = Path.Combine(targetDir, $"{finalFilenameBase}_{c++}{fi.Extension}");

                try
                {
                    fi.MoveTo(dest);
                    moved++;
                    progress?.Report($"✅ {fi.Name} → {Path.GetFileName(dest)}  (→ {targetLabel})");
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Verplaatsen mislukt voor {File}", fi.FullName);
                    progress?.Report($"❌ Kon {fi.Name} niet verplaatsen: {ex.Message}");
                }
            }

            progress?.Report($"Organisatie klaar – {processed} verwerkt, {moved} verplaatst.");
            return (processed, moved);
        }

        #region ――――――――――――  GEMINI  ――――――――――――――――――――――――――――――――
        private async Task<string> ClassifyAsync(string text, CancellationToken ct)
        {
            // 1) Prompt Gemini
            string catList = string.Join(" | ", _settings.Categories.Keys);
            string prompt =
                "Je krijgt documenttekst en retourneert *exact* één van deze labels (case‑insensitive):\n" +
                catList + "\n" +
                "Antwoord enkel het label, zonder punt, nummering of extra woorden." +
                "\n\nDOCUMENT:\n" +
                text[..Math.Min(text.Length, _settings.MaxPromptChars)];

            _logger.LogDebug("Classify‑prompt →\n{Prompt}", prompt);

            var model = _google.GenerativeModel(_settings.ModelName);
            string? aiAns = null;
            try
            {
                var res = await model.GenerateContent(prompt, cancellationToken: ct);
                aiAns = res.Text?.Trim();
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Gemini classificatie mislukt.");
            }

            // 2) Normaliseer & match
            if (!string.IsNullOrWhiteSpace(aiAns))
            {
                string norm = Normalize(aiAns);
                string? match = _settings.Categories.Keys.FirstOrDefault(k => Normalize(k) == norm);
                if (match != null)
                {
                    _logger.LogInformation("AI → '{AiAns}' ⇒ '{Match}'", aiAns, match);
                    return match;
                }
            }

            // 3) Keyword‑heuristiek
            foreach (var (regex, cat) in _keywordMap)
            {
                if (regex.IsMatch(text))
                {
                    _logger.LogInformation("Heuristiek trof '{Cat}'", cat);
                    return cat;
                }
            }

            _logger.LogWarning("Geen match – fallback {Fallback}", _settings.FallbackCategory);
            return _settings.FallbackCategory;
        }

        private async Task<string> GenerateFilenameAsync(string text, string originalFilename, string category, CancellationToken ct)
        {
            string relevant = text[..Math.Min(text.Length, _settings.MaxPromptChars)];
            string prompt =
                "Generate a short, descriptive filename (no extension) for this document. " +
                $"Category: '{category}'. Use dates YYYY‑MM‑DD if present. " +
                "Avoid invalid path characters. Respond *only* with the name.\n\n" +
                relevant;

            var model = _google.GenerativeModel(_settings.ModelName);
            string? ans = null;
            try
            {
                var res = await model.GenerateContent(prompt, cancellationToken: ct);
                ans = res.Text?.Trim();
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Gemini filename‑gen mislukt.");
            }

            return string.IsNullOrWhiteSpace(ans)
                   ? Path.GetFileNameWithoutExtension(originalFilename)
                   : SanitizeFilename(ans);
        }
        #endregion

        #region ――――――――――――  TEXT EXTRACTION  ――――――――――――――――――――――――――
        private async Task<string> ExtractTextAsync(FileInfo fi)
        {
            string ext = fi.Extension.ToLowerInvariant();
            if (ext is ".txt" or ".md")
            {
                try { return await File.ReadAllTextAsync(fi.FullName, Encoding.UTF8); }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Lezen TXT mislukt: {File}", fi.Name);
                    return string.Empty;
                }
            }

            if (ext == ".docx")
            {
                try
                {
                    using var doc = WordprocessingDocument.Open(fi.FullName, false);
                    return doc.MainDocumentPart?.Document?.Body?.InnerText ?? string.Empty;
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "DOCX extractie mislukt: {File}", fi.Name);
                    return string.Empty;
                }
            }

            if (ext == ".pdf")
            {
                var sb = new StringBuilder();
                try
                {
                    using var pdf = PdfDocument.Open(fi.FullName);
                    foreach (PdfPage p in pdf.GetPages()) sb.Append(p.Text).Append(' ');
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "PDF extractie mislukt: {File}", fi.Name);
                }

                // OCR fallback (optional)
                if (sb.Length < 30 && _settings.EnableDescriptiveFilenames)
                {
                    try { sb.Append(await OcrHelper.RunAsync(fi.FullName)); }
                    catch (Exception ex) { _logger.LogError(ex, "OCR mislukt voor {File}", fi.Name); }
                }
                return sb.ToString();
            }

            return string.Empty;
        }
        #endregion

        #region ――――――――――――  HELPERS  ――――――――――――――――――――――――――――――――――――
        private static string Normalize(string s)
        {
            if (string.IsNullOrWhiteSpace(s)) return string.Empty;

            // Strip accents
            string formD = s.Normalize(NormalizationForm.FormD);
            var sb = new StringBuilder(formD.Length);
            foreach (var ch in formD)
                if (CharUnicodeInfo.GetUnicodeCategory(ch) != UnicodeCategory.NonSpacingMark)
                    sb.Append(ch);
            string noDiacritics = sb.ToString();

            // Keep only letters/numbers
            return Regex.Replace(noDiacritics, "[^A-Za-z0-9]", "", RegexOptions.IgnoreCase)
                         .ToLowerInvariant();
        }

        public static string SanitizeFilename(string filename)
        {
            if (string.IsNullOrWhiteSpace(filename)) return string.Empty;

            char[] invalid = Path.GetInvalidFileNameChars().Concat(Path.GetInvalidPathChars()).Distinct().ToArray();
            string cleaned = new string(filename.Select(c => invalid.Contains(c) ? '_' : c).ToArray());
            cleaned = MyRegex().Replace(cleaned, "_");
            cleaned = Regex.Replace(cleaned, "_+", "_");
            if (cleaned.Length > 100) cleaned = cleaned[..100];
            return cleaned.Trim('_');
        }

        [GeneratedRegex("[\\s]+")]
        private static partial Regex MyRegex();
        #endregion
    }

    //---------------------------------------------------------------------
    //  Simple OCR helper (placeholder) – plug your favourite engine here
    //---------------------------------------------------------------------
    internal static class OcrHelper
    {
        public static Task<string> RunAsync(string file) => Task.FromResult(string.Empty); // TODO implement
    }
}
