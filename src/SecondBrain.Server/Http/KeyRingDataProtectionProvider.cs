using Microsoft.AspNetCore.DataProtection;
using SecondBrain.Core.Security;

namespace SecondBrain.Server.Http;

/// <summary>Framework envelopes use the persistent ring without opening it on anonymous liveness requests.</summary>
public sealed class KeyRingDataProtectionProvider(IServiceProvider services) : IDataProtectionProvider
{
    public IDataProtector CreateProtector(string purpose) => new Protector(services, ["secondbrain.antiforgery", purpose]);

    private sealed class Protector(IServiceProvider services, string[] purposes) : IDataProtector
    {
        public IDataProtector CreateProtector(string purpose) => new Protector(services, [.. purposes, purpose]);
        public byte[] Protect(byte[] plaintext) => Resolve().Protect(plaintext);
        public byte[] Unprotect(byte[] protectedData) => Resolve().Unprotect(protectedData);

        private IDataProtector Resolve()
        {
            IDataProtectionProvider provider = services.GetRequiredService<IKeyRing>().DataProtectionProvider;
            IDataProtector? protector = null;
            foreach (var purpose in purposes)
            {
                protector = provider.CreateProtector(purpose);
                provider = protector;
            }
            return protector!;
        }
    }
}
