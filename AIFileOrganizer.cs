// ---------- AIFileOrganizer.cs (v7 - Hierarchical Folders & Full Settings) ---
// Requires .NET 8+ (for C# 12 collection expressions used in Azure chat messages)
// If using .NET 7, replace `[...]` with `new List<ChatMessage> { ... }`
//
// NuGet packages:
//   • Mscc.GenerativeAI
//   • Azure.AI.OpenAI (≥1.0.0-beta.17, or latest stable)
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
using Mscc.GenerativeAI;  // Google Gemini
using Azure;
using Azure.AI.OpenAI;
using OpenAI.Chat;
using static AI_bestandsorganizer.FileUtils;

namespace AI_bestandsorganizer
{

    public delegate Task<string> FilenameConfirmationHandler(string originalBase,
                                                             string suggestedBase,
                                                             IProgress<string>? progress);

    public delegate Task<string> FolderPathConfirmationHandler(string predefinedRelativePath,
                                                               string suggestedRelativePath,
                                                               IProgress<string>? progress);



    //──────────────────────────────────── Organizer ────────────────────────
    public class AIFileOrganizer
    {
        private readonly AIOrganizerSettings _cfg;
        private readonly ILogger<AIFileOrganizer> _log;
        private readonly HashSet<string> _supportedFileExtensions;
        private readonly GoogleAI? _geminiClient;
        private readonly AzureOpenAIClient? _azureClient;
        private readonly HttpClient? _openAIHttpClient;

