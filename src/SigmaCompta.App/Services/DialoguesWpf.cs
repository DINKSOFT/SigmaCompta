using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Windows;
using Microsoft.Win32;
using SigmaCompta.App.Views;

namespace SigmaCompta.App.Services
{
    /// <summary>Implémentation WPF de <see cref="IDialogues"/>.</summary>
    public sealed class DialoguesWpf : IDialogues
    {
        private static Window Proprietaire
        {
            get
            {
                var app = Application.Current;
                if (app == null) return null;
                return app.Windows.OfType<Window>().FirstOrDefault(w => w.IsActive) ?? app.MainWindow;
            }
        }

        public string ChoisirFichier(string titre, string filtre, string dossierInitial = null)
        {
            var d = new OpenFileDialog { Title = titre, Filter = filtre, CheckFileExists = true, Multiselect = false };
            if (!string.IsNullOrEmpty(dossierInitial) && Directory.Exists(dossierInitial)) d.InitialDirectory = dossierInitial;
            return d.ShowDialog(Proprietaire) == true ? d.FileName : null;
        }

        public string ChoisirEmplacement(string titre, string filtre, string nomParDefaut, string dossierInitial = null)
        {
            var d = new SaveFileDialog { Title = titre, Filter = filtre, FileName = nomParDefaut, OverwritePrompt = true, AddExtension = true };
            if (!string.IsNullOrEmpty(dossierInitial) && Directory.Exists(dossierInitial)) d.InitialDirectory = dossierInitial;
            return d.ShowDialog(Proprietaire) == true ? d.FileName : null;
        }

        public bool Confirmer(string titre, string message, string libelleConfirmation = "Confirmer", bool danger = false)
        {
            var f = new FenetreMessage(titre, message, libelleConfirmation, "Annuler", danger ? FenetreMessage.Genre.Danger : FenetreMessage.Genre.Question)
            {
                Owner = Proprietaire
            };
            return f.ShowDialog() == true;
        }

        public void Informer(string titre, string message, bool erreur = false)
        {
            var f = new FenetreMessage(titre, message, "OK", null, erreur ? FenetreMessage.Genre.Erreur : FenetreMessage.Genre.Information)
            {
                Owner = Proprietaire
            };
            f.ShowDialog();
        }

        public void OuvrirFichier(string chemin)
        {
            if (string.IsNullOrEmpty(chemin) || !File.Exists(chemin)) return;
            try
            {
                // .NET 8 : UseShellExecute doit être demandé explicitement pour ouvrir un document.
                Process.Start(new ProcessStartInfo(chemin) { UseShellExecute = true });
            }
            catch (Exception)
            {
                OuvrirDansExplorateur(chemin);
            }
        }

        public void OuvrirDansExplorateur(string chemin)
        {
            if (string.IsNullOrEmpty(chemin)) return;
            try
            {
                if (File.Exists(chemin)) Process.Start("explorer.exe", "/select,\"" + chemin + "\"");
                else if (Directory.Exists(chemin)) Process.Start("explorer.exe", "\"" + chemin + "\"");
            }
            catch (Exception)
            {
                // Explorateur indisponible : sans conséquence.
            }
        }
    }
}
