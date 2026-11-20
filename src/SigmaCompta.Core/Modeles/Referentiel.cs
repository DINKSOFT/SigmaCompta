using System;
using System.Collections.Generic;

namespace SigmaCompta.Core.Modeles
{
    public sealed class Journal
    {
        public string Code { get; set; }
        public string Intitule { get; set; }
        public int Type { get; set; }
        public int ModeNumerotation { get; set; }
        public bool EnSommeil { get; set; }
        /// <summary>Compte de trésorerie du journal (F_JOURNAUX.CG_Num), vide si non renseigné.</summary>
        public string CompteTresorerie { get; set; }

        public string TypeLibelle
        {
            get
            {
                switch (Type)
                {
                    case 0: return "Achats";
                    case 1: return "Ventes";
                    case 2: return "Trésorerie";
                    case 3: return "Général";
                    case 4: return "Situation";
                    default: return "Type " + Type;
                }
            }
        }

        public string NumerotationLibelle
        {
            get
            {
                switch (ModeNumerotation)
                {
                    case 0: return "Manuelle";
                    case 1: return "Continue (journal)";
                    case 2: return "Continue (base)";
                    case 3: return "Mensuelle";
                    default: return "Mode " + ModeNumerotation;
                }
            }
        }

        public string Affichage { get { return Code + " — " + Intitule; } }
        public override string ToString() { return Affichage; }
    }

    public sealed class CompteGeneral
    {
        public string Numero { get; set; }
        public string Intitule { get; set; }
        public int Type { get; set; }
        public bool EstImputable { get { return Type == 0; } }
    }

    public sealed class Tiers
    {
        public string Numero { get; set; }
        public string Intitule { get; set; }
        public bool EstProspect { get; set; }
        /// <summary>F_COMPTET.CT_Type (voir <see cref="TypesTiers"/>), -1 si non renseigné.</summary>
        public int Type { get; set; } = -1;
        /// <summary>Compte collectif principal du tiers (F_COMPTET.CG_NumPrinc), vide si non renseigné.</summary>
        public string Collectif { get; set; } = "";
    }

    public sealed class Exercice
    {
        public DateTime Debut { get; set; }
        public DateTime Fin { get; set; }
        public bool Contient(DateTime d) { return d.Date >= Debut.Date && d.Date <= Fin.Date; }
        public override string ToString() { return Debut.ToString("dd/MM/yyyy") + " – " + Fin.ToString("dd/MM/yyyy"); }
    }

    /// <summary>Données de référence comptables chargées une fois pour la validation locale.</summary>
    public sealed class Referentiel
    {
        public Dictionary<string, Journal> Journaux { get; } = new Dictionary<string, Journal>(StringComparer.OrdinalIgnoreCase);
        public Dictionary<string, CompteGeneral> Comptes { get; } = new Dictionary<string, CompteGeneral>(StringComparer.OrdinalIgnoreCase);
        public Dictionary<string, Tiers> Tiers { get; } = new Dictionary<string, Tiers>(StringComparer.OrdinalIgnoreCase);
        public List<Exercice> Exercices { get; } = new List<Exercice>();
        /// <summary>Clés « JOURNAL|yyyyMM » des périodes clôturées totalement.</summary>
        public HashSet<string> PeriodesCloturees { get; } = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        public DateTime ChargeLe { get; set; } = DateTime.Now;

        public static string ClePeriode(string journal, DateTime date)
        {
            return (journal ?? "") + "|" + date.ToString("yyyyMM");
        }
    }
}
