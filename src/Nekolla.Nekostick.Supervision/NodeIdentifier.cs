namespace Nekolla.Nekostick.Supervision;

/// <summary>Contains a validated node identifier for local port lease keys.</summary>
public readonly record struct NodeIdentifier
{
    /// <summary>The maximum node identifier length.</summary>
    public const int MaxLength = 128;

    /// <summary>Creates a validated node identifier.</summary>
    /// <param name="value">The stable node identifier.</param>
    public NodeIdentifier(string value)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Length > MaxLength ||
            value.Any(char.IsControl))
        {
            throw new ArgumentException("The node identifier is invalid.", nameof(value));
        }

        Value = value;
    }

    /// <summary>Gets the validated identifier text.</summary>
    public string Value { get; }

    /// <summary>Gets whether this value is a valid non-default node identifier.</summary>
    public bool IsValid => !string.IsNullOrWhiteSpace(Value) && Value.Length <= MaxLength &&
        !Value.Any(char.IsControl);

    /// <summary>Returns the validated identifier text.</summary>
    /// <returns>The identifier text.</returns>
    public override string ToString() => Value ?? string.Empty;
}
