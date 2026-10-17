using System.Windows.Controls;
using KaraokeFilmMaker.Domain.Lyrics;

namespace KaraokeFilmMaker.App.Tests;

public sealed class LanguageRegionEditorTests
{
    [Fact]
    public async Task SelectionProducesExplicitLanguageAndOptionalReadingWithoutChangingSource() => await Sta(() =>
    {
        var window = new LanguageRegionEditorWindow("君と I love you", [new("ja", "日语", true), new("en", "英语")], []);
        try
        {
            Assert.False(window.TryCreateEdit(false, out _, out _));
            var input = (TextBox)window.FindName("RegionText");
            input.Select(0, 2);
            ((ComboBox)window.FindName("RegionLanguage")).SelectedValue = "ja";
            ((TextBox)window.FindName("RegionReading")).Text = "きみと";
            Assert.True(window.TryCreateEdit(false, out var edit, out var error), error);
            Assert.Equal("ja", edit!.LanguageId); Assert.Equal("きみと", edit.Reading);
            Assert.Equal(new TextRange(0, 2), edit.Range); Assert.Equal("君と I love you", input.Text);
            Assert.Null(window.RequestedEdit); // Validation alone never commits a dialog.
            input.Select(3, 10);
            Assert.Equal("", ((TextBox)window.FindName("RegionReading")).Text);
            Assert.True(window.TryCreateEdit(true, out edit, out error), error);
            Assert.Null(edit!.LanguageId); Assert.Null(edit.Reading); Assert.Equal(new TextRange(3, 10), edit.Range);
        }
        finally { window.Close(); }
    });

    [Fact]
    public async Task SelectionCannotSplitSurrogatesOrCombiningCharacters() => await Sta(() =>
    {
        var window = new LanguageRegionEditorWindow("A\u0301😀 love", [new("en", "英语")], []);
        try
        {
            var input = (TextBox)window.FindName("RegionText");
            input.Select(0, 1);
            // WPF can itself expand a partial selection to the complete grapheme.
            if (window.TryCreateEdit(false, out var combined, out _)) { Assert.Equal(new TextRange(0, 2), combined!.Range); }
            input.Select(2, 1);
            // WPF may expand a surrogate selection to a full character; either way the result must be whole.
            if (window.TryCreateEdit(false, out var edit, out _)) { Assert.Equal(new TextRange(2, 2), edit!.Range); }
            input.Select(0, 2);
            Assert.True(window.TryCreateEdit(false, out var valid, out _)); Assert.Equal(2, valid!.Range.Length);
        }
        finally { window.Close(); }
    });

    [Fact]
    public async Task NewWindowDefaultsToOfflineAutomaticLanguage() => await Sta(() =>
    {
        var window = new MainWindow();
        try
        {
            var combo = (ComboBox)window.FindName("LyricLanguageCombo");
            Assert.Equal("Auto", ((ComboBoxItem)combo.SelectedItem).Tag);
            Assert.Contains("离线", ((ComboBoxItem)combo.SelectedItem).Content.ToString());
        }
        finally { window.Close(); }
    });

    private static async Task Sta(Action action)
    {
        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var thread = new Thread(() =>
        {
            try { action(); completion.TrySetResult(); }
            catch (Exception exception) { completion.TrySetException(exception); }
        }) { IsBackground = true };
        thread.SetApartmentState(ApartmentState.STA); thread.Start();
        await completion.Task.WaitAsync(TimeSpan.FromSeconds(20));
    }
}
