#if NETSTANDARD2_0 || NETSTANDARD2_1
using System;

namespace System.Diagnostics.CodeAnalysis;

[Flags]
internal enum DynamicallyAccessedMemberTypes
{
    None = 0,
    PublicProperties = 512,
}

[AttributeUsage(
    AttributeTargets.Field | AttributeTargets.ReturnValue | AttributeTargets.GenericParameter |
    AttributeTargets.Parameter | AttributeTargets.Property | AttributeTargets.Method,
    Inherited = false)]
internal sealed class DynamicallyAccessedMembersAttribute : Attribute
{
    internal DynamicallyAccessedMembersAttribute(DynamicallyAccessedMemberTypes memberTypes) =>
        MemberTypes = memberTypes;

    internal DynamicallyAccessedMemberTypes MemberTypes { get; }
}

[AttributeUsage(AttributeTargets.Constructor | AttributeTargets.Method | AttributeTargets.Class, Inherited = false)]
internal sealed class RequiresUnreferencedCodeAttribute : Attribute
{
    internal RequiresUnreferencedCodeAttribute(string message) => Message = message;
    internal string Message { get; }
}
#endif
