using System.Collections.Immutable;

namespace Nekolla.Nekostick.Routing;

internal sealed class PrefixTrieBuilder
{
    private readonly PrefixTrieBuilderNode _root = new();

    internal void Add(string key, CompiledRoute route)
    {
        var node = _root;
        foreach (var character in key)
        {
            if (!node.Children.TryGetValue(character, out var child))
            {
                child = new PrefixTrieBuilderNode();
                node.Children.Add(character, child);
            }

            node = child;
        }

        node.Routes.Add(route);
    }

    internal PrefixTrie Freeze() => new(FreezeNode(_root, ImmutableArray<CompiledRoute>.Empty));

    private static PrefixTrieNode FreezeNode(
        PrefixTrieBuilderNode source,
        ImmutableArray<CompiledRoute> ancestorCandidates)
    {
        var candidates = new List<CompiledRoute>(ancestorCandidates.Length + source.Routes.Count);
        candidates.AddRange(ancestorCandidates);
        candidates.AddRange(source.Routes);
        candidates.Sort(CompiledRouteComparer.Instance);
        var frozenCandidates = ImmutableArray.CreateRange(candidates);

        return new PrefixTrieNode(
            source.Children.ToImmutableDictionary(
                pair => pair.Key,
                pair => FreezeNode(pair.Value, frozenCandidates)),
            frozenCandidates);
    }
}

internal sealed class PrefixTrieBuilderNode
{
    internal Dictionary<char, PrefixTrieBuilderNode> Children { get; } = new();
    internal List<CompiledRoute> Routes { get; } = new();
}

internal sealed class PrefixTrie
{
    private readonly PrefixTrieNode _root;

    internal PrefixTrie(PrefixTrieNode root) => _root = root;

    internal ImmutableArray<CompiledRoute> GetCandidates(string path)
    {
        var node = _root;

        foreach (var character in path)
        {
            if (!node.Children.TryGetValue(character, out var child))
            {
                break;
            }

            node = child;
        }

        return node.Candidates;
    }
}

internal sealed class PrefixTrieNode
{
    internal PrefixTrieNode(
        ImmutableDictionary<char, PrefixTrieNode> children,
        ImmutableArray<CompiledRoute> candidates)
    {
        Children = children;
        Candidates = candidates;
    }

    internal ImmutableDictionary<char, PrefixTrieNode> Children { get; }
    internal ImmutableArray<CompiledRoute> Candidates { get; }
}