        private static readonly (Regex rx, string catKey)[] _keywords =
        {
            // --- FINANCES ---
            (new(@"\b(bankafschrift|rekeningoverzicht bank|bank statement)\b", RegexOptions.IgnoreCase), "Bankafschriften"),
            (new(@"\b(creditcard|kredietkaart|credit card statement)\b", RegexOptions.IgnoreCase), "Creditcardafschriften"),
            (new(@"\b(belegging|investering|portfolio overzicht|investment)\b", RegexOptions.IgnoreCase), "Beleggingen en Investeringen"),
            (new(@"\b(lening|schuld|hypotheekakte)\b", RegexOptions.IgnoreCase), "Leningen en Schulden"), // Hypotheekakte might also fit "Hypotheekdocumenten Woning"
            (new(@"\b(budget|uitgavenplan|spending plan)\b", RegexOptions.IgnoreCase), "Budgetten en Uitgavenplanning"),
            (new(@"\b(pensioenoverzicht|pension statement)\b", RegexOptions.IgnoreCase), "Pensioendocumenten Privé"),
            (new(@"\b(financieel rapport|financial statement)\b", RegexOptions.IgnoreCase), "Financiële Overzichten en Rapporten (Privé)"),
            (new(@"\b(donatie|gift|charity)\b", RegexOptions.IgnoreCase), "Donaties en Giften"),
            (new(@"\b(factuur betaald|paid invoice)\b", RegexOptions.IgnoreCase), "Facturen Betaald (Privé)"),
            (new(@"\b(garantiebewijs|aankoopbon|warranty|receipt)\b", RegexOptions.IgnoreCase), "Garantiebewijzen en Aankoopbonnen Algemeen"),

            // --- TAXES ---
            (new(@"\b(belastingaangifte|tax return|inkomstenbelasting)\b", RegexOptions.IgnoreCase), "Belastingaangiften Particulier"),
            (new(@"\b(belastingaanslag|tax assessment)\b", RegexOptions.IgnoreCase), "Belastingaanslagen Particulier"),
            (new(@"\b(toeslag|huurtoeslag|zorgtoeslag|benefit)\b", RegexOptions.IgnoreCase), "Toeslagen Belastingdienst (Huur, Zorg, etc.)"),
            (new(@"\b(brief belastingdienst|correspondentie fiscus)\b", RegexOptions.IgnoreCase), "Correspondentie Belastingdienst Particulier"),

            // --- INSURANCE ---
            (new(@"\b(zorgverzekering polis|health insurance policy)\b", RegexOptions.IgnoreCase), "Zorgverzekeringspolis"),
            (new(@"\b(declaratie zorg|zorgfactuur|medical bill)\b", RegexOptions.IgnoreCase), "Zorgverzekering Declaraties en Facturen"),
            (new(@"\b(autoverzekering polis|car insurance policy)\b", RegexOptions.IgnoreCase), "Autoverzekeringspolis"),
            (new(@"\b(inboedelverzekering polis|contents insurance)\b", RegexOptions.IgnoreCase), "Inboedelverzekeringspolis"),
            (new(@"\b(opstalverzekering polis|home insurance)\b", RegexOptions.IgnoreCase), "Opstalverzekeringspolis"),
            (new(@"\b(aansprakelijkheidsverzekering polis|liability insurance)\b", RegexOptions.IgnoreCase), "Aansprakelijkheidsverzekeringspolis Particulier"),
            (new(@"\b(reisverzekering polis|travel insurance)\b", RegexOptions.IgnoreCase), "Reisverzekeringspolis"),
            (new(@"\b(levensverzekering polis|life insurance)\b", RegexOptions.IgnoreCase), "Levensverzekeringspolis"),
            (new(@"\b(uitvaartverzekering polis|funeral insurance)\b", RegexOptions.IgnoreCase), "Uitvaartverzekeringspolis"),
            (new(@"\b(schadeclaim|insurance claim)\b", RegexOptions.IgnoreCase), "Schadeclaims Verzekeringen Particulier"),

            // --- HOUSING ---
            (new(@"\b(koopcontract woning|deed of sale house)\b", RegexOptions.IgnoreCase), "Koopcontract Woning"),
            (new(@"\b(hypotheek document|mortgage document)\b", RegexOptions.IgnoreCase), "Hypotheekdocumenten Woning"),
            (new(@"\b(huurcontract woning|rental agreement)\b", RegexOptions.IgnoreCase), "Huurcontract Woning"),
            (new(@"\b(VVE|vereniging van eigenaren)\b", RegexOptions.IgnoreCase), "VVE Documenten (Vereniging van Eigenaren)"),
            (new(@"\b(bouwtekening|verbouwing vergunning|building plan)\b", RegexOptions.IgnoreCase), "Bouwtekeningen en Vergunningen Verbouwing Woning"),
            (new(@"\b(energielabel|inspectierapport woning)\b", RegexOptions.IgnoreCase), "Energielabels en Inspectierapporten Woning"),
            (new(@"\b(WOZ waarde|property tax assessment)\b", RegexOptions.IgnoreCase), "WOZ Waarde Beschikkingen Woning"),
            (new(@"\b(contract energie|contract internet|utility contract)\b", RegexOptions.IgnoreCase), "Contracten Nutsvoorzieningen Woning"),
            (new(@"\b(onderhoudsfactuur woning|home repair invoice)\b", RegexOptions.IgnoreCase), "Onderhoudsfacturen en Reparaties Woning"),

            // --- HEALTH & MEDICAL ---
            (new(@"\b(doktersrekening|verwijsbrief arts|doctor's bill)\b", RegexOptions.IgnoreCase), "Doktersrekeningen en Verwijsbrieven"),
            (new(@"\b(recept|medicatieoverzicht|prescription)\b", RegexOptions.IgnoreCase), "Recepten en Medicatieoverzichten"),
            (new(@"\b(medisch onderzoek|labuitslag|scan resultaat|medical test results)\b", RegexOptions.IgnoreCase), "Medische Onderzoeksresultaten (Lab, Scans, etc.)"),
            (new(@"\b(vaccinatiebewijs|inentingsbewijs|vaccination certificate)\b", RegexOptions.IgnoreCase), "Vaccinatiebewijzen en Medische Paspoorten"),
            (new(@"\b(tandartsrekening|dental bill)\b", RegexOptions.IgnoreCase), "Tandartsdocumenten en Facturen"),
            (new(@"\b(ziekenhuisopname|hospital admission)\b", RegexOptions.IgnoreCase), "Ziekenhuisopname en Ontslagdocumenten"),
            (new(@"\b(medische verklaring|medical certificate)\b", RegexOptions.IgnoreCase), "Medische Verklaringen en Attesten"),
            (new(@"\b(fysiotherapie|paramedische zorg)\b", RegexOptions.IgnoreCase), "Fysiotherapie en Paramedische Zorg Documenten"),

            // --- FAMILY & CHILDREN ---
            (new(@"\b(geboorteakte|birth certificate|adoptiepapieren)\b", RegexOptions.IgnoreCase), "Geboorteaktes en Adoptiedocumenten"),
            (new(@"\b(schoolrapport|diploma kind|school report)\b", RegexOptions.IgnoreCase), "Schoolrapporten en Educatieve Documenten Kinderen"),
            (new(@"\b(kinderopvang contract|daycare contract)\b", RegexOptions.IgnoreCase), "Kinderopvang Contracten en Facturen"),
            (new(@"\b(huwelijksakte|geregistreerd partnerschap|marriage certificate)\b", RegexOptions.IgnoreCase), "Huwelijksakte of Geregistreerd Partnerschap Documenten"),
            (new(@"\b(samenlevingscontract|cohabitation agreement)\b", RegexOptions.IgnoreCase), "Samenlevingscontract"),
            (new(@"\b(echtscheidingspapieren|alimentatie|divorce papers)\b", RegexOptions.IgnoreCase), "Echtscheidingspapieren en Alimentatie"),
            (new(@"\b(testament|nalatenschap|will|estate planning)\b", RegexOptions.IgnoreCase), "Testamenten en Nalatenschapsplanning Familie"),
            (new(@"\b(kinderbijslag|jeugdzorg)\b", RegexOptions.IgnoreCase), "Correspondentie Kinderbijslag en Jeugdzorg"),

            // --- VEHICLES ---
            (new(@"\b(kentekenbewijs|autopapieren|vehicle registration)\b", RegexOptions.IgnoreCase), "Kentekenbewijzen en Registratiedocumenten Voertuig"),
            (new(@"\b(APK keuring|MOT test)\b", RegexOptions.IgnoreCase), "APK Keuringsrapporten Voertuig"),
            (new(@"\b(onderhoudsboekje auto|garagefactuur auto|car maintenance)\b", RegexOptions.IgnoreCase), "Onderhoudsboekjes en Facturen Voertuig"),
            (new(@"\b(aankoopcontract auto|verkoop auto|car purchase agreement)\b", RegexOptions.IgnoreCase), "Aankoopcontract en Verkoopdocumenten Voertuig"),
            (new(@"\b(schadeformulier auto|car accident report)\b", RegexOptions.IgnoreCase), "Schadeformulieren en Reparatieoffertes Voertuig"),
            (new(@"\b(verkeersboete|parking ticket)\b", RegexOptions.IgnoreCase), "Verkeersboetes en Gerelateerde Correspondentie"),

            // --- PERSONAL DOCUMENTS ---
            (new(@"\b(paspoort kopie|ID kaart kopie|passport copy)\b", RegexOptions.IgnoreCase), "Kopie Paspoort en Identiteitskaart"),
            (new(@"\b(rijbewijs kopie|driver's license copy)\b", RegexOptions.IgnoreCase), "Kopie Rijbewijs"),
            (new(@"\b(CV|curriculum vitae|motivatiebrief)\b", RegexOptions.IgnoreCase), "Persoonlijke CV en Motivatiebrieven (Algemeen)"), // Generic CV
            (new(@"\b(diploma persoonlijk|certificaat persoonlijk|personal certificate)\b", RegexOptions.IgnoreCase), "Persoonlijke Diploma's en Certificaten"),
            (new(@"\b(persoonlijke brief|personal letter)\b", RegexOptions.IgnoreCase), "Belangrijke Persoonlijke Correspondentie"),

            // --- HOBBIES & INTERESTS ---
            (new(@"\b(clublidmaatschap hobby|hobby club membership)\b", RegexOptions.IgnoreCase), "Clublidmaatschappen Hobby"),
            (new(@"\b(cursus hobby|workshop hobby)\b", RegexOptions.IgnoreCase), "Cursussen en Workshops Hobby"),
            (new(@"\b(verzameling|collection)\b", RegexOptions.IgnoreCase), "Documentatie Verzamelingen (Postzegels, etc.)"), // General for collections
            (new(@"\b(sport uitslag|wedstrijd sport)\b", RegexOptions.IgnoreCase), "Sportgerelateerde Documenten"),

            // --- CAREER & PROFESSIONAL DEVELOPMENT ---
            (new(@"\b(arbeidscontract|werkcontract|employment agreement)\b", RegexOptions.IgnoreCase), "Arbeidscontracten en Werkgeversverklaringen"),
            (new(@"\b(salarisstrook|loonstrook|payslip)\b", RegexOptions.IgnoreCase), "Salarisspecificaties en Jaaropgaven Werk"),
            (new(@"\b(beoordeling werk|functioneringsgesprek)\b", RegexOptions.IgnoreCase), "Beoordelingsformulieren en Functioneringsgesprekken Werk"),
            (new(@"\b(certificaat werk|training werk|professional certificate)\b", RegexOptions.IgnoreCase), "Werkgerelateerde Certificaten en Trainingen"),
            (new(@"\b(referentie werk|aanbevelingsbrief werk|job reference)\b", RegexOptions.IgnoreCase), "Referenties en Aanbevelingsbrieven Werk"),
            (new(@"\b(portfolio|werkvoorbeelden)\b", RegexOptions.IgnoreCase), "Portfolio en Werkvoorbeelden Professioneel"),
            (new(@"\b(professioneel lidmaatschap|beroepsvereniging)\b", RegexOptions.IgnoreCase), "Professionele Lidmaatschappen Werk"),
            (new(@"\b(pensioenregeling werkgever)\b", RegexOptions.IgnoreCase), "Pensioenregeling Werkgever Documenten"),
            (new(@"\b(sollicitatie|vacature|job application)\b", RegexOptions.IgnoreCase), "Sollicitaties en Vacatures"),

            // --- BUSINESS ADMINISTRATION ---
            (new(@"\b(inkomende factuur bedrijf|supplier invoice)\b", RegexOptions.IgnoreCase), "Zakelijke Inkomende Facturen"),
            (new(@"\b(uitgaande factuur bedrijf|offerte bedrijf|client invoice)\b", RegexOptions.IgnoreCase), "Zakelijke Uitgaande Facturen en Offertes"),
            (new(@"\b(zakelijk bankafschrift|business bank statement)\b", RegexOptions.IgnoreCase), "Zakelijke Bankafschriften"),
            (new(@"\b(contract klant|leverancierscontract|business contract)\b", RegexOptions.IgnoreCase), "Contracten Klanten en Leveranciers (Zakelijk)"),
            (new(@"\b(KvK uittreksel|Kamer van Koophandel|chamber of commerce)\b", RegexOptions.IgnoreCase), "KvK Uittreksels en Bedrijfsregistratie"),
            (new(@"\b(BTW aangifte|VAT return|ICP opgave)\b", RegexOptions.IgnoreCase), "BTW Aangiften en ICP Opgaven Zakelijk"),
            (new(@"\b(zakelijke polis|bedrijfsverzekering)\b", RegexOptions.IgnoreCase), "Zakelijke Verzekeringspolissen"),
            (new(@"\b(marketingplan|promotiemateriaal bedrijf)\b", RegexOptions.IgnoreCase), "Marketingplannen en Promotiemateriaal (Zakelijk)"),
            (new(@"\b(projectplan bedrijf|projectdocumentatie zakelijk)\b", RegexOptions.IgnoreCase), "Projectplanning en Documentatie (Zakelijk)"),
            (new(@"\b(jaarrekening bedrijf|balans bedrijf|annual report business)\b", RegexOptions.IgnoreCase), "Jaarrekeningen en Balansen (Zakelijk)"),
            (new(@"\b(algemene voorwaarden|terms and conditions)\b", RegexOptions.IgnoreCase), "Algemene Voorwaarden Bedrijf"),

            // --- TRAVEL & HOLIDAYS ---
            (new(@"\b(vliegticket|treinticket|boarding pass)\b", RegexOptions.IgnoreCase), "Vervoerstickets en Reserveringen Reizen"),
            (new(@"\b(hotelboeking|accommodatie reservering|hotel booking)\b", RegexOptions.IgnoreCase), "Hotel- en Accommodatieboekingen Reizen"),
            (new(@"\b(reisroute|reisplanning|itinerary)\b", RegexOptions.IgnoreCase), "Reisroutes en Planning Documenten"),
            (new(@"\b(visumaanvraag|visa application)\b", RegexOptions.IgnoreCase), "Visumaanvragen en Reisdocumenten (Niet Paspoort)"),
            // "Reisverzekering Documenten (Specifiek voor reis)" is tricky for keywords if already covered by general travel insurance keywords

            // --- LEGAL & OFFICIAL DOCUMENTS ---
            (new(@"\b(notariële akte|deed|notary)\b", RegexOptions.IgnoreCase), "Notariële Akten (Algemeen)"),
            (new(@"\b(juridische brief|advocaat correspondentie|legal letter)\b", RegexOptions.IgnoreCase), "Juridische Correspondentie en Adviezen Algemeen"),
            (new(@"\b(dagvaarding|gerechtelijk document|summons)\b", RegexOptions.IgnoreCase), "Gerechtelijke Documenten en Dagvaardingen"),
            (new(@"\b(volmacht|power of attorney)\b", RegexOptions.IgnoreCase), "Volmachten en Machtigingen Algemeen"),

            // --- EDUCATION & STUDY (Personal) ---
            (new(@"\b(inschrijving studie|college registration)\b", RegexOptions.IgnoreCase), "Inschrijvingsbewijzen Persoonlijke Studie"),
            (new(@"\b(lesmateriaal|studieaantekeningen|course material)\b", RegexOptions.IgnoreCase), "Lesmateriaal en Aantekeningen Persoonlijke Studie"),
            (new(@"\b(studieresultaat|scriptie|thesis)\b", RegexOptions.IgnoreCase), "Studieresultaten en Scripties Persoonlijke Studie"),
            (new(@"\b(studiefinanciering|student loan)\b", RegexOptions.IgnoreCase), "Studiefinanciering Documenten Persoonlijk"),

            // --- MANUALS & INSTRUCTIONS ---
            (new(@"\b(handleiding|gebruiksaanwijzing|manual|instructions)\b", RegexOptions.IgnoreCase), "Handleidingen Apparatuur en Electronica"), // Generic manual
            (new(@"\b(softwarelicentie|installatie instructies)\b", RegexOptions.IgnoreCase), "Softwarelicenties en Installatie-instructies"),
            (new(@"\b(montage instructie|assembly instructions)\b", RegexOptions.IgnoreCase), "Montage-instructies Meubels en Producten"),

            // --- DIGITAL SERVICES & SUBSCRIPTIONS ---
            (new(@"\b(abonnement streaming|netflix|spotify)\b", RegexOptions.IgnoreCase), "Abonnementen Streamingdiensten en Media"),
            (new(@"\b(software abonnement|SaaS)\b", RegexOptions.IgnoreCase), "Online Software Abonnementen (SaaS)"),
            (new(@"\b(gaming abonnement|steam|playstation plus)\b", RegexOptions.IgnoreCase), "Gaming Abonnementen en Aankopen"),
            (new(@"\b(cloud opslag|dropbox|google drive)\b", RegexOptions.IgnoreCase), "Cloudopslag Abonnementen en Facturen"),
            (new(@"\b(domeinregistratie|webhosting)\b", RegexOptions.IgnoreCase), "Domeinregistratie en Hosting"),

            // --- ARCHIVAL ---
            // Archival is usually determined by age or user decision, harder for keywords unless specific terms like "archief" appear.

            // --- PETS ---
            (new(@"\b(dierenarts|vaccinatie huisdier|pet vaccination)\b", RegexOptions.IgnoreCase), "Dierenartsrekeningen en Vaccinatieboekjes Huisdier"),
            (new(@"\b(chipregistratie huisdier|pet registration)\b", RegexOptions.IgnoreCase), "Registratie en Chipgegevens Huisdier"),
            (new(@"\b(huisdierverzekering|pet insurance)\b", RegexOptions.IgnoreCase), "Verzekeringspolis Huisdier"),
        };
        public AIFileOrganizer(IOptions<AIOrganizerSettings> options,
                               ILogger<AIFileOrganizer> logger)
        {
            _cfg = options.Value ?? throw new ArgumentNullException(nameof(options));
            _log = logger ?? throw new ArgumentNullException(nameof(logger));

            if (_cfg.Categories == null || !_cfg.Categories.Any())
            {
                _log.LogWarning("Categorieënlijst in AIOrganizerSettings is leeg of null. Organisatie zal sterk afhankelijk zijn van de fallback categorie.");
                // Application might still function if FallbackCategory is set, but classification will be poor.
                // Consider throwing an ArgumentException if categories are essential.
            }


            _supportedFileExtensions = new HashSet<string>(_cfg.SupportedExtensions.Select(e => e.ToLowerInvariant()),
                                            StringComparer.OrdinalIgnoreCase);

            _log.LogInformation($"AIFileOrganizer initialiseren met provider: {_cfg.Provider}");
            switch (_cfg.Provider)
            {
                case LlmProvider.Gemini:
                    if (string.IsNullOrWhiteSpace(_cfg.ApiKey))
                        throw new ArgumentException("ApiKey is verplicht voor Gemini provider.", nameof(_cfg.ApiKey));
                    _geminiClient = new GoogleAI(_cfg.ApiKey);
                    _log.LogInformation("Gemini client geïnitialiseerd.");
                    break;

                case LlmProvider.AzureOpenAI:
                    if (string.IsNullOrWhiteSpace(_cfg.AzureEndpoint))
                        throw new ArgumentException("AzureEndpoint is verplicht voor Azure OpenAI provider.", nameof(_cfg.AzureEndpoint));
                    if (string.IsNullOrWhiteSpace(_cfg.ApiKey))
                        throw new ArgumentException("ApiKey is verplicht voor Azure OpenAI provider.", nameof(_cfg.ApiKey));
                    if (string.IsNullOrWhiteSpace(_cfg.AzureDeployment))
                        throw new ArgumentException("AzureDeployment is verplicht voor Azure OpenAI provider.", nameof(_cfg.AzureDeployment));
                    _azureClient = new AzureOpenAIClient(new Uri(_cfg.AzureEndpoint), new AzureKeyCredential(_cfg.ApiKey));
                    _log.LogInformation($"Azure OpenAI client geïnitialiseerd voor endpoint: {_cfg.AzureEndpoint} en deployment: {_cfg.AzureDeployment}");
                    break;

                case LlmProvider.OpenAI:
                    if (string.IsNullOrWhiteSpace(_cfg.ApiKey))
                        throw new ArgumentException("ApiKey is verplicht voor OpenAI provider.", nameof(_cfg.ApiKey));
                    _openAIHttpClient = new HttpClient();
                    _openAIHttpClient.DefaultRequestHeaders.Authorization =
                        new AuthenticationHeaderValue("Bearer", _cfg.ApiKey);
                    _openAIHttpClient.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
                    _log.LogInformation("Standaard OpenAI HTTP client geïnitialiseerd.");
                    break;
                default:
                    _log.LogError($"Ongeldige LLM provider geconfigureerd: {_cfg.Provider}. AI-functies zullen niet werken.");
                    // Consider throwing an exception if AI functionality is critical
                    break;
            }
        }



