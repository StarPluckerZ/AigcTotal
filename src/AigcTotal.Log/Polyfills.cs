// netstandard2.0 上使用 record（init 访问器）所需的编译器合成类型。
#if NETSTANDARD2_0
namespace System.Runtime.CompilerServices
{
    internal static class IsExternalInit
    {
    }
}
#endif
