// ---------- AIFileOrganizer.cs (v6 – Gemini | Azure OpenAI | OpenAI) ---
// Requires .NET 7+
//
// NuGet packages:
//   • Mscc.GenerativeAI
//   • Azure.AI.OpenAI (≥1.0.0-beta.17)
//   • UglyToad.PdfPig
//   • DocumentFormat.OpenXml
// -----------------------------------------------------------------------
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using DocumentFormat.OpenXml.Packaging;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Mscc.GenerativeAI;                         // Google Gemini
using Azure;
using Azure.AI.OpenAI;                           // Azure OpenAI
using PdfDocument = UglyToad.PdfPig.PdfDocument;
using PdfPage = UglyToad.PdfPig.Content.Page;

// ── Alias om naamconflict met Mscc.GenerativeAI.ChatMessage te vermijden
// CORRECTED ALIASES:
using OpenAI;
using OpenAI.Chat; // This 'using OpenAI;' can cause ambiguity if you also have the official OpenAI SDK.
                   // If _azure client was intended to be OpenAI.OpenAIClient, this needs a different approach.
                   // Given the errors, it's clear the intention was to use Azure.AI.OpenAI.OpenAIClient.

namespace AI_bestandsorganizer;


//──────────────────────────────────── Delegate ─────────────────────────
public delegate Task<string> FilenameConfirmationHandler(string originalBase,
                                                         string suggestedBase,
                                                         IProgress<string>? progress);

//──────────────────────────────────── Organizer ────────────────────────
public class AIFileOrganizer
{
    private readonly AIOrganizerSettings _cfg;
    private readonly ILogger<AIFileOrganizer> _log;
    private readonly HashSet<string> _supported;

    private readonly GoogleAI? _gemini;
    private readonly AzureOpenAIClient _azure; // Explicitly type for clarity
    private readonly HttpClient? _openaiHttp;

    private static readonly (Regex rx, string cat)[] _keywords =
    {
        (new(@"\b(invoice|factuur|order|offerte|bon)\b", RegexOptions.IgnoreCase), "Bedrijfsadministratie"),
        (new(@"\b(belasting|tax|aangifte)\b",           RegexOptions.IgnoreCase), "Belastingen"),
        (new(@"\b(bankafschrift|belegging)\b",          RegexOptions.IgnoreCase), "Financiën"),
        (new(@"\b(verzekering|polis)\b",                RegexOptions.IgnoreCase), "Verzekeringen"),
        (new(@"\b(hypotheek|huurcontract)\b",           RegexOptions.IgnoreCase), "Woning"),
        (new(@"\b(medisch|recept|dokter)\b",            RegexOptions.IgnoreCase), "Gezondheid en Medisch")
    };

    public AIFileOrganizer(IOptions<AIOrganizerSettings> options,
                           ILogger<AIFileOrganizer> logger)
    {
        _cfg = options.Value ?? throw new ArgumentNullException(nameof(options));
        _log = logger        ?? throw new ArgumentNullException(nameof(logger));

        _supported = new(_cfg.SupportedExtensions.Select(e => e.ToLowerInvariant()),
                         StringComparer.OrdinalIgnoreCase);

        switch (_cfg.Provider)
        {
            case LlmProvider.Gemini:
                _gemini = new GoogleAI(_cfg.ApiKey);
                break;

            case LlmProvider.AzureOpenAI:
                if (string.IsNullOrWhiteSpace(_cfg.AzureEndpoint))
                    throw new ArgumentException("AzureEndpoint is verplicht voor Azure OpenAI.");
                // CORRECTED INSTANTIATION: Use fully qualified name or ensure no ambiguity
                _azure = new Azure.AI.OpenAI.AzureOpenAIClient(new Uri(_cfg.AzureEndpoint),
                                                          new AzureKeyCredential(_cfg.ApiKey));
                break;

            case LlmProvider.OpenAI:
                _openaiHttp = new HttpClient();
                _openaiHttp.DefaultRequestHeaders.Authorization =
                    new AuthenticationHeaderValue("Bearer", _cfg.ApiKey);
                break;
        }
    }

