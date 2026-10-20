using KaraokeFilmMaker.Domain.Lyrics;
using KaraokeFilmMaker.Domain.Projects;

namespace KaraokeFilmMaker.Application.Lyrics;

public static class RoleAssignments
{
    public static IReadOnlyList<Guid> Effective(LyricLine line, LyricUnit unit) => unit.RoleIds ?? line.RoleIds;

    public static KaraokeProject Assign(KaraokeProject project, IReadOnlySet<Guid> lineIds,
        IReadOnlySet<Guid> unitIds, IReadOnlyList<Guid> roles, bool inherit = false)
    {
        if (roles.Any(id => project.Roles.All(role => role.Id != id))) throw new InvalidOperationException("角色不存在。");
        var ordered = project.Roles.Where(role => roles.Contains(role.Id)).Select(role => role.Id).ToArray();
        var lines = project.Lyrics.Lines.Select(line => lineIds.Contains(line.Id)
            ? line with { RoleIds = ordered, Units = line.Units.Select(unit => unit with { RoleIds = null }).ToArray() }
            : line with { Units = line.Units.Select(unit => unitIds.Contains(unit.Id)
                ? unit with { RoleIds = inherit ? null : ordered } : unit).ToArray() }).ToArray();
        return project with { Lyrics = project.Lyrics with { Lines = lines }, ModifiedAtUtc = DateTimeOffset.UtcNow };
    }

    public static KaraokeProject Remove(KaraokeProject project, Guid id) => project with
    {
        Roles = project.Roles.Where(role => role.Id != id).ToArray(),
        Lyrics = project.Lyrics with { Lines = project.Lyrics.Lines.Select(line => line with
        {
            RoleIds = line.RoleIds.Where(role => role != id).ToArray(),
            Units = line.Units.Select(unit => unit with { RoleIds = unit.RoleIds?.Where(role => role != id).ToArray() }).ToArray(),
        }).ToArray() },
    };

    // Re-segmentation changes unit identity, but assignments remain attached to overlapping source text.
    public static IReadOnlyList<LyricUnit> Transfer(LyricLine source, IReadOnlyList<LyricUnit> target) =>
        target.Select(unit =>
        {
            var previous = source.Units.Where(old => old.DisplayRanges.Any(a => unit.DisplayRanges.Any(b =>
                a.Start < b.EndExclusive && b.Start < a.EndExclusive))).ToArray();
            if (previous.All(old => old.RoleIds is null)) return unit;
            return unit with { RoleIds = previous.SelectMany(old => Effective(source, old)).Distinct().ToArray() };
        }).ToArray();
}
