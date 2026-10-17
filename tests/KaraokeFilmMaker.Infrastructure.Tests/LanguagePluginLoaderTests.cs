using System.Runtime.Loader;
using KaraokeFilmMaker.Application.Languages;
using KaraokeFilmMaker.Domain.Lyrics;
using KaraokeFilmMaker.Domain.Time;
using KaraokeFilmMaker.Infrastructure.Languages;
using Liraoke.ExampleSpanish;

namespace KaraokeFilmMaker.Infrastructure.Tests;

public sealed class LanguagePluginLoaderTests
{
    [Fact]
    public async Task ExplicitDllLoadSharesHostContractsAndKeepsPluginCodeIsolated()
    {
        using var loader = new LanguagePluginLoader();
        ILanguageAdaptor adaptor = Assert.Single(loader.Load(typeof(SpanishLanguageAdaptor).Assembly.Location));
        Assert.Equal("es", adaptor.LanguageId);
        Assert.NotSame(AssemblyLoadContext.Default, AssemblyLoadContext.GetLoadContext(adaptor.GetType().Assembly));
        Assert.Same(typeof(BaseLanguageAdaptor), adaptor.GetType().BaseType);

        var result = await adaptor.Policy.AnalyzeAsync(new LanguageAnalysisContext("hola corazón",
            new TimeRange(ProjectTime.Zero, ProjectTime.FromSeconds(2))));
        Assert.Equal(new[] { "hola", "corazón" }, result.Units.Select(unit => unit.Reading));
        Assert.Equal(2, result.RecordingCells.Count);
        Assert.Equal(2, result.SubtitleRuns.Count);
        Assert.False(result.NeedsReview);
        Assert.Equal(0, adaptor.Policy.Detect(new("hello", "hello", "hello", null)).Score);
    }

    [Fact]
    public void SameLanguageCannotBeLoadedTwice()
    {
        using var loader = new LanguagePluginLoader();
        string path = typeof(SpanishLanguageAdaptor).Assembly.Location;
        loader.Load(path);
        var exception = Assert.Throws<LanguagePluginLoadException>(() => loader.Load(path));
        Assert.Equal(LanguagePluginError.DuplicateLanguage, exception.Code);
    }

    [Fact]
    public void MissingFileAndNonPluginAssemblyProduceActionableErrors()
    {
        using var loader = new LanguagePluginLoader();
        var missing = Assert.Throws<LanguagePluginLoadException>(() => loader.Load("missing.dll"));
        Assert.Equal(LanguagePluginError.InvalidFile, missing.Code);
        var noAdaptor = Assert.Throws<LanguagePluginLoadException>(() => loader.Load(typeof(LyricLine).Assembly.Location));
        Assert.Equal(LanguagePluginError.NoAdaptor, noAdaptor.Code);
    }

    [Fact]
    public void CorruptDllProducesActionableError()
    {
        string path = Path.Combine(Path.GetTempPath(), $"liraoke-invalid-plugin-{Guid.NewGuid():N}.dll");
        try
        {
            File.WriteAllText(path, "not a managed DLL");
            using var loader = new LanguagePluginLoader();
            var exception = Assert.Throws<LanguagePluginLoadException>(() => loader.Load(path));
            Assert.Equal(LanguagePluginError.InvalidFile, exception.Code);
        }
        finally { File.Delete(path); }
    }

    [Theory]
    [InlineData(2, "es", LyricTextDirection.LeftToRight, LanguagePluginError.IncompatibleApi)]
    [InlineData(1, "", LyricTextDirection.LeftToRight, LanguagePluginError.InvalidMetadata)]
    [InlineData(1, "en", LyricTextDirection.LeftToRight, LanguagePluginError.DuplicateLanguage)]
    [InlineData(1, "EN", LyricTextDirection.LeftToRight, LanguagePluginError.DuplicateLanguage)]
    [InlineData(1, "ar", LyricTextDirection.RightToLeft, LanguagePluginError.UnsupportedCapability)]
    public void MetadataValidationRejectsUnsupportedPackages(int api, string id,
        LyricTextDirection direction, LanguagePluginError error)
    {
        var exception = Assert.Throws<LanguagePluginLoadException>(() =>
            LanguagePluginLoader.ValidateAdaptors([new StubAdaptor(api, id, direction)], ["en"]));
        Assert.Equal(error, exception.Code);
    }

    [Fact]
    public void DisposalPreventsFurtherLoading()
    {
        var loader = new LanguagePluginLoader();
        loader.Dispose();
        Assert.Throws<ObjectDisposedException>(() => loader.Load(typeof(SpanishLanguageAdaptor).Assembly.Location));
    }

    private sealed class StubAdaptor(int api, string id, LyricTextDirection direction) : BaseLanguageAdaptor
    {
        public override int ApiVersion => api;
        public override string LanguageId => id;
        public override string DisplayName => "Test";
        public override LanguageCapabilities Capabilities => new(TextDirection: direction);
        public override BaseLanguagePolicy Policy { get; } = new SpanishLanguagePolicy();
    }
}
