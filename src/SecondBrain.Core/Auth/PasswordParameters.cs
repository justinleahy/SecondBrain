namespace SecondBrain.Core.Auth;

public sealed record PasswordParameters(int Version = 1, int MemoryKiB = 65536, int Iterations = 3, int Lanes = 1);
