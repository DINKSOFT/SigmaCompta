/* =============================================================================
   02 — imp.PS_ImportPiece : import d'une pièce comptable dans F_ECRITUREC
   Base comptable — insertion SQL directe (voie 2)

   Ce que fait la procédure, dans une seule transaction :
     1. Contrôles préalables (journal, comptes, tiers, sens, équilibre, exercice,
        chaînage cbHash*) → toutes les erreurs sont remontées en une fois.
     2. Verrou applicatif (sp_getapplock) : deux imports ne numérotent jamais
        en même temps.
     3. Garantie F_JMOUV : crée la ligne journal + mois si absente, refuse si
        la période est clôturée totalement (JM_Cloture = 2).
     4. Numérotation automatique de EC_Piece selon F_JOURNAUX.JO_NumPiece
        (si @EC_Piece n'est pas fourni) + contrôle de doublon : un n° fourni
        déjà utilisé est remplacé par le n° suivant de sa séquence
        (@RenumeroterDoublon = 1, par défaut) ou refusé (erreur 50005).
     5. INSERT ligne par ligne (ordre NumLigne) : EC_No = 0 → posé par
        TG_CBINS_F_ECRITUREC, aligné sur cbMarq comme une saisie manuelle.
        Clés cb* : selon la version de la base, ce sont des colonnes calculées
        par SQL Server (versions récentes) ou des colonnes à remplir
        (dbo.CB_FntStr). Les lignes marquées « --#CB:<table>.<colonne> » ne sont
        gardées que si la colonne existe et n'est pas calculée : l'application
        les retire automatiquement à l'installation. En exécution manuelle,
        supprimer ces lignes pour chaque colonne calculée (script 01, §2).
        Les lignes « --#SI:<table>.<colonne> » ne sont gardées que si la colonne existe.
     6. Contrôle post-insertion (EC_No attribués, débits = crédits en base).
     7. COMMIT — ou ROLLBACK si @Simulation = 1 (les déclencheurs de la base ont
        quand même tout validé).

   Hors périmètre (voir notes en fin de fichier) : ventilation analytique
   (F_ECRITUREA), chaînage cbHash*, lignes de contrepartie automatiques des
   journaux de trésorerie.

   Prérequis : exécuter 01_preverification_schema.sql et vérifier ses résultats.
   ============================================================================= */
SET ANSI_NULLS ON;
SET QUOTED_IDENTIFIER ON;
GO

IF SCHEMA_ID(N'imp') IS NULL
    EXEC (N'CREATE SCHEMA imp AUTHORIZATION dbo;');
GO

IF OBJECT_ID(N'imp.PS_ImportPiece', N'P') IS NOT NULL
    DROP PROCEDURE imp.PS_ImportPiece;
GO

IF TYPE_ID(N'imp.TT_LigneEcriture') IS NOT NULL
    DROP TYPE imp.TT_LigneEcriture;
GO

/* Lignes de la pièce — une ligne = un montant au débit OU au crédit */
CREATE TYPE imp.TT_LigneEcriture AS TABLE
(
    NumLigne     int            NOT NULL PRIMARY KEY,   -- ordre d'insertion
    CG_Num       varchar(13)    NOT NULL,               -- compte général
    CT_Num       varchar(17)    NULL,                   -- tiers (comptes collectifs)
    EC_Intitule  varchar(69)    NULL,                   -- NULL → @EC_Intitule
    Debit        numeric(24,6)  NOT NULL DEFAULT (0),
    Credit       numeric(24,6)  NOT NULL DEFAULT (0),
    EC_Echeance  date           NULL,                   -- NULL → 1753-01-01
    EC_RefPiece  varchar(17)    NULL                    -- NULL → @EC_RefPiece
);
GO

