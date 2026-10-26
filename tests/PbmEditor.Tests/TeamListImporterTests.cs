using PbmEditor.Core;
using Xunit;

namespace PbmEditor.Tests;

public class TeamListImporterTests
{
    // the format of the list prepared for the 1. Regionalliga: BOM, semicolons, German headers
    const string RegioCsv =
        "﻿Gruppe;Staffel;Team;Stadt;Breitengrad;Laengengrad;Quelle\r\n" +
        "Nord;;Berlin Dreams;;;;wiki\r\n" +
        "Nord;;VfL Stade;;;;wiki\r\n" +
        "Südost;Nord;Culture City Weimar;Weimar;;;wiki\r\n" +
        "Südost;Süd;TS Jahn München;München;;;wiki\r\n" +
        "Südwest;;BBU 01 Ulm;;48.4000;9.9833;wiki\r\n";

    [Fact]
    public void ReadsTheRegionalligaListFormat()
    {
        var list = TeamListImporter.Parse(RegioCsv);
        Assert.Equal(5, list.Entries.Count);
        Assert.Equal(new[] { "Nord", "Südost", "Südwest" }, list.GroupLabels);
        Assert.Equal(new TeamEntry("Berlin Dreams", 1), list.Entries[0]);
        Assert.Equal(new TeamEntry("Culture City Weimar", 2, "Weimar"), list.Entries[2]);
        Assert.Equal(2, list.Entries[3].Group); // both Staffeln of Südost share the group
        Assert.Equal(new TeamEntry("BBU 01 Ulm", 3, null, 48.4, 9.9833), list.Entries[4]);
        Assert.Empty(list.Warnings);
    }

    [Fact]
    public void GroupLabels_AreNumberedByFirstAppearance_IgnoringCase()
    {
        var list = TeamListImporter.Parse("Team;Gruppe\nA;West\nB;Nord\nC;west\nD;\nE;Nord");
        Assert.Equal(new[] { "West", "Nord" }, list.GroupLabels);
        Assert.Equal(new int?[] { 1, 2, 1, null, 2 }, list.Entries.Select(e => e.Group).ToArray());
    }

    [Fact]
    public void ReadsCommaSeparatedFilesWithQuotes_AndEnglishHeaders()
    {
        var list = TeamListImporter.Parse("Name,Group,City,Latitude,Longitude\n\"Hawks, Berlin\",A,\"Berlin\",52.52,13.40\n\"The \"\"Wolves\"\"\",B,,,\n");
        Assert.Equal("Hawks, Berlin", list.Entries[0].Name);
        Assert.Equal("Berlin", list.Entries[0].City);
        Assert.Equal(13.40, list.Entries[0].Lon);
        Assert.Equal("The \"Wolves\"", list.Entries[1].Name);
        Assert.Null(list.Entries[1].Lat);
    }

    [Fact]
    public void ReadsTabSeparatedFiles_AndDecimalCommas()
    {
        var tab = TeamListImporter.Parse("Team\tGruppe\nHawks\tNord\n");
        Assert.Equal(new TeamEntry("Hawks", 1), tab.Entries.Single());
        var comma = TeamListImporter.Parse("Team;Breitengrad;Laengengrad\nHawks;48,4;9,98\n");
        Assert.Equal(48.4, comma.Entries.Single().Lat);
        Assert.Equal(9.98, comma.Entries.Single().Lon);
    }

    [Fact]
    public void HeaderOrderDoesNotMatter_AndUnknownColumnsAreIgnored()
    {
        var list = TeamListImporter.Parse("Quelle;Stadt;Team\nwiki;Ulm;BBU 01 Ulm\n");
        Assert.Equal(new TeamEntry("BBU 01 Ulm", null, "Ulm"), list.Entries.Single());
    }

    [Fact]
    public void BlankLinesAreSkipped_AndLineNumbersStayTrue()
    {
        var ex = Assert.Throws<FormatException>(() => TeamListImporter.Parse("Team;Gruppe\n\nA;1\n\n;2\n"));
        Assert.Contains("5", ex.Message); // the line with the missing name is line 5 of the file
        var withQuotedBreak = Assert.Throws<FormatException>(() => TeamListImporter.Parse("Team;Gruppe\n\"A\nB\";1\n;2\n"));
        Assert.Contains("4", withQuotedBreak.Message); // a line break inside quotes counts as a line
    }

    [Fact]
    public void DuplicateNames_GiveAWarning()
    {
        var list = TeamListImporter.Parse("Team\nHawks\nhawks\nWolves");
        Assert.Equal(3, list.Entries.Count);
        Assert.Single(list.Warnings);
        Assert.Contains("hawks", list.Warnings[0]);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   \n \n")]
    [InlineData("Team;Gruppe\n")] // header only
    public void EmptyLists_AreRejected(string text) => Assert.Throws<FormatException>(() => TeamListImporter.Parse(text));

    [Fact]
    public void MissingTeamColumn_IsRejected()
    {
        var ex = Assert.Throws<FormatException>(() => TeamListImporter.Parse("Stadt;Gruppe\nUlm;1\n"));
        Assert.Contains("Team", ex.Message);
    }

    [Theory]
    [InlineData("Team;Breitengrad;Laengengrad\nA;abc;9\n")]
    [InlineData("Team;Breitengrad;Laengengrad\nA;48;\n")]   // only one of the two
    [InlineData("Team;Breitengrad;Laengengrad\nA;;9\n")]
    [InlineData("Team;Breitengrad;Laengengrad\nA;91;9\n")]  // latitude out of range
    [InlineData("Team;Breitengrad;Laengengrad\nA;48;181\n")] // longitude out of range
    public void BadCoordinates_AreRejected(string text)
    {
        var ex = Assert.Throws<FormatException>(() => TeamListImporter.Parse(text));
        Assert.Contains("2", ex.Message); // names the line
    }
}
