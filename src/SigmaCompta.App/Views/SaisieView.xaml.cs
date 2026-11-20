using System;
using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using SigmaCompta.App.ViewModels;

namespace SigmaCompta.App.Views
{
    public partial class SaisieView : UserControl
    {
        /// <summary>Champ (compte ou tiers) sous lequel la liste des suggestions est déroulée.</summary>
        private TextBox _champSource;
        private INotifyPropertyChanged _vmSuivi;

        public SaisieView()
        {
            InitializeComponent();
            DataContextChanged += (s, e) => SuivreViewModel();
            // La liste déroulante ne suit pas la fenêtre : on la ferme quand la fenêtre bouge ou perd la main.
            Loaded += (s, e) =>
            {
                var fenetre = Window.GetWindow(this);
                if (fenetre == null) return;
                fenetre.LocationChanged -= Fenetre_Changee;
                fenetre.LocationChanged += Fenetre_Changee;
                fenetre.Deactivated -= Fenetre_Changee;
                fenetre.Deactivated += Fenetre_Changee;
            };
        }

        private void Fenetre_Changee(object sender, EventArgs e)
        {
            FermerSuggestions();
        }

        private SaisieViewModel Vm { get { return DataContext as SaisieViewModel; } }

        /// <summary>Quand la grille est reconstruite (ligne ajoutée, journal relu), on fait défiler jusqu'à la pièce en cours.</summary>
        private void SuivreViewModel()
        {
            if (_vmSuivi != null) _vmSuivi.PropertyChanged -= Vm_PropertyChanged;
            _vmSuivi = DataContext as INotifyPropertyChanged;
            if (_vmSuivi != null) _vmSuivi.PropertyChanged += Vm_PropertyChanged;
        }

        private void Vm_PropertyChanged(object sender, PropertyChangedEventArgs e)
        {
            if (e.PropertyName != "LignesGrille") return;
            Dispatcher.BeginInvoke(DispatcherPriority.Background, new Action(() =>
            {
                if (GrilleJournal.Items.Count > 0) GrilleJournal.ScrollIntoView(GrilleJournal.Items[GrilleJournal.Items.Count - 1]);
            }));
        }

        // ---------------------------------------------------------------- Ligne de saisie au clavier

        /// <summary>
        /// Entrée passe au champ suivant ; Entrée sur le crédit, ou sur un débit renseigné, ajoute la ligne et revient
        /// sur le compte. Flèche bas sur le compte ou le tiers : descend dans la liste. Échap : ferme la liste.
        /// </summary>
        private void LigneSaisie_PreviewKeyDown(object sender, KeyEventArgs e)
        {
            var champ = e.OriginalSource as TextBox;
            var vm = Vm;
            if (champ == null || vm == null) return;

            if (e.Key == Key.Escape && PopupSuggestions.IsOpen)
            {
                e.Handled = true;
                FermerSuggestions();
                return;
            }

            if (e.Key == Key.Down && (champ == ChampCompte || champ == ChampTiers))
            {
                if (ListeSuggestions.Items.Count == 0) return;
                e.Handled = true;
                OuvrirSuggestions(champ);
                Dispatcher.BeginInvoke(DispatcherPriority.Input, new Action(EntrerDansSuggestions));
                return;
            }

            if (e.Key != Key.Enter) return;
            e.Handled = true;
            FermerSuggestions();
            bool valider = champ == ChampCredit || (champ == ChampDebit && !string.IsNullOrWhiteSpace(champ.Text));
            if (!valider)
            {
                champ.MoveFocus(new TraversalRequest(FocusNavigationDirection.Next));
                return;
            }
            vm.ValiderLigneCommand.Execute(null);
            if (string.IsNullOrEmpty(vm.MessageLigne))
            {
                ChampCompte.Focus();
                ChampCompte.SelectAll();
            }
        }

        // ---------------------------------------------------------------- Liste déroulante des comptes et des tiers