        public async Task<(int processed, int moved)> OrganizeAsync(
          string srcDir, string dstDir,
          FilenameConfirmationHandler? confirmFilename = null,
          FolderPathConfirmationHandler? confirmFolderPath = null,
          IProgress<string>? progress = null,
          CancellationToken ct = default)
        {
            srcDir = Path.GetFullPath(srcDir);
            dstDir = Path.GetFullPath(dstDir);

            if (!Directory.Exists(srcDir))
            {
                progress?.Report($"❌ Bronmap niet gevonden: {srcDir}");
                _log.LogError($"Bronmap niet gevonden: {srcDir}");
                throw new DirectoryNotFoundException($"Bronmap niet gevonden: {srcDir}");
            }

            _log.LogInformation($"Organisatie gestart. Bron: '{srcDir}', Doel: '{dstDir}'. SearchSubdirectories: {_cfg.SearchSubdirectories}");
            progress?.Report($"🔍 Scannen van bronmap: {srcDir} (inclusief submappen: {_cfg.SearchSubdirectories})...");

            int proc = 0, movedCount = 0;
            List<FileInfo> filesToProcess;
            try
            {
                filesToProcess = new DirectoryInfo(srcDir)
                    .EnumerateFiles("*", _cfg.SearchSubdirectories ? SearchOption.AllDirectories : SearchOption.TopDirectoryOnly)
                    .ToList();
            }
            catch (Exception ex)
            {
                _log.LogError(ex, $"Fout bij het ophalen van bestanden uit bronmap: {srcDir}");
                progress?.Report($"❌ Fout bij toegang tot bronmap: {ex.Message}");
                return (0, 0); // Early exit
            }

            progress?.Report($"ℹ️ {filesToProcess.Count} bestanden gevonden in bronmap.");
            if (!filesToProcess.Any())
            {
                progress?.Report("🏁 Geen bestanden gevonden om te verwerken.");
                _log.LogInformation("Geen bestanden gevonden in bronmap om te verwerken.");
                return (0, 0); // Early exit
            }

            foreach (var fi in filesToProcess)
            {
                ct.ThrowIfCancellationRequested();

                if (!_supportedFileExtensions.Contains(fi.Extension.ToLowerInvariant()))
                {
                    progress?.Report($"⏭️ Overslaan (extensie niet ondersteund): {fi.Name}");
                    _log.LogDebug($"Overslaan (extensie niet ondersteund): {fi.FullName}");
                    continue; // Next file
                }

                proc++;
                progress?.Report($"📄 Verwerken ({proc}/{filesToProcess.Count}): {fi.Name}");
                _log.LogInformation($"Verwerken bestand: {fi.FullName}");

                string categoryKey = _cfg.FallbackCategory;
                string originalBaseName = Path.GetFileNameWithoutExtension(fi.Name);
                string currentBaseName = originalBaseName;
                string? aiSuggestedFilenameForMetadata = null; // AI's raw suggestion for filename
                string extractedText = string.Empty;
                string predefinedTargetRelativePath;

                try
                {
                    extractedText = await ExtractTextAsync(fi, ct);
                    if (!string.IsNullOrWhiteSpace(extractedText))
                    {
                        _log.LogDebug($"Tekst geëxtraheerd uit {fi.Name} (lengte: {extractedText.Length}). Starten classificatie...");
                        categoryKey = await ClassifyAsync(extractedText, ct);
                        _log.LogInformation($"Bestand {fi.Name} geclassificeerd met KEY: '{categoryKey}'.");

                        predefinedTargetRelativePath = _cfg.Categories.TryGetValue(categoryKey, out var mappedPath)
                                                     ? mappedPath
                                                     : _cfg.Categories.TryGetValue(_cfg.FallbackCategory, out var fallbackMappedPath)
                                                       ? fallbackMappedPath
                                                       : FileUtils.SanitizePathPart(_cfg.FallbackCategory);

                        if (_cfg.EnableFileRenaming && _cfg.EnableDescriptiveFilenames)
                        {
                            string rawAiFilenameSuggestion = await GenerateFilenameAsync(extractedText, fi.Name, categoryKey, ct);
                            aiSuggestedFilenameForMetadata = FileUtils.SanitizeAsFilename(rawAiFilenameSuggestion);

                            if (confirmFilename != null)
                            {
                                string sanitizedOriginalBase = FileUtils.SanitizeAsFilename(originalBaseName);
                                currentBaseName = await confirmFilename(sanitizedOriginalBase, aiSuggestedFilenameForMetadata, progress);
                                currentBaseName = FileUtils.SanitizeAsFilename(currentBaseName);
                            }
                            else
                            {
                                currentBaseName = aiSuggestedFilenameForMetadata;
                            }
                        }
                        else
                        {
                            currentBaseName = FileUtils.SanitizeAsFilename(originalBaseName);
                        }
                    }
                    else
                    {
                        _log.LogWarning($"Geen tekst geëxtraheerd uit {fi.Name}. Gebruik fallback categorie KEY: '{_cfg.FallbackCategory}'.");
                        progress?.Report($"⚠️ Geen tekst uit {fi.Name}, gebruik fallback: {_cfg.FallbackCategory}");
                        currentBaseName = FileUtils.SanitizeAsFilename(originalBaseName);
                        predefinedTargetRelativePath = _cfg.Categories.TryGetValue(_cfg.FallbackCategory, out var fallbackMappedPath)
                                                       ? fallbackMappedPath
                                                       : FileUtils.SanitizePathPart(_cfg.FallbackCategory);
                    }
                }
                catch (OperationCanceledException) { throw; }
                catch (Exception ex)
                {
                    _log.LogError(ex, $"Fout tijdens extractie/classificatie/naamgeving van {fi.Name}.");
                    progress?.Report($"❌ Fout bij {fi.Name}: {ex.Message.Split('\n')[0]}. Gebruik fallback.");
                    categoryKey = _cfg.FallbackCategory;
                    currentBaseName = FileUtils.SanitizeAsFilename(originalBaseName);
                    predefinedTargetRelativePath = _cfg.Categories.TryGetValue(_cfg.FallbackCategory, out var fallbackMappedPath)
                                                   ? fallbackMappedPath
                                                   : FileUtils.SanitizePathPart(_cfg.FallbackCategory);
                }

                string finalTargetRelativePath = predefinedTargetRelativePath; // Initialize with (unsanitized) predefined

                if (_cfg.EnableAISuggestedFolders && !string.IsNullOrWhiteSpace(extractedText))
                {
                    _log.LogInformation($"AI-suggestie voor volledige mappenstructuur is ingeschakeld voor {fi.Name}.");
                    string sanitizedPredefinedPathForHint = FileUtils.SanitizePathStructure(predefinedTargetRelativePath);
                    string aiSuggestedFullPathRaw = await GenerateFolderPathAsync(extractedText, categoryKey, sanitizedPredefinedPathForHint, ct);
                    string aiSuggestedFullPathSanitized = string.Empty;

                    if (!string.IsNullOrWhiteSpace(aiSuggestedFullPathRaw))
                    {
                        aiSuggestedFullPathSanitized = FileUtils.SanitizePathStructure(aiSuggestedFullPathRaw);
                    }

                    if (confirmFolderPath != null)
                    {
                        string suggestionForDialog = !string.IsNullOrWhiteSpace(aiSuggestedFullPathSanitized) ? aiSuggestedFullPathSanitized : sanitizedPredefinedPathForHint;
                        _log.LogDebug($"Vooraf gedefinieerd pad (hint): '{sanitizedPredefinedPathForHint}', AI-gesuggereerd volledig pad: '{aiSuggestedFullPathSanitized}'. Wachten op bevestiging.");

                        finalTargetRelativePath = await confirmFolderPath(sanitizedPredefinedPathForHint, suggestionForDialog, progress);
                        // User's choice from dialog is already sanitized by FolderPathInputDialog's ProcessAndClose
                        // But, to be absolutely sure, we can sanitize again.
                        finalTargetRelativePath = FileUtils.SanitizePathStructure(finalTargetRelativePath);

                        _log.LogInformation($"Gebruiker koos doelmap: '{finalTargetRelativePath}' voor {fi.Name}.");
                    }
                    else
                    {
                        finalTargetRelativePath = !string.IsNullOrWhiteSpace(aiSuggestedFullPathSanitized) ? aiSuggestedFullPathSanitized : sanitizedPredefinedPathForHint;
                        _log.LogInformation($"Automatisch toegepaste doelmap: '{finalTargetRelativePath}' voor {fi.Name}.");
                    }
                }
                else
                {
                    if (_cfg.EnableAISuggestedFolders && string.IsNullOrWhiteSpace(extractedText))
                    {
                        _log.LogWarning($"AI-suggestie voor mappenstructuur ingeschakeld, maar geen tekst uit {fi.Name}. Gebruikt: '{predefinedTargetRelativePath}'.");
                    }
                    finalTargetRelativePath = FileUtils.SanitizePathStructure(predefinedTargetRelativePath);
                }

                string targetDirectory = Path.Combine(dstDir, finalTargetRelativePath);
                _log.LogDebug($"Doelmap voor {fi.Name} wordt: '{targetDirectory}'.");

                try
                {
                    Directory.CreateDirectory(targetDirectory);
                }
                catch (Exception ex)
                {
                    _log.LogError(ex, $"Kan doelmap niet aanmaken: {targetDirectory}. Bestand {fi.Name} wordt overgeslagen.");
                    progress?.Report($"❌ Kan map niet aanmaken: '{targetDirectory}'. Overslaan {fi.Name}.");
                    continue;
                }

                string tempFilenameForConflict = currentBaseName; // Use the name determined by renaming logic
                string finalFullFilename = Path.Combine(targetDirectory, tempFilenameForConflict + fi.Extension);
                int conflictCounter = 1;
                while (File.Exists(finalFullFilename))
                {
                    tempFilenameForConflict = $"{currentBaseName}_{conflictCounter++}"; // Use original currentBaseName for numbering
                    finalFullFilename = Path.Combine(targetDirectory, tempFilenameForConflict + fi.Extension);
                }
                // The actual final name on disk (if changed due to conflict)
                string actualFinalBaseNameOnDisk = tempFilenameForConflict;

                _log.LogDebug($"Definitieve bestandsnaam voor {fi.Name} wordt: '{Path.GetFileName(finalFullFilename)}'.");

                try
                {
                    fi.MoveTo(finalFullFilename);
                    movedCount++;
                    progress?.Report($"✅ {fi.Name} → {finalTargetRelativePath}{Path.DirectorySeparatorChar}{Path.GetFileName(finalFullFilename)}");
                    _log.LogInformation($"Bestand {fi.Name} verplaatst naar {finalFullFilename}.");

                    if (_cfg.GenerateMetadataFiles)
                    {
                        // aiSuggestedFilenameForMetadata stores the AI's idea before user interaction or conflict numbering.
                        // actualFinalBaseNameOnDisk + fi.Extension is what's actually on disk.
                        await GenerateMetadataFileAsync(fi, finalFullFilename, categoryKey, finalTargetRelativePath,
                                                        aiSuggestedFilenameForMetadata,
                                                        extractedText, progress, ct);
                    }
                }
                catch (OperationCanceledException) { throw; }
                catch (Exception ex)
                {
                    _log.LogError(ex, $"Verplaatsen of metadata voor {fi.Name} naar {finalFullFilename} mislukt.");
                    progress?.Report($"❌ Fout bij verplaatsen/metadata {fi.Name}: {ex.Message.Split('\n')[0]}");
                }
            } // End of foreach loop

            progress?.Report($"🏁 Klaar! {movedCount} van de {proc} verwerkte bestanden verplaatst.");
            _log.LogInformation($"Organisatie voltooid. {movedCount}/{proc} bestanden verplaatst.");
            return (proc, movedCount);
        }
    
