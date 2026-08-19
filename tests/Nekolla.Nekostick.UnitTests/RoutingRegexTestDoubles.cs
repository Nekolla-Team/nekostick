using System.Collections.Immutable;
using System.Text.RegularExpressions;
using Nekolla.Nekostick.Routing;

namespace Nekolla.Nekostick.UnitTests;

internal sealed class DeterministicRegexEvaluator : IRouteRegexEvaluator
{
    private readonly ImmutableDictionary<Guid, RouteRegexEvaluationOutcome> _outcomes;
    private readonly Func<Guid, Regex, string, RouteRegexEvaluationOutcome> _productionDefault;

    internal DeterministicRegexEvaluator(
        ImmutableDictionary<Guid, RouteRegexEvaluationOutcome> outcomes,
        Func<Guid, Regex, string, RouteRegexEvaluationOutcome>? productionDefault = null)
    {
        _outcomes = outcomes ?? throw new ArgumentNullException(nameof(outcomes));
        _productionDefault = productionDefault ?? EvaluateWithProductionDefault;
    }

    public RouteRegexEvaluationOutcome Evaluate(Guid routeId, Regex regex, string normalizedPath) =>
        _outcomes.TryGetValue(routeId, out var outcome)
            ? outcome
            : _productionDefault(routeId, regex, normalizedPath);
    public RouteRegexEvaluation EvaluateMatch(Guid routeId, Regex regex, string normalizedPath)
    {
        if (_outcomes.TryGetValue(routeId, out var outcome))
        {
            return RouteRegexEvaluation.FromOutcome(outcome);
        }

        try
        {
            var match = regex.Match(normalizedPath);
            return match.Success
                ? RouteRegexEvaluation.Matched(match)
                : RouteRegexEvaluation.NotMatched();
        }
        catch (RegexMatchTimeoutException)
        {
            return RouteRegexEvaluation.TimedOut();
        }
    }

    private static RouteRegexEvaluationOutcome EvaluateWithProductionDefault(
        Guid _,
        Regex regex,
        string normalizedPath)
    {
        try
        {
            return regex.IsMatch(normalizedPath)
                ? RouteRegexEvaluationOutcome.Matched
                : RouteRegexEvaluationOutcome.NotMatched;
        }
        catch (RegexMatchTimeoutException)
        {
            return RouteRegexEvaluationOutcome.TimedOut;
        }
    }
}
