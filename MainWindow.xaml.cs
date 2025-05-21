using System;
using System.Diagnostics;
using System.IO;
using System.Linq;                         // ← nodig voor FirstOrDefault
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;

using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

using SysForms = System.Windows.Forms;              // alleen FolderBrowserDialog
using WpfMsg = System.Windows.MessageBox;
using WpfTextBox = System.Windows.Controls.TextBox;   // expliciete alias

namespace AI_bestandsorganizer
{
    public partial class MainWindow : Window
    {
        private readonly AIFileOrganizer _organizer;
        private readonly ILogger<MainWindow> _logger;
        private readonly AIOrganizerSettings _settings; // Dit is de singleton die door DI wordt geïnjecteerd
        private CancellationTokenSource? _cts;

        public MainWindow(
            AIFileOrganizer organizer,
            ILogger<MainWindow> logger,
            IOptions<AIOrganizerSettings> settings) // AIOrganizerSettings wordt hier via IOptions<T> binnengehaald
        {
            InitializeComponent();

            _organizer = organizer ?? throw new ArgumentNullException(nameof(organizer));
            _logger    = logger     ?? throw new ArgumentNullException(nameof(logger));
            _settings  = settings.Value ?? throw new ArgumentNullException(nameof(settings)); // Haal de geconfigureerde instellingen op

            // UI init
            ApiKeyBox.Password = _settings.ApiKey;

            var modelItem = ModelBox.Items.OfType<ComboBoxItem>()
                                .FirstOrDefault(i => (string?)i.Content == _settings.ModelName);
            ModelBox.SelectedItem = modelItem ?? ModelBox.Items[0];

            SrcBox.Text = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads");
            DstBox.Text = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "AI-mappen");

            // NIEUW: Stel de checkbox in op basis van de geladen instelling
            EnableRenamingCheckBox.IsChecked = _settings.EnableFileRenaming;
        }

        // ---------- Browse-knoppen ----------
        private void BrowseSrc(object? _, RoutedEventArgs e) => Browse(SrcBox);
        private void BrowseDst(object? _, RoutedEventArgs e) => Browse(DstBox);

        private static void Browse(WpfTextBox target)
        {
            using var dlg = new SysForms.FolderBrowserDialog
            {
                InitialDirectory = Directory.Exists(target.Text)
                    ? target.Text
                    : Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments)
            };
            if (dlg.ShowDialog() == SysForms.DialogResult.OK)
                target.Text = dlg.SelectedPath;
        }

        // ---------- Run-knop ----------
        private async void Run_Click(object sender, RoutedEventArgs e)
        {
            var runBtn = sender as System.Windows.Controls.Button;
            if (runBtn is not null) runBtn.IsEnabled = false;

            LogBox.Clear();
            Log("🚀 Organiseren gestart …");

            if (string.IsNullOrWhiteSpace(ApiKeyBox.Password))
            {
                Log("❌ Fout: API-key ontbreekt");
                WpfMsg.Show("API-key ontbreekt.", "AI File Organizer",
                            MessageBoxButton.OK, MessageBoxImage.Warning);
                if (runBtn is not null) runBtn.IsEnabled = true;
                return;
            }

            // settings live bijwerken voor de huidige run
            _settings.ApiKey = ApiKeyBox.Password;
            _settings.ModelName = ((ComboBoxItem)ModelBox.SelectedItem)!.Content!.ToString()!;
            // NIEUW: Werk de EnableFileRenaming setting bij van de UI
            _settings.EnableFileRenaming = EnableRenamingCheckBox.IsChecked ?? false;


            _cts = new CancellationTokenSource();

            try
            {
                var prog = new Progress<string>(Log);

                // Initialiseer de FilenameConfirmationHandler ALLEEN als hernoemen is ingeschakeld
                FilenameConfirmationHandler? filenameConfirmer = null;
                // Deel 'EnableDescriptiveFilenames' blijft actief om de AI-suggesties te genereren.
                // 'EnableFileRenaming' is de override om te bepalen of *überhaupt* hernoemd wordt.
                if (_settings.EnableFileRenaming && _settings.EnableDescriptiveFilenames)
                {
                    filenameConfirmer = async (originalFilenameBase, suggestedFilenameBase, progressReporter) =>
                    {
                        return await Dispatcher.Invoke(async () =>
                        {
                            progressReporter?.Report($"Awaiting filename confirmation for '{originalFilenameBase}'...");

                            var dialog = new FilenameInputDialog(originalFilenameBase, suggestedFilenameBase);
                            dialog.Owner = this;

                            bool? dialogResult = dialog.ShowDialog();

                            if (dialogResult == true)
                            {
                                progressReporter?.Report($"Filename confirmed: '{dialog.ResultFilename}'");
                                return dialog.ResultFilename;
                            }
                            else
                            {
                                progressReporter?.Report($"Filename confirmation cancelled. Keeping original name: '{originalFilenameBase}'");
                                return originalFilenameBase;
                            }
                        });
                    };
                }
                // Als EnableFileRenaming FALSE is, blijft filenameConfirmer NULL.
                // AIFileOrganizer zal dan default de originele bestandsnaam gebruiken.


                var (proc, moved) = await _organizer
                    .OrganizeAsync(SrcBox.Text, DstBox.Text, filenameConfirmer, prog, _cts.Token);

                Log($"✅ Klaar! {moved}/{proc} bestanden verplaatst.");
            }
            catch (OperationCanceledException)
            {
                Log("⚠ Organisatie geannuleerd door gebruiker.");
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Onverwachte fout");
                Log($"❌ Onverwachte fout: {ex.Message}");
            }
            finally
            {
                if (runBtn is not null) runBtn.IsEnabled = true;
                _cts?.Dispose();
                _cts = null;
            }
        }

        // ---------- Log helper ----------
        private void Log(string line) =>
            Dispatcher.Invoke(() =>
            {
                LogBox.AppendText(line + Environment.NewLine);
                LogBox.ScrollToEnd();
            });

        // ---------- LinkedIn-knop ----------
        private void OpenLinkedIn(object? _, RoutedEventArgs e)
        {
            try
            {
                Process.Start(new ProcessStartInfo
                {
                    FileName       = "https://www.linkedin.com/in/remseymailjard/",
                    UseShellExecute = true
                });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "LinkedIn link openen mislukt.");
                WpfMsg.Show($"Kan link niet openen: {ex.Message}",
                            "Fout", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }
    }
}