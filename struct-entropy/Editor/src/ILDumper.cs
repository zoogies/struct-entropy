// ILDumper.cs — IL pretty-printer utility for StructEntropy ILPP development.
// Made by Claude (claude-sonnet-4-6).

using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using Mono.Cecil;
using Mono.Cecil.Cil;

/// <summary>
/// Developer utility for pretty-printing Mono.Cecil IL to StructEntropyLogger, a string, or a file.
///
/// Log to StructEntropyLogger:
///   ILDumper.Method(method);
///   ILDumper.Method(method, "after my transform");
///   ILDumper.Type(typeDef);
///   ILDumper.Assembly(module);
///   ILDumper.Assembly(module, m => m.Name == "Bake");
///
/// Write to file (appends; creates file and directories if needed):
///   ILDumper.MethodToFile(method, "Logs/before.il");
///   ILDumper.MethodToFile(method, "Logs/after.il", "after my transform");
///   ILDumper.AssemblyToFile(module, "Logs/full.il");
///   ILDumper.AssemblyToFile(module, "Logs/bakers.il", m => m.Name == "Bake");
///
/// Get a string (no side effects):
///   string s = ILDumper.FormatMethod(method);
///   string s = ILDumper.FormatAssembly(module, m => m.DeclaringType.Name == "Foo");
/// </summary>
public static class ILDumper
{
    // ──────────────────────────────────────────────────────────────
    //  Log API  (writes to StructEntropyLogger)
    // ──────────────────────────────────────────────────────────────

    /// <summary>Logs the IL of a single method.</summary>
    public static void Method(MethodDefinition method, string context = null)
    {
        StructEntropyLogger.Log(FormatMethod(method, context));
    }

    /// <summary>Logs all methods with bodies in a type.</summary>
    public static void Type(TypeDefinition type, string context = null, Func<MethodDefinition, bool> filter = null)
    {
        StructEntropyLogger.Log(FormatType(type, context, filter));
    }

    /// <summary>Logs all methods with bodies in a module, optionally filtered.</summary>
    public static void Assembly(ModuleDefinition module, Func<MethodDefinition, bool> filter = null, string context = null)
    {
        StructEntropyLogger.Log(FormatAssembly(module, filter, context));
    }

    // ──────────────────────────────────────────────────────────────
    //  File API  (appends to a file; creates path if needed)
    // ──────────────────────────────────────────────────────────────

    /// <summary>
    /// Appends the IL of a single method to <paramref name="path"/>.
    /// The file and any missing directories are created automatically.
    /// </summary>
    public static void MethodToFile(MethodDefinition method, string path, string context = null)
    {
        WriteToFile(path, FormatMethod(method, context));
    }

    /// <summary>
    /// Appends the IL of all methods (with bodies) in a type to <paramref name="path"/>.
    /// </summary>
    public static void TypeToFile(TypeDefinition type, string path, string context = null, Func<MethodDefinition, bool> filter = null)
    {
        WriteToFile(path, FormatType(type, context, filter));
    }

    /// <summary>
    /// Appends the IL of all methods in a module to <paramref name="path"/>, optionally filtered.
    /// </summary>
    public static void AssemblyToFile(ModuleDefinition module, string path, Func<MethodDefinition, bool> filter = null, string context = null)
    {
        WriteToFile(path, FormatAssembly(module, filter, context));
    }

    private static void WriteToFile(string path, string content)
    {
        try
        {
            string dir = Path.GetDirectoryName(path);
            if (!string.IsNullOrEmpty(dir))
                Directory.CreateDirectory(dir);
            File.AppendAllText(path, content + Environment.NewLine);
        }
        catch (Exception ex)
        {
            StructEntropyLogger.Log($"[ILDumper] Failed to write to {path}: {ex.Message}");
        }
    }

    // ──────────────────────────────────────────────────────────────
    //  Format API  (returns string, no side effects)
    // ──────────────────────────────────────────────────────────────

    /// <summary>Returns a formatted IL listing of a single method.</summary>
    public static string FormatMethod(MethodDefinition method, string context = null)
    {
        if (method == null) return "(null method)";

        var sb = new StringBuilder();
        string header = context != null
            ? $"[IL DUMP: {context}] {method.FullName}"
            : $"[IL DUMP] {method.FullName}";
        sb.AppendLine(header);

        if (!method.HasBody)
        {
            sb.AppendLine("  (no body)");
            return sb.ToString().TrimEnd();
        }

        var body = method.Body;

        // Locals
        if (body.HasVariables)
        {
            var locals = new StringBuilder("  Locals: ");
            for (int i = 0; i < body.Variables.Count; i++)
            {
                var v = body.Variables[i];
                if (i > 0) locals.Append(", ");
                locals.Append($"V_{v.Index}:{v.VariableType.Name}");
            }
            sb.AppendLine(locals.ToString());
        }

        // Exception handlers summary
        if (body.HasExceptionHandlers)
        {
            foreach (var eh in body.ExceptionHandlers)
            {
                string kind = eh.HandlerType.ToString();
                string catchType = eh.HandlerType == ExceptionHandlerType.Catch && eh.CatchType != null
                    ? $"<{eh.CatchType.Name}>"
                    : "";
                sb.AppendLine($"  .try IL_{eh.TryStart?.Offset:X4}–IL_{eh.TryEnd?.Offset:X4} " +
                              $"{kind}{catchType} IL_{eh.HandlerStart?.Offset:X4}–IL_{eh.HandlerEnd?.Offset:X4}");
            }
        }

        // Instructions
        foreach (var instr in body.Instructions)
            sb.AppendLine($"  {FormatInstruction(instr)}");

        return sb.ToString().TrimEnd();
    }

