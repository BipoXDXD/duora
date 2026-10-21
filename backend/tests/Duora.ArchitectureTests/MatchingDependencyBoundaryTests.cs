using System.Buffers.Binary;
using System.Reflection;
using System.Reflection.Emit;

namespace Duora.ArchitectureTests;

/// <summary>
/// ADR-0012: o pareamento usa regras determinísticas e dependências aprovadas.
/// A inspeção inclui assinaturas, atributos e corpos dos métodos.
/// </summary>
public sealed class MatchingDependencyBoundaryTests
{
    private const string CoreAssembly = "Duora.Core";
    private const string MatchingNamespace = "Duora.Core.Matching";
    private static readonly HashSet<string> ApprovedAssemblies =
    [
        "Duora.Core", "System", "netstandard", "System.Private.CoreLib", "System.Runtime",
        "System.Collections", "System.Collections.Concurrent", "System.Linq", "System.Memory",
        "System.ObjectModel", "System.Security.Cryptography", "System.Text.Json",
        "System.Text.RegularExpressions", "System.Collections.Immutable", "System.Net.Mail",
        "System.Runtime.InteropServices",
    ];

    private static readonly Dictionary<short, OpCode> OpCodesByValue = typeof(OpCodes)
        .GetFields(BindingFlags.Public | BindingFlags.Static)
        .Select(field => field.GetValue(null))
        .OfType<OpCode>()
        .ToDictionary(opCode => opCode.Value);

    [Fact]
    public void CoreReferencesOnlyApprovedAssemblies()
    {
        var unapprovedReferences = Assembly.Load(CoreAssembly).GetReferencedAssemblies()
            .Select(reference => reference.Name ?? "")
            .Where(IsUnapprovedAssembly)
            .ToList();

        Assert.True(
            unapprovedReferences.Count == 0,
            $"ADR-0012: o Matching vive em {CoreAssembly}, que só pode referenciar dependências aprovadas. Encontrado: "
            + string.Join(", ", unapprovedReferences));
    }

    [Fact]
    public void MatchingTypesUseOnlyApprovedDependencies()
    {
        var violations = MatchingTypes()
            .SelectMany(type => DependenciesOf(type)
                .Where(dependency => IsUnapprovedAssembly(dependency.Assembly.GetName().Name ?? ""))
                .Select(dependency => $"{type.FullName} -> {dependency.FullName ?? dependency.Name}"))
            .Distinct()
            .ToList();

        Assert.True(
            violations.Count == 0,
            "ADR-0012: o pareamento usa exclusivamente dependências aprovadas. Dependências proibidas:"
            + Environment.NewLine + string.Join(Environment.NewLine, violations));
    }

    [Fact]
    public void DependencyScanSeesTypesUsedOnlyInsideMethodBodies()
    {
        // Sentinela: BinaryPrimitives só aparece no corpo de PairSelector.TieBreakKey. Se a leitura do IL quebrar,
        // o teste acima passaria olhando só assinaturas.
        var dependencies = MatchingTypes().SelectMany(DependenciesOf);

        Assert.Contains(typeof(BinaryPrimitives), dependencies);
    }

