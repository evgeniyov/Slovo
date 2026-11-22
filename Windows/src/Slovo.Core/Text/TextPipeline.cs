using Slovo.Core.Model;
using Slovo.Core.Platform;

namespace Slovo.Core.Text;

/// <summary>
/// One step of the text pipeline. A stage returns the result with its changes applied, so
/// adding a stage (for example a local LLM rewrite) needs no change to the others.
/// </summary>
public interface ITextStage
{
    TextPipelineResult Apply(TextPipelineResult result, TextPipelineContext context);
}

/// <summary>
/// Turns an engine transcript into the text Slovo inserts (port of the macOS
/// <c>TextPipeline</c>). Pure and synchronous; safe to use from any thread when its lexicon is.
/// </summary>
public sealed class TextPipeline : ITextProcessor
{
    public IReadOnlyList<ITextStage> Stages { get; }

    /// <summary>The standard stages asking <paramref name="lexicon"/> which words are real (the system spell checker in the app).</summary>
    public TextPipeline(IWordLexicon lexicon) : this(StandardStages(lexicon))
    {
    }

    public TextPipeline(IReadOnlyList<ITextStage> stages)
    {
        Stages = stages;
    }

    /// <summary>Snippets, dictionary replacements and terms, auto cleanup, style — in that order.</summary>
    public static IReadOnlyList<ITextStage> StandardStages(IWordLexicon lexicon) =>
        [new SnippetExpander(), new DictionaryCorrector(lexicon), new FillerCleaner(), new StyleFormatter(lexicon)];

    public TextPipelineResult Process(string raw, TextPipelineContext context)
    {
        var result = new TextPipelineResult(SwiftString.Canonical(raw));
        foreach (var stage in Stages)
        {
            result = stage.Apply(result, context);
        }
        return result;
    }
}
