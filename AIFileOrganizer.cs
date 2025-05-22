// ---------- AIFileOrganizer.cs ----------
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Text.RegularExpressions;

using DocumentFormat.OpenXml.Packaging;
using PdfDocument = UglyToad.PdfPig.PdfDocument;
using PdfPage = UglyToad.PdfPig.Content.Page;
using WordText = DocumentFormat.OpenXml.Wordprocessing.Text;

using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

using Mscc.GenerativeAI; // Belangrijk voor GenerateContentResponse

namespace AI_bestandsorganizer
{
    public delegate Task<string> FilenameConfirmationHandler(string originalFilenameBase, string suggestedFilenameBase, IProgress<string>? progress);

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

        // GEWIJZIGD: Retourneert nu ook totalTokensUsed
        public async Task<(int processed, int moved, long totalTokensUsed)> OrganizeAsync(
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
            long totalTokensUsed = 0; // NIEUW: Token teller

            foreach (var fileInfo in src.EnumerateFiles("*", SearchOption.AllDirectories))
            {
                cancellationToken.ThrowIfCancellationRequested();

                if (!_supported.Contains(fileInfo.Extension.ToLowerInvariant()))
                {
                    progress?.Report($"Skipping {fileInfo.Name} (unsupported extension)");
                    continue;
                }

                processed++;
                progress?.Report($"📄 {fileInfo.FullName} lezen …");

                string category = _settings.FallbackCategory;
                string targetLabel = $"0. {_settings.FallbackCategory}";
                string finalFilenameBase = Path.GetFileNameWithoutExtension(fileInfo.Name);

                try
                {
                    string text = await ExtractTextAsync(fileInfo).ConfigureAwait(false);
                    if (!string.IsNullOrWhiteSpace(text))
                    {
                        // Classify
                        var (classifiedCategory, classificationTokens) = await ClassifyAsync(text, progress, cancellationToken).ConfigureAwait(false);
                        category = classifiedCategory;
                        totalTokensUsed += classificationTokens; // Tokens optellen
                        progress?.Report($"Tokens for classification: {classificationTokens}");


                        if (_settings.EnableDescriptiveFilenames && confirmFilename != null)
                        {
                            progress?.Report($"📝 Generating descriptive filename for '{fileInfo.Name}' ({category})...");
                            // Generate filename
                            var (suggestedFilename, filenameTokens) = await GenerateFilenameAsync(text, fileInfo.Name, category, progress, cancellationToken).ConfigureAwait(false);
                            totalTokensUsed += filenameTokens; // Tokens optellen
                            progress?.Report($"Tokens for filename suggestion: {filenameTokens}");

                            finalFilenameBase = await confirmFilename(
                                Path.GetFileNameWithoutExtension(fileInfo.Name),
                                suggestedFilename,
                                progress);

                            finalFilenameBase = SanitizeFilename(finalFilenameBase);
                            if (string.IsNullOrWhiteSpace(finalFilenameBase))
                            {
                                finalFilenameBase = Path.GetFileNameWithoutExtension(fileInfo.Name);
                                progress?.Report($"⚠️ Invalid name chosen. Reverting to original name for '{fileInfo.Name}'.");
                            }
                        }
                    }
                    else
                    {
                        _logger.LogWarning("Geen tekst geëxtraheerd uit {File}. Classificatie naar fallback.", fileInfo.FullName);
                        progress?.Report($"⚠️ Geen tekst geëxtraheerd uit '{fileInfo.Name}'. Gaat naar '{_settings.FallbackCategory}'.");
                    }

                    targetLabel = _settings.Categories.TryGetValue(category, out var mapped)
                                  ? mapped
                                  : $"0. {_settings.FallbackCategory}";
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Classificatie/naamgeneratie mislukt voor {File}", fileInfo.FullName);
                    progress?.Report($"❌ Fout bij verwerken van {fileInfo.Name}: {ex.Message}. Gaat naar '{_settings.FallbackCategory}'.");
                }

                string targetDir = Path.Combine(dstDir, targetLabel);
                Directory.CreateDirectory(targetDir);

                string dest = Path.Combine(targetDir, finalFilenameBase + fileInfo.Extension);
                int c = 1;
                while (File.Exists(dest))
                {
                    dest = Path.Combine(targetDir, $"{finalFilenameBase}_{c++}{fileInfo.Extension}");
                }

                try
                {
                    fileInfo.MoveTo(dest);
                    moved++;
                    progress?.Report($"✅ {fileInfo.Name} → {Path.GetFileName(dest)} (naar {targetLabel})");
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Verplaatsen mislukt voor {File}", fileInfo.FullName);
                    progress?.Report($"❌ Fout bij verplaatsen van {fileInfo.Name}: {ex.Message}");
                }
            }

            progress?.Report($"Organisatie klaar – {processed} verwerkt, {moved} verplaatst. Totale tokens: {totalTokensUsed}");
            return (processed, moved, totalTokensUsed); // GEWIJZIGD
        }