    [Theory]
    [InlineData("External.Provider")]
    [InlineData("Microsoft.Extensions.External.Abstractions")]
    public void UnapprovedProvidersAreRejected(string name)
    {
        Assert.True(IsUnapprovedAssembly(name));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void ExternalDependencyIsDetectedInSignatureAndBody(bool signature)
    {
        var consumer = signature ? typeof(SignatureConsumer) : typeof(BodyConsumer);
        var dependencies = DependenciesOf(consumer);
        Assert.Contains(typeof(ExternalDependency), dependencies);
        Assert.True(IsUnapprovedAssembly(typeof(ExternalDependency).Assembly.GetName().Name!));
    }

    private sealed class ExternalDependency;

    private sealed class SignatureConsumer
    {
        public ExternalDependency? Value { get; }
    }

    private sealed class BodyConsumer
    {
        public static void Load() => GC.KeepAlive(new ExternalDependency());
    }

    private static bool IsUnapprovedAssembly(string name) =>
        !ApprovedAssemblies.Contains(name);

    /// <summary>Inclui tipos aninhados e gerados (closures, state machines), onde mora o corpo de lambdas.</summary>
    private static List<Type> MatchingTypes()
    {
        var types = Assembly.Load(CoreAssembly).GetTypes()
            .Where(type => type.Namespace == MatchingNamespace
                || (type.Namespace ?? "").StartsWith(MatchingNamespace + ".", StringComparison.Ordinal))
            .ToList();
        Assert.NotEmpty(types);
        return types;
    }

    private static HashSet<Type> DependenciesOf(Type type)
    {
        const BindingFlags Declared = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance
            | BindingFlags.Static | BindingFlags.DeclaredOnly;

        var found = new HashSet<Type>();
        Add(found, type.BaseType);
        Add(found, type.GetInterfaces());
        Add(found, AttributeTypes(type.CustomAttributes));

        foreach (var field in type.GetFields(Declared))
        {
            Add(found, field.FieldType);
            Add(found, AttributeTypes(field.CustomAttributes));
        }

        foreach (var property in type.GetProperties(Declared))
        {
            Add(found, property.PropertyType);
            Add(found, AttributeTypes(property.CustomAttributes));
        }

        foreach (var method in type.GetMethods(Declared).Cast<MethodBase>().Concat(type.GetConstructors(Declared)))
        {
            if (method is MethodInfo info)
            {
                Add(found, info.ReturnType);
            }

            Add(found, ParameterTypes(method));
            Add(found, AttributeTypes(method.CustomAttributes));
            Add(found, method.GetMethodBody()?.LocalVariables.Select(local => local.LocalType) ?? []);
            Add(found, TypesReferencedInBody(method));
        }

        return found;
    }

    private static IEnumerable<Type> AttributeTypes(IEnumerable<CustomAttributeData> attributes) =>
        attributes.Select(attribute => attribute.AttributeType);

    /// <summary>
    /// Percorre o IL e resolve os tokens de tipo, campo e método. Uma referência externa só no corpo de um
    /// método (um <c>new ExternalClient(...)</c>) não aparece em nenhuma assinatura.
    /// </summary>
    private static IEnumerable<Type?> TypesReferencedInBody(MethodBase method)
    {
        var il = method.GetMethodBody()?.GetILAsByteArray();
        if (il is null)
        {
            yield break;
        }

        var typeArguments = method.DeclaringType is { IsGenericType: true } declaring
            ? declaring.GetGenericArguments()
            : null;
        var methodArguments = method is MethodInfo { IsGenericMethod: true } ? method.GetGenericArguments() : null;

        var position = 0;
        while (position < il.Length)
        {
            var opCode = ReadOpCode(il, ref position);
            if (opCode.OperandType is OperandType.InlineType or OperandType.InlineField or OperandType.InlineMethod
                or OperandType.InlineTok)
            {
                var token = BitConverter.ToInt32(il, position);
                foreach (var type in TypesOf(method.Module.ResolveMember(token, typeArguments, methodArguments)))
                {
                    yield return type;
                }
            }

            position += OperandSize(opCode, il, position);
        }
    }

    private static OpCode ReadOpCode(byte[] il, ref int position)
    {
        short value = il[position++];
        if (value == 0xFE)
        {
            value = unchecked((short)(0xFE00 | il[position++]));
        }

        return OpCodesByValue[value];
    }

    private static int OperandSize(OpCode opCode, byte[] il, int position) => opCode.OperandType switch
    {
        OperandType.InlineNone => 0,
        OperandType.ShortInlineBrTarget or OperandType.ShortInlineI or OperandType.ShortInlineVar => 1,
        OperandType.InlineVar => 2,
        OperandType.InlineI8 or OperandType.InlineR => 8,
        OperandType.InlineSwitch => 4 + (4 * BitConverter.ToInt32(il, position)),
        _ => 4,
    };

    private static IEnumerable<Type?> TypesOf(MemberInfo? member) => member switch
    {
        Type type => [type],
        FieldInfo field => [field.DeclaringType, field.FieldType],
        MethodInfo method => [method.DeclaringType, method.ReturnType, .. ParameterTypes(method)],
        ConstructorInfo constructor => [constructor.DeclaringType, .. ParameterTypes(constructor)],
        _ => [],
    };

    private static IEnumerable<Type> ParameterTypes(MethodBase method) =>
        method.GetParameters().Select(parameter => parameter.ParameterType);

    /// <summary>Desce em arrays, ponteiros, referências e argumentos genéricos: <c>List&lt;ExternalMessage&gt;</c> conta.</summary>
    private static void Add(HashSet<Type> found, IEnumerable<Type?> types)
    {
        foreach (var type in types)
        {
            Add(found, type);
        }
    }

    private static void Add(HashSet<Type> found, Type? type)
    {
        if (type is null || type.IsGenericParameter || !found.Add(type))
        {
            return;
        }

        if (type.HasElementType)
        {
            Add(found, type.GetElementType());
        }

        if (type.IsGenericType)
        {
            Add(found, type.GetGenericArguments());
            Add(found, type.GetGenericTypeDefinition());
        }
    }
}
