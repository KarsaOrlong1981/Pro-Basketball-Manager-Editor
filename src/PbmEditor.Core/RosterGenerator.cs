namespace PbmEditor.Core;

/// <summary>First names, last names and looks of existing male players of one country; used to make plausible new players and staff.</summary>
sealed class NamePool
{
    static readonly (long Skin, long Hair, long HairColor, long Beard) DefaultLook = (0, 11, 1, 0);

    public List<string> First { get; } = new();
    public List<string> Last { get; } = new();
    public List<(long Skin, long Hair, long HairColor, long Beard)> Looks { get; } = new();

    static string Dec(object? v) => v is string s && PbmCodec.TryDecode(s, out var d) ? d : v?.ToString() ?? "";

    public static NamePool Load(PbmDatabase db, long country)
    {
        var pool = new NamePool();
        // fall back to all countries when the country has too few players to draw from
        var filter = db.Scalar("SELECT COUNT(*) FROM DYN_joueur WHERE nationalite=@p0 AND sexe=0", country) is long n && n >= 60
            ? "nationalite=@p0 AND sexe=0" : "sexe=0 AND @p0=@p0";
        foreach (var r in db.Query($"SELECT DISTINCT joueur_prenom FROM DYN_joueur WHERE {filter} AND joueur_prenom<>''", country))
            if (Dec(r["joueur_prenom"]) is { Length: > 0 } f) pool.First.Add(f);
        foreach (var r in db.Query($"SELECT DISTINCT joueur_nom FROM DYN_joueur WHERE {filter} AND joueur_nom<>''", country))
            if (Dec(r["joueur_nom"]) is { Length: > 0 } l) pool.Last.Add(l);
        foreach (var r in db.Query($"SELECT couleur, type_coiffure, couleur_cheveux, type_barbe FROM DYN_joueur WHERE {filter} LIMIT 3000", country))
            if (!GameService.NoHairModel.Contains(r.Int("type_coiffure")))
                pool.Looks.Add((r.Int("couleur"), r.Int("type_coiffure"), r.Int("couleur_cheveux"), r.Int("type_barbe")));
        if (pool.First.Count == 0) pool.First.Add("Max");
        if (pool.Last.Count == 0) pool.Last.Add("Mustermann");
        if (pool.Looks.Count == 0) pool.Looks.Add(DefaultLook);
        return pool;
    }

    public string FirstName(Random rng) => First[rng.Next(First.Count)];
    public string LastName(Random rng) => Last[rng.Next(Last.Count)];
    public (long Skin, long Hair, long HairColor, long Beard) Look(Random rng) => Looks[rng.Next(Looks.Count)];
}

/// <summary>Generates a believable roster for a new team with <see cref="PlayerCreator"/>.</summary>
static class RosterGenerator
{
    // quality of each roster slot relative to the team level; the first ten average +0.33
    static readonly double[] Offsets = { 1.6, 1.2, 0.8, 0.5, 0.3, 0.1, 0, -0.2, -0.4, -0.6, -0.9, -1.2, -1.5 };
    const double TopTenOffset = 0.33;
    public const int MaxSize = 13, MinSize = 10;

    static readonly PlayerPosition[] Positions =
    {
        PlayerPosition.PointGuard, PlayerPosition.PointGuard, PlayerPosition.ShootingGuard, PlayerPosition.ShootingGuard,
        PlayerPosition.Wing, PlayerPosition.Wing, PlayerPosition.Wing, PlayerPosition.PowerForward, PlayerPosition.PowerForward,
        PlayerPosition.PowerForward, PlayerPosition.Center, PlayerPosition.Center, PlayerPosition.Center,
    };

    static (int Min, int Max) HeightRange(PlayerPosition p) => p switch
    {
        PlayerPosition.PointGuard => (178, 192), PlayerPosition.ShootingGuard => (186, 198), PlayerPosition.Wing => (192, 204),
        PlayerPosition.PowerForward => (198, 209), _ => (203, 216),
    };

    static void Shuffle<T>(IList<T> list, Random rng)
    {
        for (var i = list.Count - 1; i > 0; i--) { var j = rng.Next(i + 1); (list[i], list[j]) = (list[j], list[i]); }
    }

    /// <param name="topTenAverage">Target average overall of the ten best players.</param>
    /// <param name="monthlyPayroll">Total monthly salary, shared by market-value weight.</param>
    public static List<long> Fill(PbmDatabase db, long teamId, long country, NamePool pool, Random rng, double topTenAverage, long monthlyPayroll, int size)
    {
        size = Math.Clamp(size, MinSize, MaxSize);
        var (year, _, _) = GameService.CurrentDate(db);
        var positions = Positions.ToList();
        Shuffle(positions, rng);

        var overalls = Enumerable.Range(0, size)
            .Select(i => Math.Clamp(topTenAverage - TopTenOffset + Offsets[i] + (rng.NextDouble() - 0.5) * 0.6, 4, 16)).ToArray();
        var weights = overalls.Select(o => (double)Math.Max(200, PlayerCreator.SuggestSalary(o))).ToArray();
        var numbers = Enumerable.Range(4, 31).ToList();
        Shuffle(numbers, rng);

        var ids = new List<long>();
        for (var i = 0; i < size; i++)
        {
            var pos = positions[i];
            var (hMin, hMax) = HeightRange(pos);
            var height = rng.Next(hMin, hMax + 1);
            var age = rng.Next(19, 36);
            var look = pool.Look(rng);
            var potential = age <= 23 ? overalls[i] + 1 + rng.NextDouble() * 3 : overalls[i] + rng.NextDouble() * 0.5;
            var salary = Math.Max(300, (long)Math.Round(weights[i] / weights.Sum() * monthlyPayroll / 10) * 10);
            var notes = db.Changes.Count;
            ids.Add(PlayerCreator.Create(db, new NewPlayer(
                pool.FirstName(rng), pool.LastName(rng), rng.Next(1, 29), rng.Next(1, 13), year - age,
                false, country, height, (int)Math.Round((height - 100) * 0.9 + rng.Next(-4, 9)),
                pos, Math.Round(overalls[i], 2), Math.Round(Math.Min(potential, 20), 1),
                look.Skin, look.Hair, look.HairColor, look.Beard,
                teamId, numbers[i], salary, rng.Next(1, 4))));
            db.Changes.RemoveRange(notes, db.Changes.Count - notes); // one summary note per team instead of one per player
        }
        return ids;
    }
}
