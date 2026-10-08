namespace SecondBrain.Server.Limits;

/// <summary>The independently bounded work classes of spec §15.8.</summary>
public enum AdmissionResource
{
    None,
    Upload,
    Search,
    Turn,
    Stream,
    Circuit,
}

/// <summary>Endpoint metadata describing capacity reserved before its handler runs.</summary>
public sealed record AdmissionPolicy(
    AdmissionResource Resource = AdmissionResource.None,
    int QueuedJobs = 0,
    long AdmittedBytes = 0,
    bool Ingestion = false,
    bool BytesFromContentLength = false);

/// <summary>Observable active reservations; no rejected request contributes to these counts.</summary>
public sealed record AdmissionSnapshot(int QueuedJobs, long AdmittedBytes, int ActiveReservations);

public sealed record AdmissionDecision(IDisposable? Reservation, int StatusCode, int RetryAfterSeconds, string? Detail)
{
    public bool Accepted => Reservation is not null;
}

/// <summary>Atomic per-credential and global admission, also usable by future background dispatchers.</summary>
public interface IAdmissionController
{
    AdmissionDecision TryReserve(string credentialId, AdmissionPolicy policy);
    AdmissionSnapshot Snapshot { get; }
}

public interface IDiskCapacity
{
    long? AvailableBytes(string path);
}
