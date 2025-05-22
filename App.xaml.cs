using System;
using System.Windows;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Mscc.GenerativeAI;
// DEZE IS NU CORRECT: Nieuwe namespace voor MessagingService in WPF UI v3.x
 // Let op: enkelvoud 'Service' i.p.v. 'Services'
// DEZE IS OOK NODIG: Bevat o.a. IThemeService en ThemeService
using Wpf.Ui.Appearance;
using Wpf.Ui;
using Wpf.Ui.Controls;


namespace AI_bestandsorganizer
{
    public partial class App : System.Windows.Application
    {
        public static IHost Host { get; private set; } = default!;

        protected override void OnStartup(StartupEventArgs e)
        {
            base.OnStartup(e);

            Host = Microsoft.Extensions.Hosting.Host
                .CreateDefaultBuilder()
                .ConfigureAppConfiguration(cfg =>
                {
                    cfg.AddJsonFile("appsettings.json", optional: false, reloadOnChange: true)
                       .AddEnvironmentVariables();
                })
                .ConfigureLogging(log =>
                {
                    log.ClearProviders();
                    log.AddConsole();
                })
                .ConfigureServices((ctx, svcs) =>
                {
                    svcs.Configure<AIOrganizerSettings>(ctx.Configuration.GetSection("AIOrganizer"));

                    // Registreer WPF UI services met de correcte namespaces
                    svcs.AddSingleton<IThemeService, ThemeService>();
                    // Gebruik nu de correcte 'Service' namespace voor MessagingService


                    svcs.AddSingleton(new GoogleAI(ctx.Configuration["AIOrganizer:ApiKey"] ?? ""));
                    svcs.AddSingleton<AIFileOrganizer>();
                    svcs.AddSingleton<MainWindow>();
                    svcs.AddSingleton<ISnackbarService, SnackbarService>();
                })
                .Build();

            Host.Start();
            Host.Services.GetRequiredService<MainWindow>().Show();
        }

        protected override async void OnExit(ExitEventArgs e)
        {
            if (Host is not null)
                await Host.StopAsync();
            base.OnExit(e);
        }
    }
}