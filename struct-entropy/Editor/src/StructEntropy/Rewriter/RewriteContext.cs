using Mono.Cecil;
using Mono.Cecil.Cil;

namespace StructEntropy.Rewriter
{
    internal sealed class PendingEcbMerge
    {
        public MethodDefinition Method { get; set; }
        public VariableDefinition ExistingLocal { get; set; }
        public VariableDefinition TempLocal { get; set; }
        public FieldReference NewField { get; set; }
        public TypeDefinition ExistingType { get; set; }
    }
}
