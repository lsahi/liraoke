using KaraokeFilmMaker.Application.Preferences;
using KaraokeFilmMaker.Domain.Projects;
using KaraokeFilmMaker.Infrastructure.Persistence;

namespace KaraokeFilmMaker.Infrastructure.Tests;

public sealed class AiPreferenceTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "Liraoke.AiTests", Guid.NewGuid().ToString("N"));
    [Fact]
    public void CredentialsRoundTripEncryptedAndStyleUpdatesKeepProviderData()
    {
        var store = new UserPreferenceStore(_root); Guid id = Guid.NewGuid();
        var secrets = new AiCredentials("test-key-unique-123", "test-ak-unique-456", "test-sk-unique-789");
        store.SaveAiProvider(id, " Relay ", false, "https://example.invalid/v1", secrets);
        store.SaveStyle("Keep", KaraokeProject.Create("Test", DateTimeOffset.UnixEpoch).SubtitleStyle);
        var restored = new UserPreferenceStore(_root).Load();
        var provider = Assert.Single(restored.AiProviders);
        Assert.Equal("Relay", provider.ProviderName); Assert.False(provider.IsOfficial);
        Assert.Equal("https://example.invalid/v1", provider.ApiPath);
        Assert.Equal(secrets, UserPreferenceStore.ReadAiCredentials(provider));
        foreach (string file in new[] { store.FilePath, store.FilePath + ".bak" })
        {
            var json = File.ReadAllText(file);
            Assert.DoesNotContain(secrets.APIKey!, json); Assert.DoesNotContain(secrets.AK!, json); Assert.DoesNotContain(secrets.SK!, json);
        }
        store.SaveAiProvider(id, "Official", true, null, new());
        Assert.Equal(new AiCredentials(), UserPreferenceStore.ReadAiCredentials(Assert.Single(store.Load().AiProviders)));
        Assert.Single(store.DeleteAiProvider(id).SubtitleStyles); Assert.Empty(store.Load().AiProviders);
    }
    [Fact]
    public void RequiredFieldsDuplicatesAndInvalidEndpointsDoNotOverwritePreferences()
    {
        var store = new UserPreferenceStore(_root);
        store.SaveAiProvider(Guid.NewGuid(), "Provider", true, null, new());
        var before = File.ReadAllText(store.FilePath);
        Assert.Throws<InvalidDataException>(() => store.SaveAiProvider(Guid.NewGuid(), " ", true, null, new()));
        Assert.Throws<InvalidDataException>(() => store.SaveAiProvider(Guid.NewGuid(), "Other", null, null, new()));
        Assert.Throws<InvalidDataException>(() => store.SaveAiProvider(Guid.NewGuid(), "provider", false, null, new()));
        Assert.Throws<InvalidDataException>(() => store.SaveAiProvider(Guid.NewGuid(), "Other", true, "file:///example", new()));
        Assert.Equal(before, File.ReadAllText(store.FilePath));
    }
    [Fact]
    public void OldPreferencesAndFutureFieldsArePreserved()
    {
        Directory.CreateDirectory(_root); var store = new UserPreferenceStore(_root);
        File.WriteAllText(store.FilePath, """{"schemaVersion":1,"subtitleStyles":[],"roles":[],"apiConnections":{"old":{"endpoint":"https://example.invalid","custom":42}},"future":{"enabled":true}}""");
        Assert.Empty(store.Load().AiProviders);
        store.SaveAiProvider(Guid.NewGuid(), "New", false, "http://localhost:8080/v1", new(AK: "ak-only"));
        var result = store.Load();
        Assert.Equal(42, result.ApiConnections["old"].AdditionalSettings!["custom"].GetInt32());
        Assert.True(result.AdditionalSettings!["future"].GetProperty("enabled").GetBoolean());
    }
    [Fact]
    public void InvalidEncryptedPayloadDoesNotExposeContentInError()
    {
        var bad = new AiProviderPreference(Guid.NewGuid(), "Broken", true, ProtectedCredentials: "test-secret-invalid");
        var error = Assert.Throws<InvalidDataException>(() => UserPreferenceStore.ReadAiCredentials(bad));
        Assert.DoesNotContain("test-secret-invalid", error.Message);
    }
    [Fact]
    public void PersistedProvidersMustIncludeExplicitOfficialChoice()
    {
        Directory.CreateDirectory(_root); var store = new UserPreferenceStore(_root);
        string invalid = """{"schemaVersion":1,"aiProviders":[{"id":"d16676a5-4aab-448b-91b0-6430989252cf","providerName":"Missing choice"}]}""";
        File.WriteAllText(store.FilePath, invalid);
        Assert.Throws<InvalidDataException>(() => store.Load());
        Assert.Throws<InvalidDataException>(() => store.SaveAiProvider(Guid.NewGuid(), "Other", true, null, new()));
        Assert.Equal(invalid, File.ReadAllText(store.FilePath));
    }
    public void Dispose() { if (Directory.Exists(_root)) Directory.Delete(_root, true); }
}
