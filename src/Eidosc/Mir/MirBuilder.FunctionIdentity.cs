using Eidosc.Diagnostic;
using Eidosc.Hir;
using Eidosc.Semantic;
using Eidosc.Symbols;
using Eidosc.Types;
using Eidosc.Utils;

namespace Eidosc.Mir;

public sealed partial class MirBuilder
{
    private SymbolKind ResolveSymbolKind(SymbolId symbolId)
    {
        if (!symbolId.IsValid || _symbolTable == null)
            return SymbolKind.Function;
        return _symbolTable.GetSymbol(symbolId)?.Kind ?? SymbolKind.Function;
    }

    private FunctionId BuildFunctionId(SymbolId symbolId, string name, SymbolKind kind)
    {
        if (TryGetRegisteredIntrinsicName(symbolId, out var intrinsicName))
        {
            return MirBuiltinFunctions.CreateIntrinsicFunctionId(symbolId, intrinsicName);
        }

        var (moduleName, moduleIdentityKey) = ResolveSymbolModuleIdentity(symbolId);
        return new FunctionId
        {
            SymbolId = symbolId,
            Kind = kind,
            Name = name,
            Module = moduleName,
            ModuleIdentityKey = moduleIdentityKey,
            StableIdentityKey = BuildStableFunctionIdentityKey(symbolId, kind, moduleIdentityKey),
            QualifiedName = string.IsNullOrWhiteSpace(moduleName)
                ? name
                : $"{moduleName}{WellKnownStrings.Separators.Path}{name}"
        };
    }

    private FunctionId BuildConstructorFunctionId(SymbolId symbolId, string name, TypeId resultType)
    {
        var functionId = BuildFunctionId(symbolId, name, SymbolKind.Constructor);
        if (!_constructorLayouts.TryGetValue(resultType.Value, out var layouts))
        {
            return functionId;
        }

        var layout = layouts.Count == 1
            ? layouts[0]
            : layouts.FirstOrDefault(candidate =>
                string.Equals(candidate.ConstructorName, name, StringComparison.Ordinal) ||
                name.EndsWith(
                    $"{WellKnownStrings.Separators.Path}{candidate.ConstructorName}",
                    StringComparison.Ordinal) ||
                name.EndsWith($"__{candidate.ConstructorName}", StringComparison.Ordinal));
        return layout is { RuntimeTypeId: not 0 }
            ? functionId with
            {
                Kind = SymbolKind.Constructor,
                StableIdentityKey = $"runtime-ctor:{layout.RuntimeTypeId}"
            }
            : functionId;
    }

    private string BuildStableFunctionIdentityKey(
        SymbolId symbolId,
        SymbolKind kind,
        string moduleIdentityKey)
    {
        if (kind == SymbolKind.Constructor &&
            ConstructorRuntimeTypeId.TryGetStableIdentityKey(_symbolTable, symbolId, "", out var constructorKey))
        {
            return constructorKey;
        }

        if (!symbolId.IsValid ||
            _symbolTable?.GetSymbol(symbolId) is not { } symbol ||
            symbol.Span.Equals(SourceSpan.Empty))
        {
            return "";
        }

        var sourcePath = string.IsNullOrWhiteSpace(symbol.Span.FilePath)
            ? "<unknown>"
            : symbol.Span.FilePath.Replace('\\', '/');
        return string.Join(
            "\0",
            moduleIdentityKey,
            kind.ToString(),
            symbol.Name,
            sourcePath,
            symbol.Span.Position.ToString(System.Globalization.CultureInfo.InvariantCulture));
    }

    private bool TryGetRegisteredIntrinsicName(SymbolId symbolId, out string intrinsicName)
    {
        intrinsicName = string.Empty;
        if (symbolId.IsValid &&
            _symbolTable?.GetSymbol<FuncSymbol>(symbolId) is
               {
                   HasBody: false,
                   IsModuleLevel: true,
                   IsExternal: false,
                   IsCStructAccessor: false
               } functionSymbol)
        {
            if (functionSymbol.IsCompilerIntrinsic)
            {
                intrinsicName = functionSymbol.IntrinsicName!;
                return true;
            }

            if (functionSymbol.Span.Equals(SourceSpan.Empty) &&
                IntrinsicRegistry.IsKnownIntrinsicName(functionSymbol.Name))
            {
                intrinsicName = functionSymbol.Name;
                return true;
            }
        }

        return false;
    }

