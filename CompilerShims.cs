// Compiler shims.
//
// The Il2Cpp interop assemblies referenced by this project expose a
// System.Runtime.CompilerServices.NullableAttribute (carried over from the
// game's original compilation) that lacks the constructors Roslyn requires.
// When compiling lambdas or async methods, Roslyn tries to emit nullable
// metadata, finds that incompatible definition, and fails with:
//   "Missing compiler required member 'System.Runtime.CompilerServices.NullableAttribute..ctor'"
//
// Defining the attributes locally makes the compiler use these instead.
// They are internal and carry no behavior; they exist purely to satisfy
// metadata emission.

// ReSharper disable All
#pragma warning disable

namespace System.Runtime.CompilerServices
{
    internal sealed class NullableAttribute : global::System.Attribute
    {
        public NullableAttribute(byte b) { }
        public NullableAttribute(byte[] b) { }
    }

    internal sealed class NullableContextAttribute : global::System.Attribute
    {
        public NullableContextAttribute(byte b) { }
    }

    internal sealed class NullablePublicOnlyAttribute : global::System.Attribute
    {
        public NullablePublicOnlyAttribute(bool b) { }
    }
}
