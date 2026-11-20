using System;
using System.Collections;
using System.Globalization;
using System.Windows;
using System.Windows.Data;
using System.Windows.Media;
using SigmaCompta.App.Services;
using SigmaCompta.Core.Modeles;

namespace SigmaCompta.App.Converters
{
    internal static class Ressources
    {
        public static Brush Pinceau(string cle)
        {
            return (Application.Current != null ? Application.Current.TryFindResource(cle) as Brush : null) ?? Brushes.Gray;
        }

        public static bool Inverse(object parametre)
        {
            return string.Equals(parametre as string, "inverse", StringComparison.OrdinalIgnoreCase);
        }
    }

    /// <summary>bool → Visible/Collapsed (paramètre « inverse » pour l'inverse).</summary>
    public sealed class BoolVisibilite : IValueConverter
    {
        public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        {
            bool v = value is bool && (bool)value;
            if (Ressources.Inverse(parameter)) v = !v;
            return v ? Visibility.Visible : Visibility.Collapsed;
        }

        public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        {
            bool v = value is Visibility && (Visibility)value == Visibility.Visible;
            return Ressources.Inverse(parameter) ? !v : v;
        }
    }

    /// <summary>null, chaîne vide, 0 ou collection vide → Collapsed (paramètre « inverse » pour l'inverse).</summary>
    public sealed class ValeurVisibilite : IValueConverter
    {
        public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        {
            bool present;
            if (value == null) present = false;
            else if (value is string) present = ((string)value).Length > 0;
            else if (value is int) present = (int)value != 0;
            else if (value is decimal) present = (decimal)value != 0;
            else if (value is ICollection) present = ((ICollection)value).Count > 0;
            else present = true;
            if (Ressources.Inverse(parameter)) present = !present;
            return present ? Visibility.Visible : Visibility.Collapsed;
        }

        public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        {
            throw new NotSupportedException();
        }
    }

    public sealed class InverseBool : IValueConverter
    {
        public object Convert(object value, Type targetType, object parameter, CultureInfo culture) { return !(value is bool && (bool)value); }
        public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) { return !(value is bool && (bool)value); }
    }

    /// <summary>valeur.ToString() == paramètre → true ; en retour, true → paramètre (pour les filtres en RadioButton).</summary>
    public sealed class EgalVersBool : IValueConverter
    {
        public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        {
            return value != null && string.Equals(value.ToString(), parameter as string, StringComparison.Ordinal);
        }

        public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        {
            return value is bool && (bool)value ? parameter : Binding.DoNothing;
        }
    }

    /// <summary>Première lettre en majuscule (pastille d'avatar).</summary>
    public sealed class Initiale : IValueConverter
    {
        public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        {
            var s = (value as string ?? "").Trim();
            return s.Length == 0 ? "?" : s.Substring(0, 1).ToUpperInvariant();
        }

        public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) { throw new NotSupportedException(); }
    }

    /// <summary>Couleur, couleur douce ou icône d'un statut (pièce, contrôle, notification, anomalie). Paramètre : texte | fond | icone.</summary>
    public sealed class StatutApparence : IValueConverter
    {
        public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        {
            string famille;
            if (value is StatutPiece) famille = Famille((StatutPiece)value);
            else if (value is StatutControle) famille = Famille((StatutControle)value);
            else if (value is TypeNotification) famille = Famille((TypeNotification)value);
            else if (value is NiveauAnomalie) famille = (NiveauAnomalie)value == NiveauAnomalie.Erreur ? "Erreur" : "Avert";
            else if (value is bool) famille = (bool)value ? "Succes" : "Neutre";
            else famille = "Neutre";

            var mode = (parameter as string ?? "texte").ToLowerInvariant();
            if (mode == "icone") return Icone(famille, value);
            if (famille == "Neutre")
                return mode == "fond" ? Ressources.Pinceau("SurfaceSurvol") : Ressources.Pinceau("TexteSecondaire");
            return Ressources.Pinceau(mode == "fond" ? famille + "Doux" : famille);
        }

        private static string Famille(StatutPiece s)
        {
            switch (s)
            {
                case StatutPiece.Valide:
                case StatutPiece.Simulee:
                case StatutPiece.Importee: return "Succes";
                case StatutPiece.Avertissement: return "Avert";
                case StatutPiece.Erreur:
                case StatutPiece.Echec: return "Erreur";
                case StatutPiece.Annulee: return "Neutre";
                default: return "Info";
            }
        }

        private static string Famille(StatutControle s)
        {
            switch (s)
            {
                case StatutControle.Ok: return "Succes";
                case StatutControle.Avertissement: return "Avert";
                case StatutControle.Erreur: return "Erreur";
                default: return "Info";
            }
        }

        private static string Famille(TypeNotification t)
        {
            switch (t)
            {
                case TypeNotification.Succes: return "Succes";
                case TypeNotification.Avertissement: return "Avert";
                case TypeNotification.Erreur: return "Erreur";
                default: return "Info";
            }
        }

        private static string Icone(string famille, object valeur)
        {
            if (valeur is StatutPiece && (StatutPiece)valeur == StatutPiece.Importee) return "\uE930"; // Completed
            if (valeur is StatutPiece && (StatutPiece)valeur == StatutPiece.Annulee) return "\uE711";
            switch (famille)
            {
                case "Succes": return "\uE73E";
                case "Avert": return "\uE7BA";
                case "Erreur": return "\uE711";
                case "Info": return "\uE946";
                default: return "\uE738";
            }
        }

        public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) { throw new NotSupportedException(); }
    }

    /// <summary>Libellé français d'un statut de pièce.</summary>
    public sealed class LibelleStatut : IValueConverter
    {
        public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        {
            if (value is StatutPiece) return Core.Import.ServiceImport.LibelleStatut((StatutPiece)value);
            if (value is StatutControle)
            {
                switch ((StatutControle)value)
                {
                    case StatutControle.Ok: return "Conforme";
                    case StatutControle.Avertissement: return "Attention";
                    case StatutControle.Erreur: return "Bloquant";
                    default: return "Info";
                }
            }
            return value == null ? "" : value.ToString();
        }

        public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) { throw new NotSupportedException(); }
    }
}
