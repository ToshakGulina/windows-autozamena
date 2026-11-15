using AutoZamena.App.Config;
using AutoZamena.Core.Lexicon;
using Xunit;

namespace AutoZamena.Tests;

public class SwiftKeyVocabularyTests
{
    [Theory]
    [InlineData("привет", true)]
    [InlineData("you're", true)]
    [InlineData("кое-как", true)]
    [InlineData("# Словарь SwiftKey", false)]
    [InlineData("00 10", false)]
    [InlineData("0.25", false)]
    [InlineData("a", false)]
    [InlineData("word\u0002", false)]
    [InlineData("-word", false)]
    [InlineData("word-", false)]
    public void OnlyIndividualWordsBecomeExclusions(string input, bool expected) =>
        Assert.Equal(expected, SwiftKeyVocabulary.IsWord(input));

    [Fact]
    public void SwiftKeyTextImportIsAdditiveDeduplicatedAndDoesNotReplaceSnippets()
    {
        string path = Path.GetTempFileName();
        try
        {
            File.WriteAllText(path, "# Словарь SwiftKey\n# Читайте предупреждение\n\u0002\nпривет\nHello\nпривет\nHELLO\n00 10\nкое-как\n");
            var dictionary = new UserDictionary();
            dictionary.SetCustomReplacement("brb", "скоро вернусь");
            Assert.Equal(3, SwiftKeyVocabulary.Import(path, dictionary));
            Assert.Equal(0, SwiftKeyVocabulary.Import(path, dictionary));
            Assert.True(dictionary.IsIgnored("ПРИВЕТ"));
            Assert.True(dictionary.IsIgnored("hello"));
            Assert.True(dictionary.IsIgnored("кое-как"));
            Assert.False(dictionary.IsIgnored("00 10"));
            Assert.True(dictionary.TryGetCustomReplacement("brb", out string expansion));
            Assert.Equal("скоро вернусь", expansion);
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public void ExportIsReadableTextButExplicitlyDoesNotClaimSwiftKeyModelImport()
    {
        string path = Path.GetTempFileName();
        try
        {
            var source = new UserDictionary();
            source.AddIgnoredWord("привет");
            source.AddIgnoredWord("hello");
            source.AddIgnoredWord("file name");
            SwiftKeyVocabulary.Export(path, source);
            string text = File.ReadAllText(path);
            Assert.Contains("SwiftKey не импортирует", text);
            Assert.DoesNotContain("file name", text);
            var destination = new UserDictionary();
            Assert.Equal(2, SwiftKeyVocabulary.Import(path, destination));
        }
        finally { File.Delete(path); }
    }
}
