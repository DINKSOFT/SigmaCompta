using System.Windows;
using System.Windows.Input;
using System.Windows.Media;

namespace SigmaCompta.App.Views
{
    /// <summary>Boîte de message / confirmation aux couleurs de l'application.</summary>
    public partial class FenetreMessage : Window
    {
        public enum Genre { Information, Question, Danger, Erreur }

        public FenetreMessage(string titre, string message, string libelleValider, string libelleAnnuler, Genre genre)
        {
            InitializeComponent();
            Titre.Text = titre;
            Message.Text = message;
            BoutonValider.Content = libelleValider;
            if (libelleAnnuler == null) BoutonAnnuler.Visibility = Visibility.Collapsed;
            else BoutonAnnuler.Content = libelleAnnuler;

            string fond, texte, glyphe;
            switch (genre)
            {
                case Genre.Danger:
                    fond = "AvertDoux"; texte = "Avert"; glyphe = "\uE7BA";
                    BoutonValider.Style = (Style)FindResource("BoutonDanger");
                    break;
                case Genre.Erreur:
                    fond = "ErreurDoux"; texte = "Erreur"; glyphe = "\uE783";
                    break;
                case Genre.Question:
                    fond = "PrimaireDoux"; texte = "Primaire"; glyphe = "\uE9CE";
                    break;
                default:
                    fond = "InfoDoux"; texte = "Info"; glyphe = "\uE946";
                    break;
            }
            Pastille.Background = (Brush)FindResource(fond);
            Icone.Foreground = (Brush)FindResource(texte);
            Icone.Text = glyphe;
        }

        private void Valider(object sender, RoutedEventArgs e)
        {
            DialogResult = true;
        }

        private void Annuler(object sender, RoutedEventArgs e)
        {
            DialogResult = false;
        }

        private void Deplacer(object sender, MouseButtonEventArgs e)
        {
            if (e.ButtonState == MouseButtonState.Pressed) DragMove();
        }
    }
}