    private (string ModuleName, string ModuleIdentityKey) ResolveSymbolModuleIdentity(SymbolId symbolId)
    {
        if (!symbolId.IsValid || _symbolTable == null)
        {
            return (
                FormatModuleName(null, _currentModulePath),
                ModuleRegistry.ToModuleIdentityKey(null, null, _currentModulePath));
        }

        foreach (var moduleId in _symbolTable.Modules.ModulePaths.Values.Distinct())
        {
            var module = _symbolTable.Modules.GetModule(moduleId);
            if (module?.Members.Contains(symbolId) == true)
            {
                return (FormatModuleName(module.PackageAlias, module.Path), module.Identity.ToIdentityKey());
            }
        }

        return (
            FormatModuleName(null, _currentModulePath),
            ModuleRegistry.ToModuleIdentityKey(null, null, _currentModulePath));
    }

    private static string FormatModuleName(string? packageAlias, IReadOnlyList<string> modulePath)
    {
        var path = string.Join(
            WellKnownStrings.Separators.ModulePath,
            modulePath.Where(static segment => !string.IsNullOrWhiteSpace(segment)));
        if (string.IsNullOrWhiteSpace(packageAlias))
        {
            return path;
        }

        return string.IsNullOrWhiteSpace(path)
            ? packageAlias
            : $"{packageAlias}{WellKnownStrings.Separators.Path}{path}";
    }

    private TraitInvokeHelperKind GetTraitInvokeHelperKind(HirFunc func)
    {
        var modulePath = ResolveFunctionModulePath(func);
        if (modulePath is not ["Std", "TraitInvoke"] and not ["TraitInvoke"])
        {
            return TraitInvokeHelperKind.None;
        }

        var sourceFunctionName = ResolveSourceFunctionName(func);
        return sourceFunctionName switch
        {
            "eq_value" => TraitInvokeHelperKind.EqValue,
            "compare_value" => TraitInvokeHelperKind.CompareValue,
            "show_value" => TraitInvokeHelperKind.ShowValue,
            "hash_value" => TraitInvokeHelperKind.HashValue,
            "clone_value" => TraitInvokeHelperKind.CloneValue,
            _ => TraitInvokeHelperKind.None
        };
    }

    private IReadOnlyList<string> ResolveFunctionModulePath(HirFunc func)
    {
        if (func.SymbolId.IsValid &&
            _symbolTable != null)
        {
            foreach (var moduleId in _symbolTable.Modules.ModulePaths.Values)
            {
                var module = _symbolTable.Modules.GetModule(moduleId);
                if (module?.Members.Contains(func.SymbolId) == true)
                {
                    return module.Path;
                }
            }
        }

        return _currentModulePath;
    }

    private string ResolveSourceFunctionName(HirFunc func)
    {
        if (func.SymbolId.IsValid &&
            _symbolTable?.GetSymbol<FuncSymbol>(func.SymbolId) is { Name.Length: > 0 } funcSymbol)
        {
            return funcSymbol.Name;
        }

        return string.IsNullOrWhiteSpace(func.SourceName) ? func.Name : func.SourceName;
    }

    private void RegisterFunctionSignature(SymbolId symbolId, string name, IReadOnlyList<TypeId> parameterTypes, TypeId returnType)
    {
        var signatureTypeId = GetOrCreateFunctionSignatureTypeId(parameterTypes, returnType);
        if (!signatureTypeId.IsValid)
        {
            return;
        }

        if (symbolId.IsValid)
        {
            _functionSignatureTypesBySymbol[symbolId] = signatureTypeId;
        }

        if (!string.IsNullOrWhiteSpace(name))
        {
            RegisterFunctionSignatureNameFallback(name, signatureTypeId);
        }
    }