    /// <summary>Returns a formatted IL listing of all methods (with bodies) in a type.</summary>
    public static string FormatType(TypeDefinition type, string context = null, Func<MethodDefinition, bool> filter = null)
    {
        if (type == null) return "(null type)";

        var sb = new StringBuilder();
        string header = context != null
            ? $"[IL DUMP: {context}] type {type.FullName}"
            : $"[IL DUMP] type {type.FullName}";
        sb.AppendLine(header);

        bool any = false;
        foreach (var method in type.Methods)
        {
            if (!method.HasBody) continue;
            if (filter != null && !filter(method)) continue;
            sb.AppendLine();
            sb.AppendLine(FormatMethod(method));
            any = true;
        }

        if (!any) sb.AppendLine("  (no matching methods with bodies)");

        return sb.ToString().TrimEnd();
    }

    /// <summary>
    /// Returns a formatted IL listing of all methods in a module (including nested types),
    /// optionally filtered by a predicate on <see cref="MethodDefinition"/>.
    /// </summary>
    public static string FormatAssembly(ModuleDefinition module, Func<MethodDefinition, bool> filter = null, string context = null)
    {
        if (module == null) return "(null module)";

        var sb = new StringBuilder();
        string header = context != null
            ? $"[IL DUMP: {context}] assembly {module.Name}"
            : $"[IL DUMP] assembly {module.Name}";
        sb.AppendLine(header);

        int count = 0;
        foreach (var type in EnumerateTypes(module))
        {
            foreach (var method in type.Methods)
            {
                if (!method.HasBody) continue;
                if (filter != null && !filter(method)) continue;
                sb.AppendLine();
                sb.AppendLine(FormatMethod(method));
                count++;
            }
        }

        if (count == 0) sb.AppendLine("  (no matching methods with bodies)");

        return sb.ToString().TrimEnd();
    }

    // ──────────────────────────────────────────────────────────────
    //  Single-instruction formatting
    // ──────────────────────────────────────────────────────────────

    /// <summary>Formats a single instruction as a human-readable string.</summary>
    public static string FormatInstruction(Instruction instr)
    {
        if (instr == null) return "(null)";

        string offset  = $"IL_{instr.Offset:X4}";
        string opcode  = instr.OpCode.Name.PadRight(12);
        string operand = FormatOperand(instr);

        return operand.Length > 0
            ? $"{offset}  {opcode} {operand}"
            : $"{offset}  {opcode.TrimEnd()}";
    }

    // ──────────────────────────────────────────────────────────────
    //  Internal helpers
    // ──────────────────────────────────────────────────────────────

    private static string FormatOperand(Instruction instr)
    {
        var op = instr.Operand;
        if (op == null) return "";

        switch (op)
        {
            case FieldReference fr:
                return $"{fr.DeclaringType.Name}::{fr.Name} ({fr.FieldType.Name})";

            case MethodReference mr:
                // Keep it concise: DeclaringType::MethodName(params) : ReturnType
                var parms = new StringBuilder();
                if (mr is GenericInstanceMethod gim)
                {
                    parms.Append("<");
                    for (int i = 0; i < gim.GenericArguments.Count; i++)
                    {
                        if (i > 0) parms.Append(", ");
                        parms.Append(gim.GenericArguments[i].Name);
                    }
                    parms.Append(">");
                }
                var paramList = new StringBuilder("(");
                for (int i = 0; i < mr.Parameters.Count; i++)
                {
                    if (i > 0) paramList.Append(", ");
                    paramList.Append(mr.Parameters[i].ParameterType.Name);
                }
                paramList.Append(")");
                return $"{mr.DeclaringType.Name}::{mr.Name}{parms}{paramList} : {mr.ReturnType.Name}";

            case TypeReference tr:
                return tr.FullName;

            case VariableDefinition vd:
                return $"V_{vd.Index} ({vd.VariableType.Name})";

            case ParameterDefinition pd:
                return $"{pd.Name} ({pd.ParameterType.Name})";

            case Instruction target:
                // Branch target — show destination offset
                return $"IL_{target.Offset:X4}";

            case Instruction[] targets:
                // Switch table
                var sw = new StringBuilder("[ ");
                for (int i = 0; i < targets.Length; i++)
                {
                    if (i > 0) sw.Append(", ");
                    sw.Append($"IL_{targets[i].Offset:X4}");
                }
                sw.Append(" ]");
                return sw.ToString();

            case string s:
                // ldstr — truncate long strings
                string escaped = s.Replace("\n", "\\n").Replace("\r", "\\r");
                return escaped.Length > 60 ? $"\"{escaped.Substring(0, 57)}...\"" : $"\"{escaped}\"";

            case sbyte sb2:   return sb2.ToString();
            case byte b:      return b.ToString();
            case int i32:     return i32.ToString();
            case long i64:    return i64.ToString();
            case float f32:   return f32.ToString("G");
            case double f64:  return f64.ToString("G");

            default:
                return op.ToString();
        }
    }

    private static IEnumerable<TypeDefinition> EnumerateTypes(ModuleDefinition module)
    {
        foreach (var type in module.Types)
        {
            yield return type;
            foreach (var nested in EnumerateTypesRecursive(type))
                yield return nested;
        }
    }

    private static IEnumerable<TypeDefinition> EnumerateTypesRecursive(TypeDefinition type)
    {
        foreach (var nested in type.NestedTypes)
        {
            yield return nested;
            foreach (var deep in EnumerateTypesRecursive(nested))
                yield return deep;
        }
    }
}
