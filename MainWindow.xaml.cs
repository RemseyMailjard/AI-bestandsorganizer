using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
// Explicitly use System.Windows.Controls for common WPF controls
using WpfControls = System.Windows.Controls; // Alias for WPF controls

using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Microsoft.Win32; // For WPF SaveFileDialog

// Alias for Windows Forms to avoid ambiguity
using WinForms = System.Windows.Forms;
using System.Windows.Controls;

// Make sure FolderPathConfirmationHandler is accessible from the AI_bestandsorganizer namespace
// (defined in AIFileOrganizer.cs at the namespace level)

namespace AI_bestandsorganizer
{
    public partial class MainWindow : Window
    {
        private readonly AIFileOrganizer _organizer;
        private readonly ILogger<MainWindow> _logger;
        private readonly AIOrganizerSettings _settings;
        private CancellationTokenSource? _cts;
        private bool _uiInitialized = false; // Flag to manage UI initialization sequence

        public MainWindow(
            AIFileOrganizer organizer,
            ILogger<MainWindow> logger,
            IOptions<AIOrganizerSettings> options)
        {
            _organizer = organizer ?? throw new ArgumentNullException(nameof(organizer));
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));
            _settings = options?.Value ?? throw new ArgumentNullException(nameof(options));

            InitializeComponent(); // This initializes XAML-defined controls

            // Initialize settings-dependent UI elements
            ApiKeyBox.Password = _settings.ApiKey;

            // Set up ProviderBox first, as ModelBox depends on its selection.
            // ProviderBox_SelectionChanged will be called, which populates ModelBox.
            var providerItem = ProviderBox.Items.OfType<WpfControls.ComboBoxItem>()
                .FirstOrDefault(i => i.Content?.ToString() == _settings.Provider.ToString());
            if (providerItem != null)
            {
                ProviderBox.SelectedItem = providerItem;
            }
            else if (ProviderBox.Items.Count > 0)
            {
                ProviderBox.SelectedIndex = 0; // Default to first item
            }
            // ModelBox will be populated by ProviderBox_SelectionChanged

