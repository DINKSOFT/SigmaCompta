using System;
using System.Collections.Generic;
using System.Data;
using Microsoft.Data.SqlClient;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using SigmaCompta.Core.Modeles;
using SigmaCompta.Core.Sql;

namespace SigmaCompta.Core.Import
{
    public sealed class ProgressionImport
    {
        public int Index { get; set; }
        public int Total { get; set; }
        public PieceImport Piece { get; set; }
        public string Message { get; set; }
        public double Pourcentage { get { return Total == 0 ? 0 : 100.0 * Index / Total; } }
    }

    /// <summary>
    /// Appelle imp.PS_ImportPiece pièce par pièce. Réutilisable tel quel par un futur
    /// module de saisie directe : une pièce saisie à l'écran est un <see cref="PieceImport"/>.
    /// </summary>
    public sealed class ServiceImport
    {
        private readonly FabriqueConnexion _fabrique;
        private readonly string _base;

        public ServiceImport(FabriqueConnexion fabrique, string baseDeDonnees)
        {
            _fabrique = fabrique;
            _base = baseDeDonnees;
        }

        /// <summary>Envoie chaque pièce sans erreur locale avec @Simulation = 1 : déclencheurs de la base exécutés, rien n'est conservé.</summary>
        public async Task SimulerAsync(IList<PieceImport> pieces, OptionsProfil options, IProgress<ProgressionImport> progression, CancellationToken ct)
        {
            var aTraiter = pieces.Where(p => !p.AErreur).ToList();
            // Rien n'est conservé en simulation : on lève le blocage du chaînage pour pouvoir
            // tester la pièce et observer si la base renseigne cbHash à l'insertion.
            var optionsSimulation = new OptionsProfil
            {
                AutoriserDoublon = options.AutoriserDoublon,
                RenumeroterDoublon = options.RenumeroterDoublon,
                IgnorerHash = true,
                ModeTransaction = options.ModeTransaction
            };
            using (var c = await _fabrique.OuvrirAsync(_base, ct).ConfigureAwait(false))
            {
                for (int i = 0; i < aTraiter.Count; i++)
                {
                    ct.ThrowIfCancellationRequested();
                    var p = aTraiter[i];
                    Rapporter(progression, i, aTraiter.Count, p, "Simulation");
                    try
                    {
                        await AppelerAsync(c, null, p, optionsSimulation, true, ct).ConfigureAwait(false);
                        p.Statut = StatutPiece.Simulee;
                        p.MessageServeur = p.Renumerotee
                            ? "Acceptée par le serveur — n° « " + p.PieceFournie.Trim() + " » déjà utilisé : n° provisoire " + p.NumeroAttribue
                            : p.NumerotationAuto
                                ? "Acceptée par le serveur (n° provisoire " + p.NumeroAttribue + ")"
                                : "Acceptée par le serveur";
                    }
                    catch (SqlException ex)
                    {
                        p.Statut = StatutPiece.Echec;
                        p.MessageServeur = ErreursComptables.Expliquer(ex);
                    }
                }
                Rapporter(progression, aTraiter.Count, aTraiter.Count, null, "Simulation terminée");
            }
        }

