using KaraokeFilmMaker.Application.Ai;
using KaraokeFilmMaker.Application.Preferences;
using KaraokeFilmMaker.Infrastructure.Ai;
using KaraokeFilmMaker.Infrastructure.Persistence;

namespace KaraokeFilmMaker.Infrastructure.Tests;

public sealed class AiRuntimeTests
{
    [Fact]
    public void ModelAndProtocolRoundTripWithoutChangingOtherPreferences()
    {
        string root = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
        try
        {
            var store = new UserPreferenceStore(root);
            var preset = AiModelCatalog.Presets[0];
            store.SaveAiProvider(Guid.NewGuid(), "模型", true, preset.Endpoint, new("dummy-secret"), preset.Model, preset.ProviderId, preset.Protocol);
            var provider = Assert.Single(store.Load().AiProviders);
            Assert.Equal(preset.Model, provider.Model); Assert.Equal(preset.Protocol, provider.Protocol);
            Assert.DoesNotContain("dummy-secret", File.ReadAllText(store.FilePath));
            Assert.Equal("dummy-secret", UserPreferenceStore.ReadAiCredentials(provider).APIKey);
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
    }
    [Theory]
    [InlineData("http://external.invalid/chat/completions")]
    [InlineData("https://user:pass@example.invalid/v1")]
    [InlineData("file:///tmp/model")]
    public void RejectsCredentialLeakEndpoints(string endpoint)
    {
        Assert.Throws<InvalidOperationException>(() => AiModelCatalog.ValidateConnection(new(Guid.NewGuid(), "x", false, endpoint, Model:"test"), new("secret")));
    }
    [Fact]
    public async Task MissingRuntimeHasActionableError()
    {
        var service = new PiCordisChatService(Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N")));
        var error = await Assert.ThrowsAsync<InvalidOperationException>(async () =>
        { await foreach (var _ in service.StreamAsync(new(Guid.NewGuid(), "x", false, "https://test.invalid/chat/completions", Model:"test"), new("secret"), [], "hi")) { } });
        Assert.Contains("bootstrap-ai", error.Message);
    }
}
