✨ AI Bestandsorganizer (WPF) ✨
Een slimme desktop applicatie gebouwd met WPF en C# die de Google Gemini API inzet om je bestanden automatisch te categoriseren en, indien gewenst, een beschrijvende naam te geven. Zeg vaarwel tegen rommelige downloadmappen!
🌟 Functies
Intelligente Bestandsclassificatie: Analyseert de inhoud van documenten (PDF, DOCX, TXT, MD) met behulp van Google Gemini om ze in vooraf gedefinieerde categorieën in te delen.
AI-gestuurde Bestandsnaamgeneratie: Stelt een beschrijvende en menselijk leesbare bestandsnaam voor op basis van de documentinhoud en de geclassificeerde categorie.
Interactieve Hernoemingsbevestiging: Biedt een pop-upvenster voor elke voorgestelde bestandsnaam, zodat je deze kunt accepteren, de originele kunt behouden of een eigen naam kunt invoeren. Deze functie is in- of uitschakelbaar.
Diepgaande Mapscan: Zoekt automatisch naar bestanden in de opgegeven bronmap en al zijn submappen.
Flexibele Configuratie: Categorieën, ondersteunde bestandstypen, API-instellingen en meer zijn eenvoudig aan te passen via appsettings.json.
Moderne Gebruikersinterface: Een strakke, professionele donkere UI voor een prettige gebruikerservaring.
🚀 Aan de slag
Volg deze stappen om de AI Bestandsorganizer op te zetten en te draaien.
Vereisten
.NET 8 SDK
Een Google Gemini API-sleutel (Gratis te verkrijgen via Google AI Studio).
Installatie (voor ontwikkeling)
Kloon de repository:
git clone https://github.com/jouwgebruikersnaam/AI-bestandsorganizer.git
cd AI-bestandsorganizer
Use code with caution.
Bash
Open in Visual Studio:
Open het AI-bestandsorganizer.sln bestand in Visual Studio 2022 (of nieuwer).
Herstel NuGet-pakketten:
Visual Studio zou dit automatisch moeten doen. Zo niet, klik met de rechtermuisknop op de oplossing in de Solution Explorer en selecteer "Restore NuGet Packages".
Configuratie van de API-sleutel en Instellingen
Voordat je de applicatie draait, moet je je Google Gemini API-sleutel instellen en eventueel andere voorkeuren aanpassen.
Open appsettings.json in de hoofdmap van het project.
Voer je API-sleutel in: Vervang "YOUR_GEMINI_API_KEY" door je daadwerkelijke API-sleutel:
{
  "Logging": {
    // ... (logging instellingen)
  },
  "AIOrganizer": {
    "ApiKey": "AIzaSyXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXX", // <--- VERVANG DIT!
    "ModelName": "gemini-1.5-pro-latest", // Aanbevolen model voor betere prestaties
    "MaxPromptChars": 8000,
    "FallbackCategory": "Overig",
    "SupportedExtensions": [ ".pdf", ".docx", ".txt", ".md" ],
    "EnableDescriptiveFilenames": true, // Zet op 'false' als je geen AI-suggesties wilt
    "EnableFileRenaming": true,       // Zet op 'false' als je bestanden NIET wilt hernoemen
    "Categories": {
      "Financiën": "1. Financiën",
      "Belastingen": "2. Belastingen",
      // ... (jouw categorieën)
    }
  }
}
Use code with caution.
Json
Belangrijk: Wijzig EnableDescriptiveFilenames en EnableFileRenaming naar true of false afhankelijk van je voorkeur voor hernoemen. De Categories kun je naar wens aanpassen; de sleutel is wat de AI als antwoord moet geven, de waarde is de daadwerkelijke mapnaam.
De Applicatie Draaien
Optie 1: Vanuit Visual Studio
Zorg ervoor dat AI-bestandsorganizer als het opstartproject is ingesteld (rechtermuisklik op project -> "Set as Startup Project").
Druk op F5 (Start Debugging) of klik op de "Start" knop (meestal een groene driehoek).
Optie 2: Vanaf de Command Line (CLI)
Navigeer in je terminal of command prompt naar de hoofdmap van het project (waar AI-bestandsorganizer.csproj zich bevindt).
Build de applicatie:
dotnet build
Use code with caution.
Bash
Draai de applicatie:
dotnet run
Use code with caution.
Bash
Of, als je de gecompileerde .exe wilt draaien (bijvoorbeeld na een dotnet publish):
.\bin\Debug\net8.0-windows\AI-bestandsorganizer.exe
Use code with caution.
Bash
(Pas het pad aan als je in Release-modus bouwt, bijvoorbeeld .\bin\Release\net8.0-windows\AI-bestandsorganizer.exe)
🖥️ Gebruik
Selecteer Bronmap: Gebruik de "..." knop naast "Bronmap" om de map te kiezen waar je bestanden wilt organiseren. De applicatie zal bestanden in deze map en al zijn submappen verwerken.
Selecteer Doelmap: Gebruik de "..." knop naast "Doelmap" om de map te kiezen waar de georganiseerde bestanden naartoe moeten. Hier worden de categorielabels als submappen aangemaakt.
API-key & Model: Zorg ervoor dat je API-key is ingevuld en het gewenste Gemini-model is geselecteerd.
Bestanden Hernoemen (Optioneel): Vink de checkbox "Bestanden hernoemen (incl. AI-suggesties)" aan of uit, afhankelijk van of je wilt dat de applicatie nieuwe namen voorstelt en bevestigt.
Start Organisatie: Klik op de "Organiseer" knop.
Hernoemingsbevestiging (indien ingeschakeld): Als het hernoemen is ingeschakeld, verschijnt er een pop-up voor elk bestand met een voorgestelde naam. Je kunt:
Accept Suggested (Voorgestelde accepteren)
Keep Original (Originele behouden)
Een eigen naam invoeren en op Apply Custom (Aangepast toepassen) klikken.
Logboek: Volg de voortgang en eventuele fouten in het logboek onderaan het scherm.
🤝 Bijdragen
Voel je vrij om de code te forken, verbeteringen aan te dragen en pull requests in te dienen. Ideeën voor nieuwe functies of bugfixes zijn altijd welkom!
📧 Contact
Made by Remsey Mailjard
LinkedIn Profiel
