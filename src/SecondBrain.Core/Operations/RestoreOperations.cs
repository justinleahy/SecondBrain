namespace SecondBrain.Core.Operations;

/// <summary>A retained, exclusive lease for the root whose state will be replaced.</summary>
public interface IRestoreLease
{
    string DataRoot { get; }
    bool IsHeld { get; }
}

public static class RestoreState
{
    public const string PendingFileName = ".restore-pending.json";
}