        // GEWIJZIGD: Retourneert nu (string category, int tokens) en accepteert IProgress
        private async Task<(string category, int tokens)> ClassifyAsync(string text, IProgress<string>? progress, CancellationToken ct)
        {
            string catList = string.Join('\n', _settings.Categories.Keys.Select(k => k.Trim()));
            int tokensUsed = 0; // Token teller voor deze call

            string prompt =
                $"Classificeer dit document in ÉÉN van de volgende categorieën:\n" +
                $"{catList}\n" +
                $"Als het document in geen van deze categorieën past, antwoord dan ' {_settings.FallbackCategory} '.\n" +
                $"BELANGRIJK: Antwoord ALLEEN met de naam van de categorie, zonder extra tekst, cijfers, punten of uitleg. Bijvoorbeeld: 'Financiën' of 'Gezondheid en Medisch'.\n\n" +
                "Documentinhoud:\n" +
                text[..Math.Min(text.Length, _settings.MaxPromptChars)];

            _logger.LogDebug("Classify Prompt voor AI:\n{Prompt}", prompt);

            var model = _google.GenerativeModel(_settings.ModelName);
            string? ans = null;
            GenerateContentResponse? result = null; // Hou de response bij
            try
            {
                result = await model.GenerateContent(prompt, cancellationToken: ct);
                ans = result.Text?.Trim();
                tokensUsed = result.UsageMetadata?.TotalTokenCount ?? 0; // Haal tokens op
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Fout bij aanroepen van Gemini API voor classificatie.");
                progress?.Report($"⚠️ API error during classification: {ex.Message}");
            }

            _logger.LogDebug("Ruwe AI-antwoord (classificatie): '{Ans}'", ans);
            _logger.LogDebug("Tokens gebruikt voor classificatie: {Tokens}", tokensUsed);


            if (string.IsNullOrEmpty(ans))
            {
                _logger.LogWarning("AI gaf geen antwoord voor classificatie. Terugval naar '{Fallback}'.", _settings.FallbackCategory);
                return (_settings.FallbackCategory, tokensUsed);
            }

            string normalizedAns = ans.Trim();
            normalizedAns = Regex.Replace(normalizedAns, @"^(Category:\s*|Categorie:\s*|\[|\]|\.|\d+\.\s*)", "", RegexOptions.IgnoreCase);
            normalizedAns = Regex.Replace(normalizedAns, @"(\.|\d+\.\s*)$", "");
            normalizedAns = normalizedAns.Trim();

            string? matchedCategory = _settings.Categories.Keys
                .FirstOrDefault(key => string.Equals(key, normalizedAns, StringComparison.OrdinalIgnoreCase));

            if (matchedCategory != null)
            {
                _logger.LogInformation("AI geclassificeerd als '{MatchedCategory}' (genormaliseerd van '{OriginalAns}').", matchedCategory, ans);
                return (matchedCategory, tokensUsed);
            }
            else
            {
                _logger.LogWarning("AI-antwoord '{OriginalAns}' kon niet worden gemapt op een bekende categorie. Genormaliseerd naar '{NormalizedAns}'. Terugval naar '{Fallback}'.", ans, normalizedAns, _settings.FallbackCategory);
                return (_settings.FallbackCategory, tokensUsed);
            }
        }