            private async Task<string> GenerateFilenameAsync(string text, string originalFilename, string categoryKey, CancellationToken ct)
            {
                string originalBaseName = Path.GetFileNameWithoutExtension(originalFilename);
                if (_geminiClient == null && _azureClient == null && _openAIHttpClient == null)
                {
                    _log.LogWarning("Geen LLM provider geconfigureerd voor bestandsnaam generatie. Gebruik originele gesaneerde naam.");
                    return FileUtils.SanitizeAsFilename(originalBaseName);
                }
                // ... (rest of the method, ensure FileUtils.SanitizeAsFilename is used for the result and fallback)
                string prompt = $"{_cfg.SystemPrompt}\n\nGegeven de volgende tekst en de categorie '{_cfg.Categories.GetValueOrDefault(categoryKey, categoryKey)}', " +
                                $"stel een korte, beschrijvende, Engelse bestandsnaam voor (alleen letters, cijfers, underscores; geen spaties of speciale tekens; geen bestandsextensie). " +
                                $"De originele bestandsnaam was '{originalBaseName}'. Focus op de kerninhoud en houd het beknopt (maximaal 5-7 woorden).\n\n" +
                                $"Tekst:\n\"\"\"\n{text[..Math.Min(text.Length, _cfg.MaxPromptCharsFilename)]}\n\"\"\"";
                string? ans = null; // ... LLM call ...
                                    // ... (LLM call logic as before) ...
                if (string.IsNullOrWhiteSpace(ans))
                {
                    return FileUtils.SanitizeAsFilename(originalBaseName);
                }
                return FileUtils.SanitizeAsFilename(ans.Trim());
            }

