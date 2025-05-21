using System;
using System.Windows;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Mscc.GenerativeAI;

namespace AI_bestandsorganizer
{
    public partial class App : System.Windows.Application   // volledig gekwalificeerd
    {
        public static IHost Host { get; private set; } = default!;

        protected override void OnStartup(StartupEventArgs e)
        {
            base.OnStartup(e);

            Host = Microsoft.Extensions.Hosting.Host    // let op: static Host
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

                    svcs.AddSingleton(new GoogleAI(ctx.Configuration["AIOrganizer:ApiKey"] ?? ""));
                    svcs.AddSingleton<AIFileOrganizer>();
                    svcs.AddSingleton<MainWindow>();
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
