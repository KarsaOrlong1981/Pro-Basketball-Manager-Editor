namespace PbmEditor.Core;

/// <summary>Main position; sets the aptitude columns and the attribute profile.</summary>
public enum PlayerPosition { PointGuard, ShootingGuard, Wing, PowerForward, Center }

/// <summary>Input for a new player. TeamId 0 = free agent (no contract, no jersey number).</summary>
public sealed record NewPlayer(
    string FirstName, string LastName,
    int BirthDay, int BirthMonth, int BirthYear,
    bool Female, long Nationality, int Height, int Weight,
    PlayerPosition Position, double Overall, double Potential,
    long Skin, long Hairstyle, long HairColor, long Beard,
    long TeamId = 0, long? Number = null, long Salary = 0, int ContractYears = 1);

/// <summary>Creates a complete player (person row, attributes, languages and, for a team, an active contract).</summary>
public static class PlayerCreator
{
    // overall ~ 0.4245 + 0.03627 x sum of the 26 attributes (fitted on the default database)
    const double OverallBase = 0.4245, OverallPerPoint = 0.03627;

    // (overall, average market value / monthly salary) measured in the default database
    static readonly (double R, double Value, double Salary)[] Scale =
    {
        (4, 2334, 240), (5, 2706, 251), (6, 3359, 294), (7, 5442, 467), (8, 11319, 837), (9, 25471, 1722), (10, 70216, 5024),
        (11, 142299, 15924), (12, 342284, 47686), (13, 620800, 247708), (14, 943319, 1080333), (15, 1256250, 2043675), (16, 1311000, 2247100),
    };

    // attribute offsets per position, in FieldConfigs.AttributeColumns order (6 mental values at the end stay neutral)
    static readonly double[][] Profiles =
    {
        //  3pt 2pt  FT  crt  ins  dri  pas  pen  dEx  dIn  stl  oRb  dRb  blk  spd  end  jmp  pow  acc  exp
        new double[] { 1, 1, 1, 2, -3, 3, 3, 2, 1, -3, 1, -3, -3, -3, 2, 0, 0, -2, 2, 1 },      // PG
        new double[] { 2, 2, 1, 2, -1, 1, 0, 1, 1, -2, 1, -2, -2, -2, 1, 0, 0, -1, 1, 1 },      // SG
        new double[] { 1, 1, 0, 1, 0, 0, 0, 1, 1, 0, 0, 0, 0, 0, 1, 0, 1, 0, 1, 1 },            // wing
        new double[] { -2, 0, -1, -1, 2, -2, -1, -1, -1, 2, 0, 2, 2, 1, -1, 0, 1, 2, -1, 0 },   // PF
        new double[] { -4, -2, -2, -2, 3, -4, -2, -2, -3, 3, -1, 3, 3, 3, -2, 0, 1, 3, -2, -1 }, // C
    };

    public static double OverallFor(double attributeSum) => Math.Round(OverallBase + OverallPerPoint * attributeSum, 2);

    static double Interpolate(double overall, Func<(double R, double Value, double Salary), double> pick)
    {
        var r = Math.Clamp(overall, Scale[0].R, Scale[^1].R);
        for (var i = 1; i < Scale.Length; i++)
        {
            if (r > Scale[i].R) continue;
            var (a, b) = (Math.Log(pick(Scale[i - 1])), Math.Log(pick(Scale[i])));
            return Math.Exp(a + (b - a) * (r - Scale[i - 1].R) / (Scale[i].R - Scale[i - 1].R));
        }
        return pick(Scale[^1]);
    }

    public static long SuggestValue(double overall) => (long)Math.Round(Interpolate(overall, s => s.Value) / 100) * 100;
    public static long SuggestSalary(double overall) => (long)Math.Round(Interpolate(overall, s => s.Salary) / 10) * 10;

    static double Round05(double v) => Math.Round(v * 2, MidpointRounding.AwayFromZero) / 2;

    /// <summary>26 attribute values (FieldConfigs.AttributeColumns order) for a target overall and position profile.</summary>
    public static double[] Attributes(double overall, PlayerPosition position)
    {
        var profile = Profiles[(int)position];
        var count = FieldConfigs.AttributeColumns.Length;
        var targetAverage = (overall - OverallBase) / OverallPerPoint / count;
        var offsets = Enumerable.Range(0, count).Select(i => i < profile.Length ? profile[i] : 0).ToArray();
        var baseValue = targetAverage - offsets.Average();
        return offsets.Select(o => Math.Clamp(Round05(baseValue + o), 1, 20)).ToArray();
    }

