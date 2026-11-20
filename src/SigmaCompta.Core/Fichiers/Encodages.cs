using System.Text;

namespace SigmaCompta.Core.Fichiers
{
    /// <summary>
    /// .NET 8 ne connaît par défaut que l'UTF-8, l'UTF-16, l'ASCII et le Latin-1. Les pages de code
    /// Windows (1252 pour les CSV « ANSI ») sont fournies par CodePagesEncodingProvider, à enregistrer
    /// une fois : au démarrage de l'application, des tests, et avant toute lecture CSV.
    /// </summary>
    public static class Encodages
    {
        private static readonly object Verrou = new object();
        private static bool _fait;

        public static void Initialiser()
        {
            lock (Verrou)
            {
                if (_fait) return;
                Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
                _fait = true;
            }
        }
    }
}
