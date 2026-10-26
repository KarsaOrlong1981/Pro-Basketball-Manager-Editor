namespace PbmEditor.Core;

/// <summary>
/// Splits teams into regional groups of equal size by the coordinates of their cities (the game's own geographic split does the same in later seasons).
/// Teams can be pinned to a group; the other teams fill the remaining places, nearest group first.
/// </summary>
public static class GroupAssigner
{
    /// <returns>Group number (1-based) per point, in input order. Every group holds exactly <paramref name="capacity"/> teams when points.Count = groups x capacity.</returns>
    public static int[] Assign(IReadOnlyList<(double Lat, double Lon)> points, int groups, int capacity,
        IReadOnlyList<int?>? pinned = null, int seed = 0)
    {
        if (groups < 1 || capacity < 1) throw new ArgumentOutOfRangeException(nameof(groups));
        if (points.Count > groups * capacity) throw new ArgumentException("More teams than places.", nameof(points));
        if (pinned != null && pinned.Count != points.Count) throw new ArgumentException("pinned must match points.", nameof(pinned));

        var n = points.Count;
        var result = new int[n];
        var used = new int[groups + 1];
        for (var i = 0; i < n; i++)
        {
            if (pinned?[i] is not { } g) continue;
            if (g < 1 || g > groups) throw new ArgumentOutOfRangeException(nameof(pinned), $"Group {g} does not exist.");
            if (++used[g] > capacity) throw new ArgumentException($"Group {g} has more than {capacity} pinned teams.", nameof(pinned));
            result[i] = g;
        }
        if (groups == 1) { Array.Fill(result, 1); return result; }

        // flat approximation: longitude shrinks with latitude
        var cos = n == 0 ? 1 : Math.Cos(points.Average(p => p.Lat) * Math.PI / 180);
        (double X, double Y) P(int i) => (points[i].Lon * cos, points[i].Lat);
        static double Dist((double X, double Y) a, (double X, double Y) b) => Math.Sqrt((a.X - b.X) * (a.X - b.X) + (a.Y - b.Y) * (a.Y - b.Y));

        var free = Enumerable.Range(0, n).Where(i => result[i] == 0).ToList();
        if (free.Count == 0) return result;

        var centers = new (double X, double Y)?[groups + 1];
        void Recenter()
        {
            for (var g = 1; g <= groups; g++)
            {
                var members = Enumerable.Range(0, n).Where(i => result[i] == g).ToList();
                if (members.Count > 0) centers[g] = (members.Average(i => P(i).X), members.Average(i => P(i).Y));
            }
        }
        Recenter(); // groups with pinned teams start at their pinned teams

        // other groups: farthest-point seeding among the free teams (first seed picked by the seed, so results are reproducible)
        var rng = new Random(seed);
        foreach (var g in Enumerable.Range(1, groups).Where(g => centers[g] == null))
        {
            var seeds = centers.Where(c => c != null).Select(c => c!.Value).ToList();
            var pick = seeds.Count == 0
                ? free[rng.Next(free.Count)]
                : free.OrderByDescending(i => seeds.Min(c => Dist(P(i), c))).ThenBy(i => i).First();
            centers[g] = P(pick);
        }

        int[]? previous = null;
        for (var round = 0; round < 12; round++)
        {
            var left = Enumerable.Range(0, groups + 1).Select(g => g == 0 ? 0 : capacity - used[g]).ToArray();
            var pairs = free.SelectMany(i => Enumerable.Range(1, groups).Select(g => (I: i, G: g, D: Dist(P(i), centers[g]!.Value))))
                .OrderBy(p => p.D).ThenBy(p => p.I).ThenBy(p => p.G);
            foreach (var i in free) result[i] = 0;
            foreach (var (i, g, _) in pairs)
            {
                if (result[i] != 0 || left[g] == 0) continue;
                result[i] = g; left[g]--;
            }
            if (previous != null && previous.SequenceEqual(result)) break;
            previous = (int[])result.Clone();
            Recenter();
        }
        return result;
    }
}
