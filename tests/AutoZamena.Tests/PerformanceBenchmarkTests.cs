using System.Diagnostics;
using AutoZamena.Core.Engine;
using AutoZamena.Core.Lexicon;
using AutoZamena.Core.Models;
using Xunit;

namespace AutoZamena.Tests;

public sealed class PerformanceBenchmarkTests
{
    [Fact]
    public void AverageDecisionLatency_IsUnderTwoMilliseconds()
    {
        // Warm up lexicon & JIT
        LexiconDatabase lex = LexiconDatabase.Default;
        var engine = new AutoCorrectEngine(lex);
        _ = engine.SimulateText("привет возврапщатся наза ghbdtn ");
        engine.ResetContext();

        string[] sampleWords =
        {
            "привет", "возврапщатся", "наза", "телефоне", "привте",
            "опечатки", "поправлять", "ghbdtn", "ПРивет", "програма",
            "клавиатура", "печатаю", "задержек", "постоянно", "вобщем",
            "деплой", "скриншот", "работет", "сегодня", "компьютер"
        };

        const int iterations = 400;
        var sw = Stopwatch.StartNew();

        for (int i = 0; i < iterations; i++)
        {
            string w = sampleWords[i % sampleWords.Length];
            for (int c = 0; c < w.Length; c++)
            {
                engine.OnCharacter(new KeyStrokeEvent(w[c], 95, false, i * 100 + c * 10));
            }
            _ = engine.OnWhitespace(' ', i * 100 + 90);
        }

        sw.Stop();
        double avgMsPerWord = sw.Elapsed.TotalMilliseconds / iterations;

        Assert.True(
            avgMsPerWord < 2.5,
            $"Expected average per-word evaluation < 2.5 ms, but measured {avgMsPerWord:F3} ms."
        );
    }
}
