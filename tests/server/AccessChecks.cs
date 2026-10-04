using System;
namespace System.Runtime.CompilerServices;
// DispatchProxy needs access to the API's non-public default interface members.
[AttributeUsage(AttributeTargets.Assembly, AllowMultiple = true)]
public sealed class IgnoresAccessChecksToAttribute(string assemblyName) : Attribute
{
    public string AssemblyName { get; } = assemblyName;
}