    /// <summary>Aptitude columns (PG, SG, wing, PF, C): 100 for the main position, 0 elsewhere (the most common pattern in the game data).</summary>
    public static double[] Aptitudes(PlayerPosition position)
    {
        var a = new double[5];
        a[(int)position] = 100;
        return a;
    }

    public static string? Validate(PbmDatabase db, NewPlayer p)
    {
        if (p.FirstName.Trim().Length == 0 || p.LastName.Trim().Length == 0) return Loc.T("Please enter a first and a last name.");
        if (!ValidDate(p.BirthYear, p.BirthMonth, p.BirthDay)) return Loc.T("The birth date is not a valid date.");
        if (p.Height is < 120 or > 260) return Loc.T("Height must be between 120 and 260 cm.");
        if (p.Weight is < 40 or > 250) return Loc.T("Weight must be between 40 and 250 kg.");
        if (p.TeamId != 0 && p.Salary < 0) return Loc.T("The salary cannot be negative.");
        if (p.TeamId != 0 && p.ContractYears is < 1 or > 10) return Loc.T("The contract length must be between 1 and 10 years.");
        if (p.TeamId != 0 && p.Number is { } n && (n is < 0 or > 99 ||
                db.Scalar("SELECT 1 FROM DYN_joueur WHERE team_id=@p0 AND numero_actuel=@p1", p.TeamId, n) != null))
            return Loc.F("Jersey number {0} is already taken in the target team.", n);
        return null;
    }

    static bool ValidDate(int y, int m, int d) => y is >= 1900 and <= 2100 && m is >= 1 and <= 12 && d >= 1 && d <= DateTime.DaysInMonth(y, m);

    /// <summary>Writes the player into the open transaction and returns the new player ID.</summary>
    public static long Create(PbmDatabase db, NewPlayer p)
    {
        var error = Validate(db, p);
        if (error != null) throw new InvalidOperationException(error);
        if (db.GetTable("DYN_joueur") == null) throw new InvalidOperationException(Loc.T("Data not found."));

        // all-or-nothing: a failure in the middle must not leave a half-written player in the transaction
        db.Execute("SAVEPOINT new_player");
        try
        {
            var id = Write(db, p);
            db.Execute("RELEASE new_player");
            return id;
        }
        catch
        {
            db.Execute("ROLLBACK TO new_player");
            db.Execute("RELEASE new_player");
            throw;
        }
    }

    static long Write(PbmDatabase db, NewPlayer p)
    {
        var attrs = Attributes(p.Overall, p.Position);
        var overall = OverallFor(attrs.Sum());
        var id = Convert.ToInt64(db.Scalar("SELECT COALESCE(MAX(joueur_id),0)+1 FROM DYN_joueur"));
        var (y, m, d) = GameService.CurrentDate(db);
        var age = y - p.BirthYear - ((m, d).CompareTo((p.BirthMonth, p.BirthDay)) < 0 ? 1 : 0);
        var number = p.TeamId == 0 ? 255 : p.Number ?? GameService.NextFreeNumber(db, p.TeamId);
        var apt = Aptitudes(p.Position);

        Insert(db, "DYN_joueur", new()
        {
            ["joueur_id"] = id,
            ["joueur_prenom"] = PbmCodec.Encode(p.FirstName.Trim()), ["joueur_nom"] = PbmCodec.Encode(p.LastName.Trim()),
            ["joueur_middle"] = "", ["joueur_suffixe"] = "", ["joueur_surnom"] = "", ["nom_chinese"] = "", ["prenom_chinese"] = "",
            ["jour_naissance"] = p.BirthDay, ["mois_naissance"] = p.BirthMonth, ["annee_naissance"] = p.BirthYear, ["age"] = age,
            ["is_alive"] = 1, ["team_id"] = p.TeamId, ["nationalite"] = p.Nationality, ["nationalite_bis"] = 0,
            ["taille"] = p.Height, ["poids"] = p.Weight, ["sexe"] = p.Female ? 1 : 0, ["statut"] = 0,
            ["couleur"] = p.Skin, ["type_coiffure"] = p.Hairstyle, ["couleur_cheveux"] = p.HairColor, ["type_barbe"] = p.Beard,
            ["aptitude_meneur"] = apt[0], ["aptitude_arriere"] = apt[1], ["aptitude_ailier"] = apt[2], ["aptitude_interieur"] = apt[3], ["aptitude_pivot"] = apt[4],
            ["main_droite"] = 100, ["main_gauche"] = 0,
            ["moyenne"] = overall, ["joueur_valeur"] = SuggestValue(overall),
            ["caractere_comportement"] = 0, ["retraite"] = 0, ["retraite_internationale"] = 0, ["blesse"] = 0,
            ["renomee_nationale"] = 0, ["renomee_monde"] = 0, ["loyaute"] = 10, ["id_selection_choisie"] = p.Nationality,
            ["numero_actuel"] = number, ["numero_favori"] = 255,
        });

        var attributeValues = new Dictionary<string, object?> { ["joueur_id"] = id, ["moyenne"] = overall,
            ["cache_motivation"] = 10.0, ["cache_potentiel"] = Math.Clamp(p.Potential, 0, 20), ["cache_frequence_blessure"] = 10.0 };
        for (var i = 0; i < attrs.Length; i++) attributeValues[FieldConfigs.AttributeColumns[i]] = attrs[i];
        Insert(db, "DYN_joueur_attributes", attributeValues);

        AddNativeLanguage(db, id, p.Nationality);
        if (p.TeamId != 0) InsertContract(db, id, p.TeamId, p.Salary, p.ContractYears, (y, m, d));

        var who = $"{p.FirstName.Trim()} {p.LastName.Trim()}";
        db.Note(p.TeamId == 0
            ? Loc.F("{0}: new player created as free agent (ID {1})", who, id)
            : Loc.F("{0}: new player created for {1} (#{2}, ID {3})", who, ChangeNames.Team(db, p.TeamId), number, id));
        return id;
    }