        public async Task ImporterAsync(IList<PieceImport> pieces, OptionsProfil options, IProgress<ProgressionImport> progression, CancellationToken ct)
        {
            var aTraiter = pieces.Where(p => !p.AErreur).ToList();
            foreach (var p in pieces.Where(x => x.AErreur))
            {
                p.Statut = StatutPiece.Erreur;
                p.MessageServeur = "Non importée : erreurs à corriger dans le fichier.";
            }

            bool toutOuRien = options.ModeTransaction == ModesTransaction.ToutOuRien;
            if (toutOuRien && aTraiter.Count != pieces.Count)
                throw new InvalidOperationException("Mode « tout ou rien » : corrige d'abord les pièces en erreur.");

            using (var c = await _fabrique.OuvrirAsync(_base, ct).ConfigureAwait(false))
            {
                SqlTransaction tx = toutOuRien ? c.BeginTransaction(IsolationLevel.ReadCommitted) : null;
                bool echec = false;
                try
                {
                    for (int i = 0; i < aTraiter.Count; i++)
                    {
                        if (ct.IsCancellationRequested)
                        {
                            foreach (var reste in aTraiter.Skip(i)) { reste.Statut = StatutPiece.Annulee; reste.MessageServeur = "Import interrompu par l'utilisateur."; }
                            if (toutOuRien) echec = true;
                            break;
                        }
                        var p = aTraiter[i];
                        Rapporter(progression, i, aTraiter.Count, p, "Import");
                        try
                        {
                            await AppelerAsync(c, tx, p, options, false, ct).ConfigureAwait(false);
                            p.Statut = StatutPiece.Importee;
                            p.MessageServeur = "Enregistrée — pièce " + p.NumeroAttribue
                                + (p.Renumerotee ? " (n° « " + p.PieceFournie.Trim() + " » déjà utilisé)" : "")
                                + ", écritures " + p.PlageEcNo;
                        }
                        catch (SqlException ex)
                        {
                            p.Statut = StatutPiece.Echec;
                            p.MessageServeur = ErreursComptables.Expliquer(ex);
                            if (toutOuRien || options.ArreterPremiereErreur)
                            {
                                echec = toutOuRien;
                                foreach (var reste in aTraiter.Skip(i + 1)) { reste.Statut = StatutPiece.Annulee; reste.MessageServeur = "Non traitée (arrêt sur la pièce " + p.Numero + ")."; }
                                break;
                            }
                        }
                    }

                    if (tx != null)
                    {
                        if (echec)
                        {
                            try { tx.Rollback(); } catch (Exception) { /* déjà annulée par XACT_ABORT */ }
                            foreach (var p in aTraiter.Where(x => x.Statut == StatutPiece.Importee))
                            {
                                p.Statut = StatutPiece.Annulee;
                                p.ReinitialiserResultatServeur();
                                p.MessageServeur = "Annulée : mode tout ou rien, une autre pièce a échoué.";
                            }
                        }
                        else tx.Commit();
                    }
                }
                finally
                {
                    if (tx != null) tx.Dispose();
                }
                Rapporter(progression, aTraiter.Count, aTraiter.Count, null, "Import terminé");
            }
        }

        private static void Rapporter(IProgress<ProgressionImport> p, int index, int total, PieceImport piece, string action)
        {
            if (p == null) return;
            p.Report(new ProgressionImport
            {
                Index = index,
                Total = total,
                Piece = piece,
                Message = piece == null ? action : action + " — pièce " + piece.Numero + "/" + total + " (" + piece.Journal + " " + (piece.Date.HasValue ? piece.Date.Value.ToString("dd/MM/yyyy") : "") + ")"
            });
        }

        /// <summary>Construit la variable table imp.TT_LigneEcriture (ordre des colonnes = ordre du type).</summary>
        public static DataTable ConstruireLignes(PieceImport piece)
        {
            var dt = new DataTable();
            dt.Columns.Add("NumLigne", typeof(int));
            dt.Columns.Add("CG_Num", typeof(string));
            dt.Columns.Add("CT_Num", typeof(string));
            dt.Columns.Add("EC_Intitule", typeof(string));
            dt.Columns.Add("Debit", typeof(decimal));
            dt.Columns.Add("Credit", typeof(decimal));
            dt.Columns.Add("EC_Echeance", typeof(DateTime));
            dt.Columns.Add("EC_RefPiece", typeof(string));
            foreach (var l in piece.Lignes)
            {
                dt.Rows.Add(
                    l.NumLigne,
                    l.CompteGeneral,
                    string.IsNullOrWhiteSpace(l.CompteTiers) ? (object)DBNull.Value : l.CompteTiers,
                    string.IsNullOrEmpty(l.Libelle) ? (object)DBNull.Value : l.Libelle,
                    l.Debit,
                    l.Credit,
                    l.Echeance.HasValue ? (object)l.Echeance.Value.Date : DBNull.Value,
                    string.IsNullOrWhiteSpace(l.RefPiece) ? (object)DBNull.Value : l.RefPiece);
            }
            return dt;
        }

