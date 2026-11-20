using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.Serialization;
using System.Runtime.Serialization.Json;
using System.Security.Cryptography;
using System.Text;
using SigmaCompta.Core.Modeles;

namespace SigmaCompta.Core.Stockage
{
    public static class Chemins
    {
        private static string _racine;

        /// <summary>%AppData%\SigmaCompta (modifiable pour les tests).</summary>
        public static string Racine
        {
            get
            {
                if (_racine == null)
                    _racine = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "SigmaCompta");
                Directory.CreateDirectory(_racine);
                return _racine;
            }
            set { _racine = value; }
        }

        public static string Profils { get { var d = Path.Combine(Racine, "Profils"); Directory.CreateDirectory(d); return d; } }
        public static string Rapports { get { var d = Path.Combine(Racine, "Rapports"); Directory.CreateDirectory(d); return d; } }
        public static string Parametres { get { return Path.Combine(Racine, "parametres.json"); } }
        public static string Historique { get { return Path.Combine(Racine, "historique.json"); } }
    }

    /// <summary>Sérialisation JSON (DataContractJsonSerializer, intégré à .NET 4.8) avec indentation.</summary>
    public static class Json
    {
        private static DataContractJsonSerializer Serialiseur<T>()
        {
            return new DataContractJsonSerializer(typeof(T), new DataContractJsonSerializerSettings
            {
                DateTimeFormat = new DateTimeFormat("yyyy-MM-dd'T'HH:mm:ss"),
                UseSimpleDictionaryFormat = true
            });
        }

        public static string Serialiser<T>(T objet)
        {
            using (var ms = new MemoryStream())
            {
                using (var w = JsonReaderWriterFactory.CreateJsonWriter(ms, Encoding.UTF8, false, true, "  "))
                {
                    Serialiseur<T>().WriteObject(w, objet);
                    w.Flush();
                }
                return Encoding.UTF8.GetString(ms.ToArray()).Replace("\\/", "/");
            }
        }

        public static T Deserialiser<T>(string json)
        {
            using (var ms = new MemoryStream(Encoding.UTF8.GetBytes(json)))
                return (T)Serialiseur<T>().ReadObject(ms);
        }

        public static void Ecrire<T>(string chemin, T objet)
        {
            var dossier = Path.GetDirectoryName(chemin);
            if (!string.IsNullOrEmpty(dossier)) Directory.CreateDirectory(dossier);
            var temp = chemin + ".tmp";
            File.WriteAllText(temp, Serialiser(objet), new UTF8Encoding(false));
            if (File.Exists(chemin)) File.Replace(temp, chemin, null);
            else File.Move(temp, chemin);
        }

        public static T Lire<T>(string chemin) where T : class
        {
            if (!File.Exists(chemin)) return null;
            var texte = File.ReadAllText(chemin, Encoding.UTF8);
            if (texte.Length > 0 && texte[0] == '﻿') texte = texte.Substring(1);
            return Deserialiser<T>(texte);
        }
    }

    /// <summary>Fichiers de paramètres d'import (.json) dans %AppData%\SigmaCompta\Profils ou ailleurs.</summary>
    public static class MagasinProfils
    {
        public const string Extension = ".json";
        public const string FiltreDialogue = "Fichier de paramètres d'import (*.json)|*.json|Tous les fichiers|*.*";

        public static ProfilImport Charger(string chemin)
        {
            ProfilImport p;
            try
            {
                p = Json.Lire<ProfilImport>(chemin);
            }
            catch (SerializationException ex)
            {
                throw new InvalidDataException("Fichier de paramètres illisible (" + Path.GetFileName(chemin) + ") : " + ex.Message, ex);
            }
            if (p == null) throw new FileNotFoundException("Fichier de paramètres introuvable.", chemin);
            return p.Normaliser();
        }

        public static void Enregistrer(string chemin, ProfilImport profil)
        {
            Json.Ecrire(chemin, profil.Normaliser());
        }

        public static string CheminPourNom(string nom)
        {
            var sur = new string((nom ?? "profil").Select(ch => Path.GetInvalidFileNameChars().Contains(ch) ? '_' : ch).ToArray()).Trim();
            return Path.Combine(Chemins.Profils, (sur.Length == 0 ? "profil" : sur) + Extension);
        }

        /// <summary>Liste les profils du dossier ; crée « Brouillard standard » au premier lancement.</summary>
        public static List<KeyValuePair<string, ProfilImport>> Lister()
        {
            var res = new List<KeyValuePair<string, ProfilImport>>();
            var fichiers = Directory.GetFiles(Chemins.Profils, "*" + Extension);
            if (fichiers.Length == 0)
            {
                var defaut = ProfilImport.BrouillardStandard();
                var chemin = CheminPourNom(defaut.Nom);
                Enregistrer(chemin, defaut);
                fichiers = new[] { chemin };
            }
            foreach (var f in fichiers.OrderBy(x => x, StringComparer.OrdinalIgnoreCase))
            {
                try { res.Add(new KeyValuePair<string, ProfilImport>(f, Charger(f))); }
                catch (Exception) { /* fichier corrompu : ignoré dans la liste */ }
            }
            return res;
        }
    }

    public static class MagasinParametres
    {
        private static readonly byte[] Entropie = Encoding.UTF8.GetBytes("SigmaCompta.Import.v1");

        public static ParametresApplication Charger()
        {
            try
            {
                var p = Json.Lire<ParametresApplication>(Chemins.Parametres) ?? new ParametresApplication();
                if (p.ServeursRecents == null) p.ServeursRecents = new List<string>();
                return p;
            }
            catch (Exception)
            {
                return new ParametresApplication();
            }
        }

        public static void Enregistrer(ParametresApplication p)
        {
            try { Json.Ecrire(Chemins.Parametres, p); }
            catch (Exception) { /* préférences non critiques */ }
        }

        /// <summary>Chiffre avec DPAPI (lisible uniquement par le même compte Windows sur ce poste).</summary>
        public static string Proteger(string motDePasse)
        {
            if (string.IsNullOrEmpty(motDePasse)) return "";
            var octets = ProtectedData.Protect(Encoding.UTF8.GetBytes(motDePasse), Entropie, DataProtectionScope.CurrentUser);
            return Convert.ToBase64String(octets);
        }

        public static string Deproteger(string protege)
        {
            if (string.IsNullOrEmpty(protege)) return "";
            try
            {
                var octets = ProtectedData.Unprotect(Convert.FromBase64String(protege), Entropie, DataProtectionScope.CurrentUser);
                return Encoding.UTF8.GetString(octets);
            }
            catch (Exception)
            {
                return "";
            }
        }
    }

    public static class MagasinHistorique
    {
        public const int Maximum = 300;

        public static List<ExecutionImport> Charger()
        {
            try
            {
                var l = Json.Lire<List<ExecutionImport>>(Chemins.Historique) ?? new List<ExecutionImport>();
                foreach (var e in l) if (e.Pieces == null) e.Pieces = new List<ResultatPieceHistorique>();
                return l.OrderByDescending(e => e.Date).ToList();
            }
            catch (Exception)
            {
                return new List<ExecutionImport>();
            }
        }

        public static void Ajouter(ExecutionImport execution)
        {
            var l = Charger();
            l.Insert(0, execution);
            if (l.Count > Maximum) l = l.Take(Maximum).ToList();
            Json.Ecrire(Chemins.Historique, l);
        }

        public static void Vider()
        {
            if (File.Exists(Chemins.Historique)) File.Delete(Chemins.Historique);
        }
    }
}
