namespace SuperSemantics.Vsl;

/// <summary>Root of every exception raised by this package.</summary>
public class VslException : Exception
{
    /// <summary>Creates the exception with a message.</summary>
    public VslException(string message) : base(message) { }

    /// <summary>Creates the exception with a message and an inner exception.</summary>
    public VslException(string message, Exception innerException) : base(message, innerException) { }
}

/// <summary>
/// Raised when a gate denies an action: a PreNode's Gamma estimate is insufficient, a Fallback is
/// exhausted, or a Terminal State is entered.
/// </summary>
/// <remarks>
/// The specification describes this as an exception that "cannot be silently caught". That is a
/// convention, not something C# can enforce: nothing stops <c>catch (Exception) { }</c>. What this
/// type does guarantee is narrower and real: it is a distinctive, purpose-built type that is never
/// reused for anything else, so a blanket handler that swallows it is conspicuous in code review.
/// </remarks>
public class AutomationDeniedException : VslException
{
    /// <summary>Creates the exception.</summary>
    public AutomationDeniedException(string reason, string identityKey, string? instanceId = null, double? timestamp = null)
        : base($"{reason} (identity_key='{identityKey}')")
    {
        Reason = reason;
        IdentityKey = identityKey;
        InstanceId = instanceId;
        Timestamp = timestamp ?? VslClock.Now();
    }

    /// <summary>Why the action was denied.</summary>
    public string Reason { get; }

    /// <summary>The identity the denied action belonged to.</summary>
    public string IdentityKey { get; }

    /// <summary>The instance the denied action belonged to, if known.</summary>
    public string? InstanceId { get; }

    /// <summary>When the denial happened, in seconds since the Unix epoch.</summary>
    public double Timestamp { get; }
}

/// <summary>
/// Raised when an Invariant's rule returns false. A subclass of
/// <see cref="AutomationDeniedException"/>, so a handler for denials still catches it, while a caller
/// that must tell "straight to Terminal" apart from an ordinary PreNode denial can catch this type.
/// </summary>
public class InvariantViolationException : AutomationDeniedException
{
    /// <summary>Creates the exception.</summary>
    public InvariantViolationException(
        string reason,
        string identityKey,
        string invariantName = "",
        string? terminalStateName = null,
        string? instanceId = null,
        double? timestamp = null)
        : base(reason, identityKey, instanceId, timestamp)
    {
        InvariantName = invariantName;
        TerminalStateName = terminalStateName;
    }

    /// <summary>The violated Invariant's name.</summary>
    public string InvariantName { get; }

    /// <summary>
    /// The Terminal State this violation entered, when the Invariant named one in
    /// <c>OnViolation</c>; null otherwise. Lets a caller tell a plain violation from one that halted
    /// the system without parsing <see cref="AutomationDeniedException.Reason"/>.
    /// </summary>
    public string? TerminalStateName { get; }
}

/// <summary>
/// Raised by <see cref="VerbaLedger.Write"/> when appending would extend a chain the ledger can already
/// tell is broken. <see cref="VerbaLedger.VerifyIntegrity"/> never raises; it returns false.
/// </summary>
public class LedgerIntegrityException : VslException
{
    /// <summary>Creates the exception.</summary>
    public LedgerIntegrityException(string message) : base(message) { }
}

/// <summary>
/// Raised only for structural catalog-loading errors (malformed JSON, a missing required key). The
/// catalog's known data-quality findings are returned by <c>DriftCatalog.Validate()</c>, never raised.
/// </summary>
public class CatalogValidationException : VslException
{
    /// <summary>Creates the exception.</summary>
    public CatalogValidationException(string message, Exception? innerException = null)
        : base(message, innerException ?? new InvalidOperationException(message)) { }
}

/// <summary>
/// Raised by the conformance suite only when an adapter is broken badly enough that the suite cannot
/// run (for example, a compile method returning null). Ordinary conformance failures are returned as
/// strings.
/// </summary>
public class ConformanceException : VslException
{
    /// <summary>Creates the exception.</summary>
    public ConformanceException(string message) : base(message) { }
}
