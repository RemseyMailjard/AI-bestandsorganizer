using System.Collections.Generic;

namespace AI_bestandsorganizer
{
    // Define LlmProvider enum if it's not already in a shared location
    public enum LlmProvider
    {
        Gemini,
        AzureOpenAI,
        OpenAI
    }

    public class AIOrganizerSettings
    {
        // LLM Provider Configuration
        public LlmProvider Provider { get; set; } = LlmProvider.Gemini; // Default provider
        public string ApiKey { get; set; } = string.Empty; // Must be set in appsettings.json or UI

        // Azure OpenAI Specific
        public string? AzureEndpoint { get; set; }
        public string? AzureDeployment { get; set; } // Deployment name for Azure OpenAI

        // OpenAI (Direct API) Specific
        public string OpenAICompletionsEndpoint { get; set; } = "https://api.openai.com/v1/chat/completions";

        // Model and Prompt Configuration
        public string ModelName { get; set; } = "gemini-1.5-pro-latest"; // Default model
        public string SystemPrompt { get; set; } = "You are a highly intelligent and meticulous assistant specialized in document analysis and organization. Your goal is to accurately categorize documents and suggest concise, relevant filenames.";
        public int MaxPromptChars { get; set; } = 8000; // For classification prompt
        public int MaxPromptCharsFilename { get; set; } = 2000; // For filename generation prompt (can be shorter)
        public int MaxCompletionTokens { get; set; } = 150; // Max tokens for LLM response (classification/filename)
        public double Temperature { get; set; } = 0.2; // LLM temperature (0.0 to 1.0 or 2.0 depending on model)

        // File Processing Configuration
        public List<string> SupportedExtensions { get; set; } = new List<string> { ".pdf", ".docx", ".txt", ".md" };
        public bool SearchSubdirectories { get; set; } = true; // Whether to search subdirectories in the source path

        // Renaming Configuration
        public bool EnableFileRenaming { get; set; } = true; // Master switch for renaming
        public bool EnableDescriptiveFilenames { get; set; } = true; // AI to suggest descriptive names (requires EnableFileRenaming)

        // OCR Configuration
        public bool EnableOcr { get; set; } = false; // Enable OCR for PDFs with little text
        public int OcrTriggerMinTextLength { get; set; } = 50; // If PdfPig extracts less than this, trigger OCR (if enabled)

        // Metadata Configuration
        public bool GenerateMetadataFiles { get; set; } = false; // Generate .metadata.json files
        public int MetadataExtractedTextPreviewLength { get; set; } = 500; // Length of text preview in metadata

        // Categorization Configuration
        public string FallbackCategory { get; set; } = "Overig"; // This is the KEY for the fallback category

