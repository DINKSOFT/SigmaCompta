using System;
using System.Globalization;
using System.IO;
using System.Threading;
using System.Windows;
using System.Windows.Input;
using System.Windows.Markup;
using System.Windows.Threading;
using SigmaCompta.App.Mvvm;
using SigmaCompta.App.Services;
using SigmaCompta.App.ViewModels;
using SigmaCompta.Core.Fichiers;
using SigmaCompta.Core.Stockage;

namespace SigmaCompta.App
{
    public partial class App : Application
    {
        // Référence conservée : CommandManager.RequerySuggested ne garde qu'une référence faible.
        private EventHandler _reevaluation;

        protected override void OnStartup(StartupEventArgs e)
        {
            // Formats français partout (montants « 1 500 000 », dates jj/mm/aaaa).
            var fr = new CultureInfo("fr-FR");
            Thread.CurrentThread.CurrentCulture = fr;
            Thread.CurrentThread.CurrentUICulture = fr;
            CultureInfo.DefaultThreadCurrentCulture = fr;
            CultureInfo.DefaultThreadCurrentUICulture = fr;
            FrameworkElement.LanguageProperty.OverrideMetadata(typeof(FrameworkElement),
                new FrameworkPropertyMetadata(XmlLanguage.GetLanguage(fr.IetfLanguageTag)));

            // Pages de code Windows (CSV « ANSI ») : absentes par défaut sous .NET 8.
            Encodages.Initialiser();

            DispatcherUnhandledException += SurErreurNonGeree;

            _reevaluation = (s, a) => CommandeBase.ReevaluerTout();
            CommandManager.RequerySuggested += _reevaluation;

            base.OnStartup(e);

            var fenetre = new MainWindow { DataContext = new MainViewModel(new DialoguesWpf()) };
            MainWindow = fenetre;
            fenetre.Show();
        }

        private void SurErreurNonGeree(object sender, DispatcherUnhandledExceptionEventArgs e)
        {
            try
            {
                File.AppendAllText(Path.Combine(Chemins.Racine, "erreurs.log"),
                    DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss") + " " + e.Exception + Environment.NewLine + Environment.NewLine);
            }
            catch (Exception) { }
            new DialoguesWpf().Informer("Erreur inattendue",
                e.Exception.Message + "\n\nLe détail a été enregistré dans " + Path.Combine(Chemins.Racine, "erreurs.log") + ".", true);
            e.Handled = true;
        }
    }
}
