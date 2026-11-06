using System;
using System.Collections.Generic;
using System.Threading;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.DataProtection.KeyManagement;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Wasp.AspNetCore;

/// <summary>
/// No-op data-protection provider for canister apps. The real
/// <c>XmlKeyManager</c> + <c>KeyRingBasedDataProtector</c> pipeline:
///   1. cannot AOT-compile clean on wasm32-wasi (multiple framework methods
///      get trimmed; <see cref="KeyManagementOptions"/>..ctor field
///      initializers reference <c>TimeSpan.FromMilliseconds(long)</c> which
///      ILC drops);
///   2. is pointless on a canister anyway — there's no shared secret to
///      protect and per-replica keys would diverge.
///
/// Wire it by calling <see cref="UseIcDataProtection"/> on the service
/// collection BEFORE any framework call that transitively registers
/// data protection (e.g. <c>AddRazorComponents</c>, <c>AddAuthentication</c>).
/// The framework's <c>TryAdd</c> calls then see our registration and skip
/// <c>XmlKeyManager</c>.
/// </summary>
public sealed class NoOpDataProtectionProvider : IDataProtectionProvider, IDataProtector
{
    public static readonly NoOpDataProtectionProvider Instance = new();

    public IDataProtector CreateProtector(string purpose) => this;

    public byte[] Protect(byte[] plaintext) => plaintext;

    public byte[] Unprotect(byte[] protectedData) => protectedData;
}

internal sealed class NoOpKeyManager : IKeyManager
{
    public IKey CreateNewKey(DateTimeOffset activationDate, DateTimeOffset expirationDate)
        => throw new NotSupportedException("No-op key manager — canister apps must not use DataProtection.");

    public IReadOnlyCollection<IKey> GetAllKeys() => Array.Empty<IKey>();

    public CancellationToken GetCacheExpirationToken() => CancellationToken.None;

    public void RevokeAllKeys(DateTimeOffset revocationDate, string? reason = null) { }

    public void RevokeKey(Guid keyId, string? reason = null) { }

    public bool DeleteKeys(Func<IKey, bool> shouldDelete) => false;
}

public static class DataProtectionExtensions
{
    /// <summary>
    /// Registers no-op <see cref="IDataProtectionProvider"/> and
    /// <see cref="IKeyManager"/>. Must be called BEFORE the first
    /// framework call that triggers <c>AddDataProtection</c> internally
    /// (e.g. <c>AddRazorComponents</c>, <c>AddAuthentication</c>).
    /// </summary>
    public static IServiceCollection UseIcDataProtection(this IServiceCollection services)
    {
        services.TryAddSingleton<IDataProtectionProvider>(NoOpDataProtectionProvider.Instance);
        services.TryAddSingleton<IKeyManager, NoOpKeyManager>();
        return services;
    }
}