            private async Task<string> GenerateFolderPathAsync(string text, string classifiedCategoryKey, string predefinedBasePathHint, CancellationToken ct)
            {
                if (_geminiClient == null && _azureClient == null && _openAIHttpClient == null)
                {
                    _log.LogWarning("Geen LLM provider geconfigureerd voor mappad generatie. Geeft lege pad terug.");
                    return string.Empty;
                }

                // The classifiedCategoryKey and predefinedBasePathHint are now more for context/inspiration
                // rather than strict prefixing by the AI.
                string prompt = $"{_cfg.AISuggestedFoldersSystemPrompt}\n\n" +
                                $"Document inhoud (fragment):\n\"\"\"\n{text[..Math.Min(text.Length, _cfg.MaxPromptCharsFilename)]}\n\"\"\"\n" +
                                $"Algemene categorie (ter referentie): '{classifiedCategoryKey}' (bekend als '{_cfg.Categories.GetValueOrDefault(classifiedCategoryKey, classifiedCategoryKey)}')\n" +
                                $"Vooraf gedefinieerd pad voor deze categorie (ter inspiratie, niet als verplicht prefix): '{predefinedBasePathHint}'\n" +
                                $"Stel een volledig, logisch en beschrijvend relatief mappad voor om dit document op te slaan. Het pad hoeft niet te beginnen met de bovenstaande referenties, maar mag dat wel als het zinvol is. Gebruik '/' als scheidingsteken.";

                _log.LogDebug($"Mappad generatie prompt (start): {prompt.Substring(0, Math.Min(prompt.Length, 300))}(...)");

                string? ans = null;
                // ... (LLM call logic as before, ensuring the correct system prompt is used if needed by AskAzureAsync/AskOpenAIAsync) ...
                ans = _cfg.Provider switch
                {
                    LlmProvider.Gemini => await AskGeminiAsync(prompt, ct),
                    LlmProvider.AzureOpenAI => await AskAzureAsync(_azureClient, _cfg.AzureDeployment, prompt, _cfg.AISuggestedFoldersSystemPrompt, ct),
                    LlmProvider.OpenAI => await AskOpenAiAsync(prompt, ct), // Might need system prompt override here
                    _ => null
                };


                if (string.IsNullOrWhiteSpace(ans))
                {
                    _log.LogWarning("LLM gaf geen antwoord of een leeg antwoord voor mappad generatie.");
                    return string.Empty;
                }

                _log.LogDebug($"LLM antwoord voor volledig mappad (ruw): '{ans}'");
                // Sanitize the full path suggested by AI
                string sanitizedFullPath = FileUtils.SanitizePathStructure(ans.Trim());
                _log.LogInformation($"Gesaneerd AI volledig mappad suggestie: '{sanitizedFullPath}'.");

                // Check if AI returned a placeholder or effectively empty path
                if (string.IsNullOrWhiteSpace(sanitizedFullPath) ||
                    sanitizedFullPath == "_" ||
                    sanitizedFullPath.Equals(FileUtils.SanitizePathStructure(""), StringComparison.OrdinalIgnoreCase) || // What SanitizePathStructure returns for ""
                    sanitizedFullPath.Equals("Default_Path", StringComparison.OrdinalIgnoreCase) ||
                    sanitizedFullPath.Equals("Uncategorized_Path", StringComparison.OrdinalIgnoreCase))
                {
                    _log.LogDebug("AI gaf een leeg of placeholder pad terug. Beschouw als geen suggestie.");
                    return string.Empty;
                }
                return sanitizedFullPath;
            }
            private async Task GenerateMetadataFileAsync(
                FileInfo originalFile, // Original FileInfo
                string newFullFilePath, // Full path of the moved file
                string detectedCategoryKey, // The KEY AI identified (e.g., "Bankafschriften")
                string targetFolderRelativePath, // The VALUE from Categories (e.g., "1. Financiën/1.01. Bankafschriften")
                string? aiSuggestedFilename,
                string extractedText,
                IProgress<string>? progress,
                CancellationToken ct)
            {
                var metadata = new FileMetadata
                {
                    OriginalFullPath = originalFile.FullName,
                    OriginalFilename = originalFile.Name,
                    ProcessedTimestampUtc = DateTime.UtcNow,
                    DetectedCategoryKey = detectedCategoryKey,
                    TargetFolderRelativePath = targetFolderRelativePath,
                    AISuggestedFilename = aiSuggestedFilename,
                    FinalFilename = Path.GetFileName(newFullFilePath),
                    ExtractedTextPreview = extractedText.Length > _cfg.MetadataExtractedTextPreviewLength
                                           ? extractedText.Substring(0, _cfg.MetadataExtractedTextPreviewLength) + "..."
                                           : extractedText
                };

                string metadataPath = Path.ChangeExtension(newFullFilePath, ".metadata.json");
                _log.LogDebug($"Genereren metadata bestand: {metadataPath}");

                try
                {
                    ct.ThrowIfCancellationRequested();
                    string json = JsonSerializer.Serialize(metadata, new JsonSerializerOptions { WriteIndented = true });
                    await File.WriteAllTextAsync(metadataPath, json, ct);
                    progress?.Report($"Ⓜ️ Metadata voor {Path.GetFileName(newFullFilePath)}");
                }
                catch (OperationCanceledException)
                {
                    _log.LogWarning($"Metadata generatie geannuleerd voor {Path.GetFileName(newFullFilePath)}.");
                    progress?.Report($"⚠️ Metadata generatie geannuleerd voor {Path.GetFileName(newFullFilePath)}");
                    // Optionally delete partially written file if that's a concern
                }
                catch (Exception ex)
                {
                    _log.LogError(ex, $"Schrijven metadata voor {originalFile.Name} naar {metadataPath} mislukt.");
                    progress?.Report($"❌ Fout bij metadata voor {Path.GetFileName(newFullFilePath)}");
                }
            }

