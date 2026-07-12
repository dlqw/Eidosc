using System.Runtime.CompilerServices;

namespace Eidosc.Parsing.Lexer;

/// <summary>
/// Trie-based keyword matcher providing O(L) longest-keyword match
/// where L is the length of the matched keyword.
/// Replaces the O(K×L) sequential MatchSymbol approach where K is
/// the number of keywords sharing the same first character.
/// </summary>
public sealed class KeywordTrie
{
    private sealed class Node
    {
        public Dictionary<char, Node>? Children;
        public KeywordEntry? Entry; // non-null when this node completes a keyword
    }

    /// <summary>
    /// Metadata for a matched keyword.
    /// </summary>
    public readonly record struct KeywordEntry(int TerminalId, SyntaxKind Kind, string Text, bool IsKeyword);

    private readonly Node _root = new();

    /// <summary>
    /// Add a keyword to the trie.
    /// </summary>
    public void Add(string text, int terminalId, SyntaxKind kind, bool isKeyword)
    {
        var node = _root;
        foreach (var c in text)
        {
            node.Children ??= new Dictionary<char, Node>();
            if (!node.Children.TryGetValue(c, out var child))
            {
                child = new Node();
                node.Children[c] = child;
            }
            node = child;
        }
        node.Entry = new KeywordEntry(terminalId, kind, text, isKeyword);
    }

    /// <summary>
    /// Walk the trie to find the longest matching keyword at the start of <paramref name="input"/>.
    /// Returns the matched entry and its character length, or null if no keyword matches.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public (KeywordEntry Entry, int Length)? TryMatchLongest(ReadOnlySpan<char> input)
    {
        var node = _root;
        (KeywordEntry Entry, int Length)? best = null;

        for (int i = 0; i < input.Length; i++)
        {
            if (node.Children == null || !node.Children.TryGetValue(input[i], out var next))
                break;
            node = next;
            if (node.Entry != null)
                best = (node.Entry.Value, i + 1);
        }

        return best;
    }

    /// <summary>
    /// Build a KeywordTrie from a terminal list.
    /// Only terminals with <see cref="TerminalFlag.IsKeyword"/> are included.
    /// </summary>
    public static KeywordTrie BuildFromTerminals(IList<Terminal> terminals)
    {
        var trie = new KeywordTrie();
        foreach (var terminal in terminals)
        {
            if (!terminal.Flags.HasFlag(TerminalFlag.IsKeyword))
                continue;

            var text = terminal.DebugName;
            SyntaxKindHelper.TryFromText(text, out var kind);
            trie.Add(text, terminal.Id, kind, isKeyword: true);
        }
        return trie;
    }
}
