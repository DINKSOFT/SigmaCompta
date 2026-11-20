/* =============================================================================
   01 — PRÉ-VÉRIFICATION (lecture seule) avant création de imp.PS_ImportPiece
   Base : base comptable (ex. SKIOUAGA)
   Chaque contrôle est dans son propre lot (GO) : si une colonne n'existe pas,
   seul ce lot échoue — c'est justement l'information recherchée.
   ============================================================================= */
SET NOCOUNT ON;
GO

/* 1. Version / niveau de compatibilité. THROW, TRY_CAST et DATEFROMPARTS exigent un serveur
      SQL Server 2012 ou plus (ProductVersion 11.x) ; un niveau de base inférieur à 110 est accepté. */
SELECT  SERVERPROPERTY('ProductVersion') AS Version_SQL,
        d.compatibility_level            AS Niveau_compat,
        d.collation_name                 AS Classement
FROM    sys.databases d
WHERE   d.name = DB_NAME();
GO

/* 2. Colonnes utilisées par la procédure : doivent toutes exister, aucune calculée
      → attendu : 19 lignes F_ECRITUREC, 5 lignes F_JMOUV, Est_calculee = 0 partout */
SELECT  t.name AS Table_, c.name AS Colonne, TYPE_NAME(c.user_type_id) AS Type_,
        c.max_length, c.is_nullable, c.is_computed AS Est_calculee,
        OBJECT_DEFINITION(c.default_object_id) AS Defaut
FROM    sys.columns c
JOIN    sys.tables  t ON t.object_id = c.object_id
WHERE  (t.name = 'F_ECRITUREC' AND c.name IN
          ('JO_Num','cbJO_Num','EC_No','JM_Date','EC_Jour','EC_Date',
           'EC_Piece','cbEC_Piece','EC_RefPiece','cbEC_RefPiece',
           'CG_Num','cbCG_Num','CT_Num','cbCT_Num',
           'EC_Intitule','EC_Sens','EC_Montant','EC_Echeance','EC_Norme'))
    OR (t.name = 'F_JMOUV' AND c.name IN
          ('JO_Num','cbJO_Num','JM_Date','JM_Cloture','JM_Impression'))
ORDER BY t.name, c.column_id;
GO

/* 3. Colonnes NOT NULL sans valeur par défaut que la procédure NE fournit PAS
      → attendu : 0 ligne. Sinon, il faut les ajouter à l'INSERT. */
SELECT  t.name AS Table_, c.name AS Colonne, TYPE_NAME(c.user_type_id) AS Type_
FROM    sys.columns c
JOIN    sys.tables  t ON t.object_id = c.object_id
WHERE   t.name IN ('F_ECRITUREC','F_JMOUV')
  AND   c.is_nullable = 0 AND c.default_object_id = 0
  AND   c.is_identity = 0 AND c.is_computed = 0
  AND   TYPE_NAME(c.user_type_id) NOT IN ('timestamp','rowversion')
  AND   c.name NOT IN ('JO_Num','cbJO_Num','EC_No','JM_Date','EC_Jour','EC_Date',
                       'EC_Piece','cbEC_Piece','EC_RefPiece','cbEC_RefPiece',
                       'CG_Num','cbCG_Num','CT_Num','cbCT_Num','EC_Intitule',
                       'EC_Sens','EC_Montant','EC_Echeance','EC_Norme',
                       'JM_Cloture','JM_Impression');
GO

/* 4. Encodage cb* : comportement de CB_FntStr sur NULL / chaîne vide */
SELECT  dbo.CB_FntStr('OD')  AS FntStr_OD,      -- attendu 0x4F44
        dbo.CB_FntStr('')    AS FntStr_vide,
        dbo.CB_FntStr(NULL)  AS FntStr_NULL;    -- la proc remplace NULL par 0x
GO

/* 5. Confirme CB_FntStr pour TOUTES les clés (dont cbEC_Piece / cbEC_RefPiece)
      sur les 2 000 dernières écritures saisies par le logiciel comptable → attendu : 0 partout */
