using System;
using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using SigmaCompta.Core.Fichiers;

namespace SigmaCompta.App.Controls
{
    /// <summary>
    /// Champ de date : zone de texte (la frappe reste libre) et calendrier déroulant.
    /// Le calendrier s'ouvre par le bouton, F4 ou Alt+Bas ; un clic sur un jour, ou Entrée, écrit la date
    /// au format jj/mm/aaaa. <see cref="Texte"/> se lie à une propriété texte du ViewModel.
    /// </summary>
    public partial class ChampDate : UserControl
    {
        private const string Formats = "dd/MM/yyyy|d/M/yyyy|dd/MM/yy|d/M/yy|ddMMyyyy|ddMMyy|yyyy-MM-dd";

        /// <summary>Le texte du champ (lié en double sens, transmis quand on quitte le champ ou qu'on choisit un jour).</summary>
        public static readonly DependencyProperty TexteProperty = DependencyProperty.Register(
            "Texte", typeof(string), typeof(ChampDate),
            new FrameworkPropertyMetadata("", FrameworkPropertyMetadataOptions.BindsTwoWayByDefault));

        /// <summary>Texte grisé affiché quand le champ est vide.</summary>
        public static readonly DependencyProperty IndicationProperty = DependencyProperty.Register(
            "Indication", typeof(string), typeof(ChampDate), new PropertyMetadata("jj/mm/aaaa"));

        /// <summary>Première date proposée par le calendrier (null = sans limite).</summary>
        public static readonly DependencyProperty DateMinProperty = DependencyProperty.Register(
            "DateMin", typeof(DateTime?), typeof(ChampDate), new PropertyMetadata(null));

        /// <summary>Dernière date proposée par le calendrier (null = sans limite).</summary>
        public static readonly DependencyProperty DateMaxProperty = DependencyProperty.Register(
            "DateMax", typeof(DateTime?), typeof(ChampDate), new PropertyMetadata(null));

        /// <summary>
        /// Mois affiché à l'ouverture quand le champ est vide ; sert aussi à lire un jour seul (« 17 »).
        /// Null = mois en cours.
        /// </summary>
        public static readonly DependencyProperty MoisParDefautProperty = DependencyProperty.Register(
            "MoisParDefaut", typeof(DateTime?), typeof(ChampDate), new PropertyMetadata(null));

        public string Texte { get { return (string)GetValue(TexteProperty); } set { SetValue(TexteProperty, value); } }
        public string Indication { get { return (string)GetValue(IndicationProperty); } set { SetValue(IndicationProperty, value); } }
        public DateTime? DateMin { get { return (DateTime?)GetValue(DateMinProperty); } set { SetValue(DateMinProperty, value); } }
        public DateTime? DateMax { get { return (DateTime?)GetValue(DateMaxProperty); } set { SetValue(DateMaxProperty, value); } }
        public DateTime? MoisParDefaut { get { return (DateTime?)GetValue(MoisParDefautProperty); } set { SetValue(MoisParDefautProperty, value); } }

        public ChampDate()
        {
            InitializeComponent();
            // Liaisons internes posées ici : aucun x:Name sur la racine, le contrôle peut donc être nommé par l'écran qui l'utilise.
            Saisie.SetBinding(TextBox.TextProperty, new Binding("Texte")
            {
                Source = this,
                Mode = BindingMode.TwoWay,
                UpdateSourceTrigger = UpdateSourceTrigger.LostFocus
            });
            Saisie.SetBinding(FrameworkElement.TagProperty, new Binding("Indication") { Source = this });
        }

        /// <summary>Zone de texte interne (navigation au clavier des écrans).</summary>
        public TextBox ZoneTexte { get { return Saisie; } }

        // ---------------------------------------------------------------- Ouverture

        private void Saisie_PreviewKeyDown(object sender, KeyEventArgs e)
        {
            bool ouvrir = e.Key == Key.F4 || (e.Key == Key.System && e.SystemKey == Key.Down);
            if (!ouvrir) return;
            e.Handled = true;
            Fenetre.IsOpen = !Fenetre.IsOpen;
        }

