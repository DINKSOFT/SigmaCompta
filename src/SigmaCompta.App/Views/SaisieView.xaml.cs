using System.Windows.Controls;
using System.Windows.Input;
using SigmaCompta.App.ViewModels;

namespace SigmaCompta.App.Views
{
    public partial class SaisieView : UserControl
    {
        /// <summary>Champ (compte ou tiers) depuis lequel on est descendu dans la liste des suggestions.</summary>
        private TextBox _champSource;

        public SaisieView()
        {
            InitializeComponent();
        }

        private SaisieViewModel Vm { get { return DataContext as SaisieViewModel; } }

        /// <summary>
        /// Ligne de saisie au clavier : Entrée passe au champ suivant ; Entrée sur le crédit, ou sur un débit
        /// renseigné, ajoute la ligne et revient sur le compte. Flèche bas sur le compte ou le tiers : liste des suggestions.
        /// </summary>
        private void LigneSaisie_PreviewKeyDown(object sender, KeyEventArgs e)
        {
            var champ = e.OriginalSource as TextBox;
            var vm = Vm;
            if (champ == null || vm == null) return;

            if (e.Key == Key.Down && (champ == ChampCompte || champ == ChampTiers) && ListeSuggestions.Items.Count > 0)
            {
                e.Handled = true;
                _champSource = champ;
                ListeSuggestions.SelectedIndex = 0;
                ListeSuggestions.ScrollIntoView(ListeSuggestions.SelectedItem);
                var element = ListeSuggestions.ItemContainerGenerator.ContainerFromIndex(0) as ListBoxItem;
                if (element != null) element.Focus();
                else ListeSuggestions.Focus();
                return;
            }

            if (e.Key != Key.Enter) return;
            e.Handled = true;
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

        /// <summary>Entrée choisit la suggestion et passe au champ suivant ; Échap revient au champ.</summary>
        private void Suggestions_PreviewKeyDown(object sender, KeyEventArgs e)
        {
            if (e.Key == Key.Enter)
            {
                e.Handled = true;
                Choisir();
            }
            else if (e.Key == Key.Escape && _champSource != null)
            {
                e.Handled = true;
                _champSource.Focus();
            }
        }

        private void Suggestions_MouseDoubleClick(object sender, MouseButtonEventArgs e)
        {
            Choisir();
        }

        private void Choisir()
        {
            var vm = Vm;
            var element = ListeSuggestions.SelectedItem;
            if (vm == null || element == null || !vm.UtiliserSuggestionCommand.CanExecute(element)) return;
            vm.UtiliserSuggestionCommand.Execute(element);
            var champ = _champSource ?? ChampCompte;
            champ.Focus();
            champ.CaretIndex = champ.Text.Length;
            champ.MoveFocus(new TraversalRequest(FocusNavigationDirection.Next));
        }

        /// <summary>Double-clic sur une ligne de la pièce : elle revient dans la ligne de saisie pour être modifiée.</summary>
        private void LignesPiece_MouseDoubleClick(object sender, MouseButtonEventArgs e)
        {
            var vm = Vm;
            if (vm == null || !vm.ModifierLigneCommand.CanExecute(null)) return;
            vm.ModifierLigneCommand.Execute(null);
            ChampCompte.Focus();
            ChampCompte.SelectAll();
        }
    }
}
