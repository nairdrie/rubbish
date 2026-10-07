using System;

/// <summary>
/// Marks a NetworkBehaviour that belongs on the shared <c>NetSession.prefab</c> (D5). <c>Setup_Coop</c> finds every class with
/// this attribute (TypeCache) and adds it to the prefab, so packages never edit <c>Setup_Coop</c>. Lower <see cref="Order"/>
/// is added first.
/// </summary>
[AttributeUsage(AttributeTargets.Class, AllowMultiple = false, Inherited = false)]
public sealed class NetSessionComponentAttribute : Attribute
{
    public int Order;
}
