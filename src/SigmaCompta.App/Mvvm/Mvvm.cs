using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Threading.Tasks;
using System.Windows.Input;

namespace SigmaCompta.App.Mvvm
{
    public abstract class ObservableObject : INotifyPropertyChanged
    {
        public event PropertyChangedEventHandler PropertyChanged;

        protected bool Set<T>(ref T champ, T valeur, [CallerMemberName] string nom = null)
        {
            if (EqualityComparer<T>.Default.Equals(champ, valeur)) return false;
            champ = valeur;
            Notifier(nom);
            return true;
        }

        protected void Notifier([CallerMemberName] string nom = null)
        {
            var h = PropertyChanged;
            if (h != null) h(this, new PropertyChangedEventArgs(nom));
        }

        protected void NotifierPlusieurs(params string[] noms)
        {
            foreach (var n in noms) Notifier(n);
        }

        /// <summary>Signale que toutes les propriétés ont pu changer.</summary>
        protected void NotifierTout()
        {
            var h = PropertyChanged;
            if (h != null) h(this, new PropertyChangedEventArgs(null));
        }
    }

    /// <summary>
    /// Base des commandes : l'application relie <see cref="ReevaluerTout"/> à
    /// CommandManager.RequerySuggested, sans que les ViewModels dépendent de WPF.
    /// </summary>
    public abstract class CommandeBase : ICommand
    {
        private static event EventHandler Reevaluation;

        public static void ReevaluerTout()
        {
            var h = Reevaluation;
            if (h != null) h(null, EventArgs.Empty);
        }

        protected CommandeBase()
        {
            Reevaluation += (s, e) => Reevaluer();
        }

        public event EventHandler CanExecuteChanged;

        public void Reevaluer()
        {
            var h = CanExecuteChanged;
            if (h != null) h(this, EventArgs.Empty);
        }

        public abstract bool CanExecute(object parameter);
        public abstract void Execute(object parameter);
    }

    public sealed class Commande : CommandeBase
    {
        private readonly Action<object> _action;
        private readonly Func<object, bool> _peut;

        public Commande(Action action, Func<bool> peut = null)
            : this(_ => action(), peut == null ? (Func<object, bool>)null : _ => peut())
        {
        }

        public Commande(Action<object> action, Func<object, bool> peut = null)
        {
            _action = action;
            _peut = peut;
        }

        public override bool CanExecute(object parameter) { return _peut == null || _peut(parameter); }
        public override void Execute(object parameter) { _action(parameter); }
    }

    /// <summary>Commande asynchrone : empêche la double exécution et remonte les erreurs au gestionnaire global.</summary>
    public sealed class CommandeAsync : CommandeBase
    {
        /// <summary>Gestionnaire d'erreurs non traitées (affecté par MainViewModel).</summary>
        public static Action<Exception> GestionnaireErreur;

        private readonly Func<object, Task> _action;
        private readonly Func<object, bool> _peut;
        private bool _enCours;

        public CommandeAsync(Func<Task> action, Func<bool> peut = null)
            : this(_ => action(), peut == null ? (Func<object, bool>)null : _ => peut())
        {
        }

        public CommandeAsync(Func<object, Task> action, Func<object, bool> peut = null)
        {
            _action = action;
            _peut = peut;
        }

        public bool EnCours { get { return _enCours; } }

        public override bool CanExecute(object parameter) { return !_enCours && (_peut == null || _peut(parameter)); }

        public override async void Execute(object parameter)
        {
            if (!CanExecute(parameter)) return;
            _enCours = true;
            Reevaluer();
            try
            {
                await _action(parameter);
            }
            catch (Exception ex)
            {
                var g = GestionnaireErreur;
                if (g != null) g(ex);
            }
            finally
            {
                _enCours = false;
                Reevaluer();
                ReevaluerTout();
            }
        }
    }
}
