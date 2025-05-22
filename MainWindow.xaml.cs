using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;

using Windows.Storage;
using Windows.Storage.Pickers;
using WinRT.Interop;

using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

// WPF UI namespaces
using Wpf.Ui;                      // ISnackbarService, ControlAppearance
using Wpf.Ui.Controls;            // FluentWindow, PasswordBox, TextBox, Button, MessageBox, etc.

// Aliases
using UiButton = Wpf.Ui.Controls.Button;
using UiMsgBox = Wpf.Ui.Controls.MessageBox;
using UiMsgBtn = Wpf.Ui.Controls.MessageBoxButton;
using WpfTextBox = Wpf.Ui.Controls.TextBox;
using Wpf.Ui.Extensions;

namespace AI_bestandsorganizer
{
    public partial class MainWindow : FluentWindow
    {
        private readonly AIFileOrganizer _organizer;
        private readonly ILogger<MainWindow> _logger;
        private readonly AIOrganizerSettings _settings;
        private readonly ISnackbarService _snackbar;

        private CancellationTokenSource? _cts;

        public MainWindow(
            AIFileOrganizer organizer,
            ILogger<MainWindow> logger,
            IOptions<AIOrganizerSettings> settings,
            ISnackbarService snackbar)
        {
            InitializeComponent();

            _organizer = organizer ?? throw new ArgumentNullException(nameof(organizer));
            _logger    = logger    ?? throw new ArgumentNullException(nameof(logger));
            _settings  = settings.Value ?? throw new ArgumentNullException(nameof(settings));
            _snackbar  = snackbar  ?? throw new ArgumentNullException(nameof(snackbar));

            // Initialiseer UI
            ApiKeyBox.Password = _settings.ApiKey;

            var modelItem = ModelBox.Items
                                     .OfType<ComboBoxItem>()
                                     .FirstOrDefault(i => (string?)i.Content == _settings.ModelName);
            ModelBox.SelectedItem = modelItem ?? ModelBox.Items[0];

            SrcBox.Text = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads");
            DstBox.Text = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "AI-mappen");

            EnableRenamingCheckBox.IsChecked = _settings.EnableFileRenaming;
        }

        // ---------------- Folder pickers ----------------
        private async void BrowseSrc(object? _, RoutedEventArgs e) => await BrowseFolder(SrcBox);
        private async void BrowseDst(object? _, RoutedEventArgs e) => await BrowseFolder(DstBox);

        private async Task BrowseFolder(WpfTextBox target)
        {
            var picker = new FolderPicker
            {
                ViewMode = PickerViewMode.List,
                SuggestedStartLocation = PickerLocationId.DocumentsLibrary
            };
            picker.FileTypeFilter.Add("*");

            InitializeWithWindow.Initialize(picker, new WindowInteropHelper(this).Handle);

            if (await picker.PickSingleFolderAsync() is StorageFolder folder)
                target.Text = folder.Path;
        }

        // ---------------- Run ----------------
        private async void Run_Click(object sender, RoutedEventArgs e)
        {
            if (sender is UiButton runBtn) runBtn.IsEnabled = false;

            LogBox.Clear();
            Log("🚀 Organiseren gestart…");

            if (string.IsNullOrWhiteSpace(ApiKeyBox.Password))
            {
                _snackbar.Show(
                    "API‑key ontbreekt",
                    "Voer een geldige sleutel in om verder te gaan.",
                    ControlAppearance.Caution,
                    TimeSpan.FromSeconds(4));
                if (sender is UiButton rb) rb.IsEnabled = true;
                return;
            }

            _settings.ApiKey             = ApiKeyBox.Password;
            _settings.ModelName          = ((ComboBoxItem)ModelBox.SelectedItem)!.Content!.ToString()!;
            _settings.EnableFileRenaming = EnableRenamingCheckBox.IsChecked ?? false;

            _cts = new CancellationTokenSource();

            try
            {
                var progress = new Progress<string>(Log);
                FilenameConfirmationHandler? confirmer = null;

                if (_settings.EnableFileRenaming && _settings.EnableDescriptiveFilenames)
                {
                    confirmer = async (orig, sugg, prog) =>
                    {
                        return await Dispatcher.Invoke(async () =>
                        {
                            prog?.Report($"Wacht op bevestiging voor '{orig}'…");
                            var dlg = new FilenameInputDialog(orig, sugg) { Owner = this };
                            return dlg.ShowDialog() == true ? dlg.ResultFilename : orig;
                        });
                    };
                }

                // Ná  (negeert het 3e tuple-element)
                // 1️⃣  Bewaar het derde tuple-element (tokens)
                (int processed, int moved, long tokensUsed) = await _organizer.OrganizeAsync(
                    SrcBox.Text, DstBox.Text, confirmer, progress, _cts.Token);

                // 2️⃣  Toon resultaat in log
                Log($"✅ Klaar! {moved}/{processed} bestanden verplaatst.");

                // 3️⃣  Update de TextBlock op de UI-thread
                TokenUsageTextBlock.Text = $"Tokens used: {tokensUsed:N0}";


           

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
                if (sender is UiButton rb) rb.IsEnabled = true;
                _cts?.Dispose();
                _cts = null;
            }
        }

        // ---------------- Helpers ----------------
        private void Log(string message) => Dispatcher.Invoke(() =>
        {
            LogBox.AppendText(message + Environment.NewLine);
            LogBox.ScrollToEnd();
        });

        private void OpenLinkedIn(object? _, RoutedEventArgs e)
        {
            try
            {
                Process.Start(new ProcessStartInfo
                {
                    FileName = "https://www.linkedin.com/in/remseymailjard/",
                    UseShellExecute = true
                });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "LinkedIn-link openen mislukt");
                //UiMsgBox.Show(
                //    "Fout",
                //    $"Kan link niet openen: {ex.Message}",
                //    UiMsgBtn.Close,
                //    ControlAppearance.Danger);
            }
        }
    }
}