        private void Fenetre_Opened(object sender, EventArgs e)
        {
            // Le texte tapé est transmis avant l'ouverture, pour que le calendrier parte de cette date.
            var liaison = Saisie.GetBindingExpression(TextBox.TextProperty);
            if (liaison != null) liaison.UpdateSource();

            var min = DateMin.HasValue ? DateMin.Value.Date : (DateTime?)null;
            var max = DateMax.HasValue ? DateMax.Value.Date : (DateTime?)null;
            var date = Interpreter(Saisie.Text);

            // Ordre imposé par le calendrier : vider la sélection et les bornes avant de les reposer.
            Calendrier.SelectedDate = null;
            Calendrier.DisplayDateStart = null;
            Calendrier.DisplayDateEnd = null;

            var affiche = date ?? (MoisParDefaut.HasValue ? MoisParDefaut.Value.Date : DateTime.Today);
            if (min.HasValue && affiche < min.Value) affiche = min.Value;
            if (max.HasValue && affiche > max.Value) affiche = max.Value;
            Calendrier.DisplayDateStart = min;
            Calendrier.DisplayDateEnd = max;
            Calendrier.DisplayDate = affiche;
            if (date.HasValue && Autorisee(date.Value)) Calendrier.SelectedDate = date.Value;

            BoutonAujourdhui.IsEnabled = Autorisee(DateTime.Today);
            Calendrier.Focus();
        }

        // ---------------------------------------------------------------- Choix

        /// <summary>Clic sur un jour : la date de la case cliquée est écrite et le calendrier se ferme.</summary>
        private void Calendrier_PreviewMouseUp(object sender, MouseButtonEventArgs e)
        {
            // Le calendrier garde la capture de la souris après un clic : sans cela, le clic suivant serait perdu.
            if (Mouse.Captured is CalendarItem) Mouse.Capture(null);

            var jour = Ancetre<CalendarDayButton>(e.OriginalSource as DependencyObject);
            if (jour == null || !jour.IsEnabled) return;
            if (jour.DataContext is DateTime)
            {
                var date = (DateTime)jour.DataContext;
                if (Autorisee(date)) Choisir(date);
                return;
            }
            // Repli : on laisse le calendrier enregistrer la sélection, puis on la reprend.
            Dispatcher.BeginInvoke(new Action(() =>
            {
                if (Fenetre.IsOpen && Calendrier.SelectedDate.HasValue && Autorisee(Calendrier.SelectedDate.Value))
                    Choisir(Calendrier.SelectedDate.Value);
            }), DispatcherPriority.Input);
        }

        private void Calendrier_PreviewKeyDown(object sender, KeyEventArgs e)
        {
            if (e.Key == Key.Enter)
            {
                e.Handled = true;
                if (Calendrier.SelectedDate.HasValue) Choisir(Calendrier.SelectedDate.Value);
            }
            else if (e.Key == Key.Escape)
            {
                e.Handled = true;
                Fermer();
            }
        }

        private void Aujourdhui_Click(object sender, RoutedEventArgs e)
        {
            if (Autorisee(DateTime.Today)) Choisir(DateTime.Today);
        }

        private void Choisir(DateTime date)
        {
            Texte = date.ToString("dd/MM/yyyy", CultureInfo.InvariantCulture);
            Fermer();
        }

        private void Fermer()
        {
            Fenetre.IsOpen = false;
            Saisie.Focus();
            Saisie.CaretIndex = Saisie.Text.Length;
        }

        private bool Autorisee(DateTime d)
        {
            return (!DateMin.HasValue || d.Date >= DateMin.Value.Date) && (!DateMax.HasValue || d.Date <= DateMax.Value.Date);
        }

        /// <summary>Date lue dans le texte : date complète, ou jour seul rapporté au mois par défaut.</summary>
        private DateTime? Interpreter(string texte)
        {
            if (string.IsNullOrWhiteSpace(texte)) return null;
            var t = texte.Trim();
            var reference = MoisParDefaut.HasValue ? MoisParDefaut.Value : DateTime.Today;
            int jour, mois;
            if (t.Length <= 2 && int.TryParse(t, NumberStyles.None, CultureInfo.InvariantCulture, out jour))
                return jour >= 1 && jour <= DateTime.DaysInMonth(reference.Year, reference.Month)
                    ? new DateTime(reference.Year, reference.Month, jour) : (DateTime?)null;
            var parts = t.Split('/', '.', '-');
            if (parts.Length == 2 && int.TryParse(parts[0], NumberStyles.None, CultureInfo.InvariantCulture, out jour)
                && int.TryParse(parts[1], NumberStyles.None, CultureInfo.InvariantCulture, out mois))
                return mois >= 1 && mois <= 12 && jour >= 1 && jour <= DateTime.DaysInMonth(reference.Year, mois)
                    ? new DateTime(reference.Year, mois, jour) : (DateTime?)null;
            DateTime d;
            return Analyseurs.EssayerDate(t, Formats, out d) ? d.Date : (DateTime?)null;
        }

        private static T Ancetre<T>(DependencyObject o) where T : DependencyObject
        {
            while (o != null && !(o is T))
                o = o is Visual ? VisualTreeHelper.GetParent(o) : LogicalTreeHelper.GetParent(o);
            return o as T;
        }
    }
}