            SrcBox.Text = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads");
            DstBox.Text = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "AI-Mappen");

            RenameChk.IsChecked = _settings.EnableFileRenaming && _settings.EnableDescriptiveFilenames;
            MetadataChk.IsChecked = _settings.GenerateMetadataFiles;
            AISuggestedFoldersChk.IsChecked = _settings.EnableAISuggestedFolders;
        }

        protected override void OnContentRendered(EventArgs e)
        {
            base.OnContentRendered(e);
            _uiInitialized = true; // Mark UI as fully initialized
            // If ProviderBox still doesn't have a selection, force one to trigger ModelBox population
            if (ProviderBox.SelectedItem == null && ProviderBox.Items.Count > 0)
            {
                ProviderBox.SelectedIndex = 0;
            }
            else if (ProviderBox.SelectedItem != null) // Or if it has one, ensure the event has run for ModelBox
            {
                ProviderBox_SelectionChanged(ProviderBox, new WpfControls.SelectionChangedEventArgs(
                    WpfControls.Primitives.Selector.SelectionChangedEvent,
                    Array.Empty<object>(), // No removed items
                    new object[] { ProviderBox.SelectedItem } // Added items
                ));
            }
        }


        private void BrowseSrc(object? _, RoutedEventArgs e) => Browse(SrcBox);
        private void BrowseDst(object? _, RoutedEventArgs e) => Browse(DstBox);

        private static void Browse(WpfControls.TextBox target) // Explicit WPF TextBox
        {
            using var dlg = new WinForms.FolderBrowserDialog // Explicit WinForms Dialog
            {
                Description = "Selecteer een map",
                UseDescriptionForTitle = true,
                InitialDirectory = Directory.Exists(target.Text)
                    ? target.Text
                    : Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments)
            };
            if (dlg.ShowDialog() == WinForms.DialogResult.OK)
            {
                target.Text = dlg.SelectedPath;
            }
        }

        private async void Run_Click(object sender, RoutedEventArgs e)
        {
            // Line 95
            if (sender is WpfControls.Button runBtn) runBtn.IsEnabled = false;
            LogBox.Clear();
            Log("🚀 Organiseren gestart …");

            if (string.IsNullOrWhiteSpace(ApiKeyBox.Password))
            {
                Log("❌ Fout: API-key ontbreekt");
                System.Windows.MessageBox.Show("API-key ontbreekt.", "AI File Organizer",
                            MessageBoxButton.OK, MessageBoxImage.Warning);
                // Line 104
                if (sender is WpfControls.Button btn) btn.IsEnabled = true;
                return;
            }

            _settings.ApiKey = ApiKeyBox.Password;

            if (ProviderBox.SelectedItem is WpfControls.ComboBoxItem selectedProviderItem &&
                Enum.TryParse<LlmProvider>(selectedProviderItem.Content?.ToString(), out var selectedProvider))
            {
                _settings.Provider = selectedProvider;
            }

            _settings.ModelName = (ModelBox.SelectedItem as WpfControls.ComboBoxItem)?.Content?.ToString() ?? _settings.ModelName;
            _settings.EnableFileRenaming = RenameChk.IsChecked ?? false;
            _settings.EnableDescriptiveFilenames = RenameChk.IsChecked ?? false;
            _settings.GenerateMetadataFiles = MetadataChk.IsChecked ?? false;
            _settings.EnableAISuggestedFolders = AISuggestedFoldersChk.IsChecked ?? false;

            if (_settings.Provider == LlmProvider.AzureOpenAI)
            {
                _settings.AzureEndpoint = EndpointBox.Text;
                _settings.AzureDeployment = DeployBox.Text;
                if (string.IsNullOrWhiteSpace(_settings.AzureEndpoint) || string.IsNullOrWhiteSpace(_settings.AzureDeployment))
                {
                    Log("❌ Fout: Azure Endpoint of Deployment naam ontbreekt voor Azure OpenAI provider.");
                    System.Windows.MessageBox.Show("Azure Endpoint en Deployment naam zijn vereist voor de Azure OpenAI provider.", "Configuratie Fout", MessageBoxButton.OK, MessageBoxImage.Warning);
                    // Line 130
                    if (sender is WpfControls.Button btn) btn.IsEnabled = true;
                    return;
                }
            }

            _cts = new CancellationTokenSource();
            try
            {
                var prog = new Progress<string>(Log);

                FilenameConfirmationHandler? filenameConfirmer = null;
                if (_settings.EnableFileRenaming && _settings.EnableDescriptiveFilenames)
                {
                    filenameConfirmer = async (orig, sugg, reporter) =>
                    {
                        reporter?.Report($"Bevestig bestandsnaam voor '{orig}'…");
                        var result = await Dispatcher.InvokeAsync(() =>
                        {
                            var dlg = new FilenameInputDialog(orig, sugg) { Owner = this };
                            return dlg.ShowDialog() == true ? dlg.ResultFilename : orig;
                        });
                        reporter?.Report($"Naam gekozen: '{result}'");
                        return result;
                    };
                }

                // Line 157: FolderPathConfirmationHandler is now from AI_bestandsorganizer namespace
                FolderPathConfirmationHandler? folderPathConfirmer = null;
                if (_settings.EnableAISuggestedFolders)
                {
                    // Line 160
                    folderPathConfirmer = async (predefinedPath, suggestedPathByAI, reporter) =>
                    {
                        reporter?.Report($"Bevestig doelmap (Basis: '{predefinedPath}', Suggestie: '{suggestedPathByAI}')...");
                        var result = await Dispatcher.InvokeAsync(() =>
                        {
                            var dlg = new FolderPathInputDialog(predefinedPath, suggestedPathByAI) { Owner = this };
                            return dlg.ShowDialog() == true ? dlg.ResultFolderPath : predefinedPath;
                        });
                        reporter?.Report($"Doelmap gekozen: '{result}'");
                        return result;
                    };
                }

                // Line 177: Call to OrganizeAsync
                (int processed, int moved) = await _organizer.OrganizeAsync(
                    SrcBox.Text,
                    DstBox.Text,
                    filenameConfirmer,
                    folderPathConfirmer, // This should now match the expected delegate type
                    prog,
                    _cts.Token);

                Log($"✅ Klaar! {moved}/{processed} bestanden verplaatst.");
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
                // Line 194
                if (sender is WpfControls.Button btn) btn.IsEnabled = true;
                _cts?.Dispose();
                _cts = null;
            }
        }

        private void Log(string line) =>
            Dispatcher.Invoke(() =>
            {
                LogBox.AppendText(line + Environment.NewLine);
                LogBox.ScrollToEnd();
            });

        private void OpenLinkedIn(object? _, RoutedEventArgs e)
        {
            try
            {
                Process.Start(new ProcessStartInfo("https://www.linkedin.com/in/remseymailjard/") { UseShellExecute = true });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "LinkedIn link openen mislukt.");
                System.Windows.MessageBox.Show($"Kan link niet openen: {ex.Message}", "Fout", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        private void ProviderBox_SelectionChanged(object? s, SelectionChangedEventArgs e)
        {
            if (ProviderBox == null || ModelBox == null) return;

            ModelBox.Items.Clear();

            switch (ProviderBox.SelectedIndex)
            {
                case 0: // Gemini
                    ModelBox.Items.Add(new ComboBoxItem { Content = "gemini-1.5-pro-latest" });
                    ModelBox.Items.Add(new ComboBoxItem { Content = "gemini-1.5-flash-latest" });
                    ModelBox.Items.Add(new ComboBoxItem { Content = "gemini-1.5-pro-001" });
                    ModelBox.Items.Add(new ComboBoxItem { Content = "gemini-1.5-flash-001" });
                    ModelBox.Items.Add(new ComboBoxItem { Content = "gemini-1.0-pro-002" });
                    ModelBox.Items.Add(new ComboBoxItem { Content = "gemini-1.0-pro-latest" });
                    ModelBox.Items.Add(new ComboBoxItem { Content = "gemini-1.0-ultra-latest" });
                    ModelBox.Items.Add(new ComboBoxItem { Content = "gemini-pro" });
                    ModelBox.Items.Add(new ComboBoxItem { Content = "gemini-2.5-pro-preview-05-06" });
                    ModelBox.Items.Add(new ComboBoxItem { Content = "gemini-2.5-pro" });
                    break;
                case 1: // Azure OpenAI
                case 2: // OpenAI
                    ModelBox.Items.Add(new ComboBoxItem { Content = "gpt-4o" });
                    ModelBox.Items.Add(new ComboBoxItem { Content = "gpt-4o-mini" });
                    ModelBox.Items.Add(new ComboBoxItem { Content = "gpt-4-turbo" });
                    ModelBox.Items.Add(new ComboBoxItem { Content = "gpt-4-turbo-2024-04-09" });
                    ModelBox.Items.Add(new ComboBoxItem { Content = "gpt-4" });
                    ModelBox.Items.Add(new ComboBoxItem { Content = "gpt-4-32k" });
                    ModelBox.Items.Add(new ComboBoxItem { Content = "gpt-35-turbo" });
                    ModelBox.Items.Add(new ComboBoxItem { Content = "gpt-35-turbo-16k" });
                    ModelBox.Items.Add(new ComboBoxItem { Content = "gpt-3.5-turbo" });
                    ModelBox.Items.Add(new ComboBoxItem { Content = "gpt-3.5-turbo-16k" });
                    ModelBox.Items.Add(new ComboBoxItem { Content = "gpt-3.5-turbo-0125" });
                    ModelBox.Items.Add(new ComboBoxItem { Content = "gpt-3.5-turbo-instruct" });
                    break;
                default:
                    break;
            }

            if (ModelBox.Items.Count > 0)
                ((ComboBoxItem)ModelBox.Items[0]).IsSelected = true;
        }

        private void ExportLog_Click(object sender, RoutedEventArgs e)
        {
            if (string.IsNullOrWhiteSpace(LogBox.Text))
            {
                System.Windows.MessageBox.Show("Log is leeg, niets te exporteren.", "Export Log", MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }

            // Line 310: SaveFileDialog is now Microsoft.Win32.SaveFileDialog
            var saveDialog = new Microsoft.Win32.SaveFileDialog
            {
                Filter = "Tekstbestanden (*.txt)|*.txt|Alle bestanden (*.*)|*.*",
                FileName = $"AIOrganizer_Log_{DateTime.Now:yyyyMMdd_HHmmss}",
                Title = "Logbestand Opslaan"
            };

            if (saveDialog.ShowDialog() == true)
            {
                try
                {
                    File.WriteAllText(saveDialog.FileName, LogBox.Text);
                    Log("📋 Log geëxporteerd naar: " + saveDialog.FileName);
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Log exporteren mislukt.");
                    System.Windows.MessageBox.Show($"Log exporteren mislukt: {ex.Message}", "Fout", MessageBoxButton.OK, MessageBoxImage.Error);
                }
            }
        }
    }
}