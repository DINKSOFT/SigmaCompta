using System;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading.Tasks;
using SigmaCompta.App.Mvvm;
using SigmaCompta.App.Services;
using SigmaCompta.Core.Modeles;
using SigmaCompta.Core.Sql;
using SigmaCompta.Core.Stockage;

namespace SigmaCompta.App.ViewModels
{
    public sealed class ConnexionViewModel : PageViewModel
    {
        public ConnexionViewModel(Session session, INavigation navigation, IDialogues dialogues)
            : base(session, navigation, dialogues)
        {
            Serveurs = new ObservableCollection<string>();
            Recents = new ObservableCollection<string>();

            var p = session.Parametres;
            _serveur = p.DernierServeur ?? "";
            _authWindows = p.AuthentificationWindows;
            _login = p.Login ?? "";
            _memoriser = p.MemoriserMotDePasse;
            if (_memoriser) _motDePasse = MagasinParametres.Deproteger(p.MotDePasseProtege);
            foreach (var r in p.ServeursRecents ?? new System.Collections.Generic.List<string>()) Recents.Add(r);

            DetecterCommand = new CommandeAsync(DetecterAsync);
            ConnecterCommand = new CommandeAsync(ConnecterAsync, () => !string.IsNullOrWhiteSpace(Serveur) && !EstOccupe);
            DeconnecterCommand = new Commande(Deconnecter, () => Session.EstConnecte);
            ChoisirServeurCommand = new Commande(o => { Serveur = o as string; });
            ContinuerCommand = new Commande(() => Navigation.AllerA(2), () => Session.EstConnecte);
        }

        public override string Titre { get { return "Connexion au serveur"; } }
        public override string SousTitre { get { return "Choisis l'instance SQL Server qui héberge les bases comptables."; } }

        public ObservableCollection<string> Serveurs { get; private set; }
        public ObservableCollection<string> Recents { get; private set; }

        private string _serveur, _login, _motDePasse;
        private bool _authWindows, _memoriser, _detection, _serveursCharges;
        private InfoServeur _info;
        private string _erreur;

        public string Serveur { get { return _serveur; } set { if (Set(ref _serveur, value)) Erreur = null; } }
        public bool AuthentificationWindows
        {
            get { return _authWindows; }
            set { if (Set(ref _authWindows, value)) Notifier("AuthentificationSql"); }
        }
        public bool AuthentificationSql
        {
            get { return !_authWindows; }
            set { AuthentificationWindows = !value; }
        }
        public string Login { get { return _login; } set { Set(ref _login, value); } }
        public string MotDePasse { get { return _motDePasse ?? ""; } set { Set(ref _motDePasse, value); } }
        public bool Memoriser { get { return _memoriser; } set { Set(ref _memoriser, value); } }
        public bool DetectionEnCours { get { return _detection; } private set { Set(ref _detection, value); } }
        public InfoServeur Info { get { return _info; } private set { Set(ref _info, value); Notifier("EstConnecte"); } }
        public bool EstConnecte { get { return _info != null && Session.EstConnecte; } }
        public string Erreur { get { return _erreur; } private set { Set(ref _erreur, value); } }

        public CommandeAsync DetecterCommand { get; private set; }
        public CommandeAsync ConnecterCommand { get; private set; }
        public Commande DeconnecterCommand { get; private set; }
        public Commande ChoisirServeurCommand { get; private set; }
        public Commande ContinuerCommand { get; private set; }

        public override Task ActiverAsync()
        {
            if (!_serveursCharges)
            {
                _serveursCharges = true;
                foreach (var s in Recents.Concat(DecouverteServeurs.InstancesLocales()).Distinct(StringComparer.OrdinalIgnoreCase))
                    if (!Serveurs.Contains(s, StringComparer.OrdinalIgnoreCase)) Serveurs.Add(s);
                if (string.IsNullOrWhiteSpace(Serveur) && Serveurs.Count > 0) Serveur = Serveurs[0];
            }
            return Task.FromResult(0);
        }

        private async Task DetecterAsync()
        {
            DetectionEnCours = true;
            try
            {
                var trouves = await DecouverteServeurs.ToutesAsync(TimeSpan.FromSeconds(12));
                int nouveaux = 0;
                foreach (var s in trouves)
                    if (!Serveurs.Contains(s, StringComparer.OrdinalIgnoreCase)) { Serveurs.Add(s); nouveaux++; }
                Navigation.Notifier(trouves.Count == 0
                    ? "Aucune instance détectée. Saisis le nom manuellement (ex. SERVEUR\\COMPTA) — SQL Browser est peut-être arrêté."
                    : trouves.Count + " instance(s) détectée(s), dont " + nouveaux + " nouvelle(s).",
                    trouves.Count == 0 ? TypeNotification.Avertissement : TypeNotification.Succes);
                if (string.IsNullOrWhiteSpace(Serveur) && Serveurs.Count > 0) Serveur = Serveurs[0];
            }
            finally
            {
                DetectionEnCours = false;
            }
        }

        private async Task ConnecterAsync()
        {
            Erreur = null;
            var parametres = new ParametresConnexion
            {
                Serveur = Serveur.Trim(),
                AuthentificationWindows = AuthentificationWindows,
                Login = Login,
                MotDePasse = MotDePasse
            };
            InfoServeur info = null;
            var reussi = await OccuperAsync("Connexion à " + parametres.Serveur + "…", async () =>
            {
                var fabrique = new FabriqueConnexion(parametres);
                try
                {
                    using (var c = await fabrique.OuvrirAsync())
                        info = await ExplorateurBases.InfoServeurAsync(c);
                }
                catch (Exception ex)
                {
                    Erreur = SqlOutils.MessageConvivial(ex);
                    throw;
                }
                Session.DefinirConnexion(fabrique, info);
            });
            if (!reussi) return;

            Info = info;
            var p = Session.Parametres;
            p.DernierServeur = parametres.Serveur;
            p.AuthentificationWindows = parametres.AuthentificationWindows;
            p.Login = parametres.Login;
            p.MemoriserMotDePasse = Memoriser && !AuthentificationWindows;
            p.MotDePasseProtege = p.MemoriserMotDePasse ? MagasinParametres.Proteger(MotDePasse) : "";
            p.ServeursRecents.RemoveAll(x => string.Equals(x, parametres.Serveur, StringComparison.OrdinalIgnoreCase));
            p.ServeursRecents.Insert(0, parametres.Serveur);
            if (p.ServeursRecents.Count > 8) p.ServeursRecents.RemoveRange(8, p.ServeursRecents.Count - 8);
            Session.EnregistrerParametres();

            Recents.Clear();
            foreach (var r in p.ServeursRecents) Recents.Add(r);
            if (!Serveurs.Contains(parametres.Serveur, StringComparer.OrdinalIgnoreCase)) Serveurs.Insert(0, parametres.Serveur);

            Navigation.Notifier("Connecté à " + info.Nom + " (" + info.VersionCommerciale + ").", TypeNotification.Succes);
            Navigation.AllerA(2);
        }

        private void Deconnecter()
        {
            Session.DefinirConnexion(null, null);
            Info = null;
            Navigation.ActualiserEtapes();
        }
    }
}