    static void AddNativeLanguage(PbmDatabase db, long playerId, long country)
    {
        if (db.GetTable("DYN_joueur_langue") == null || db.GetTable("DYN_langue_pays") == null) return;
        var lang = db.Scalar("SELECT id_langue FROM DYN_langue_pays WHERE id_pays=@p0 ORDER BY ordre LIMIT 1", country);
        if (lang != null) Insert(db, "DYN_joueur_langue", new() { ["id_joueur"] = playerId, ["id_langue"] = lang, ["apprentissage"] = 100.0 });
    }

    static void InsertContract(PbmDatabase db, long playerId, long teamId, long salary, int years, (int Y, int M, int D) start)
    {
        if (db.GetTable("DYN_contrat") == null) return;
        var end = new DateTime(start.Y, start.M, start.D).AddYears(years);
        static long Stamp(int y, int m, int d) => new DateTimeOffset(y, m, d, 0, 0, 0, TimeSpan.Zero).ToUnixTimeSeconds();
        Insert(db, "DYN_contrat", new()
        {
            ["id"] = Convert.ToInt64(db.Scalar("SELECT COALESCE(MAX(id),0)+1 FROM DYN_contrat")),
            ["id_joueur"] = playerId, ["id_equipe"] = teamId, ["salaire"] = salary, ["accepte"] = 0,
            ["debut_jour"] = start.D, ["debut_mois"] = start.M, ["debut_annee"] = start.Y,
            ["fin_jour"] = end.Day, ["fin_mois"] = end.Month, ["fin_annee"] = end.Year,
            ["emission_jour"] = start.D, ["emission_mois"] = start.M, ["emission_annee"] = start.Y,
            ["start_timestamp"] = Stamp(start.Y, start.M, start.D), ["end_timestamp"] = Stamp(end.Year, end.Month, end.Day),
            ["statut"] = 7, ["responsabilite"] = 6, ["type_contrat"] = 0, ["duree"] = 0,
        });
    }

    /// <summary>Inserts only columns that exist (other game versions); required columns without a default get a neutral value.</summary>
    static void Insert(PbmDatabase db, string table, Dictionary<string, object?> values)
    {
        var info = db.GetTable(table);
        if (info == null) return;
        var row = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);
        foreach (var c in info.Columns)
        {
            if (values.TryGetValue(c.Name, out var v)) row[c.Name] = v;
            else if (c.NotNull && c.Default == null && c.PkIndex == 0)
                row[c.Name] = c.Kind switch { ColumnKind.Integer => 0L, ColumnKind.Real => 0.0, _ => "" };
        }
        var cols = row.Keys.ToList();
        db.Execute($"INSERT INTO \"{table}\" ({string.Join(",", cols.Select(c => $"\"{c}\""))}) VALUES ({string.Join(",", cols.Select((_, i) => "@p" + i))})",
            cols.Select(c => row[c]).ToArray());
    }
}
