using KaraokeFilmMaker.Application.Lyrics;
using KaraokeFilmMaker.Domain.Lyrics;
using KaraokeFilmMaker.Domain.Projects;
using KaraokeFilmMaker.Domain.Time;
using KaraokeFilmMaker.Domain.Validation;

namespace KaraokeFilmMaker.Application.Tests;

public sealed class RoleAssignmentsTests
{
    [Fact]
    public void LineRolesUnitOverridesAndRemovalPreserveEveryTimingField()
    {
        var project = Project();
        var line = project.Lyrics.Lines[0];
        var original = line.Units.Select(u => (u.Id, u.Timing, u.RawInputTime, u.IsConfirmed, u.DisplayRanges)).ToArray();
        project = RoleAssignments.Assign(project, new HashSet<Guid> { line.Id }, new HashSet<Guid>(),
            project.Roles.Reverse().Select(r => r.Id).ToArray());
        line = project.Lyrics.Lines[0];
        Assert.Equal(project.Roles.Select(r => r.Id), line.RoleIds);
        Assert.All(line.Units, u => Assert.Equal(line.RoleIds, RoleAssignments.Effective(line, u)));
        project = RoleAssignments.Assign(project, new HashSet<Guid>(), new HashSet<Guid> { line.Units[0].Id }, []);
        line = project.Lyrics.Lines[0];
        Assert.Empty(RoleAssignments.Effective(line, line.Units[0]));
        Assert.Equal(2, RoleAssignments.Effective(line, line.Units[1]).Count);
        project = RoleAssignments.Assign(project, new HashSet<Guid>(), new HashSet<Guid> { line.Units[0].Id }, [], inherit: true);
        project = RoleAssignments.Remove(project, project.Roles[0].Id);
        line = project.Lyrics.Lines[0];
        Assert.Single(RoleAssignments.Effective(line, line.Units[0]));
        Assert.Equal(original, line.Units.Select(u => (u.Id, u.Timing, u.RawInputTime, u.IsConfirmed, u.DisplayRanges)).ToArray());
        Assert.Empty(ProjectValidator.Validate(project));
    }

    [Fact]
    public void ResegmentationTransfersOverridesButKeepsInheritance()
    {
        var project = Project(); var source = project.Lyrics.Lines[0];
        source = source with { RoleIds = [project.Roles[0].Id], Units = [source.Units[0] with { RoleIds = [] }, source.Units[1]] };
        var result = RoleAssignments.Transfer(source, source.Units.Select(u => u with { Id = Guid.NewGuid(), RoleIds = null }).ToArray());
        Assert.Empty(result[0].RoleIds!); Assert.Null(result[1].RoleIds);
        var joined = RoleAssignments.Transfer(source, [source.Units[0] with { DisplayRanges = [new(0, source.PrimaryText.Length)], RoleIds = null }]);
        Assert.Equal(source.RoleIds, joined[0].RoleIds);
    }

    [Fact]
    public void ValidationRejectsDanglingDuplicateAndMalformedRoleOrTitleData()
    {
        var p = Project();
        Assert.NotEmpty(ProjectValidator.Validate(p with { Roles = [p.Roles[0], p.Roles[0]] }));
        Assert.NotEmpty(ProjectValidator.Validate(p with { SongTitle = new() { FontSize = double.NaN } }));
        Assert.NotEmpty(ProjectValidator.Validate(p with { SongTitle = new() { Text = "a\nb" } }));
        Assert.NotEmpty(ProjectValidator.Validate(p with { Lyrics = p.Lyrics with { Lines = [p.Lyrics.Lines[0] with { RoleIds = [Guid.NewGuid()] }] } }));
        Assert.Throws<InvalidOperationException>(() => RoleAssignments.Assign(p, new HashSet<Guid>(), new HashSet<Guid>(), [Guid.NewGuid()]));
    }

    private static KaraokeProject Project()
    {
        var p = KaraokeProject.Create("Roles", DateTimeOffset.UnixEpoch);
        var doc = new LyricImportService().Parse("[00:01]Hello world", ".lrc", LyricLanguage.English);
        var line = doc.Lines[0];
        line = line with { Timing = new(ProjectTime.FromSeconds(1), ProjectTime.FromSeconds(5)), Units = line.Units.Select((u, i) => u with
        { Timing = new(ProjectTime.FromSeconds(i + 1), ProjectTime.FromSeconds(i + 2)), RawInputTime = ProjectTime.FromSeconds(i + 1), IsConfirmed = true }).ToArray() };
        return p with { Roles = [new(Guid.NewGuid(), "A", p.SubtitleStyle), new(Guid.NewGuid(), "B", p.SubtitleStyle)],
            Lyrics = doc with { Lines = [line] } };
    }
}
