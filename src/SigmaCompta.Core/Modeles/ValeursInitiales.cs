using System.Reflection;

namespace SigmaCompta.Core.Modeles
{
    /// <summary>
    /// DataContractSerializer n'appelle pas les constructeurs : sans ce helper, une
    /// propriété absente du fichier JSON prendrait null/false au lieu de sa valeur
    /// par défaut. Appelé depuis les méthodes [OnDeserializing].
    /// </summary>
    internal static class ValeursInitiales
    {
        public static void Copier<T>(T source, T cible) where T : class
        {
            foreach (var p in typeof(T).GetProperties(BindingFlags.Public | BindingFlags.Instance))
            {
                if (p.CanRead && p.CanWrite && p.GetIndexParameters().Length == 0)
                    p.SetValue(cible, p.GetValue(source, null), null);
            }
        }
    }
}
