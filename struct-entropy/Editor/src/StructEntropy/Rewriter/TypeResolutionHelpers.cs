using System;
using Mono.Cecil;

namespace StructEntropy.Rewriter
{
    internal static class TypeResolutionHelpers
    {
        public static bool TypeRefFullNameEquals(TypeReference typeRef, string expectedFullName)
        {
            if (typeRef == null || string.IsNullOrEmpty(expectedFullName))
                return false;

            if (typeRef is TypeDefinition td)
                return string.Equals(td.FullName, expectedFullName, StringComparison.Ordinal);

            if (string.Equals(typeRef.FullName, expectedFullName, StringComparison.Ordinal))
                return true;

            try
            {
                return string.Equals(typeRef.Resolve()?.FullName, expectedFullName, StringComparison.Ordinal);
            }
            catch
            {
                return false;
            }
        }
    }
}