CREATE PROCEDURE imp.PS_ImportPiece
    @JO_Num            varchar(7),
    @EC_Date           date,
    @Lignes            imp.TT_LigneEcriture READONLY,
    @EC_Piece          varchar(13)  = NULL,   -- NULL ou '' → numérotation automatique
    @EC_RefPiece       varchar(17)  = NULL,   -- référence par défaut des lignes
    @EC_Intitule       varchar(69)  = NULL,   -- libellé par défaut des lignes
    @Simulation        bit          = 0,      -- 1 : tout s'exécute puis ROLLBACK
    @AutoriserDoublon  bit          = 0,      -- 1 : accepte un n° de pièce déjà utilisé
    @RenumeroterDoublon bit         = 1,      -- 1 : n° déjà utilisé → n° suivant de la séquence ; 0 : erreur 50005
    @IgnorerHash       bit          = 0,      -- 1 : insère même si le chaînage cbHash* est actif
    @EC_PieceOut       varchar(13)  = NULL OUTPUT,
    @EC_NoDebut        int          = NULL OUTPUT,
    @EC_NoFin          int          = NULL OUTPUT
AS
BEGIN
    /* SigmaCompta-Version: 1.3 */
    SET NOCOUNT ON;
    SET XACT_ABORT ON;

    DECLARE @Msg         nvarchar(2048),
            @JO          varchar(7),
            @cbJO        varbinary(255),
            @JO_NumPiece smallint,
            @JO_Sommeil  smallint,
            @JM_Date     datetime,
            @Piece       varchar(13),
            @NbLignes    int,
            @TotDebit    numeric(28,6),
            @TotCredit   numeric(28,6);

    DECLARE @Err TABLE (Id int IDENTITY(1,1) PRIMARY KEY, Msg nvarchar(400) NOT NULL);

    /* Lignes normalisées. Les codes sont repris tels qu'ils sont stockés dans
       F_COMPTEG / F_COMPTET / F_JOURNAUX : la base est en général insensible à
       la casse, mais les clés cb* sont binaires — 'od' ≠ 'OD' pour la navigation. */
    DECLARE @L TABLE
    (
        NumLigne     int            NOT NULL PRIMARY KEY,
        CG_Saisi     varchar(13)    NULL,
        CG_Num       varchar(13)    NULL,
        CG_Type      smallint       NULL,
        CT_Saisi     varchar(17)    NULL,
        CT_Num       varchar(17)    NULL,
        CT_Prospect  smallint       NULL,
        EC_Intitule  varchar(69)    NOT NULL,
        Debit        numeric(24,6)  NOT NULL,
        Credit       numeric(24,6)  NOT NULL,
        EC_Sens      smallint       NOT NULL,      -- 0 = débit, 1 = crédit
        EC_Montant   numeric(24,6)  NOT NULL,
        EC_Echeance  datetime       NOT NULL,
        EC_RefPiece  varchar(17)    NOT NULL
    );

    /* ---------------------------------------------------------------------
       1. CONTRÔLES PRÉALABLES (lecture seule, hors transaction)
       --------------------------------------------------------------------- */
    IF @EC_Date IS NULL
        INSERT @Err (Msg) VALUES (N'Date de pièce (@EC_Date) obligatoire.');

    SELECT  @JO          = j.JO_Num,
            @JO_NumPiece = ISNULL(j.JO_NumPiece, 1),
            @JO_Sommeil  = j.JO_Sommeil
    FROM    dbo.F_JOURNAUX j
    WHERE   j.JO_Num = LTRIM(RTRIM(@JO_Num));

    IF @JO IS NULL
        INSERT @Err (Msg) VALUES (N'Journal « ' + ISNULL(@JO_Num, N'(NULL)') + N' » inconnu dans F_JOURNAUX.');
    ELSE IF @JO_Sommeil = 1
        INSERT @Err (Msg) VALUES (N'Journal « ' + @JO + N' » mis en sommeil.');

    /* Clé binaire du journal telle que la base la stocke (indépendant de l'encodage) */
    IF @JO IS NOT NULL
        SELECT TOP (1) @cbJO = e.cbJO_Num FROM dbo.F_ECRITUREC e WHERE e.JO_Num = @JO;
    IF @cbJO IS NULL SET @cbJO = ISNULL(dbo.CB_FntStr(@JO), 0x);
    SET @JM_Date = CASE WHEN @EC_Date IS NOT NULL
                        THEN DATEFROMPARTS(YEAR(@EC_Date), MONTH(@EC_Date), 1) END;

    INSERT @L (NumLigne, CG_Saisi, CG_Num, CG_Type, CT_Saisi, CT_Num, CT_Prospect,
               EC_Intitule, Debit, Credit, EC_Sens, EC_Montant, EC_Echeance, EC_RefPiece)
    SELECT  l.NumLigne,
            s.CG, g.CG_Num, g.CG_Type,
            s.CT, t.CT_Num, t.CT_Prospect,
            ISNULL(NULLIF(LTRIM(RTRIM(l.EC_Intitule)), ''), ISNULL(@EC_Intitule, '')),
            l.Debit, l.Credit,
            CASE WHEN l.Debit <> 0 THEN 0 ELSE 1 END,
            CASE WHEN l.Debit <> 0 THEN l.Debit ELSE l.Credit END,
            ISNULL(CAST(l.EC_Echeance AS datetime), '17530101'),
            ISNULL(NULLIF(LTRIM(RTRIM(l.EC_RefPiece)), ''), ISNULL(@EC_RefPiece, ''))
    FROM    @Lignes l
    CROSS APPLY (SELECT LTRIM(RTRIM(l.CG_Num))              AS CG,
                        NULLIF(LTRIM(RTRIM(l.CT_Num)), '')  AS CT) s
    LEFT JOIN dbo.F_COMPTEG g ON g.CG_Num = s.CG
    LEFT JOIN dbo.F_COMPTET t ON t.CT_Num = s.CT;

    SELECT  @NbLignes  = COUNT(*),
            @TotDebit  = ISNULL(SUM(Debit), 0),
            @TotCredit = ISNULL(SUM(Credit), 0)
    FROM    @L;

    IF @NbLignes < 2
        INSERT @Err (Msg) VALUES (N'Une pièce doit comporter au moins 2 lignes (' + CAST(@NbLignes AS nvarchar(10)) + N' reçue[s]).');

    INSERT @Err (Msg)
    SELECT  N'Ligne ' + CAST(NumLigne AS nvarchar(10)) + N' : ' + x.Msg
    FROM    @L
    CROSS APPLY (VALUES
        (CASE WHEN CG_Num IS NULL
              THEN N'compte général « ' + ISNULL(CG_Saisi, N'') + N' » inconnu.' END),
        (CASE WHEN CG_Num IS NOT NULL AND CG_Type <> 0
              THEN N'compte « ' + CG_Num + N' » non imputable (compte de total).' END),
        (CASE WHEN CT_Saisi IS NOT NULL AND CT_Num IS NULL
              THEN N'tiers « ' + CT_Saisi + N' » inconnu.' END),
        (CASE WHEN CT_Num IS NOT NULL AND CT_Prospect <> 0
              THEN N'tiers « ' + CT_Num + N' » est un prospect.' END),
        (CASE WHEN Debit < 0 OR Credit < 0
              THEN N'montant négatif interdit.' END),
        (CASE WHEN (Debit = 0 AND Credit = 0) OR (Debit <> 0 AND Credit <> 0)
              THEN N'renseigner soit le débit, soit le crédit.' END)
    ) x(Msg)
    WHERE   x.Msg IS NOT NULL;

    IF @TotDebit <> @TotCredit
        INSERT @Err (Msg) VALUES (N'Pièce déséquilibrée : débit = ' + CONVERT(nvarchar(40), @TotDebit)
                                 + N', crédit = ' + CONVERT(nvarchar(40), @TotCredit)
                                 + N', écart = ' + CONVERT(nvarchar(40), @TotDebit - @TotCredit) + N'.');

    IF @EC_Date IS NOT NULL AND NOT EXISTS (
        SELECT 1
        FROM   dbo.P_DOSSIER p
        CROSS APPLY (VALUES (p.D_DebutExo01, p.D_FinExo01), (p.D_DebutExo02, p.D_FinExo02),
                            (p.D_DebutExo03, p.D_FinExo03), (p.D_DebutExo04, p.D_FinExo04),
                            (p.D_DebutExo05, p.D_FinExo05)) v(Debut, Fin)
        WHERE  @EC_Date BETWEEN v.Debut AND v.Fin)
        INSERT @Err (Msg) VALUES (N'La date ' + CONVERT(nvarchar(10), @EC_Date, 103)
                                 + N' n''appartient à aucun exercice de P_DOSSIER.');

    IF @IgnorerHash = 0 AND EXISTS (
        SELECT 1
        FROM  (SELECT TOP (1) cbHashVersion FROM dbo.F_ECRITUREC ORDER BY cbMarq DESC) h
        WHERE  h.cbHashVersion IS NOT NULL)
        INSERT @Err (Msg) VALUES (N'Chaînage d''inaltérabilité (cbHash*) actif : une insertion SQL ne le calcule pas. '
                                 + N'Utiliser l''import natif, ou @IgnorerHash = 1 en connaissance de cause.');

    IF EXISTS (SELECT 1 FROM @Err)
    BEGIN
        SELECT @Msg = N'Import refusé — ' + CAST(COUNT(*) AS nvarchar(10)) + N' erreur(s) :' FROM @Err;
        SELECT @Msg = LEFT(@Msg + (SELECT NCHAR(10) + N' • ' + Msg
                                   FROM @Err ORDER BY Id
                                   FOR XML PATH(''), TYPE).value('.', 'nvarchar(max)'), 2000);
        THROW 50001, @Msg, 1;
    END;

    /* ---------------------------------------------------------------------
       2 à 7. TRANSACTION
       Si l'appelant a déjà une transaction ouverte, on travaille sous un point
       de sauvegarde et c'est l'appelant qui valide.
       --------------------------------------------------------------------- */
    DECLARE @TranExterne bit = CASE WHEN @@TRANCOUNT > 0 THEN 1 ELSE 0 END,
            @rc          int;

    DECLARE @Marq TABLE (NumLigne int NOT NULL PRIMARY KEY, cbMarq int NOT NULL);

    BEGIN TRY
        IF @TranExterne = 1 SAVE TRANSACTION spImportPiece;
        ELSE BEGIN TRANSACTION;

        /* 2. Sérialisation des imports (numérotation sans collision) */
        EXEC @rc = sys.sp_getapplock @Resource    = N'imp.PS_ImportPiece',
                                     @LockMode    = N'Exclusive',
                                     @LockOwner   = N'Transaction',
                                     @LockTimeout = 15000;
        IF @rc < 0
            THROW 50002, N'Verrou d''import indisponible : un autre import est en cours.', 1;

        /* 3. Garantie F_JMOUV (journal + mois) — prérequis de TG_INS_F_ECRITUREC (81149) */
        IF NOT EXISTS (SELECT 1 FROM dbo.F_JMOUV WHERE JO_Num = @JO AND JM_Date = @JM_Date)
        BEGIN
            INSERT INTO dbo.F_JMOUV
                   (JO_Num, JM_Date, JM_Cloture, JM_Impression
                    , cbJO_Num                                      --#CB:F_JMOUV.cbJO_Num
                   )
            VALUES (@JO, @JM_Date, 0, 0
                    , @cbJO                                         --#CB:F_JMOUV.cbJO_Num
                   );
        END
        ELSE IF EXISTS (SELECT 1 FROM dbo.F_JMOUV
                        WHERE JO_Num = @JO AND JM_Date = @JM_Date AND JM_Cloture = 2)
        BEGIN
            SET @Msg = N'Période ' + CONVERT(nvarchar(7), @JM_Date, 120) + N' du journal « '
                     + @JO + N' » clôturée (JM_Cloture = 2).';
            THROW 50003, @Msg, 1;
        END;

        /* 4. Numérotation de la pièce
              JO_NumPiece (hypothèse, cf. 01_preverification §6) :
                0 = manuelle          → périmètre journal (repli)
                1 = continue journal  → périmètre journal
                2 = continue base     → toute la table
                3 = mensuelle         → journal + mois (JM_Date)
              Modèle = dernière pièce saisie (cbMarq le plus élevé) dans le
              périmètre, sinon dans le journal ; on garde son préfixe et la
              largeur de son suffixe numérique, puis MAX(suffixe) + 1.
              N° fourni déjà utilisé (et @AutoriserDoublon = 0) :
                @RenumeroterDoublon = 1 → le modèle est le n° fourni lui-même
                (même préfixe, même largeur) : la pièce prend le n° qui suit
                le plus grand n° existant de cette séquence ; s'il ne finit pas
                par un chiffre, numérotation automatique du journal.
                @RenumeroterDoublon = 0 → erreur 50005. */
        DECLARE @Modele  varchar(13),
                @Prefixe varchar(13),
                @LgPref  int,          -- DATALENGTH : LEN ignore les espaces finaux
                @Largeur int,
                @Dernier bigint,
                @Suivant varchar(20);

        SET @Piece = NULLIF(LTRIM(RTRIM(@EC_Piece)), '');

        IF @Piece IS NOT NULL AND @AutoriserDoublon = 0 AND EXISTS (
            SELECT 1 FROM dbo.F_ECRITUREC e
            WHERE  e.EC_Piece = @Piece
              AND  (@JO_NumPiece = 2 OR e.cbJO_Num = @cbJO)
              AND  (@JO_NumPiece <> 3 OR e.JM_Date = @JM_Date))
        BEGIN
            IF ISNULL(@RenumeroterDoublon, 0) = 0
            BEGIN
                SET @Msg = N'La pièce « ' + @Piece + N' » existe déjà (journal « ' + @JO
                         + N' », JO_NumPiece = ' + CAST(@JO_NumPiece AS nvarchar(5))
                         + N'). Utiliser @RenumeroterDoublon = 1 pour attribuer le n° suivant, ou @AutoriserDoublon = 1 pour forcer.';
                THROW 50005, @Msg, 1;
            END;
            IF @Piece LIKE '%[0-9]' SET @Modele = @Piece;   -- suite de la séquence du n° fourni
            SET @Piece = NULL;                               -- → calcul du n° suivant ci-dessous
        END;

        IF @Piece IS NULL
        BEGIN
            IF @Modele IS NULL AND @JO_NumPiece = 3
                SELECT TOP (1) @Modele = RTRIM(e.EC_Piece)
                FROM   dbo.F_ECRITUREC e
                WHERE  e.cbJO_Num = @cbJO AND e.JM_Date = @JM_Date AND e.EC_Piece <> ''
                ORDER BY e.cbMarq DESC;

            IF @Modele IS NULL
                SELECT TOP (1) @Modele = RTRIM(e.EC_Piece)
                FROM   dbo.F_ECRITUREC e
                WHERE  (@JO_NumPiece = 2 OR e.cbJO_Num = @cbJO) AND e.EC_Piece <> ''
                ORDER BY e.cbMarq DESC;

            SET @Modele  = ISNULL(@Modele, '000000');                     -- 1re pièce → 000001
            SET @Largeur = PATINDEX('%[^0-9]%', REVERSE(@Modele)) - 1;    -- nb de chiffres finaux
            IF @Largeur < 0 SET @Largeur = LEN(@Modele);                  -- pièce 100 % numérique

            IF @Largeur = 0
            BEGIN
                SET @Msg = N'Numérotation automatique impossible : la dernière pièce « ' + @Modele
                         + N' » ne se termine pas par un nombre. Fournir @EC_Piece.';
                THROW 50004, @Msg, 1;
            END;

            SET @Prefixe = LEFT(@Modele, LEN(@Modele) - @Largeur);
            SET @LgPref  = DATALENGTH(@Prefixe);

            SELECT @Dernier = MAX(TRY_CAST(x.Suffixe AS bigint))
            FROM   dbo.F_ECRITUREC e
            CROSS APPLY (SELECT SUBSTRING(RTRIM(e.EC_Piece), @LgPref + 1, 13) AS Suffixe) x
            WHERE  (@JO_NumPiece = 2 OR e.cbJO_Num = @cbJO)
              AND  (@JO_NumPiece <> 3 OR e.JM_Date = @JM_Date)
              AND  LEFT(e.EC_Piece, @LgPref) = @Prefixe
              AND  x.Suffixe <> ''
              AND  x.Suffixe NOT LIKE '%[^0-9]%';

            SET @Suivant = CAST(ISNULL(@Dernier, 0) + 1 AS varchar(20));
            IF LEN(@Suivant) < @Largeur
                SET @Suivant = RIGHT(REPLICATE('0', @Largeur) + @Suivant, @Largeur);

            IF @LgPref + LEN(@Suivant) > 13
            BEGIN
                SET @Msg = N'Numérotation automatique : « ' + @Prefixe + @Suivant + N' » dépasse 13 caractères.';
                THROW 50004, @Msg, 1;
            END;

            SET @Piece = @Prefixe + @Suivant;
        END;

        /* Doublon dans le périmètre de numérotation (garde-fou : le n° calculé est libre) */
        IF @AutoriserDoublon = 0 AND EXISTS (
            SELECT 1 FROM dbo.F_ECRITUREC e
            WHERE  e.EC_Piece = @Piece
              AND  (@JO_NumPiece = 2 OR e.cbJO_Num = @cbJO)
              AND  (@JO_NumPiece <> 3 OR e.JM_Date = @JM_Date))
        BEGIN
            SET @Msg = N'La pièce « ' + @Piece + N' » existe déjà (journal « ' + @JO
                     + N' », JO_NumPiece = ' + CAST(@JO_NumPiece AS nvarchar(5))
                     + N'). Utiliser @AutoriserDoublon = 1 pour forcer.';
            THROW 50005, @Msg, 1;
        END;

        /* 5. Insertion ligne par ligne — même séquence qu'une saisie manuelle :
              EC_No = 0 → attribué par TG_CBINS_F_ECRITUREC (MAX + 1)
              TG_INS_F_ECRITUREC valide chaque ligne (CB_Error 81035, 81149, …) */
        DECLARE @k int = (SELECT MIN(NumLigne) FROM @L);

        WHILE @k IS NOT NULL
        BEGIN
            INSERT INTO dbo.F_ECRITUREC
                   (JO_Num, EC_No, JM_Date, EC_Jour, EC_Date, EC_Piece, EC_RefPiece,
                    CG_Num, CT_Num, EC_Intitule, EC_Sens, EC_Montant, EC_Echeance, EC_Norme
                    , cbJO_Num                                      --#CB:F_ECRITUREC.cbJO_Num
                    , cbEC_Piece                                    --#CB:F_ECRITUREC.cbEC_Piece
                    , cbEC_RefPiece                                 --#CB:F_ECRITUREC.cbEC_RefPiece
                    , cbCG_Num                                      --#CB:F_ECRITUREC.cbCG_Num
                    , cbCT_Num                                      --#CB:F_ECRITUREC.cbCT_Num
                   )
            OUTPUT @k, inserted.cbMarq INTO @Marq (NumLigne, cbMarq)
            SELECT  @JO, 0, @JM_Date, DAY(@EC_Date), CAST(@EC_Date AS datetime), @Piece, l.EC_RefPiece,
                    l.CG_Num, l.CT_Num, l.EC_Intitule, l.EC_Sens, l.EC_Montant, l.EC_Echeance, 0
                    , @cbJO                                         --#CB:F_ECRITUREC.cbJO_Num
                    , ISNULL(dbo.CB_FntStr(@Piece), 0x)             --#CB:F_ECRITUREC.cbEC_Piece
                    , ISNULL(dbo.CB_FntStr(l.EC_RefPiece), 0x)      --#CB:F_ECRITUREC.cbEC_RefPiece
                    , ISNULL(dbo.CB_FntStr(l.CG_Num), 0x)           --#CB:F_ECRITUREC.cbCG_Num
                    , ISNULL(dbo.CB_FntStr(l.CT_Num), 0x)           --#CB:F_ECRITUREC.cbCT_Num
            FROM    @L l
            WHERE   l.NumLigne = @k;

            SELECT @k = MIN(NumLigne) FROM @L WHERE NumLigne > @k;
        END;

        /* 6. Contrôle post-insertion : relu en base, après déclencheurs */
        DECLARE @NbIns int, @NbSansNo int, @DebIns numeric(28,6), @CreIns numeric(28,6);

        SELECT  @NbIns    = COUNT(*),
                @NbSansNo = SUM(CASE WHEN ISNULL(e.EC_No, 0) = 0 THEN 1 ELSE 0 END),
                @DebIns   = SUM(CASE WHEN e.EC_Sens = 0 THEN e.EC_Montant ELSE 0 END),
                @CreIns   = SUM(CASE WHEN e.EC_Sens = 1 THEN e.EC_Montant ELSE 0 END),
                @EC_NoDebut = MIN(e.EC_No),
                @EC_NoFin   = MAX(e.EC_No)
        FROM    dbo.F_ECRITUREC e
        JOIN    @Marq m ON m.cbMarq = e.cbMarq;

        IF @NbIns <> @NbLignes OR @NbSansNo > 0 OR @DebIns <> @CreIns
        BEGIN
            SET @Msg = N'Contrôle post-insertion en échec : ' + CAST(ISNULL(@NbIns, 0) AS nvarchar(10)) + N'/'
                     + CAST(@NbLignes AS nvarchar(10)) + N' ligne(s), ' + CAST(ISNULL(@NbSansNo, 0) AS nvarchar(10))
                     + N' sans EC_No, débit ' + ISNULL(CONVERT(nvarchar(40), @DebIns), N'-')
                     + N' / crédit ' + ISNULL(CONVERT(nvarchar(40), @CreIns), N'-') + N'.';
            THROW 50006, @Msg, 1;
        END;

        SET @EC_PieceOut = @Piece;

        /* Diagnostic du chaînage d'inaltérabilité : cbHash renseigné à l'insertion ?
           (lignes retirées automatiquement si la colonne cbHash n'existe pas) */
        DECLARE @NbHash int;
        SELECT @NbHash = COUNT(*) FROM dbo.F_ECRITUREC e JOIN @Marq m ON m.cbMarq = e.cbMarq   --#SI:F_ECRITUREC.cbHash
        WHERE  e.cbHash IS NOT NULL;                                                           --#SI:F_ECRITUREC.cbHash

        /* Compte rendu (renvoyé aussi en simulation, avant annulation) */
        SELECT  CASE WHEN @Simulation = 1 THEN 'SIMULATION' ELSE 'ENREGISTRE' END AS Statut,
                m.NumLigne, e.cbMarq, e.EC_No, e.JO_Num, e.JM_Date, e.EC_Date, e.EC_Piece,
                e.CG_Num, e.CT_Num, e.EC_Intitule,
                CASE WHEN e.EC_Sens = 0 THEN e.EC_Montant ELSE 0 END AS Debit,
                CASE WHEN e.EC_Sens = 1 THEN e.EC_Montant ELSE 0 END AS Credit,
                @NbHash AS NbHash
        FROM    dbo.F_ECRITUREC e
        JOIN    @Marq m ON m.cbMarq = e.cbMarq
        ORDER BY m.NumLigne;

        /* 7. Validation ou annulation */
        IF @Simulation = 1
        BEGIN
            IF @TranExterne = 1 ROLLBACK TRANSACTION spImportPiece;
            ELSE ROLLBACK TRANSACTION;
        END
        ELSE IF @TranExterne = 0
            COMMIT TRANSACTION;
    END TRY
    BEGIN CATCH
        IF @TranExterne = 0
        BEGIN
            IF @@TRANCOUNT > 0 ROLLBACK TRANSACTION;
        END
        ELSE IF XACT_STATE() = 1
            ROLLBACK TRANSACTION spImportPiece;     -- XACT_STATE() = -1 : l'appelant doit annuler
        THROW;
    END CATCH;
END;
GO

/* Si l'import tourne sous un login applicatif non-dbo :
   GRANT EXECUTE ON TYPE::imp.TT_LigneEcriture TO [login_import];
   GRANT EXECUTE ON OBJECT::imp.PS_ImportPiece  TO [login_import];

   NOTES
   - Analytique : rien n'est écrit dans F_ECRITUREA. Pour un compte soumis à
     ventilation, le logiciel comptable affichera l'écriture sans analytique.
   - Journaux de trésorerie (JO_Type = 2) : fournir explicitement la ligne du
     compte de banque/caisse ; CG_NumCont / EC_TresoPiece ne sont pas renseignés.
   - Verrous de saisie : si un utilisateur a le journal/mois ouvert en saisie, le
     déclencheur TG_CBINS lève 80003/80004 → la pièce entière est annulée.
   - Concurrence avec la numérotation du logiciel comptable : le verrou applicatif ne sérialise
     que les imports entre eux ; éviter d'importer pendant une saisie manuelle
     sur le même journal.
   - Chaînage cbHash* : non calculé (contrôle bloquant si actif, cf. @IgnorerHash).
*/