        // GEWIJZIGD: Retourneert nu (string filename, int tokens) en accepteert IProgress
        private async Task<(string filename, int tokens)> GenerateFilenameAsync(string text, string originalFilename, string category, IProgress<string>? progress, CancellationToken ct)
        {
            string relevantText = text[..Math.Min(text.Length, _settings.MaxPromptChars)];
            int tokensUsed = 0; // Token teller voor deze call

            string prompt =
                "Suggest a highly descriptive, concise, and human-readable filename (without extension) for the following document content.\n" +
                $"The document has been classified into the category: '{category}'. Use this as context.\n" +
                "Focus on the core topic, relevant dates (e.g., YYYY-MM-DD or YYYYMMDD if present), and key entities (e.g., company names, project names).\n" +
                "Avoid generic terms like 'document', 'scan', 'report' unless they are part of a specific, meaningful title within the content.\n" +
                "The original filename was '{originalFilename}'.\n" +
                "Ensure the name is suitable for file paths (no invalid characters like \\ / : * ? \" < > |), keep it under 60 characters, and use underscores or hyphens for spaces.\n" +
                "BELANGRIJK: Antwoord ALLEEN met de voorgestelde bestandsnaam, zonder extra tekst of uitleg.\n\n" +
                "Documentinhoud:\n" +
                relevantText;

            _logger.LogDebug("Filename Prompt voor AI:\n{Prompt}", prompt);

            var model = _google.GenerativeModel(_settings.ModelName);
            string? ans = null;
            GenerateContentResponse? result = null; // Hou de response bij
            try
            {
                result = await model.GenerateContent(prompt, cancellationToken: ct);
                ans = result.Text?.Trim();
                tokensUsed = result.UsageMetadata?.TotalTokenCount ?? 0; // Haal tokens op
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Fout bij aanroepen van Gemini API voor bestandsnaam generatie.");
                progress?.Report($"⚠️ API error during filename generation: {ex.Message}");
            }

            _logger.LogDebug("Ruwe AI-antwoord (bestandsnaam): '{Ans}'", ans);
            _logger.LogDebug("Tokens gebruikt voor bestandsnaam: {Tokens}", tokensUsed);


            if (string.IsNullOrEmpty(ans))
            {
                _logger.LogWarning("AI failed to suggest a filename for {OriginalFile}. Falling back to original base name.", originalFilename);
                return (Path.GetFileNameWithoutExtension(originalFilename), tokensUsed);
            }

            string sanitizedAns = SanitizeFilename(ans);
            _logger.LogDebug("Gesaneerde bestandsnaam: '{SanitizedAns}'", sanitizedAns);

            string finalFilename = string.IsNullOrEmpty(sanitizedAns) ? Path.GetFileNameWithoutExtension(originalFilename) : sanitizedAns;
            return (finalFilename, tokensUsed);
        }

        public static string SanitizeFilename(string filename)
        {
            if (string.IsNullOrWhiteSpace(filename)) return string.Empty;
            char[] invalidChars = Path.GetInvalidFileNameChars().Concat(Path.GetInvalidPathChars()).Distinct().ToArray();
            string sanitized = new string(filename.Select(c => invalidChars.Contains(c) ? '_' : c).ToArray());
            sanitized = sanitized.Trim().Replace(" ", "_").Replace("__", "_").Replace("__", "_");
            if (sanitized.StartsWith("_")) sanitized = sanitized.Substring(1);
            if (sanitized.EndsWith("_")) sanitized = sanitized.Substring(0, sanitized.Length - 1);
            if (sanitized.Length > 100)
            {
                sanitized = sanitized.Substring(0, 100);
                if (sanitized.EndsWith("_")) sanitized = sanitized.Substring(0, sanitized.Length - 1);
            }
            return sanitized;
        }

        private async Task<string> ExtractTextAsync(FileInfo fi)
        {
            string ext = fi.Extension.ToLowerInvariant();
            if (ext is ".txt" or ".md")
            {
                try { return await File.ReadAllTextAsync(fi.FullName).ConfigureAwait(false); }
                catch (Exception ex) { _logger.LogError(ex, "Fout bij lezen van tekstbestand {File}", fi.Name); return string.Empty; }
            }
            if (ext == ".docx")
            {
                var sb = new StringBuilder();
                try
                {
                    using var doc = WordprocessingDocument.Open(fi.FullName, false);
                    if (doc.MainDocumentPart?.Document?.Body != null)
                    {
                        foreach (WordText t in doc.MainDocumentPart.Document.Body.Descendants<WordText>())
                            sb.Append(t.Text).Append(' ');
                    }
                }
                catch (Exception ex) { _logger.LogError(ex, "Fout bij extraheren van tekst uit DOCX {File}", fi.Name); }
                return sb.ToString();
            }
            if (ext == ".pdf")
            {
                var sb = new StringBuilder();
                try
                {
                    using PdfDocument pdf = PdfDocument.Open(fi.FullName);
                    foreach (PdfPage p in pdf.GetPages())
                        sb.Append(p.Text).Append(' ');
                }
                catch (Exception ex) { _logger.LogError(ex, "Fout bij extraheren van tekst uit PDF {File}", fi.Name); }
                return sb.ToString();
            }
            return string.Empty;
        }
    }
}