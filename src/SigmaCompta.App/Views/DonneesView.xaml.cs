using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using SigmaCompta.App.ViewModels;
using SigmaCompta.Core.Fichiers;

namespace SigmaCompta.App.Views
{
    public partial class DonneesView : UserControl
    {
        public DonneesView()
        {
            InitializeComponent();
        }

        private static string FichierAccepte(DragEventArgs e)
        {
            if (!e.Data.GetDataPresent(DataFormats.FileDrop)) return null;
            var fichiers = e.Data.GetData(DataFormats.FileDrop) as string[];
            if (fichiers == null || fichiers.Length == 0) return null;
            var ext = (Path.GetExtension(fichiers[0]) ?? "").ToLowerInvariant();
            return LecteurSource.ExtensionsExcel.Contains(ext) || LecteurSource.ExtensionsTexte.Contains(ext) ? fichiers[0] : null;
        }

        private void SurSurvolDepot(object sender, DragEventArgs e)
        {
            e.Effects = FichierAccepte(e) != null ? DragDropEffects.Copy : DragDropEffects.None;
            e.Handled = true;
        }

        private async void SurDepot(object sender, DragEventArgs e)
        {
            var chemin = FichierAccepte(e);
            var vm = DataContext as DonneesViewModel;
            if (chemin == null || vm == null) return;
            await vm.DeposerFichierAsync(chemin);
        }
    }
}