            private async Task<string> ClassifyAsync(string text, CancellationToken ct)
            {
                if (_geminiClient == null && _azureClient == null && _openAIHttpClient == null)
                {
                    _log.LogWarning("Geen LLM provider geconfigureerd voor classificatie. Gebruik heuristiek.");
                    return Heuristic(text);
                }

                if (_cfg.Categories == null || !_cfg.Categories.Any())
                {
                    _log.LogWarning("Categorieënlijst is leeg in configuratie. Gebruik fallback categorie KEY.");
                    return _cfg.FallbackCategory;
                }

                string catKeysList = string.Join(" | ", _cfg.Categories.Keys);
                string prompt = $"{_cfg.SystemPrompt}\n\nAnalyseer de volgende tekst en classificeer deze in EXACT EEN van de volgende categorieën. " +
                                $"Antwoord ALLEEN met de exacte categorienaam uit de verstrekte lijst.\n" +
                                $"Beschikbare categorieën: {catKeysList}\n\n" +
                                $"Tekst om te classificeren:\n\"\"\"\n{text[..Math.Min(text.Length, _cfg.MaxPromptChars)]}\n\"\"\"";
                _log.LogDebug($"Classificatie prompt (start): {prompt.Substring(0, Math.Min(prompt.Length, 200))}(...)");

                string? ans = null;
                try
                {
                    ct.ThrowIfCancellationRequested();
                    ans = _cfg.Provider switch
                    {
                        LlmProvider.Gemini => await AskGeminiAsync(prompt, ct),
                        LlmProvider.AzureOpenAI => await AskAzureAsync(_azureClient, _cfg.AzureDeployment, prompt, _cfg.SystemPrompt, ct),
                        LlmProvider.OpenAI => await AskOpenAiAsync(prompt, ct),
                        _ => null // Should have been caught by initial check
                    };
                }
                catch (OperationCanceledException) { throw; }
                catch (Exception ex)
                {
                    _log.LogError(ex, $"Fout tijdens LLM API call voor classificatie (Provider: {_cfg.Provider}).");
                }

                if (string.IsNullOrWhiteSpace(ans))
                {
                    _log.LogWarning("LLM gaf geen antwoord of een leeg antwoord voor classificatie. Gebruik heuristiek.");
                    return Heuristic(text);
                }

                _log.LogDebug($"LLM antwoord voor classificatie (ruw): '{ans}'");

                string normalizedAns = Normalize(ans);
                string? matchedKey = _cfg.Categories.Keys.FirstOrDefault(k => Normalize(k) == normalizedAns);

                if (matchedKey != null)
                {
                    _log.LogInformation($"Genormaliseerd LLM antwoord '{normalizedAns}' komt overeen met categorie KEY: '{matchedKey}'.");
                    return matchedKey;
                }
                else
                {
                    // Log if the answer was close to any key (optional, for debugging)
                    foreach (var key in _cfg.Categories.Keys)
                    {
                        if (ans.Contains(key, StringComparison.OrdinalIgnoreCase) || key.Contains(ans, StringComparison.OrdinalIgnoreCase))
                        {
                            _log.LogDebug($"LLM antwoord '{ans}' bevat/is bevattend in KEY '{key}', maar normalisatie mislukte match.");
                            break;
                        }
                    }
                    _log.LogWarning($"Genormaliseerd LLM antwoord '{normalizedAns}' (origineel: '{ans}') komt niet overeen met een bekende categorie KEY. Gebruik heuristiek.");
                    return Heuristic(text);
                }
            }



