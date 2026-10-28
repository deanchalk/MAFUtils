namespace MAFUtils.ToolResults;

/// <summary>Immutable stored text with a line index. Lines longer than <see cref="MaxLineCharacters"/> are wrapped
/// into several lines so every read can return at least one whole line within its character budget. The original
/// text is kept unchanged; lines are slices of it without their line breaks.</summary>
internal sealed class StoredText
{
    public const int MaxLineCharacters = 1_000;
    private readonly Range[] lines;

    private StoredText(string text, Range[] lines) => (Text, this.lines) = (text, lines);

    public string Text { get; }
    public int TotalCharacters => Text.Length;
    public int LineCount => lines.Length;
    public ReadOnlySpan<char> Line(int index) => Text.AsSpan(lines[index]);

    public static StoredText Create(string text)
    {
        List<Range> lines = [];
        foreach (var range in text.AsSpan().Split('\n'))
        {
            var (start, length) = range.GetOffsetAndLength(text.Length);
            if (length > 0 && text[start + length - 1] == '\r') length--;
            while (length > MaxLineCharacters)
            {
                var cut = Boundary(text.AsSpan(start, length), MaxLineCharacters);
                lines.Add(new(start, start + cut));
                (start, length) = (start + cut, length - cut);
            }
            lines.Add(new(start, start + length));
        }
        return new(text, [.. lines]);
    }

    /// <summary>Lines <paramref name="start"/> to <paramref name="end"/> (exclusive), separated by <c>\n</c>.</summary>
    public string Join(int start, int end) => string.Join('\n', Enumerable.Range(start, end - start).Select(i => Text[lines[i]]));

    /// <summary>The longest prefix of whole lines within <paramref name="maxCharacters"/>, or a cut first line.</summary>
    public string Preview(int maxCharacters)
    {
        var length = 0;
        var count = 0;
        while (count < LineCount)
        {
            var next = length + (count > 0 ? 1 : 0) + Line(count).Length;
            if (next > maxCharacters) break;
            (length, count) = (next, count + 1);
        }
        if (count > 0 || LineCount == 0) return Join(0, count);
        return Line(0)[..Boundary(Line(0), maxCharacters)].ToString();
    }

    /// <summary>Largest cut position at or before <paramref name="index"/> that does not split a surrogate pair.</summary>
    public static int Boundary(ReadOnlySpan<char> text, int index)
    {
        if (index >= text.Length) return text.Length;
        return index > 0 && char.IsHighSurrogate(text[index - 1]) && char.IsLowSurrogate(text[index]) ? index - 1 : index;
    }
}
