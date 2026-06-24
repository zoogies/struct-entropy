using System.Linq;
using Mono.Cecil;
using Mono.Cecil.Cil;
using static StructEntropy.Rewriter.TypeResolutionHelpers;

public static partial class StructEntropyRewriter
{
    private static MethodReference BuildEntityManagerGetComponentDataRef(
        ModuleDefinition module, Instruction setComponentDataCall, TypeDefinition targetType)
    {
        if (module == null || setComponentDataCall?.Operand is not MethodReference setRef || targetType == null)
            return null;

        try
        {
            var declaringType = setRef.DeclaringType.Resolve();
            if (declaringType == null)
                return null;

            var entityType = setRef.Parameters.Count > 0
                ? setRef.Parameters[0].ParameterType
                : null;
            if (entityType == null)
                return null;

            var getComponentDataDef = declaringType.Methods.FirstOrDefault(m =>
                m.Name == "GetComponentData" &&
                m.HasGenericParameters &&
                m.GenericParameters.Count == 1 &&
                m.Parameters.Count == 1 &&
                TypeRefFullNameEquals(m.Parameters[0].ParameterType, entityType.FullName));
            if (getComponentDataDef == null)
                return null;

            var importedDef = module.ImportReference(getComponentDataDef);
            var genericRef = new GenericInstanceMethod(importedDef);
            genericRef.GenericArguments.Add(module.ImportReference(targetType));
            return module.ImportReference(genericRef);
        }
        catch
        {
            return null;
        }
    }

    private static MethodReference BuildGenericMethodRef(
        ModuleDefinition module, Instruction originalCall, TypeDefinition newTypeArg)
    {
        if (originalCall.Operand is not GenericInstanceMethod gim) return null;
        try
        {
            var imported = module.ImportReference(gim.ElementMethod);
            var newGim = new GenericInstanceMethod(imported);
            foreach (var ga in gim.GenericArguments.Skip(1))
                newGim.GenericArguments.Add(module.ImportReference(ga));
            newGim.GenericArguments.Insert(0, module.ImportReference(newTypeArg));
            // rebuild with only one type arg if original had one
            if (gim.GenericArguments.Count == 1)
            {
                newGim = new GenericInstanceMethod(imported);
                newGim.GenericArguments.Add(module.ImportReference(newTypeArg));
            }
            return module.ImportReference(newGim);
        }
        catch { return null; }
    }

    private static MethodReference BuildGetItemRef(
        ModuleDefinition module, Instruction getItemCall, TypeDefinition newType)
    {
        if (getItemCall.Operand is not MethodReference mr) return null;
        try
        {
            if (mr.DeclaringType is not GenericInstanceType originalGit) return null;
            var newGit = new GenericInstanceType(originalGit.ElementType);
            newGit.GenericArguments.Add(module.ImportReference(newType));
            // Use mr.ReturnType (which is the generic parameter !0, not the concrete type).
            // If we substitute the concrete type here, Burst's method resolver fails because the
            // actual method definition in Unity.Entities.dll has return type "!0", not the
            // instantiated type, and the signatures won't match.
            var newRef = new MethodReference(mr.Name, mr.ReturnType, module.ImportReference(newGit))
            { HasThis = mr.HasThis, ExplicitThis = mr.ExplicitThis, CallingConvention = mr.CallingConvention };
            foreach (var p in mr.Parameters)
                newRef.Parameters.Add(new ParameterDefinition(p.ParameterType));
            return module.ImportReference(newRef);
        }
        catch { return null; }
    }

    private static MethodReference BuildSetItemRef(
        ModuleDefinition module, Instruction setItemCall, TypeDefinition newType)
        => BuildGetItemRef(module, setItemCall, newType); // same pattern

    private static MethodReference BuildComponentLookupAccessorRef(
        ModuleDefinition module,
        TypeReference lookupFieldType,
        string methodName,
        int parameterCount)
    {
        if (lookupFieldType is not GenericInstanceType lookupGit)
            return null;

        try
        {
            var lookupDef = lookupGit.ElementType.Resolve();
            if (lookupDef == null)
                return null;

            var methodDef = lookupDef.Methods.FirstOrDefault(m =>
                m.Name == methodName &&
                m.Parameters.Count == parameterCount);
            if (methodDef == null)
                return null;

            var declaringType = module.ImportReference(lookupFieldType);
            var methodRef = new MethodReference(methodDef.Name, methodDef.ReturnType, declaringType)
            {
                HasThis = methodDef.HasThis,
                ExplicitThis = methodDef.ExplicitThis,
                CallingConvention = methodDef.CallingConvention
            };

            foreach (var parameter in methodDef.Parameters)
                methodRef.Parameters.Add(new ParameterDefinition(module.ImportReference(parameter.ParameterType)));

            return module.ImportReference(methodRef);
        }
        catch
        {
            return null;
        }
    }

    // Builds NativeArray<TargetType> by substituting the generic argument of srcNativeArrayType.
    private static TypeReference BuildNativeArrayTypeRef(
        ModuleDefinition module, TypeReference srcNativeArrayType, TypeDefinition targetType)
    {
        if (srcNativeArrayType is not GenericInstanceType srcGit) return null;
        if (!srcGit.ElementType.Name.StartsWith("NativeArray")) return null;
        try
        {
            var peerGit = new GenericInstanceType(srcGit.ElementType);
            peerGit.GenericArguments.Add(module.ImportReference(targetType));
            return module.ImportReference(peerGit);
        }
        catch { return null; }
    }
}