    //──────────── Main workflow (ongewijzigd) ────────────
    public async Task<(int processed, int moved)> OrganizeAsync(
       string srcDir, string dstDir,
       FilenameConfirmationHandler? confirm = null,
       IProgress<string>? progress = null,
       CancellationToken ct = default)
    {
        // ... existing setup ...
        int proc = 0, movedCount = 0; // Renamed 'moved' to 'movedCount' to avoid conflict

        foreach (var fi in new DirectoryInfo(srcDir).EnumerateFiles("*", SearchOption.AllDirectories))
        {
            ct.ThrowIfCancellationRequested();
            if (!_supported.Contains(fi.Extension.ToLowerInvariant()))
            {
                progress?.Report($"⏭️ {fi.Name}");
                continue;
            }

            proc++; progress?.Report($"📄 {fi.FullName}");
            string category = _cfg.FallbackCategory;
            string originalBaseName = Path.GetFileNameWithoutExtension(fi.Name); // Store original for metadata
            string currentBaseName = originalBaseName; // This will be modified by user confirmation
            string? aiSuggestedFilename = null; // To store AI's suggestion for metadata
            string extractedText = string.Empty;

            try
            {
                extractedText = await ExtractTextAsync(fi); // Store extracted text
                if (!string.IsNullOrWhiteSpace(extractedText))
                {
                    category = await ClassifyAsync(extractedText, ct);

                    if (_cfg.EnableDescriptiveFilenames && confirm != null)
                    {
                        string suggestion = await GenerateFilenameAsync(extractedText, fi.Name, category, ct);
                        aiSuggestedFilename = Sanitize(suggestion); // Store sanitized AI suggestion
                        currentBaseName = Sanitize(currentBaseName);

                        currentBaseName = await confirm(currentBaseName, aiSuggestedFilename, progress);
                        currentBaseName = Sanitize(currentBaseName); // Sanitize user's choice
                    }
                    else if (_cfg.EnableDescriptiveFilenames) // If confirm is null but descriptive names are on
                    {
                        // Optionally, auto-apply AI suggestion if no confirmation handler
                        // aiSuggestedFilename = await GenerateFilenameAsync(extractedText, fi.Name, category, ct);
                        // currentBaseName = Sanitize(aiSuggestedFilename);
                        // For now, let's keep currentBaseName as originalBaseName if no confirm handler
                    }
                }
            }
            catch (Exception ex)
            {
                _log.LogError(ex, $"Processing error for {fi.Name}");
                progress?.Report($"❌ Error processing {fi.Name}: {ex.Message}");
            }

            string targetFolderLabel = _cfg.Categories.TryGetValue(category, out var mapped)
                         ? mapped : $"0. {_cfg.FallbackCategory}";

            string tgtDir = Path.Combine(dstDir, targetFolderLabel);
            Directory.CreateDirectory(tgtDir);

            string finalFullFilename = Path.Combine(tgtDir, currentBaseName + fi.Extension);
            for (int n = 1; File.Exists(finalFullFilename); n++)
                finalFullFilename = Path.Combine(tgtDir, $"{currentBaseName}_{n}{fi.Extension}");

            try
            {
                fi.MoveTo(finalFullFilename);
                movedCount++;
                progress?.Report($"✅ {fi.Name} → {targetFolderLabel}{Path.DirectorySeparatorChar}{Path.GetFileName(finalFullFilename)}");

                // Generate metadata if enabled
                if (_cfg.GenerateMetadataFiles)
                {
                    await GenerateMetadataFileAsync(fi, finalFullFilename, category, targetFolderLabel,
                                                    aiSuggestedFilename, extractedText, progress, ct);
                }
            }
            catch (Exception ex)
            {
                _log.LogError(ex, $"Move failed for {fi.Name}");
                progress?.Report($"❌ Move failed for {fi.Name}: {ex.Message}");
            }
        }

        progress?.Report($"Done – {proc} processed, {movedCount} moved");
        return (proc, movedCount);
    }
    private async Task GenerateMetadataFileAsync(
        FileInfo originalFile,
        string newFullFilePath,
        string rawCategoryFromAI,
        string targetFolderLabel,
        string? aiSuggestedFilename, // Pass this in
        string extractedText,
        IProgress<string>? progress,
        CancellationToken ct)
    {
        var metadata = new FileMetadata
        {
            OriginalFullPath = originalFile.FullName,
            OriginalFilename = originalFile.Name,
            ProcessedTimestampUtc = DateTime.UtcNow,
            DetectedCategoryRaw = rawCategoryFromAI,
            TargetFolderLabel = targetFolderLabel,
            AISuggestedFilename = aiSuggestedFilename, // Use the passed-in value
            FinalFilename = Path.GetFileName(newFullFilePath),
            ExtractedTextPreview = extractedText.Length > 500
                                   ? extractedText.Substring(0, 500) + "..."
                                   : extractedText
            // Optional: Populate FileHash, AISummary, AITags here if you implement them
        };

        // Place metadata file next to the new file, with .metadata.json extension
        string metadataPath = Path.ChangeExtension(newFullFilePath, ".metadata.json");

        try
        {
            // Use System.Text.Json for serialization
            string json = JsonSerializer.Serialize(metadata, new JsonSerializerOptions { WriteIndented = true });
            await File.WriteAllTextAsync(metadataPath, json, ct);
            progress?.Report($"Ⓜ️ Metadata created for {Path.GetFileName(newFullFilePath)}");
        }
        catch (OperationCanceledException)
        {
            progress?.Report($"Ⓜ️ Metadata generation canceled for {Path.GetFileName(newFullFilePath)}");
        }
        catch (Exception ex)
        {
            _log.LogError(ex, $"Failed to write metadata for {originalFile.Name} to {metadataPath}");
            progress?.Report($"❌ Error writing metadata for {Path.GetFileName(newFullFilePath)}");
        }
    }
    //──────────── LLM helpers ────────────
    private async Task<string> ClassifyAsync(string text, CancellationToken ct)
    {
        string catList = string.Join(" | ", _cfg.Categories.Keys);
        string prompt = $"Return EXACTLY one of: {catList}\n\n" +
                         text[..Math.Min(text.Length, _cfg.MaxPromptChars)];

        string? ans = _cfg.Provider switch
        {
            LlmProvider.Gemini => await AskGeminiAsync(prompt, ct),
            LlmProvider.AzureOpenAI => await AskAzureAsync(prompt, ct),
            LlmProvider.OpenAI => await AskOpenAiAsync(prompt, ct),
            _ => null
        };

        if (string.IsNullOrWhiteSpace(ans)) return Heuristic(text);

        string norm = Normalize(ans);
        return _cfg.Categories.Keys.FirstOrDefault(k => Normalize(k) == norm) ?? Heuristic(text);
    }

