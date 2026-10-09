using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Resonance.App.Services;
using Resonance.App.ViewModels;
using Resonance.Core;
using Resonance.Data;
using System.Windows;

namespace Resonance.App;

public partial class App : Application
{
    private IHost? host;
    protected override async void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        DispatcherUnhandledException += (_, args) => {
            args.Handled = true;
            MessageBox.Show("Resonance encountered an unexpected error and must close. Your audio files have not been changed.", "Resonance", MessageBoxButton.OK, MessageBoxImage.Error);
            Shutdown(1);
        };
        TaskScheduler.UnobservedTaskException += (_, args) => {
            args.SetObserved();
            Dispatcher.BeginInvoke(() => host?.Services.GetRequiredService<Notifications>()
                .Show("A background operation failed. Please retry the operation."));
        };
        try
        {
            var builder = Host.CreateApplicationBuilder(new HostApplicationBuilderSettings { DisableDefaults = true });
            var services = builder.Services;
            services.AddSingleton(_ => new HttpClient(new HttpClientHandler { AllowAutoRedirect = false }) { Timeout = TimeSpan.FromSeconds(45) });
            services.AddSingleton<ILibraryScanner>(_ => new LibraryScanner(AppPaths.Art));
            services.AddSingleton<ILibraryStore>(_ => new LibraryStore(AppPaths.Database));
            services.AddSingleton<Credentials>();
            services.AddSingleton<IProviderCredentials>(sp => sp.GetRequiredService<Credentials>());
            services.AddSingleton<IPlaybackEngine, PlaybackEngine>();
            services.AddSingleton<ProviderApi>();
            services.AddSingleton<IPlaylistImporter, SpotifyImporter>();
            services.AddSingleton<IPlaylistImporter, YouTubeImporter>();
            services.AddSingleton<IPlaylistImporter, AppleMusicImporter>();
            services.AddSingleton<IPlaylistImporter, SoundCloudImporter>();
            services.AddSingleton<IPlaylistImporter, GenericImporter>();
            services.AddSingleton<ThemeService>(); services.AddSingleton<Notifications>(); services.AddSingleton<SavedSettings>();
            services.AddSingleton<PlayerViewModel>(); services.AddSingleton<LibraryViewModel>();
            services.AddSingleton<PlaylistsViewModel>(); services.AddSingleton<SettingsViewModel>();
            services.AddSingleton<MainViewModel>(); services.AddTransient<ImportViewModel>();
            services.AddSingleton<Func<ImportViewModel>>(sp => () => sp.GetRequiredService<ImportViewModel>());
            services.AddSingleton<MainWindow>();
            host = builder.Build(); await host.StartAsync();
            try { await host.Services.GetRequiredService<ILibraryStore>().InitializeAsync(); }
            catch (Exception)
            {
                MessageBox.Show("Resonance could not open its library database. Check disk space and access to " + AppPaths.Root + ". Your music files are untouched.", "Cannot open library", MessageBoxButton.OK, MessageBoxImage.Error);
                await StopHostAsync(); Shutdown(1); return;
            }
            var vm = host.Services.GetRequiredService<MainViewModel>();
            await vm.InitializeAsync();
            var window = host.Services.GetRequiredService<MainWindow>();
            MainWindow = window;
            await window.RestoreGeometryAsync(); window.Show();
        }
        catch (Exception)
        {
            MessageBox.Show("Resonance could not start. Check your saved settings and library database in " + AppPaths.Root + ". No audio files have been changed.", "Cannot start Resonance", MessageBoxButton.OK, MessageBoxImage.Error);
            await StopHostAsync(); Shutdown(1);
        }
    }
    public async Task StopHostAsync()
    {
        if (host is null) return;
        try { await host.StopAsync(TimeSpan.FromSeconds(5)); }
        finally { host.Dispose(); host = null; }
    }
    protected override void OnExit(ExitEventArgs e) { host?.Dispose(); base.OnExit(e); }
}