SELECT  SUM(CASE WHEN cbJO_Num      <> ISNULL(dbo.CB_FntStr(JO_Num), 0x)      THEN 1 ELSE 0 END) AS Ecarts_JO,
        SUM(CASE WHEN cbCG_Num      <> ISNULL(dbo.CB_FntStr(CG_Num), 0x)      THEN 1 ELSE 0 END) AS Ecarts_CG,
        SUM(CASE WHEN cbCT_Num      <> ISNULL(dbo.CB_FntStr(CT_Num), 0x)      THEN 1 ELSE 0 END) AS Ecarts_CT,
        SUM(CASE WHEN cbEC_Piece    <> ISNULL(dbo.CB_FntStr(EC_Piece), 0x)    THEN 1 ELSE 0 END) AS Ecarts_Piece,
        SUM(CASE WHEN cbEC_RefPiece <> ISNULL(dbo.CB_FntStr(EC_RefPiece), 0x) THEN 1 ELSE 0 END) AS Ecarts_RefPiece,
        SUM(CASE WHEN CT_Num = ''  THEN 1 ELSE 0 END) AS CT_vide,       -- le logiciel stocke-t-il '' ...
        SUM(CASE WHEN CT_Num IS NULL THEN 1 ELSE 0 END) AS CT_NULL,     -- ... ou NULL ?
        SUM(CASE WHEN EC_RefPiece IS NULL THEN 1 ELSE 0 END) AS RefPiece_NULL
FROM   (SELECT TOP (2000) * FROM dbo.F_ECRITUREC ORDER BY cbMarq DESC) e;
GO

/* 6. Journaux : mode de numérotation (JO_NumPiece) + dernières pièces
      Hypothèse de la proc : 0 = manuelle, 1 = continue journal,
      2 = continue base, 3 = mensuelle → à comparer avec l'écran du journal */
SELECT  j.JO_Num, j.JO_Intitule, j.JO_Type, j.JO_NumPiece, j.JO_Sommeil,
        d.Dernieres_pieces
FROM    dbo.F_JOURNAUX j
OUTER APPLY (
    SELECT STUFF((
        SELECT TOP (5) ' | ' + CONVERT(varchar(7), x.JM_Date, 120) + ' ' + x.EC_Piece
        FROM  (SELECT EC_Piece, JM_Date, MAX(cbMarq) AS m
               FROM dbo.F_ECRITUREC WHERE JO_Num = j.JO_Num
               GROUP BY EC_Piece, JM_Date) x
        ORDER BY x.m DESC
        FOR XML PATH(''), TYPE).value('.', 'varchar(max)'), 1, 3, '')) d(Dernieres_pieces)
ORDER BY j.JO_Num;
GO

/* 7. Existe-t-il un compteur de pièces stocké ? (colonnes *Piece* hors écritures)
      Si une table de type compteur apparaît, la numérotation de la proc devra
      la lire/mettre à jour au lieu de calculer MAX+1. */
SELECT  t.name AS Table_, c.name AS Colonne
FROM    sys.columns c JOIN sys.tables t ON t.object_id = c.object_id
WHERE   c.name LIKE '%Piece%'
  AND   t.name NOT IN ('F_ECRITUREC','F_ECRITUREA')
ORDER BY t.name, c.name;
GO

/* 8. Exercices (P_DOSSIER) et gestion IFRS */
SELECT  D_DebutExo01, D_FinExo01, D_DebutExo02, D_FinExo02,
        D_DebutExo03, D_FinExo03, D_DebutExo04, D_FinExo04,
        D_DebutExo05, D_FinExo05, D_GestionIFRS
FROM    dbo.P_DOSSIER;
GO

/* 9. État du chaînage d'inaltérabilité sur la dernière écriture
      → la proc refuse d'insérer si cbHashVersion n'est pas NULL (sauf @IgnorerHash = 1) */
SELECT TOP (1) cbMarq, EC_No, cbHashVersion, cbHashDate, cbHashOrder
FROM   dbo.F_ECRITUREC ORDER BY cbMarq DESC;
GO

/* 10. Périodes F_JMOUV existantes (12 dernières) et leur état de clôture */
SELECT TOP (12) JO_Num, JM_Date, JM_Cloture, JM_Impression
FROM   dbo.F_JMOUV ORDER BY JM_Date DESC, JO_Num;
GO