            private async Task<string?> AskGeminiAsync(string prompt, CancellationToken ct)
            {
                if (_geminiClient == null) return null;

                _log.LogDebug($"Verzoek naar Gemini model: {_cfg.ModelName}");

                var model = _geminiClient.GenerativeModel(_cfg.ModelName);

                try
                {
                    var start = DateTime.UtcNow;
                    var response = await Task.Run(() => model.GenerateContent(prompt), ct);
                    var duration = DateTime.UtcNow - start;

                    _log.LogInformation($"Gemini antwoord ontvangen in {duration.TotalMilliseconds} ms.");
                    _log.LogDebug($"Gemini response tokens approx. (schatting): prompt={prompt.Length / 4}, response={response.Text?.Length / 4}");

                    return response.Text?.Trim();
                }
                catch (Exception ex)
                {
                    _log.LogError(ex, $"Fout tijdens Gemini API-aanroep.");
                    return null;
                }
            }

            private async Task<string?> AskOpenAiAsync(string prompt, CancellationToken ct)
            {
                if (_openAIHttpClient == null)
                {
                    _log.LogError("OpenAI HTTP client is niet geïnitialiseerd.");
                    return null;
                }

                var requestBody = new
                {
                    model = _cfg.ModelName,
                    messages = new[]
                    {
            new { role = "system", content = _cfg.SystemPrompt },
            new { role = "user", content = prompt }
        },
                    max_tokens = _cfg.MaxCompletionTokens,
                    temperature = _cfg.Temperature,
                };

                var content = new StringContent(JsonSerializer.Serialize(requestBody), Encoding.UTF8, "application/json");

                try
                {
                    var start = DateTime.UtcNow;
                    var response = await _openAIHttpClient.PostAsync(_cfg.OpenAICompletionsEndpoint, content, ct);
                    var duration = DateTime.UtcNow - start;

                    string responseJson = await response.Content.ReadAsStringAsync(ct);
                    _log.LogInformation($"OpenAI antwoord ontvangen in {duration.TotalMilliseconds} ms voor model: {_cfg.ModelName}");

                    if (!response.IsSuccessStatusCode)
                    {
                        _log.LogError($"OpenAI fout {response.StatusCode}: {responseJson}");
                        return null;
                    }

                    using var jsonDoc = JsonDocument.Parse(responseJson);
                    var root = jsonDoc.RootElement;

                    // Log tokens if present
                    if (root.TryGetProperty("usage", out var usage))
                    {
                        _log.LogDebug($"Tokens gebruikt: prompt={usage.GetProperty("prompt_tokens")}, completion={usage.GetProperty("completion_tokens")}, totaal={usage.GetProperty("total_tokens")}");
                    }

                    return root.GetProperty("choices")[0].GetProperty("message").GetProperty("content").GetString()?.Trim();
                }
                catch (Exception ex)
                {
                    _log.LogError(ex, "Fout tijdens OpenAI API-aanroep.");
                    return null;
                }
            }



            public async Task<string?> AskAzureAsync(
                AzureOpenAIClient azureClient,
                string deploymentName,
                string userPrompt,
                string systemPrompt,
                CancellationToken ct)
            {
                if (azureClient == null || string.IsNullOrWhiteSpace(deploymentName))
                {
                    _log.LogError("Azure client of deployment niet correct geïnitialiseerd.");
                    return null;
                }

                try
                {
                    // Maak de ChatClient aan
                    ChatClient chatClient = azureClient.GetChatClient(deploymentName);

                    // Bouw het chatverloop op
                    var messages = new List<OpenAI.Chat.ChatMessage>();
                    if (!string.IsNullOrWhiteSpace(systemPrompt))
                        messages.Add(new SystemChatMessage(systemPrompt));

                    messages.Add(new UserChatMessage(userPrompt));

                    // (Optioneel) voeg eerdere assistant-berichten toe voor context

                    var start = DateTime.UtcNow;

                    // Maak een completions request
                    ChatCompletion completion = await chatClient.CompleteChatAsync(messages, cancellationToken: ct);

                    var duration = DateTime.UtcNow - start;
                    _log.LogInformation($"Azure OpenAI antwoord ontvangen in {duration.TotalMilliseconds} ms voor deployment: {deploymentName}");

                    // Log token usage indien aanwezig
                    if (completion.Usage is not null)
                    {
                        _log.LogDebug($"Tokens gebruikt: prompt={completion.Usage.ToString()}, completion={completion.Usage.ToString()}, totaal={completion.Usage.ToString()}");
                    }

                    // Haal het eerste assistant-antwoord op (er kan maar één zijn bij 1 vraag)
                    string? result = completion.Content?[0]?.Text?.Trim();

                    return result;
                }
                catch (RequestFailedException ex)
                {
                    _log.LogError(ex, $"Azure SDK Fout tijdens Azure OpenAI aanroep. Status: {ex.Status}, ErrorCode: {ex.ErrorCode}, Details: {ex.Message}");
                    return null;
                }
                catch (Exception ex)
                {
                    _log.LogError(ex, "Algemene fout tijdens Azure OpenAI aanroep.");
                    return null;
                } } 
    