    private async Task<string> GenerateFilenameAsync(string text, string original,
                                                     string category, CancellationToken ct)
    {
        string prompt =
            $"Suggest a concise filename (no extension) for category '{category}'.\n\n" +
            text[..Math.Min(text.Length, _cfg.MaxPromptChars)];

        string? ans = _cfg.Provider switch
        {
            LlmProvider.Gemini => await AskGeminiAsync(prompt, ct),
            LlmProvider.AzureOpenAI => await AskAzureAsync(prompt, ct),
            LlmProvider.OpenAI => await AskOpenAiAsync(prompt, ct),
            _ => null
        };

        return string.IsNullOrWhiteSpace(ans)
               ? Path.GetFileNameWithoutExtension(original)
               : Sanitize(ans);
    }

    //── Google Gemini
    private async Task<string?> AskGeminiAsync(string prompt, CancellationToken ct) =>
        (await _gemini!.GenerativeModel(_cfg.ModelName)
                       .GenerateContent(prompt, cancellationToken: ct))
        .Text?.Trim();

    //── Azure OpenAI
    private async Task<string?> AskAzureAsync(string prompt, CancellationToken ct)
    {
        if (_azure is null || string.IsNullOrWhiteSpace(_cfg.AzureDeployment))
        {
            _log.LogError("AzureOpenAIClient of deployment ontbreekt.");
            return null;
        }

        ChatClient chat = _azure.GetChatClient(_cfg.AzureDeployment);

        ChatCompletion completion = await chat.CompleteChatAsync(
            [
                new SystemChatMessage("You are a file-sorting assistant."),
            new UserChatMessage(prompt)
            ],
                  cancellationToken: ct);

        return completion.Content[0].Text.Trim();
    }


