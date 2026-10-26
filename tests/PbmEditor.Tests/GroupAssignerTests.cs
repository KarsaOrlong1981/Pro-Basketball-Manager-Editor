using PbmEditor.Core;
using Xunit;

namespace PbmEditor.Tests;

public class GroupAssignerTests
{
    // rough city coordinates (lat, lon)
    static readonly (double, double) Hamburg = (53.55, 9.99), Bremen = (53.08, 8.80), Berlin = (52.52, 13.40), Rostock = (54.09, 12.14),
        Cologne = (50.94, 6.96), Essen = (51.46, 7.01), Duesseldorf = (51.23, 6.78), Dortmund = (51.51, 7.47),
        Munich = (48.14, 11.58), Nuremberg = (49.45, 11.08), Augsburg = (48.37, 10.90), Regensburg = (49.01, 12.10),
        Stuttgart = (48.78, 9.18), Frankfurt = (50.11, 8.68), Mannheim = (49.49, 8.47), Freiburg = (47.99, 7.85);

    static readonly (double, double)[] North = { Hamburg, Bremen, Berlin, Rostock };
    static readonly (double, double)[] West = { Cologne, Essen, Duesseldorf, Dortmund };
    static readonly (double, double)[] SouthEast = { Munich, Nuremberg, Augsburg, Regensburg };
    static readonly (double, double)[] SouthWest = { Stuttgart, Frankfurt, Mannheim, Freiburg };

    static List<(double, double)> All() => North.Concat(West).Concat(SouthEast).Concat(SouthWest).ToList();

    [Fact]
    public void FourRegions_AreSeparatedAndEqualInSize()
    {
        var g = GroupAssigner.Assign(All(), 4, 4, seed: 1);
        Assert.Equal(new[] { 4, 4, 4, 4 }, g.GroupBy(x => x).OrderBy(x => x.Key).Select(x => x.Count()).ToArray());
        // each block of four neighbouring cities ends up in one group
        for (var block = 0; block < 4; block++)
            Assert.Single(g.Skip(block * 4).Take(4).Distinct());
        Assert.Equal(4, g.Distinct().Count());
    }

    [Fact]
    public void Result_DoesNotDependOnInputOrder()
    {
        var points = All();
        var shuffled = points.OrderBy(p => (p.Item1 * 7919 + p.Item2 * 104729) % 13).ToList();
        var a = GroupAssigner.Assign(points, 4, 4, seed: 5);
        var b = GroupAssigner.Assign(shuffled, 4, 4, seed: 5);
        // the same cities share a group in both runs (group numbers may differ)
        for (var i = 0; i < points.Count; i++)
            for (var j = i + 1; j < points.Count; j++)
                Assert.Equal(a[i] == a[j], b[shuffled.IndexOf(points[i])] == b[shuffled.IndexOf(points[j])]);
    }

    [Fact]
    public void SameSeedGivesSameResult() =>
        Assert.Equal(GroupAssigner.Assign(All(), 4, 4, seed: 9), GroupAssigner.Assign(All(), 4, 4, seed: 9));

    [Fact]
    public void PinnedTeamsStayInTheirGroup_AndOthersFillTheRest()
    {
        var points = All();
        var pinned = new int?[points.Count];
        pinned[0] = 3;  // Hamburg forced into group 3
        pinned[8] = 1;  // Munich forced into group 1
        var g = GroupAssigner.Assign(points, 4, 4, pinned, seed: 2);
        Assert.Equal(3, g[0]); Assert.Equal(1, g[8]);
        Assert.Equal(new[] { 4, 4, 4, 4 }, g.GroupBy(x => x).OrderBy(x => x.Key).Select(x => x.Count()).ToArray());
    }

    [Fact]
    public void FewerTeamsThanPlaces_NeverExceedsCapacity()
    {
        var points = North.Concat(West).Concat(SouthEast).Take(10).ToList();
        var g = GroupAssigner.Assign(points, 3, 4, seed: 4);
        Assert.Equal(10, g.Length);
        Assert.All(g.GroupBy(x => x), grp => Assert.InRange(grp.Count(), 1, 4));
        Assert.All(g, x => Assert.InRange(x, 1, 3));
    }

    [Fact]
    public void OneGroup_PutsEveryoneInGroupOne() =>
        Assert.All(GroupAssigner.Assign(North, 1, 8, seed: 0), x => Assert.Equal(1, x));

    [Fact]
    public void Empty_ReturnsEmpty() => Assert.Empty(GroupAssigner.Assign(new List<(double, double)>(), 4, 4));

    [Fact]
    public void InvalidInput_Throws()
    {
        var points = North.ToList();
        Assert.Throws<ArgumentException>(() => GroupAssigner.Assign(points, 1, 3)); // 4 teams, 3 places
        Assert.Throws<ArgumentOutOfRangeException>(() => GroupAssigner.Assign(points, 0, 4));
        Assert.Throws<ArgumentOutOfRangeException>(() => GroupAssigner.Assign(points, 2, 0));
        Assert.Throws<ArgumentOutOfRangeException>(() => GroupAssigner.Assign(points, 2, 4, new int?[] { 3, null, null, null }));
        Assert.Throws<ArgumentException>(() => GroupAssigner.Assign(points, 2, 2, new int?[] { 1, 1, 1, null })); // 3 pinned, 2 places
        Assert.Throws<ArgumentException>(() => GroupAssigner.Assign(points, 2, 4, new int?[] { 1 })); // length mismatch
    }
}