    private void RegisterFunctionNameFallback(string name, SymbolId symbolId, TypeId returnType)
    {
        if (string.IsNullOrWhiteSpace(name) || _ambiguousFunctionNames.Contains(name))
        {
            return;
        }

        if (_functionNames.TryGetValue(name, out var existingSymbolId) &&
            !existingSymbolId.Equals(symbolId))
        {
            MarkFunctionNameAmbiguous(name);
            return;
        }

        _functionNames[name] = symbolId;
        if (returnType.IsValid)
        {
            _functionReturnTypesByName[name] = returnType;
        }
    }

    private void RegisterQualifiedFunctionNameFallback(HirFunc function)
    {
        if (string.IsNullOrWhiteSpace(function.SourceName) ||
            string.Equals(function.Name, function.SourceName, StringComparison.Ordinal))
        {
            return;
        }

        var suffix = $"{WellKnownStrings.InternalNames.ModuleSeparator}{function.SourceName}";
        if (!function.Name.EndsWith(suffix, StringComparison.Ordinal))
        {
            return;
        }

        var loweredPrefix = function.Name[..^suffix.Length];
        if (string.IsNullOrWhiteSpace(loweredPrefix))
        {
            return;
        }

        var qualifiedPrefix = loweredPrefix.Replace(
            WellKnownStrings.InternalNames.ModuleSeparator,
            WellKnownStrings.Separators.Path,
            StringComparison.Ordinal);
        var qualifiedName = $"{qualifiedPrefix}{WellKnownStrings.Separators.Path}{function.SourceName}";
        RegisterFunctionNameFallback(qualifiedName, function.SymbolId, function.ReturnType);
        RegisterFunctionSignature(
            function.SymbolId,
            qualifiedName,
            function.Parameters.Select(static parameter => parameter.TypeId).ToArray(),
            function.ReturnType);
    }

    private void RegisterFunctionSignatureNameFallback(string name, TypeId signatureTypeId)
    {
        if (string.IsNullOrWhiteSpace(name) || _ambiguousFunctionNames.Contains(name))
        {
            return;
        }

        if (_functionSignatureTypesByName.TryGetValue(name, out var existingSignatureTypeId) &&
            !existingSignatureTypeId.Equals(signatureTypeId))
        {
            MarkFunctionNameAmbiguous(name);
            return;
        }

        _functionSignatureTypesByName[name] = signatureTypeId;
    }

    private void MarkFunctionNameAmbiguous(string name)
    {
        _ambiguousFunctionNames.Add(name);
        _functionNames.Remove(name);
        _functionReturnTypesByName.Remove(name);
        _functionSignatureTypesByName.Remove(name);
    }

    private TypeId ResolveFunctionSignatureTypeId(SymbolId symbolId, string name, TypeId functionValueTypeId = default)
    {
        if (TryResolveFunctionValueSignatureTypeId(functionValueTypeId, out var functionValueSignatureTypeId))
        {
            return functionValueSignatureTypeId;
        }

        if (symbolId.IsValid)
        {
            return _functionSignatureTypesBySymbol.TryGetValue(symbolId, out var bySymbol) &&
                   bySymbol.IsValid
                ? bySymbol
                : TypeId.None;
        }

        return !string.IsNullOrWhiteSpace(name) &&
               _functionSignatureTypesByName.TryGetValue(name, out var byName) &&
               byName.IsValid
            ? byName
            : TypeId.None;
    }

    private bool TryResolveFunctionValueSignatureTypeId(TypeId functionValueTypeId, out TypeId signatureTypeId)
    {
        signatureTypeId = TypeId.None;
        if (!functionValueTypeId.IsValid)
        {
            return false;
        }

        if (_typeDescriptorsById.TryGetValue(functionValueTypeId.Value, out var descriptor) &&
            descriptor is TypeDescriptor.Function)
        {
            signatureTypeId = functionValueTypeId;
            return true;
        }

        if (!_dynamicTypeKeysById.TryGetValue(functionValueTypeId.Value, out var typeKey) ||
            !TypeKeyParsing.TryParseTypeDescriptor(typeKey, out descriptor) ||
            descriptor is not TypeDescriptor.Function)
        {
            return false;
        }

        _typeDescriptorsById[functionValueTypeId.Value] = descriptor;
        _dynamicTypeIdByDescriptor[descriptor] = functionValueTypeId;
        signatureTypeId = functionValueTypeId;
        return true;
    }
}