    //── Native OpenAI REST
    private async Task<string?> AskOpenAiAsync(string prompt, CancellationToken ct)
    {
        var req = new
        {
            model = _cfg.ModelName,
            messages = new[] { new { role = "user", content = prompt } },
            temperature = 0.2
        };

        using var resp = await _openaiHttp!.PostAsync(
            "https://api.openai.com/v1/chat/completions",
            new StringContent(JsonSerializer.Serialize(req), Encoding.UTF8, "application/json"), ct);

        resp.EnsureSuccessStatusCode();

        using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync(ct));
        return doc.RootElement.GetProperty("choices")[0]
                              .GetProperty("message")
                              .GetProperty("content")
                              .GetString()?.Trim();
    }

    //──────────── Text extraction (verkort) ────────────
    private async Task<string> ExtractTextAsync(FileInfo fi)
    {
        string ext = fi.Extension.ToLowerInvariant();

        if (ext is ".txt" or ".md")
            return await File.ReadAllTextAsync(fi.FullName, Encoding.UTF8);

        if (ext == ".docx")
        {
            try
            {
                using var doc = WordprocessingDocument.Open(fi.FullName, false);
                return doc.MainDocumentPart?.Document?.Body?.InnerText ?? string.Empty;
            }
            catch { return string.Empty; }
        }

        if (ext == ".pdf")
        {
            var sb = new StringBuilder();
            try
            {
                using var pdf = UglyToad.PdfPig.PdfDocument.Open(fi.FullName); // Fully qualified to be safe, though PdfDocument alias exists
                foreach (UglyToad.PdfPig.Content.Page p in pdf.GetPages()) sb.Append(p.Text).Append(' '); // Fully qualified to be safe, though PdfPage alias exists
            }
            catch { }

            if (sb.Length < 30 && _cfg.EnableOcr)
                sb.Append(await OcrHelper.RunAsync(fi.FullName));

            return sb.ToString();
        }

        return string.Empty;
    }

    //──────────── Utilities ────────────
    private string Heuristic(string txt)
    {
        foreach (var (rx, cat) in _keywords) if (rx.IsMatch(txt)) return cat;
        return _cfg.FallbackCategory;
    }

    private static string Normalize(string s)
    {
        var sb = new StringBuilder();
        foreach (var c in s.Normalize(NormalizationForm.FormD))
            if (CharUnicodeInfo.GetUnicodeCategory(c) != UnicodeCategory.NonSpacingMark)
                sb.Append(c);
        return Regex.Replace(sb.ToString(), "[^A-Za-z0-9]", "").ToLowerInvariant();
    }

    private static string Sanitize(string name)
    {
        char[] invalid = Path.GetInvalidFileNameChars();
        string clean = new(name.Select(c => invalid.Contains(c)||char.IsWhiteSpace(c) ? '_' : c).ToArray());
        clean = Regex.Replace(clean, "_+", "_");
        return clean.Length > 100 ? clean[..100] : clean.Trim('_');
    }

    internal static string SanitizeFilename(string input)
    {
        throw new NotImplementedException();
    }
}

//──────────── OCR stub ────────────
internal static class OcrHelper
{
    public static Task<string> RunAsync(string file) => Task.FromResult(string.Empty);
}

// Helper classes/enums that might be defined elsewhere (e.g., AIOrganizerSettings.cs)
// but are referenced here. Assuming they exist.

public class AIOrganizerSettings
{
    public LlmProvider Provider { get; set; }
    public string ApiKey { get; set; } = string.Empty;
    public string? AzureEndpoint { get; set; }
    public string ModelName { get; set; } = "gemini-1.5-flash-latest"; // Or your default
    public string? AzureDeployment { get; set; }
    public bool EnableFileRenaming { get; set; } = true;
    public bool GenerateMetadataFiles { get; set; } = false;
    public List<string> SupportedExtensions { get; set; } = new List<string> { ".txt", ".pdf", ".docx", ".md" };
    public string FallbackCategory { get; set; } = "Overig";
    public Dictionary<string, string> Categories { get; set; } = new Dictionary<string, string> {
        { "Bedrijfsadministratie", "1. Bedrijfsadministratie" },
        { "Belastingen", "2. Belastingen" },
        // ... add other default categories
    };
    public int MaxPromptChars { get; set; } = 4000;
    public bool EnableDescriptiveFilenames { get; set; } = true;
    public bool EnableOcr { get; set; } = false; // Assuming OCR is off by default
}

public enum LlmProvider
{
    Gemini,
    AzureOpenAI,
    OpenAI
}