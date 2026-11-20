namespace SigmaCompta.App.Services
{
    /// <summary>Boîtes de dialogue, abstraites pour garder les ViewModels indépendants de WPF.</summary>
    public interface IDialogues
    {
        /// <summary>Retourne le chemin choisi, ou null si annulé.</summary>
        string ChoisirFichier(string titre, string filtre, string dossierInitial = null);

        /// <summary>Retourne le chemin d'enregistrement choisi, ou null si annulé.</summary>
        string ChoisirEmplacement(string titre, string filtre, string nomParDefaut, string dossierInitial = null);

        bool Confirmer(string titre, string message, string libelleConfirmation = "Confirmer", bool danger = false);

        void Informer(string titre, string message, bool erreur = false);

        void OuvrirDansExplorateur(string chemin);

        /// <summary>Ouvre le fichier avec l'application associée (lecteur PDF…).</summary>
        void OuvrirFichier(string chemin);
    }

    public enum TypeNotification { Info, Succes, Avertissement, Erreur }

    /// <summary>Navigation et notifications offertes aux pages par la fenêtre principale.</summary>
    public interface INavigation
    {
        void AllerA(int numeroEtape);
        void Notifier(string message, TypeNotification type = TypeNotification.Info);
        void ActualiserEtapes();
    }
}
