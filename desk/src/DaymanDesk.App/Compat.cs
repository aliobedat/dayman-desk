// WO-95: what the C# compiler needs on .NET Framework 4.8 (with Core's Polyfills.cs, linked in the csproj).
global using System.Net.Http;

namespace System.Diagnostics.CodeAnalysis
{
    [AttributeUsage(AttributeTargets.Field | AttributeTargets.Parameter | AttributeTargets.Property)]
    internal sealed class AllowNullAttribute : Attribute { }
}
