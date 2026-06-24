using Mono.Cecil;

namespace StructEntropy.Rewriter
{
    internal sealed class Relocation
    {
        public FieldReference Field { get; set; }
        public TypeDefinition SourceType { get; set; }
        public string SourceTypeFullName { get; set; }
        public TypeDefinition TargetType { get; set; }
        public FieldReference NewField { get; set; }
        public string TargetFieldName { get; set; }
    }
}
