using System.Text.RegularExpressions;

namespace Nekolla.Nekostick.Routing;

internal enum RouteRegexEvaluationOutcome
{
    Matched,
    NotMatched,
    TimedOut
}

internal readonly struct RouteRegexEvaluation
{
    private RouteRegexEvaluation(RouteRegexEvaluationOutcome outcome, Match? match)
    {
        Outcome = outcome;
        Match = match;
    }

    internal RouteRegexEvaluationOutcome Outcome { get; }
    internal Match? Match { get; }

    internal static RouteRegexEvaluation FromOutcome(RouteRegexEvaluationOutcome outcome) =>
        new(outcome, null);

    internal static RouteRegexEvaluation Matched(Match match) =>
        new(RouteRegexEvaluationOutcome.Matched, match);

    internal static RouteRegexEvaluation NotMatched() =>
        new(RouteRegexEvaluationOutcome.NotMatched, null);

    internal static RouteRegexEvaluation TimedOut() =>
        new(RouteRegexEvaluationOutcome.TimedOut, null);
}

internal interface IRouteRegexEvaluator
{
    RouteRegexEvaluationOutcome Evaluate(Guid routeId, Regex regex, string normalizedPath);

    RouteRegexEvaluation EvaluateMatch(Guid routeId, Regex regex, string normalizedPath) =>
        RouteRegexEvaluation.FromOutcome(Evaluate(routeId, regex, normalizedPath));
}

internal sealed class DotNetRouteRegexEvaluator : IRouteRegexEvaluator
{
    internal static readonly DotNetRouteRegexEvaluator Instance = new();

    private DotNetRouteRegexEvaluator()
    {
    }

    public RouteRegexEvaluationOutcome Evaluate(Guid routeId, Regex regex, string normalizedPath)
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

    public RouteRegexEvaluation EvaluateMatch(Guid routeId, Regex regex, string normalizedPath)
    {
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
}