    private async Task<string> ExtractTextAsync(FileInfo fi, CancellationToken ct)
        {
            string ext = fi.Extension.ToLowerInvariant();
            _log.LogDebug($"Starten tekst extractie voor {fi.Name} (extensie: {ext})");
            var sb = new StringBuilder();

            try
            {
                ct.ThrowIfCancellationRequested();
                if (ext is ".txt" or ".md")
                {
                    sb.Append(await File.ReadAllTextAsync(fi.FullName, Encoding.UTF8, ct));
                }
                else if (ext == ".docx")
                {
                    // WordprocessingDocument.Open is synchronous, consider wrapping in Task.Run if it becomes a bottleneck
                    // For now, assuming it's fast enough for typical document sizes.
                    await Task.Yield(); // Allow other async operations to proceed if UI is responsive.
                    using var doc = WordprocessingDocument.Open(fi.FullName, false);
                    sb.Append(doc.MainDocumentPart?.Document?.Body?.InnerText ?? string.Empty);
                }
                else if (ext == ".pdf")
                {
                    await Task.Yield();
                    using var pdfDoc = UglyToad.PdfPig.PdfDocument.Open(fi.FullName);
                    foreach (var page in pdfDoc.GetPages())
                    {
                        ct.ThrowIfCancellationRequested();
                        sb.Append(page.Text).Append(' ');
                    }

                    if (sb.Length < _cfg.OcrTriggerMinTextLength && _cfg.EnableOcr)
                    {
                        _log.LogInformation($"PdfPig extraheerde weinig tekst ({sb.Length} karakters) uit {fi.Name}. OCR wordt uitgevoerd indien geconfigureerd.");
                        string ocrText = await OcrHelper.RunAsync(fi.FullName, _log, ct); // Pass CancellationToken
                        if (!string.IsNullOrWhiteSpace(ocrText))
                        {
                            sb.Append($"\n\n--- OCR Resultaat ({DateTime.Now:g}) ---\n").Append(ocrText);
                        }
                    }
                }
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex)
            {
                _log.LogError(ex, $"Fout tijdens tekst extractie uit {fi.Name}.");
                return string.Empty;
            }

            string resultText = sb.ToString().Trim();
            _log.LogDebug($"Tekst extractie voltooid voor {fi.Name}. Lengte: {resultText.Length}.");
            return resultText;
        }

        private string Heuristic(string txt)
        {
            if (string.IsNullOrWhiteSpace(txt))
            {
                _log.LogDebug("Heuristiek: Lege tekst, gebruik fallback categorie KEY.");
                return _cfg.FallbackCategory;
            }

            foreach (var (rx, catKey) in _keywords)
            {
                if (rx.IsMatch(txt))
                {
                    _log.LogInformation($"Heuristiek toegepast: tekst bevat patroon '{rx}', gematcht met categorie KEY: '{catKey}'.");
                    return catKey;
                }
            }
            _log.LogInformation($"Geen heuristische match gevonden. Gebruik fallback categorie KEY: '{_cfg.FallbackCategory}'.");
            return _cfg.FallbackCategory;
        }

        private static string Normalize(string s)
        {
            if (string.IsNullOrWhiteSpace(s)) return string.Empty;

            var sb = new StringBuilder();
            foreach (char c in s.Normalize(NormalizationForm.FormD)) // Decompose combined characters
            {
                if (CharUnicodeInfo.GetUnicodeCategory(c) != UnicodeCategory.NonSpacingMark) // Remove diacritics
                {
                    sb.Append(char.ToLowerInvariant(c)); // Convert to lowercase
                }
            }
            // Remove all non-alphanumeric characters
            return Regex.Replace(sb.ToString(), @"[^a-z0-9]+", string.Empty, RegexOptions.Compiled);
        }


        private static readonly char[] _invalidPathChars = Path.GetInvalidPathChars().Concat(new[] { ':', '*', '?', '"', '<', '>', '|' }).Distinct().ToArray();
        private static readonly char[] _invalidFileNameChars = Path.GetInvalidFileNameChars().Concat(new[] { ':', '*', '?', '"', '<', '>', '|', '/', '\\' }).Distinct().ToArray();

        private static string SanitizePart(string namePart)
        {
            if (string.IsNullOrWhiteSpace(namePart)) return "_";

            namePart = Regex.Replace(namePart, @"\s+", "_"); // Replace whitespace with single underscore
            var sb = new StringBuilder();
            foreach (char c in namePart)
            {
                // Allow common punctuation used in naming conventions if they are not invalid for file/path names
                if (char.IsLetterOrDigit(c) || c == '_' || c == '-' || c == '.' || c == '(' || c == ')')
                {
                    sb.Append(c);
                }
                // Check against a combined list of invalid chars for both file and path segments
                else if (_invalidPathChars.Contains(c) || _invalidFileNameChars.Contains(c))
                {
                    sb.Append('_');
                }
                else if (c < 32) // Control characters
                {
                    sb.Append('_');
                }
                else // If not explicitly allowed and not invalid, consider it for replacement based on strictness
                {
                    // For stricter sanitization, replace unknown/unexpected symbols
                    // For now, let's be a bit more permissive if it's not strictly invalid
                    // but this is a point of potential refinement.
                    // If you want very strict alphanumeric + underscore, adjust the first condition.
                    // sb.Append('_'); // Example of stricter replacement
                    sb.Append(c); // Current: allow more if not explicitly invalid
                }
            }
            string clean = sb.ToString();
            clean = Regex.Replace(clean, "_+", "_"); // Collapse multiple underscores
            clean = clean.TrimStart('_').TrimEnd('_'); // Trim leading/trailing underscores

            // Prevent names that are just dots or problematic sequences
            if (string.IsNullOrWhiteSpace(clean) || clean == "." || clean == "..")
            {
                clean = "_";
            }

            const int MaxPartLength = 100; // Max length for a single folder/file name part
            if (clean.Length > MaxPartLength)
            {
                clean = clean.Substring(0, MaxPartLength).TrimEnd('_');
            }
            return string.IsNullOrWhiteSpace(clean) ? "_" : clean;
        }

        private static string SanitizePath(string relativePath)
        {
            if (string.IsNullOrWhiteSpace(relativePath))
            {
                // Accessing _cfg here is problematic as this is a static method.
                // Pass FallbackCategory or have a hardcoded default for sanitization.
                // For now, using a hardcoded default if _cfg is not available (e.g. direct call).
                return SanitizePart("Uncategorized_Path");
            }

            var parts = relativePath.Split(new[] { Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar }, StringSplitOptions.RemoveEmptyEntries);
            var sanitizedParts = parts.Select(SanitizePart).Where(p => !string.IsNullOrWhiteSpace(p) && p != "_"); // Filter out empty or underscore-only parts

            string result = string.Join(Path.DirectorySeparatorChar.ToString(), sanitizedParts);

            return string.IsNullOrWhiteSpace(result) ? SanitizePart("Default_Path") : result;
        }

        internal async Task<(int processed, int moved)> OrganizeAsync(string text1, string text2, FilenameConfirmationHandler filenameConfirmer, Progress<string> prog, CancellationToken token)
        {
            throw new NotImplementedException();
        }
    }

    internal static class OcrHelper // Keep as internal or make public if called from elsewhere
    {
        public static Task<string> RunAsync(string filePath, ILogger logger, CancellationToken ct = default)
        {
            logger.LogInformation($"OCR stub aangeroepen voor: {filePath}. Echte OCR is niet geïmplementeerd.");
            // In a real implementation:
            // ct.ThrowIfCancellationRequested();
            // ... OCR logic ...
            // Example:
            // if (File.Exists("path/to/tesseract.exe")) { /* ... */ }
            // else { logger.LogWarning("Tesseract OCR engine not found."); }
            return Task.FromResult(string.Empty);
        }
    }
}