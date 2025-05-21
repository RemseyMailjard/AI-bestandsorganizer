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
    // Delegate for handling filename confirmation (UI-agnostic)
    /// <summary>
    /// Represents a method that prompts the user to confirm or modify a suggested filename.
    /// </summary>
    /// <param name="originalFilenameBase">The original filename without extension (e.g., "document").</param>
    /// <param name="suggestedFilenameBase">The AI-suggested filename without extension (e.g., "Project_Report_Q3_2023").</param>
    /// <param name="progress">An optional progress reporter for outputting messages to the user.</param>
    /// <returns>
    /// The final chosen filename without extension.
    /// Return <paramref name="suggestedFilenameBase"/> to accept AI suggestion.
    /// Return <paramref name="originalFilenameBase"/> to keep the original name.
    /// Return a new string for a custom name.
    /// </returns>
    public delegate Task<string> FilenameConfirmationHandler(string originalFilenameBase, string suggestedFilenameBase, IProgress<string>? progress);


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
            FilenameConfirmationHandler? confirmFilename = null, // NEW PARAMETER
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
                {
                    progress?.Report($"Skipping {fileInfo.Name} (unsupported extension)");
                    continue;
                }

                processed++;
                progress?.Report($"📄 {fileInfo.Name} lezen …");

                string category = _settings.FallbackCategory;
                string targetLabel = $"0. {_settings.FallbackCategory}";
                string finalFilenameBase = Path.GetFileNameWithoutExtension(fileInfo.Name); // Default to original name

                try
                {
                    string text = await ExtractTextAsync(fileInfo).ConfigureAwait(false);
                    if (!string.IsNullOrWhiteSpace(text))
                    {
                        category = await ClassifyAsync(text, cancellationToken).ConfigureAwait(false);

                        // NEW: Generate descriptive filename and get user confirmation
                        if (_settings.EnableDescriptiveFilenames && confirmFilename != null)
                        {
                            progress?.Report($"📝 Generating descriptive filename for '{fileInfo.Name}'...");
                            string suggestedFilename = await GenerateFilenameAsync(text, fileInfo.Name, cancellationToken).ConfigureAwait(false);

                            // Call the external handler to get the final desired filename from the user
                            finalFilenameBase = await confirmFilename(
                                Path.GetFileNameWithoutExtension(fileInfo.Name),
                                suggestedFilename,
                                progress);

                            // Basic validation for the returned filename
                            finalFilenameBase = SanitizeFilename(finalFilenameBase);
                            if (string.IsNullOrWhiteSpace(finalFilenameBase))
                            {
                                // If the handler returned an invalid name, revert to original
                                finalFilenameBase = Path.GetFileNameWithoutExtension(fileInfo.Name);
                                progress?.Report($"⚠️ Invalid name chosen. Reverting to original name for '{fileInfo.Name}'.");
                            }
                        }
                    }
                    else
                    {
                        _logger.LogWarning("Geen tekst geëxtraheerd uit {File}. Classificatie naar fallback.", fileInfo.Name);
                        progress?.Report($"⚠️ Geen tekst geëxtraheerd uit '{fileInfo.Name}'. Gaat naar '{_settings.FallbackCategory}'.");
                    }


                    targetLabel = _settings.Categories.TryGetValue(category, out var mapped)
                                  ? mapped
                                  : $"0. {_settings.FallbackCategory}";
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Classificatie/naamgeneratie mislukt voor {File}", fileInfo.Name);
                    progress?.Report($"❌ Fout bij verwerken van {fileInfo.Name}: {ex.Message}. Gaat naar '{_settings.FallbackCategory}'.");
                    // Continue to move with original name if classification/name gen failed
                    // category and targetLabel will remain fallback values
                }

                string targetDir = Path.Combine(dstDir, targetLabel);
                Directory.CreateDirectory(targetDir);

                // Use the finalFilenameBase for the destination path
                string dest = Path.Combine(targetDir, finalFilenameBase + fileInfo.Extension);
                int c = 1;
                while (File.Exists(dest))
                {
                    // If a file with the same new name exists, append a number
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
                    _logger.LogError(ex, "Verplaatsen mislukt voor {File}", fileInfo.Name);
                    progress?.Report($"❌ Fout bij verplaatsen van {fileInfo.Name}: {ex.Message}");
                }
            }

            progress?.Report($"Organisatie klaar – {processed} verwerkt, {moved} verplaatst.");
            return (processed, moved);
        }

        // ---------------- GEMINI ----------------
        private async Task<string> ClassifyAsync(string text, CancellationToken ct)
        {
            // Create a list of category keys, stripping prefixes/suffixes if present in the *keys*
            // But the current keys in appsettings.json are already clean ("Financiën", not "1. Financiën").
            // So, no need to strip here.
            string catList = string.Join('\n', _settings.Categories.Keys.Select(k => k.Trim()));

            string prompt =
                $"Classificeer dit document in ÉÉN van de volgende categorieën:\n" +
                $"{catList}\n" +
                $"Als het document in geen van deze categorieën past, antwoord dan ' {_settings.FallbackCategory} '.\n" + // Add spaces for robust matching
                $"BELANGRIJK: Antwoord ALLEEN met de naam van de categorie, zonder extra tekst, cijfers, punten of uitleg. Bijvoorbeeld: 'Financiën' of 'Gezondheid en Medisch'.\n\n" +
                "Documentinhoud:\n" +
                text[..Math.Min(text.Length, _settings.MaxPromptChars)];

            _logger.LogDebug("Classify Prompt voor AI:\n{Prompt}", prompt);

            var model = _google.GenerativeModel(_settings.ModelName);
            string? ans = null;
            try
            {
                var result = await model.GenerateContent(prompt, cancellationToken: ct);
                ans = result.Text?.Trim();
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Fout bij aanroepen van Gemini API voor classificatie.");
            }

            _logger.LogDebug("Ruwe AI-antwoord: '{Ans}'", ans);

            if (string.IsNullOrEmpty(ans))
            {
                _logger.LogWarning("AI gaf geen antwoord voor classificatie. Terugval naar '{Fallback}'.", _settings.FallbackCategory);
                return _settings.FallbackCategory;
            }

            // Normalize AI's response for matching
            string normalizedAns = ans.Trim();
            // Remove common prefixes/suffixes the AI might add despite instructions
            normalizedAns = System.Text.RegularExpressions.Regex.Replace(normalizedAns, @"^(Category:\s*|Categorie:\s*|\[|\]|\.|\d+\.\s*)", "", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
            normalizedAns = System.Text.RegularExpressions.Regex.Replace(normalizedAns, @"(\.|\d+\.\s*)$", ""); // Remove trailing periods/numbers
            normalizedAns = normalizedAns.Trim(); // Trim again after replacements

            // Attempt to find a case-insensitive match for the normalized answer in the category keys
            string? matchedCategory = _settings.Categories.Keys
                .FirstOrDefault(key => string.Equals(key, normalizedAns, StringComparison.OrdinalIgnoreCase));

            if (matchedCategory != null)
            {
                _logger.LogInformation("AI geclassificeerd als '{MatchedCategory}' (genormaliseerd van '{OriginalAns}').", matchedCategory, ans);
                return matchedCategory; // Return the *exact* key from your settings
            }
            else
            {
                _logger.LogWarning("AI-antwoord '{OriginalAns}' kon niet worden gemapt op een bekende categorie. Genormaliseerd naar '{NormalizedAns}'. Terugval naar '{Fallback}'.", ans, normalizedAns, _settings.FallbackCategory);
                return _settings.FallbackCategory;
            }
        }

        // NEW: Method to generate a descriptive filename using Gemini
        private async Task<string> GenerateFilenameAsync(string text, string originalFilename, CancellationToken ct)
        {
            // Truncate text for filename generation prompt if it's very long, to focus on key info.
            // Using a smaller section than MaxPromptChars might be beneficial for filename tasks.
            // Using a higher value here (e.g., up to MaxPromptChars) might yield better filenames.
            string relevantText = text[..Math.Min(text.Length, _settings.MaxPromptChars)]; // Use MaxPromptChars for filename prompt too

            string prompt =
                "Suggest a very concise, descriptive, and human-readable filename (without extension) for the following document content.\n" +
                "The original filename was '{originalFilename}'.\n" + // Used originalFilename directly, not base
                "Ensure the suggested name is suitable for a file path, avoiding special characters like \\ / : * ? \" < > | and starting/ending spaces.\n" +
                "Keep it under 60 characters if possible, preferably using underscores or hyphens for spaces.\n" +
                "BELANGRIJK: Antwoord ALLEEN met de voorgestelde bestandsnaam, zonder extra tekst of uitleg.\n\n" +
                "Documentinhoud:\n" +
                relevantText;

            _logger.LogDebug("Filename Prompt voor AI:\n{Prompt}", prompt);

            var model = _google.GenerativeModel(_settings.ModelName);
            string? ans = null;
            try
            {
                var result = await model.GenerateContent(prompt, cancellationToken: ct);
                ans = result.Text?.Trim();
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Fout bij aanroepen van Gemini API voor bestandsnaam generatie.");
            }

            _logger.LogDebug("Ruwe AI-antwoord (bestandsnaam): '{Ans}'", ans);

            if (string.IsNullOrEmpty(ans))
            {
                _logger.LogWarning("AI failed to suggest a filename for {OriginalFile}. Falling back to original base name.", originalFilename);
                return Path.GetFileNameWithoutExtension(originalFilename); // Fallback to original base name
            }

            // Sanitize the AI's suggestion
            string sanitizedAns = SanitizeFilename(ans);
            _logger.LogDebug("Gesaneerde bestandsnaam: '{SanitizedAns}'", sanitizedAns);
            return string.IsNullOrEmpty(sanitizedAns) ? Path.GetFileNameWithoutExtension(originalFilename) : sanitizedAns;
        }

        // NEW: Helper method to sanitize a filename string
        // Made public static so it can be re-used by the console app for user input sanitization if desired.
        public static string SanitizeFilename(string filename)
        {
            if (string.IsNullOrWhiteSpace(filename)) return string.Empty;

            // Define invalid characters for filenames and paths
            char[] invalidChars = Path.GetInvalidFileNameChars()
                                  .Concat(Path.GetInvalidPathChars())
                                  .Distinct()
                                  .ToArray();

            // Replace invalid characters with an underscore
            string sanitized = new string(filename.Select(c => invalidChars.Contains(c) ? '_' : c).ToArray());

            // Remove leading/trailing spaces, replace multiple spaces/underscores with single
            sanitized = sanitized.Trim()
                                 .Replace(" ", "_") // Replace spaces with underscores
                                 .Replace("__", "_") // Replace double underscores from previous step (can create more)
                                 .Replace("__", "_"); // One more pass for good measure

            // Ensure it doesn't start or end with an underscore (if it wasn't already)
            if (sanitized.StartsWith("_")) sanitized = sanitized.Substring(1);
            if (sanitized.EndsWith("_")) sanitized = sanitized.Substring(0, sanitized.Length - 1);

            // Limit length to avoid path issues (e.g., 100-150 characters is a good practical limit)
            if (sanitized.Length > 100)
            {
                sanitized = sanitized.Substring(0, 100);
                // Ensure it doesn't end with an underscore if truncated
                if (sanitized.EndsWith("_")) sanitized = sanitized.Substring(0, sanitized.Length - 1);
            }

            return sanitized;
        }

        // ---------------- TEXT EXTRACT ----------------
        private async Task<string> ExtractTextAsync(FileInfo fi) // Changed to non-static to use _logger
        {
            string ext = fi.Extension.ToLowerInvariant();
            if (ext is ".txt" or ".md")
            {
                try
                {
                    return await File.ReadAllTextAsync(fi.FullName).ConfigureAwait(false);
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Fout bij lezen van tekstbestand {File}", fi.Name);
                    return string.Empty;
                }
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
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Fout bij extraheren van tekst uit DOCX {File}", fi.Name);
                }
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
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Fout bij extraheren van tekst uit PDF {File}", fi.Name);
                }
                return sb.ToString();
            }

            return string.Empty;
        }
    }
}