        public Dictionary<string, string> Categories { get; set; } = new Dictionary<string, string>
        {
            // --- FINANCES ---
            // AI Key (used in prompts and for mapping AI response) : Relative Folder Path (used for directory creation)
            { "Bankafschriften",                                   "1. Financiën/1.01. Bankafschriften" },
            { "Creditcardafschriften",                             "1. Financiën/1.02. Creditcardafschriften" },
            { "Beleggingen en Investeringen",                      "1. Financiën/1.03. Beleggingen en Investeringen" },
            { "Leningen en Schulden",                              "1. Financiën/1.04. Leningen en Schulden" },
            { "Budgetten en Uitgavenplanning",                     "1. Financiën/1.05. Budgetten en Uitgavenplanning" },
            { "Pensioendocumenten Privé",                          "1. Financiën/1.06. Pensioendocumenten Privé" }, // Specified "Privé"
            { "Financiële Overzichten en Rapporten (Privé)",       "1. Financiën/1.07. Financiële Overzichten en Rapporten (Privé)" },
            { "Donaties en Giften",                                "1. Financiën/1.08. Donaties en Giften" },
            { "Facturen Betaald (Privé)",                          "1. Financiën/1.09. Facturen Betaald (Privé)" },
            { "Garantiebewijzen en Aankoopbonnen Algemeen",        "1. Financiën/1.10. Garantiebewijzen en Aankoopbonnen Algemeen" },

            // --- TAXES ---
            { "Belastingaangiften Particulier",                    "2. Belastingen/2.01. Belastingaangiften Particulier" },
            { "Belastingaanslagen Particulier",                    "2. Belastingen/2.02. Belastingaanslagen Particulier" },
            { "Toeslagen Belastingdienst (Huur, Zorg, etc.)",      "2. Belastingen/2.03. Toeslagen Belastingdienst" },
            { "Correspondentie Belastingdienst Particulier",       "2. Belastingen/2.04. Correspondentie Belastingdienst Particulier" },

            // --- INSURANCE ---
            { "Zorgverzekeringspolis",                             "3. Verzekeringen/3.01. Zorgverzekering/3.01.01. Polis Zorgverzekering" },
            { "Zorgverzekering Declaraties en Facturen",           "3. Verzekeringen/3.01. Zorgverzekering/3.01.02. Declaraties en Facturen Zorg" },
            { "Autoverzekeringspolis",                             "3. Verzekeringen/3.02. Autoverzekering/3.02.01. Polis Autoverzekering" }, // Could also be under Voertuigen
            { "Inboedelverzekeringspolis",                         "3. Verzekeringen/3.03. Inboedelverzekering/3.03.01. Polis Inboedelverzekering" },
            { "Opstalverzekeringspolis",                           "3. Verzekeringen/3.04. Opstalverzekering/3.04.01. Polis Opstalverzekering" },
            { "Aansprakelijkheidsverzekeringspolis Particulier",   "3. Verzekeringen/3.05. Aansprakelijkheidsverzekering/3.05.01. Polis AVP" },
            { "Reisverzekeringspolis",                             "3. Verzekeringen/3.06. Reisverzekering/3.06.01. Polis Reisverzekering" }, // Could also be under Reizen
            { "Levensverzekeringspolis",                           "3. Verzekeringen/3.07. Levensverzekering/3.07.01. Polis Levensverzekering" },
            { "Uitvaartverzekeringspolis",                         "3. Verzekeringen/3.08. Uitvaartverzekering/3.08.01. Polis Uitvaartverzekering" },
            { "Overige Verzekeringspolissen Particulier",          "3. Verzekeringen/3.09. Overige Verzekeringen/3.09.01. Polissen Overig" },
            { "Schadeclaims Verzekeringen Particulier",            "3. Verzekeringen/3.10. Schadeclaims Particulier" },

            // --- HOUSING ---
            { "Koopcontract Woning",                               "4. Woning/4.01. Koopcontract en Eigendom" },
            { "Hypotheekdocumenten Woning",                        "4. Woning/4.02. Hypotheek" },
            { "Huurcontract Woning",                               "4. Woning/4.03. Huurcontract" },
            { "VVE Documenten (Vereniging van Eigenaren)",         "4. Woning/4.04. VVE Documenten" },
            { "Bouwtekeningen en Vergunningen Verbouwing Woning",  "4. Woning/4.05. Bouwtekeningen en Verbouwingen" },
            { "Energielabels en Inspectierapporten Woning",        "4. Woning/4.06. Energielabels en Inspecties" },
            { "WOZ Waarde Beschikkingen Woning",                   "4. Woning/4.07. WOZ Waarde" },
            { "Contracten Nutsvoorzieningen Woning",               "4. Woning/4.08. Nutsvoorzieningen (Energie, Water, Internet)" },
            { "Onderhoudsfacturen en Reparaties Woning",           "4. Woning/4.09. Onderhoud en Reparaties Woning" },
            { "Garantiebewijzen Apparatuur Woning",                "4. Woning/4.10. Garantiebewijzen Apparatuur Woning" },

            // --- HEALTH & MEDICAL ---
            { "Doktersrekeningen en Verwijsbrieven",               "5. Gezondheid en Medisch/5.01. Huisarts en Specialisten" },
            { "Recepten en Medicatieoverzichten",                  "5. Gezondheid en Medisch/5.02. Medicatie" },
            { "Medische Onderzoeksresultaten (Lab, Scans, etc.)",  "5. Gezondheid en Medisch/5.03. Onderzoeksresultaten" },
            { "Vaccinatiebewijzen en Medische Paspoorten",         "5. Gezondheid en Medisch/5.04. Vaccinaties en Paspoorten" },
            { "Tandartsdocumenten en Facturen",                    "5. Gezondheid en Medisch/5.05. Tandarts" },
            { "Ziekenhuisopname en Ontslagdocumenten",             "5. Gezondheid en Medisch/5.06. Ziekenhuis" },
            { "Medische Verklaringen en Attesten",                 "5. Gezondheid en Medisch/5.07. Verklaringen en Attesten" },
            { "Fysiotherapie en Paramedische Zorg Documenten",     "5. Gezondheid en Medisch/5.08. Fysiotherapie en Paramedische Zorg" },

            // --- FAMILY & CHILDREN ---
            { "Geboorteaktes en Adoptiedocumenten",                "6. Familie en Kinderen/6.01. Geboorte en Adoptie" },
            { "Schoolrapporten en Educatieve Documenten Kinderen", "6. Familie en Kinderen/6.02. School en Educatie Kinderen" },
            { "Kinderopvang Contracten en Facturen",               "6. Familie en Kinderen/6.03. Kinderopvang" },
            { "Huwelijksakte of Geregistreerd Partnerschap Documenten", "6. Familie en Kinderen/6.04. Huwelijk of Partnerschap" },
            { "Samenlevingscontract",                              "6. Familie en Kinderen/6.05. Samenlevingscontract" },
            { "Echtscheidingspapieren en Alimentatie",             "6. Familie en Kinderen/6.06. Echtscheiding en Alimentatie" },
            { "Testamenten en Nalatenschapsplanning Familie",      "6. Familie en Kinderen/6.07. Testamenten en Nalatenschap" },
            { "Correspondentie Kinderbijslag en Jeugdzorg",        "6. Familie en Kinderen/6.08. Overheidsinstanties Kind" },

            // --- VEHICLES ---
            { "Kentekenbewijzen en Registratiedocumenten Voertuig","7. Voertuigen/7.01. Registratie en Kenteken" },
            { "APK Keuringsrapporten Voertuig",                    "7. Voertuigen/7.02. APK Keuringen" },
            { "Onderhoudsboekjes en Facturen Voertuig",            "7. Voertuigen/7.03. Onderhoud en Reparaties Voertuig" },
            { "Aankoopcontract en Verkoopdocumenten Voertuig",     "7. Voertuigen/7.04. Aankoop en Verkoop Voertuig" },
            { "Schadeformulieren en Reparatieoffertes Voertuig",   "7. Voertuigen/7.05. Schade en Reparatie Voertuig" },
            { "Verkeersboetes en Gerelateerde Correspondentie",    "7. Voertuigen/7.06. Verkeersboetes" },
            { "Garantiebewijzen Voertuigonderdelen",               "7. Voertuigen/7.07. Garantiebewijzen Onderdelen" },

            // --- PERSONAL DOCUMENTS ---
            { "Kopie Paspoort en Identiteitskaart",                "8. Persoonlijke Documenten/8.01. Identiteitsbewijzen (Kopieën)" },
            { "Kopie Rijbewijs",                                   "8. Persoonlijke Documenten/8.02. Rijbewijs (Kopie)" },
            { "Persoonlijke CV en Motivatiebrieven (Algemeen)",    "8. Persoonlijke Documenten/8.03. CV en Motivatiebrieven (Persoonlijk)" }, // If not under Career
            { "Persoonlijke Diploma's en Certificaten",            "8. Persoonlijke Documenten/8.04. Diploma's en Certificaten (Persoonlijk)" }, // If not under Career or Education
            { "Belangrijke Persoonlijke Correspondentie",          "8. Persoonlijke Documenten/8.05. Persoonlijke Correspondentie" },
            { "Lidmaatschapskaarten en Bewijzen (Divers)",         "8. Persoonlijke Documenten/8.06. Lidmaatschappen Divers" },

            // --- HOBBIES & INTERESTS ---
            { "Clublidmaatschappen Hobby",                         "9. Hobbies en Interesses/9.01. Clublidmaatschappen Hobby" },
            { "Cursussen en Workshops Hobby",                      "9. Hobbies en Interesses/9.02. Cursussen en Workshops Hobby" },
            { "Documentatie Verzamelingen (Postzegels, etc.)",     "9. Hobbies en Interesses/9.03. Verzamelingen Documentatie" },
            { "Sportgerelateerde Documenten",                      "9. Hobbies en Interesses/9.04. Sport" },
            { "Creatieve Projecten Documentatie",                  "9. Hobbies en Interesses/9.05. Creatieve Projecten" },

            // --- CAREER & PROFESSIONAL DEVELOPMENT ---
            { "Arbeidscontracten en Werkgeversverklaringen",       "10. Carrière en Werk/10.01. Arbeidscontracten" },
            { "Salarisspecificaties en Jaaropgaven Werk",          "10. Carrière en Werk/10.02. Salarisdocumenten" },
            { "Beoordelingsformulieren en Functioneringsgesprekken Werk","10. Carrière en Werk/10.03. Beoordelingen en Gesprekken" },
            { "Werkgerelateerde Certificaten en Trainingen",       "10. Carrière en Werk/10.04. Werkgerelateerde Trainingen" },
            { "Referenties en Aanbevelingsbrieven Werk",           "10. Carrière en Werk/10.05. Referenties Werk" },
            { "Portfolio en Werkvoorbeelden Professioneel",        "10. Carrière en Werk/10.06. Portfolio en Werkvoorbeelden" },
            { "Professionele Lidmaatschappen Werk",                "10. Carrière en Werk/10.07. Professionele Lidmaatschappen" },
            { "Pensioenregeling Werkgever Documenten",             "10. Carrière en Werk/10.08. Pensioenregeling Werkgever" },
            { "Sollicitaties en Vacatures",                        "10. Carrière en Werk/10.09. Sollicitaties" },

            // --- BUSINESS ADMINISTRATION (For Freelancers/Small Business Owners) ---
            { "Zakelijke Inkomende Facturen",                      "11. Bedrijfsadministratie/11.01. Inkomende Facturen" },
            { "Zakelijke Uitgaande Facturen en Offertes",          "11. Bedrijfsadministratie/11.02. Uitgaande Facturen en Offertes" },
            { "Zakelijke Bankafschriften",                         "11. Bedrijfsadministratie/11.03. Zakelijke Bankafschriften" },
            { "Contracten Klanten en Leveranciers (Zakelijk)",     "11. Bedrijfsadministratie/11.04. Zakelijke Contracten" },
            { "KvK Uittreksels en Bedrijfsregistratie",            "11. Bedrijfsadministratie/11.05. KvK en Registratie" },
            { "BTW Aangiften en ICP Opgaven Zakelijk",             "11. Bedrijfsadministratie/11.06. Zakelijke Belastingen (BTW, ICP)" },
            { "Zakelijke Verzekeringspolissen",                    "11. Bedrijfsadministratie/11.07. Zakelijke Verzekeringen" },
            { "Marketingplannen en Promotiemateriaal (Zakelijk)",  "11. Bedrijfsadministratie/11.08. Marketing en Promotie" },
            { "Projectplanning en Documentatie (Zakelijk)",        "11. Bedrijfsadministratie/11.09. Zakelijke Projecten" },
            { "Jaarrekeningen en Balansen (Zakelijk)",             "11. Bedrijfsadministratie/11.10. Financiële Rapporten Bedrijf" },
            { "Algemene Voorwaarden Bedrijf",                      "11. Bedrijfsadministratie/11.11. Algemene Voorwaarden" },

            // --- TRAVEL & HOLIDAYS ---
            { "Vervoerstickets en Reserveringen Reizen",           "12. Reizen en Vakanties/12.01. Vervoerstickets" },
            { "Hotel- en Accommodatieboekingen Reizen",            "12. Reizen en Vakanties/12.02. Accommodatieboekingen" },
            { "Reisroutes en Planning Documenten",                 "12. Reizen en Vakanties/12.03. Reisplanning" },
            { "Visumaanvragen en Reisdocumenten (Niet Paspoort)",  "12. Reizen en Vakanties/12.04. Visa en Reisdocumenten" },
            { "Reisverzekering Documenten (Specifiek voor reis)",  "12. Reizen en Vakanties/12.05. Reisverzekering (Specifiek)" },
            { "Digitale Tickets Evenementen op Reis",              "12. Reizen en Vakanties/12.06. Tickets Evenementen Reis" },

            // --- LEGAL & OFFICIAL DOCUMENTS (General) ---
            { "Notariële Akten (Algemeen)",                        "13. Juridisch en Officieel/13.01. Notariële Akten" },
            { "Juridische Correspondentie en Adviezen Algemeen",   "13. Juridisch en Officieel/13.02. Juridische Correspondentie" },
            { "Gerechtelijke Documenten en Dagvaardingen",         "13. Juridisch en Officieel/13.03. Gerechtelijke Documenten" },
            { "Volmachten en Machtigingen Algemeen",               "13. Juridisch en Officieel/13.04. Volmachten" },
            { "Identificatie Officieel (Anders dan ID/Paspoort)",  "13. Juridisch en Officieel/13.05. Overige Officiële Identificatie" },

            // --- EDUCATION & STUDY (Personal, not kids or career-specific training) ---
            { "Inschrijvingsbewijzen Persoonlijke Studie",         "14. Opleiding en Studie (Persoonlijk)/14.01. Inschrijvingen Studie" },
            { "Lesmateriaal en Aantekeningen Persoonlijke Studie", "14. Opleiding en Studie (Persoonlijk)/14.02. Lesmateriaal Studie" },
            { "Studieresultaten en Scripties Persoonlijke Studie", "14. Opleiding en Studie (Persoonlijk)/14.03. Resultaten en Scripties Studie" },
            { "Studiefinanciering Documenten Persoonlijk",         "14. Opleiding en Studie (Persoonlijk)/14.04. Studiefinanciering" },
            { "Persoonlijke Ontwikkeling Cursussen",               "14. Opleiding en Studie (Persoonlijk)/14.05. Persoonlijke Ontwikkeling Cursussen" },

            // --- MANUALS & INSTRUCTIONS ---
            { "Handleidingen Apparatuur en Electronica",           "15. Handleidingen en Instructies/15.01. Handleidingen Apparatuur" },
            { "Softwarelicenties en Installatie-instructies",      "15. Handleidingen en Instructies/15.02. Softwarelicenties en Instructies" },
            { "Montage-instructies Meubels en Producten",          "15. Handleidingen en Instructies/15.03. Montage-instructies" },
            { "Onderhoudsinstructies Divers",                      "15. Handleidingen en Instructies/15.04. Onderhoudsinstructies" },

            // --- DIGITAL SERVICES & SUBSCRIPTIONS ---
            { "Abonnementen Streamingdiensten en Media",           "16. Digitaal Leven/16.01. Abonnementen Media en Streaming" },
            { "Online Software Abonnementen (SaaS)",               "16. Digitaal Leven/16.02. Software Abonnementen (SaaS)" },
            { "Gaming Abonnementen en Aankopen",                   "16. Digitaal Leven/16.03. Gaming" },
            { "Cloudopslag Abonnementen en Facturen",              "16. Digitaal Leven/16.04. Cloudopslag" },
            { "Domeinregistratie en Hosting",                      "16. Digitaal Leven/16.05. Domeinen en Hosting" },

            // --- ARCHIVAL (Less frequently accessed items) ---
            { "Gearchiveerde Persoonlijke Projecten",              "17. Archief/17.01. Gearchiveerde Projecten" },
            { "Oude Correspondentie (Archief)",                    "17. Archief/17.02. Oude Correspondentie" },
            { "Verlopen Contracten en Documenten (Archief)",       "17. Archief/17.03. Verlopen Documenten" },
            { "Historische Financiële Documenten (Archief)",       "17. Archief/17.04. Historische Financiën" },

            // --- PETS ---
            { "Dierenartsrekeningen en Vaccinatieboekjes Huisdier","18. Huisdieren/18.01. Dierenarts en Vaccinaties" },
            { "Registratie en Chipgegevens Huisdier",              "18. Huisdieren/18.02. Registratie en Identificatie" },
            { "Verzekeringspolis Huisdier",                        "18. Huisdieren/18.03. Verzekering Huisdier" },
            { "Aankoopdocumenten Huisdier",                        "18. Huisdieren/18.04. Aankoop Huisdier" },

            // --- FALLBACK ---
            // The KEY "Overig" is used as FallbackCategory.
            // The VALUE "99. Overig/Ongeclassificeerd" defines the folder path.
            { "Overig",                                            "99. Overig/99.01. Ongeclassificeerd" }
        };
    }
}