        /// <summary>Un appel de imp.PS_ImportPiece pour une pièce. Lève SqlException en cas de refus.</summary>
        public static async Task AppelerAsync(SqlConnection c, SqlTransaction tx, PieceImport piece, OptionsProfil options, bool simulation, CancellationToken ct)
        {
            piece.ReinitialiserResultatServeur();
            using (var cmd = new SqlCommand("imp.PS_ImportPiece", c, tx) { CommandType = CommandType.StoredProcedure, CommandTimeout = 120 })
            {
                cmd.Parameters.Add(new SqlParameter("@JO_Num", SqlDbType.VarChar, 7) { Value = piece.Journal });
                cmd.Parameters.Add(new SqlParameter("@EC_Date", SqlDbType.Date) { Value = piece.Date.Value.Date });
                cmd.Parameters.Add(new SqlParameter("@Lignes", SqlDbType.Structured) { TypeName = "imp.TT_LigneEcriture", Value = ConstruireLignes(piece) });
                cmd.Parameters.Add(new SqlParameter("@EC_Piece", SqlDbType.VarChar, 13) { Value = piece.NumerotationAuto ? (object)DBNull.Value : piece.PieceFournie });
                cmd.Parameters.Add(new SqlParameter("@Simulation", SqlDbType.Bit) { Value = simulation });
                cmd.Parameters.Add(new SqlParameter("@AutoriserDoublon", SqlDbType.Bit) { Value = options.AutoriserDoublon });
                cmd.Parameters.Add(new SqlParameter("@RenumeroterDoublon", SqlDbType.Bit) { Value = options.RenumeroterDoublon });
                cmd.Parameters.Add(new SqlParameter("@IgnorerHash", SqlDbType.Bit) { Value = options.IgnorerHash });
                var pPiece = cmd.Parameters.Add(new SqlParameter("@EC_PieceOut", SqlDbType.VarChar, 13) { Direction = ParameterDirection.Output });
                var pDebut = cmd.Parameters.Add(new SqlParameter("@EC_NoDebut", SqlDbType.Int) { Direction = ParameterDirection.Output });
                var pFin = cmd.Parameters.Add(new SqlParameter("@EC_NoFin", SqlDbType.Int) { Direction = ParameterDirection.Output });

                var ecNoParLigne = new Dictionary<int, int>();
                int? lignesHashees = null;
                using (var r = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false))
                {
                    do
                    {
                        int iLigne = -1, iEcNo = -1, iHash = -1;
                        for (int k = 0; k < r.FieldCount; k++)
                        {
                            if (r.GetName(k) == "NumLigne") iLigne = k;
                            if (r.GetName(k) == "EC_No") iEcNo = k;
                            if (r.GetName(k) == "NbHash") iHash = k;
                        }
                        while (await r.ReadAsync(ct).ConfigureAwait(false))
                        {
                            if (iLigne >= 0 && iEcNo >= 0 && !r.IsDBNull(iLigne) && !r.IsDBNull(iEcNo))
                                ecNoParLigne[Convert.ToInt32(r.GetValue(iLigne))] = Convert.ToInt32(r.GetValue(iEcNo));
                            if (iHash >= 0 && !r.IsDBNull(iHash))
                                lignesHashees = Convert.ToInt32(r.GetValue(iHash));
                        }
                    } while (await r.NextResultAsync(ct).ConfigureAwait(false));
                }

                piece.LignesHashees = lignesHashees;
                piece.NumeroAttribue = pPiece.Value == DBNull.Value ? null : Convert.ToString(pPiece.Value);
                piece.EcNoDebut = pDebut.Value == DBNull.Value ? (int?)null : Convert.ToInt32(pDebut.Value);
                piece.EcNoFin = pFin.Value == DBNull.Value ? (int?)null : Convert.ToInt32(pFin.Value);
                foreach (var l in piece.Lignes)
                {
                    int no;
                    l.EcNo = ecNoParLigne.TryGetValue(l.NumLigne, out no) ? no : (int?)null;
                }
            }
        }

        /// <summary>Résumé d'une exécution pour l'historique.</summary>
        public static ExecutionImport Bilan(IList<PieceImport> pieces, bool simulation, string mode)
        {
            var reussite = simulation ? StatutPiece.Simulee : StatutPiece.Importee;
            var e = new ExecutionImport
            {
                Date = DateTime.Now,
                Simulation = simulation,
                Mode = mode,
                NombrePieces = pieces.Count,
                NombreReussies = pieces.Count(p => p.Statut == reussite),
                NombreEchecs = pieces.Count(p => p.Statut != reussite),
                TotalDebit = pieces.Where(p => p.Statut == reussite).Sum(p => p.TotalDebit),
                Utilisateur = Environment.UserName
            };
            e.Bilan = e.NombreReussies + " pièce(s) " + (simulation ? "acceptée(s)" : "importée(s)") + " sur " + e.NombrePieces;
            e.Pieces = pieces.Select(p => new ResultatPieceHistorique
            {
                Journal = p.Journal,
                Date = p.Date,
                Piece = p.PieceAffichee,
                Statut = LibelleStatut(p.Statut),
                EcNo = p.PlageEcNo,
                Montant = p.TotalDebit,
                Message = p.Resume
            }).ToList();
            return e;
        }

        public static string LibelleStatut(StatutPiece s)
        {
            switch (s)
            {
                case StatutPiece.AControler: return "À contrôler";
                case StatutPiece.Valide: return "Valide";
                case StatutPiece.Avertissement: return "Avertissement";
                case StatutPiece.Erreur: return "Erreur";
                case StatutPiece.Simulee: return "Simulée OK";
                case StatutPiece.Importee: return "Importée";
                case StatutPiece.Echec: return "Refusée";
                case StatutPiece.Annulee: return "Annulée";
                default: return s.ToString();
            }
        }
    }
}
