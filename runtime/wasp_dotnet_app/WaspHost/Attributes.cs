namespace Wasp;

// Method-level attributes that mark IC entry points. The Rust runtime
// (Mono host side) discovers these via reflection at canister init and
// builds a method-name -> MethodInfo dispatch table.
//
// Simplified vs the AOT story: no `Manual` flag because the runtime
// model lets the user call MessageContext.ArgData / Reply.Bytes
// directly anyway - every method is effectively manual under Mono.

[System.AttributeUsage(System.AttributeTargets.Method, AllowMultiple = false)]
public sealed class CanisterQueryAttribute : System.Attribute
{
    public CanisterQueryAttribute(string? name = null) { Name = name; }
    public string? Name { get; }
}

[System.AttributeUsage(System.AttributeTargets.Method, AllowMultiple = false)]
public sealed class CanisterUpdateAttribute : System.Attribute
{
    public CanisterUpdateAttribute(string? name = null) { Name = name; }
    public string? Name { get; }
}

[System.AttributeUsage(System.AttributeTargets.Method, AllowMultiple = false)]
public sealed class CanisterInitAttribute : System.Attribute
{
}

[System.AttributeUsage(System.AttributeTargets.Method, AllowMultiple = false)]
public sealed class CanisterPostUpgradeAttribute : System.Attribute
{
}
