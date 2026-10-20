using System.Security.Cryptography;
using System.Text;
using AtlasDesk.Harness.Tools;

namespace AtlasDesk.Harness.Approvals;

public static class ActionHash
{
    /// <summary>SHA-256 over version, run id, tool name and canonical (sorted, typed) arguments.</summary>
    public static string Compute(string runId, string tool, ToolArguments args)
    {
        var material = $"atlasdesk.action.v1\n{runId}\n{tool}\n{args.ToCanonicalJson()}";
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(material))).ToLowerInvariant();
    }
}

/// <summary>What a human is shown and asked to approve.</summary>
public sealed record ApprovalRequest(string RunId, string Tool, ToolArguments Args, string ActionHash);

/// <summary>The human's answer. ApprovedActionHash is the hash of what they actually reviewed.</summary>
public sealed record ApprovalDecision(bool Approved, string? Approver, string? ApprovedActionHash)
{
    public static ApprovalDecision Deny() => new(false, null, null);
    public static ApprovalDecision ApproveExact(ApprovalRequest request, string approver) => new(true, approver, request.ActionHash);
}

public interface IApprover
{
    Task<ApprovalDecision> ReviewAsync(ApprovalRequest request, CancellationToken ct);
}

public sealed record ApprovalGrant(string Id, string ActionHash, string Approver, DateTimeOffset IssuedAt, DateTimeOffset ExpiresAt);

public enum ApprovalOutcome { Approved, Unknown, ActionMismatch, Expired, AlreadyUsed }

public interface IApprovalStore
{
    void Save(ApprovalGrant grant);
    ApprovalGrant? Get(string id);

    /// <summary>Atomically marks the grant used. Returns false if it was already used.</summary>
    bool TryMarkUsed(string id);
}

public sealed class InMemoryApprovalStore : IApprovalStore
{
    private readonly Dictionary<string, ApprovalGrant> _grants = new();
    private readonly HashSet<string> _used = new();
    private readonly object _gate = new();

    public void Save(ApprovalGrant grant) { lock (_gate) _grants[grant.Id] = grant; }
    public ApprovalGrant? Get(string id) { lock (_gate) return _grants.GetValueOrDefault(id); }
    public bool TryMarkUsed(string id) { lock (_gate) return _used.Add(id); }
}

/// <summary>
/// Issues and consumes approvals. A grant is bound to one action hash, expires, and works once.
/// </summary>
public sealed class ApprovalService(IApprovalStore store, TimeProvider clock, TimeSpan? lifetime = null)
{
    public static readonly TimeSpan DefaultLifetime = TimeSpan.FromMinutes(5);
    private readonly TimeSpan _lifetime = lifetime ?? DefaultLifetime;

    public ApprovalGrant Issue(string actionHash, string approver)
    {
        var now = clock.GetUtcNow();
        var grant = new ApprovalGrant(Guid.NewGuid().ToString("N"), actionHash, approver, now, now + _lifetime);
        store.Save(grant);
        return grant;
    }

    public ApprovalOutcome Consume(string grantId, string actionHash)
    {
        var grant = store.Get(grantId);
        if (grant is null) return ApprovalOutcome.Unknown;
        if (!CryptographicOperations.FixedTimeEquals(Encoding.UTF8.GetBytes(grant.ActionHash), Encoding.UTF8.GetBytes(actionHash)))
            return ApprovalOutcome.ActionMismatch;
        if (clock.GetUtcNow() >= grant.ExpiresAt) return ApprovalOutcome.Expired;
        return store.TryMarkUsed(grantId) ? ApprovalOutcome.Approved : ApprovalOutcome.AlreadyUsed;
    }
}
