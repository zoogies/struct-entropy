using System.Linq;
using Mono.Cecil;
using Mono.Cecil.Cil;
using StructEntropy.Rewriter;
using static StructEntropy.Rewriter.TypeResolutionHelpers;

public static partial class StructEntropyRewriter
{
    // Changed-version filter patching for relocated target components.
    private static void PatchChangedVersionFilters(ModuleDefinition module, Relocation reloc)
    {
        foreach (var type in EnumerateAllTypes(module))
        {
            foreach (var method in type.Methods)
            {
                if (!method.HasBody) continue;
                var il = method.Body.GetILProcessor();
                var snapshot = method.Body.Instructions.ToList();

                foreach (var call in snapshot)
                {
                    if ((call.OpCode.Code != Code.Call && call.OpCode.Code != Code.Callvirt) ||
                        call.Operand is not MethodReference mr ||
                        mr.Name != "SetChangedVersionFilter" ||
                        mr.DeclaringType.FullName != "Unity.Entities.EntityQuery")
                        continue;

                    if (!TryFindSingleChangedFilterSource(
                        call,
                        reloc.SourceType,
                        out var countInstr,
                        out var sourceLdtoken,
                        out var sourceGetType,
                        out var sourceAccessMode,
                        out var sourceNewobj,
                        out var accessModeValue,
                        out var componentTypeCtor))
                        continue;

                    if (ArrayAlreadyContainsChangedFilterType(call, reloc.TargetType))
                        continue;

                    var componentType = componentTypeCtor.DeclaringType;
                    var sourceComponentTypeFactory = BuildComponentTypeFactoryRef(module, componentType, reloc.SourceType, accessModeValue);
                    var targetComponentTypeFactory = BuildComponentTypeFactoryRef(module, componentType, reloc.TargetType, accessModeValue);
                    if (sourceComponentTypeFactory == null || targetComponentTypeFactory == null)
                        continue;

                    sourceLdtoken.OpCode = OpCodes.Call;
                    sourceLdtoken.Operand = sourceComponentTypeFactory;
                    il.Remove(sourceGetType);
                    il.Remove(sourceAccessMode);
                    il.Remove(sourceNewobj);

                    SetI4Operand(countInstr, 2);

                    var injection = new[]
                    {
                        Instruction.Create(OpCodes.Dup),
                        Instruction.Create(OpCodes.Ldc_I4_1),
                        Instruction.Create(OpCodes.Call, targetComponentTypeFactory),
                    };

                    foreach (var instr in injection)
                        il.InsertBefore(call, instr);

                    il.InsertBefore(call, Instruction.Create(OpCodes.Stelem_Any, componentType));

                    StructEntropyLogger.Log($"[SER]   Patched changed-version filter for {reloc.TargetType.Name} in {type.Name}.{method.Name}");
                }
            }
        }
    }

    private static bool TryFindSingleChangedFilterSource(
        Instruction setChangedFilterCall, TypeDefinition sourceType,
        out Instruction countInstr,
        out Instruction ldtoken,
        out Instruction getType,
        out Instruction accessMode,
        out Instruction newobj,
        out int accessModeValue,
        out MethodReference componentTypeCtor)
    {
        countInstr = null;
        ldtoken = null;
        getType = null;
        accessMode = null;
        newobj = null;
        accessModeValue = 0;
        componentTypeCtor = null;

        var stelem = setChangedFilterCall.Previous;
        if (stelem == null || stelem.OpCode != OpCodes.Stelem_Any) return false;

        newobj = stelem.Previous;
        accessMode = newobj?.Previous;
        getType = accessMode?.Previous;
        ldtoken = getType?.Previous;
        var index = ldtoken?.Previous;
        var dup = index?.Previous;
        var newarr = dup?.Previous;
        countInstr = newarr?.Previous;

        if (newobj == null || accessMode == null || getType == null || ldtoken == null ||
            index == null || dup == null || newarr == null || countInstr == null)
            return false;

        if (newarr.OpCode != OpCodes.Newarr ||
            newarr.Operand is not TypeReference arrElem ||
            arrElem.FullName != "Unity.Entities.ComponentType")
            return false;

        if ((dup.OpCode != OpCodes.Dup) ||
            !IsLdcI4WithValue(index, 0) ||
            ldtoken.OpCode != OpCodes.Ldtoken ||
            ldtoken.Operand is not TypeReference tr ||
            !TypeRefFullNameEquals(tr, sourceType.FullName) ||
            (getType.OpCode.Code != Code.Call && getType.OpCode.Code != Code.Callvirt) ||
            getType.Operand is not MethodReference getTypeMr ||
            getTypeMr.Name != "GetTypeFromHandle" ||
            !TryGetLdcI4(accessMode, out accessModeValue) ||
            newobj.OpCode != OpCodes.Newobj)
            return false;

        componentTypeCtor = newobj.Operand as MethodReference;

        return componentTypeCtor != null && IsLdcI4WithValue(countInstr, 1);
    }