        /// <summary>La frappe dans le compte ou le tiers déroule la liste filtrée sous le champ.</summary>
        private void ChampCompte_TextChanged(object sender, TextChangedEventArgs e)
        {
            var champ = sender as TextBox;
            if (champ == null || !champ.IsKeyboardFocused) return;
            // Après la mise à jour du ViewModel (liaison) : la liste reflète le texte tapé.
            Dispatcher.BeginInvoke(DispatcherPriority.Input, new Action(() =>
            {
                if (!champ.IsKeyboardFocused) return;
                if (champ.Text.Trim().Length > 0 && ListeSuggestions.Items.Count > 0) OuvrirSuggestions(champ);
                else FermerSuggestions();
            }));
        }

        private void OuvrirSuggestions(TextBox champ)
        {
            _champSource = champ;
            PopupSuggestions.PlacementTarget = champ;
            PopupSuggestions.IsOpen = true;
        }

        private void FermerSuggestions()
        {
            PopupSuggestions.IsOpen = false;
        }

        private void EntrerDansSuggestions()
        {
            if (ListeSuggestions.Items.Count == 0) return;
            ListeSuggestions.SelectedIndex = 0;
            ListeSuggestions.ScrollIntoView(ListeSuggestions.SelectedItem);
            ListeSuggestions.UpdateLayout();
            var element = ListeSuggestions.ItemContainerGenerator.ContainerFromIndex(0) as ListBoxItem;
            if (element != null) element.Focus();
            else ListeSuggestions.Focus();
        }

        /// <summary>La liste se ferme quand le clavier quitte à la fois le champ et la liste.</summary>
        private void ChampListe_LostKeyboardFocus(object sender, KeyboardFocusChangedEventArgs e)
        {
            Dispatcher.BeginInvoke(DispatcherPriority.Input, new Action(() =>
            {
                if (ListeSuggestions.IsKeyboardFocusWithin) return;
                if (_champSource != null && _champSource.IsKeyboardFocused) return;
                FermerSuggestions();
            }));
        }

        /// <summary>Entrée choisit le compte et passe au champ suivant ; Échap revient au champ.</summary>
        private void Suggestions_PreviewKeyDown(object sender, KeyEventArgs e)
        {
            if (e.Key == Key.Enter || e.Key == Key.Tab)
            {
                e.Handled = true;
                Choisir();
            }
            else if (e.Key == Key.Escape)
            {
                e.Handled = true;
                FermerSuggestions();
                if (_champSource != null) _champSource.Focus();
            }
        }

        /// <summary>Un clic sur un compte de la liste le choisit.</summary>
        private void Suggestions_PreviewMouseLeftButtonUp(object sender, MouseButtonEventArgs e)
        {
            var element = e.OriginalSource as DependencyObject;
            while (element != null && !(element is ListBoxItem)) element = VisualTreeHelper.GetParent(element);
            var item = element as ListBoxItem;
            if (item == null) return;
            ListeSuggestions.SelectedItem = item.DataContext;
            Choisir();
        }

        private void Choisir()
        {
            var vm = Vm;
            var element = ListeSuggestions.SelectedItem;
            FermerSuggestions();
            if (vm == null || element == null || !vm.UtiliserSuggestionCommand.CanExecute(element)) return;
            vm.UtiliserSuggestionCommand.Execute(element);
            var champ = _champSource ?? ChampCompte;
            champ.Focus();
            champ.CaretIndex = champ.Text.Length;
            champ.MoveFocus(new TraversalRequest(FocusNavigationDirection.Next));
            // Choisir un compte refiltre la liste : elle ne doit pas se rouvrir sur le champ suivant.
            Dispatcher.BeginInvoke(DispatcherPriority.Input, new Action(FermerSuggestions));
        }

        // ---------------------------------------------------------------- Grille

        /// <summary>Double-clic sur une ligne de la pièce en cours : elle revient dans la ligne de saisie pour être modifiée.</summary>
        private void GrilleJournal_MouseDoubleClick(object sender, MouseButtonEventArgs e)
        {
            var vm = Vm;
            if (vm == null || !vm.ModifierLigneCommand.CanExecute(null)) return;
            vm.ModifierLigneCommand.Execute(null);
            ChampCompte.Focus();
            ChampCompte.SelectAll();
        }
    }
}
