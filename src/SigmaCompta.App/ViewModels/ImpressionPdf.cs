using System;
using System.IO;
using SigmaCompta.App.Services;

namespace SigmaCompta.App.ViewModels
{
    /// <summary>Enregistrement d'un PDF choisi par l'utilisateur, ou aperçu temporaire à imprimer depuis le lecteur PDF.</summary>
    public static class ImpressionPdf
    {
        public static void Enregistrer(IDialogues dialogues, INavigation navigation, Func<byte[]> generer, string nomFichier)
        {
            var chemin = dialogues.ChoisirEmplacement("Enregistrer en PDF", "Document PDF (*.pdf)|*.pdf", nomFichier);
            if (chemin == null) return;
            try
            {
                File.WriteAllBytes(chemin, generer());
                navigation.Notifier("PDF enregistré : " + Path.GetFileName(chemin), TypeNotification.Succes);
                dialogues.OuvrirFichier(chemin);
            }
            catch (IOException ex)
            {
                navigation.Notifier("PDF impossible : " + ex.Message + " (le fichier est peut-être ouvert).", TypeNotification.Erreur);
            }
        }

        /// <summary>PDF écrit dans le dossier temporaire puis ouvert : l'impression se fait depuis le lecteur (Ctrl+P).</summary>
        public static void Imprimer(IDialogues dialogues, INavigation navigation, Func<byte[]> generer, string nomFichier)
        {
            try
            {
                var dossier = Path.Combine(Path.GetTempPath(), "SigmaCompta");
                Directory.CreateDirectory(dossier);
                var chemin = Path.Combine(dossier, Path.GetFileNameWithoutExtension(nomFichier) + "_" + DateTime.Now.ToString("HHmmss") + ".pdf");
                File.WriteAllBytes(chemin, generer());
                dialogues.OuvrirFichier(chemin);
                navigation.Notifier("Document ouvert dans le lecteur PDF : imprimez-le avec Ctrl+P.", TypeNotification.Info);
            }
            catch (IOException ex)
            {
                navigation.Notifier("Impression impossible : " + ex.Message, TypeNotification.Erreur);
            }
        }

        public static string Societe(Session s)
        {
            return s == null || s.Base == null ? "" : (string.IsNullOrWhiteSpace(s.Base.RaisonSociale) ? s.Base.Nom : s.Base.RaisonSociale);
        }

        public static string Dossier(Session s)
        {
            return s == null || s.Base == null ? "" : s.Base.Nom;
        }
    }
}