    private static MethodReference BuildComponentTypeFactoryRef(
        ModuleDefinition module,
        TypeReference componentType,
        TypeDefinition component,
        int accessModeValue)
    {
        try
        {
            var componentTypeDef = componentType.Resolve();
            if (componentTypeDef == null)
                return null;

            var methodName = accessModeValue == 1 ? "ReadOnly" : "ReadWrite";
            var methodDef = componentTypeDef.Methods.FirstOrDefault(m =>
                m.Name == methodName &&
                m.HasGenericParameters &&
                m.GenericParameters.Count == 1 &&
                m.Parameters.Count == 0);
            if (methodDef == null)
                return null;

            var generic = new GenericInstanceMethod(module.ImportReference(methodDef));
            generic.GenericArguments.Add(module.ImportReference(component));
            return module.ImportReference(generic);
        }
        catch
        {
            return null;
        }
    }

    private static bool ArrayAlreadyContainsChangedFilterType(Instruction setChangedFilterCall, TypeDefinition targetType)
    {
        int depth = 0;
        for (var cur = setChangedFilterCall.Previous; cur != null && depth < 20; cur = cur.Previous, depth++)
        {
            if (cur.OpCode == OpCodes.Ldtoken &&
                cur.Operand is TypeReference tr &&
                TypeRefFullNameEquals(tr, targetType.FullName))
                return true;
        }
        return false;
    }

    private static bool IsLdcI4WithValue(Instruction instr, int value)
    {
        return TryGetLdcI4(instr, out var actual) && actual == value;
    }

    private static bool TryGetLdcI4(Instruction instr, out int value)
    {
        value = 0;
        if (instr == null) return false;
        return instr.OpCode.Code switch
        {
            Code.Ldc_I4_M1 => SetAndReturn(out value, -1),
            Code.Ldc_I4_0 => SetAndReturn(out value, 0),
            Code.Ldc_I4_1 => SetAndReturn(out value, 1),
            Code.Ldc_I4_2 => SetAndReturn(out value, 2),
            Code.Ldc_I4_3 => SetAndReturn(out value, 3),
            Code.Ldc_I4_4 => SetAndReturn(out value, 4),
            Code.Ldc_I4_5 => SetAndReturn(out value, 5),
            Code.Ldc_I4_6 => SetAndReturn(out value, 6),
            Code.Ldc_I4_7 => SetAndReturn(out value, 7),
            Code.Ldc_I4_8 => SetAndReturn(out value, 8),
            Code.Ldc_I4_S => SetAndReturn(out value, (sbyte)instr.Operand),
            Code.Ldc_I4 => SetAndReturn(out value, (int)instr.Operand),
            _ => false
        };
    }

    private static bool SetAndReturn(out int target, int value)
    {
        target = value;
        return true;
    }

    private static void SetI4Operand(Instruction instr, int value)
    {
        instr.OpCode = value switch
        {
            -1 => OpCodes.Ldc_I4_M1,
            0 => OpCodes.Ldc_I4_0,
            1 => OpCodes.Ldc_I4_1,
            2 => OpCodes.Ldc_I4_2,
            3 => OpCodes.Ldc_I4_3,
            4 => OpCodes.Ldc_I4_4,
            5 => OpCodes.Ldc_I4_5,
            6 => OpCodes.Ldc_I4_6,
            7 => OpCodes.Ldc_I4_7,
            8 => OpCodes.Ldc_I4_8,
            _ when value >= sbyte.MinValue && value <= sbyte.MaxValue => OpCodes.Ldc_I4_S,
            _ => OpCodes.Ldc_I4
        };

        instr.Operand = instr.OpCode.Code switch
        {
            Code.Ldc_I4_S => (sbyte)value,
            Code.Ldc_I4 => value,
            _ => null
        };
    }
}
