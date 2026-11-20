using System;
using System.Data;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;

namespace SigmaCompta.App.Controls
{
    /// <summary>Propriétés attachées utilitaires.</summary>
    public static class Aides
    {
        // ---------------- PasswordBox liable ----------------
        public static readonly DependencyProperty MotDePasseProperty = DependencyProperty.RegisterAttached(
            "MotDePasse", typeof(string), typeof(Aides),
            new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.BindsTwoWayByDefault, MotDePasseModifie));

        private static readonly DependencyProperty EnMiseAJourProperty =
            DependencyProperty.RegisterAttached("EnMiseAJour", typeof(bool), typeof(Aides), new PropertyMetadata(false));

        public static string GetMotDePasse(DependencyObject d) { return (string)d.GetValue(MotDePasseProperty); }
        public static void SetMotDePasse(DependencyObject d, string v) { d.SetValue(MotDePasseProperty, v); }

        private static void MotDePasseModifie(DependencyObject d, DependencyPropertyChangedEventArgs e)
        {
            var pb = d as PasswordBox;
            if (pb == null) return;
            pb.PasswordChanged -= SurPasswordChanged;
            if (!(bool)pb.GetValue(EnMiseAJourProperty)) pb.Password = e.NewValue as string ?? "";
            pb.PasswordChanged += SurPasswordChanged;
        }

        private static void SurPasswordChanged(object sender, RoutedEventArgs e)
        {
            var pb = (PasswordBox)sender;
            pb.SetValue(EnMiseAJourProperty, true);
            SetMotDePasse(pb, pb.Password);
            pb.SetValue(EnMiseAJourProperty, false);
        }

        // ---------------- Colonnes auto-générées lisibles ----------------
        /// <summary>
        /// Sur un DataGrid lié à un DataView : l'en-tête reprend DataColumn.Caption
        /// (les colonnes sont nommées c0…cn pour éviter les problèmes de chemin de liaison)
        /// et les dates sont affichées en jj/mm/aaaa.
        /// </summary>
        public static readonly DependencyProperty EntetesLisiblesProperty = DependencyProperty.RegisterAttached(
            "EntetesLisibles", typeof(bool), typeof(Aides), new PropertyMetadata(false, EntetesLisiblesModifie));

        public static bool GetEntetesLisibles(DependencyObject d) { return (bool)d.GetValue(EntetesLisiblesProperty); }
        public static void SetEntetesLisibles(DependencyObject d, bool v) { d.SetValue(EntetesLisiblesProperty, v); }

        private static void EntetesLisiblesModifie(DependencyObject d, DependencyPropertyChangedEventArgs e)
        {
            var g = d as DataGrid;
            if (g == null) return;
            g.AutoGeneratingColumn -= SurColonneGeneree;
            if ((bool)e.NewValue) g.AutoGeneratingColumn += SurColonneGeneree;
        }

        private static void SurColonneGeneree(object sender, DataGridAutoGeneratingColumnEventArgs e)
        {
            var g = (DataGrid)sender;
            var vue = g.ItemsSource as DataView;
            string entete = e.PropertyName;
            if (vue != null && vue.Table.Columns.Contains(e.PropertyName))
                entete = vue.Table.Columns[e.PropertyName].Caption;

            var colonne = new DataGridTextColumn
            {
                Header = entete,
                Binding = new Binding("[" + e.PropertyName + "]")
                {
                    StringFormat = e.PropertyType == typeof(DateTime) ? "dd/MM/yyyy" :
                                   e.PropertyType == typeof(decimal) || e.PropertyType == typeof(double) ? "#,##0.##" : null
                },
                MaxWidth = 420
            };
            e.Column = colonne;
        }
    }
}
