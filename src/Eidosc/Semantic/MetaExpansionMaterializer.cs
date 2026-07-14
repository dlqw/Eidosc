using Eidosc.Ast;
using Eidosc.Ast.Declarations;
using Eidosc.Ast.Expressions;
using Eidosc.Ast.Patterns;
using Eidosc.Ast.Types;
using Eidosc.Symbols;
using Eidosc.Types;
using Eidosc.Utils;
using AstAttribute = Eidosc.Ast.Attribute;

namespace Eidosc.Semantic;

internal sealed record MaterializedMetaDeclaration(
    Declaration Declaration,
    int OutputIndex,
    int NestedIndex = 0);

internal sealed record MetaAttributeAttachment(
    ComptimeDeclValue Target,
    string Name,
    IReadOnlyList<string> Arguments,
    int OutputIndex);

internal sealed record MetaExpansionDiagnostic(
    string Level,
    SourceSpan Span,
    string Message,
    int OutputIndex);

internal sealed record MetaExpansionMaterializationResult(
    IReadOnlyList<MaterializedMetaDeclaration> Declarations,
    IReadOnlyList<MetaAttributeAttachment> Attachments,
    IReadOnlyList<MetaExpansionDiagnostic> Diagnostics);

internal sealed class MetaExpansionMaterializer(
    SymbolTable symbolTable,
    AdtDef target,
    SymbolId targetModuleId,
    SourceSpan attributeSpan)
{
    private readonly SymbolTable _symbolTable = symbolTable;
    private readonly AdtDef _target = target;
    private readonly SymbolId _targetModuleId = targetModuleId;
    private readonly SourceSpan _attributeSpan = attributeSpan;

    public bool TryMaterialize(
        ComptimeValue expansionValue,
        out MetaExpansionMaterializationResult result,
        out string reason)
    {
        result = new MetaExpansionMaterializationResult([], [], []);
        reason = string.Empty;
        if (expansionValue is not ComptimeMetaObjectValue { SchemaKind: "expansion" } expansion ||
            !expansion.TryGet("declarations", out var declarationValues) ||
            declarationValues is not ComptimeSequenceValue { Kind: ComptimeSequenceKind.List } declarationList)
        {
            reason = "derive generator must return Meta.Expansion created by Meta.expansion";
            return false;
        }

        var declarations = new List<MaterializedMetaDeclaration>();
        var attachments = new List<MetaAttributeAttachment>();
        var diagnostics = new List<MetaExpansionDiagnostic>();
        for (var outputIndex = 0; outputIndex < declarationList.Elements.Count; outputIndex++)
        {
            if (declarationList.Elements[outputIndex] is not ComptimeMetaObjectValue declaration)
            {
                reason = $"expansion output {outputIndex} is not a structured Meta.Declaration";
                return false;
            }

            if (!TryMaterializeDeclaration(
                    declaration,
                    outputIndex,
                    declarations,
                    attachments,
                    diagnostics,
                    out reason))
            {
                return false;
            }
        }

        result = new MetaExpansionMaterializationResult(declarations, attachments, diagnostics);
        return true;
    }

    private bool TryMaterializeDeclaration(
        ComptimeMetaObjectValue declaration,
        int outputIndex,
        List<MaterializedMetaDeclaration> declarations,
        List<MetaAttributeAttachment> attachments,
        List<MetaExpansionDiagnostic> diagnostics,
        out string reason)
    {
        reason = string.Empty;
        switch (declaration.SchemaKind)
        {
            case "declaration.function":
                if (!TryCreateFunction(declaration, implTrait: null, out var function, out reason))
                {
                    return false;
                }

                declarations.Add(new MaterializedMetaDeclaration(function, outputIndex));
                return true;

            case "declaration.implementation":
                return TryCreateImplementation(declaration, outputIndex, declarations, out reason);

            case "declaration.comptime-value":
                if (!TryCreateComptimeValue(declaration, out var comptimeValue, out reason))
                {
                    return false;
                }

                declarations.Add(new MaterializedMetaDeclaration(comptimeValue, outputIndex));
                return true;

            case "declaration.attribute":
                if (!TryCreateAttributeAttachment(declaration, outputIndex, out var attachment, out reason))
                {
                    return false;
                }

                attachments.Add(attachment);
                return true;

            case "declaration.test":
                if (!TryCreateTest(declaration, out var test, out reason))
                {
                    return false;
                }

                declarations.Add(new MaterializedMetaDeclaration(test, outputIndex));
                return true;

            case "declaration.module-member":
                if (!TryGetObject(declaration, "declaration", out var nested, out reason))
                {
                    return false;
                }

                return TryMaterializeDeclaration(nested, outputIndex, declarations, attachments, diagnostics, out reason);

            case "declaration.diagnostic":
                if (!TryReadDiagnostic(declaration, outputIndex, out var diagnostic, out reason))
                {
                    return false;
                }

                diagnostics.Add(diagnostic);
                return true;

            default:
                reason = $"unsupported structured declaration kind '{declaration.SchemaKind}'";
                return false;
        }
    }

    private bool TryCreateFunction(
        ComptimeMetaObjectValue declaration,
        ComptimeDeclValue? implTrait,
        out FuncDef function,
        out string reason)
    {
        function = new FuncDef();
        reason = string.Empty;
        if (!TryGetString(declaration, "name", out var name, out reason) ||
            !ValidateGeneratedValueName(name, out reason) ||
            !TryGetSequence(declaration, "parameters", out var parameterValues, out reason) ||
            !TryGetType(declaration, "result", out var resultType, out reason) ||
            !TryGetObject(declaration, "body", out var bodyValue, out reason))
        {
            return false;
        }

        var bindings = new Dictionary<string, string>(StringComparer.Ordinal);
        var parameterPatterns = new List<Pattern>(parameterValues.Count);
        var parameterTypes = new List<TypeNode>(parameterValues.Count);
        for (var index = 0; index < parameterValues.Count; index++)
        {
            if (parameterValues[index] is not ComptimeMetaObjectValue { SchemaKind: "parameter" } parameter ||
                !TryGetString(parameter, "identity", out var identity, out reason) ||
                !TryGetString(parameter, "name", out var requestedName, out reason) ||
                !TryGetType(parameter, "type", out var parameterType, out reason))
            {
                reason = string.IsNullOrWhiteSpace(reason)
                    ? $"function parameter {index} is not a Meta.Parameter"
                    : reason;
                return false;
            }

            var hygienicName = CreateHygienicName("p", identity, requestedName);
            if (!bindings.TryAdd(identity, hygienicName))
            {
                reason = $"duplicate generated parameter identity '{identity}'";
                return false;
            }

            var pattern = new VarPattern();
            pattern.SetSpan(_attributeSpan);
            pattern.SetName(hygienicName);
            parameterPatterns.Add(pattern);
            parameterTypes.Add(CreateTypeNode(parameterType));
        }

        if (!TryCreateExpression(bodyValue, bindings, out var body, out reason))
        {
            return false;
        }

        var entryPattern = parameterPatterns.Count switch
        {
            0 => CreateWildcardPattern(),
            1 => parameterPatterns[0],
            _ => CreateTuplePattern(parameterPatterns)
        };
        var branch = new PatternBranch();
        branch.SetSpan(_attributeSpan);
        branch.SetPattern(entryPattern);
        branch.SetExpression(body);

        function.SetSpan(_attributeSpan);
        function.SetName(name);
        function.SetTypeParams(CloneTargetTypeParameters());
        function.SetSignature(CreateFunctionType(parameterTypes, CreateTypeNode(resultType)));
        function.SetBody([branch]);
        if (implTrait != null)
        {
            function.SetAttributes([CreateImplAttribute(implTrait)]);
        }

        return true;
    }

    private bool TryCreateImplementation(
        ComptimeMetaObjectValue declaration,
        int outputIndex,
        List<MaterializedMetaDeclaration> declarations,
        out string reason)
    {
        reason = string.Empty;
        if (!TryGetDecl(declaration, "trait", out var trait, out reason) ||
            _symbolTable.GetSymbol<TraitSymbol>(trait.SymbolId) == null)
        {
            reason = string.IsNullOrWhiteSpace(reason)
                ? "Meta.implementation requires a trait declaration handle"
                : reason;
            return false;
        }

        if (!TryGetType(declaration, "target", out var implementationTarget, out reason) ||
            !string.Equals(
                implementationTarget.TypeRef.StableIdentity,
                MetaComptimeIntrinsics.CreateAdtTargetTypeValue(_target, _symbolTable).TypeRef.StableIdentity,
                StringComparison.Ordinal))
        {
            reason = "Meta.implementation target must be the current derive target";
            return false;
        }

        if (!TryGetSequence(declaration, "methods", out var methodValues, out reason))
        {
            return false;
        }

        for (var methodIndex = 0; methodIndex < methodValues.Count; methodIndex++)
        {
            if (methodValues[methodIndex] is not ComptimeMetaObjectValue { SchemaKind: "declaration.function" } methodValue ||
                !TryCreateFunction(methodValue, trait, out var method, out reason))
            {
                reason = string.IsNullOrWhiteSpace(reason)
                    ? $"implementation method {methodIndex} must be a structured function declaration"
                    : reason;
                return false;
            }

            declarations.Add(new MaterializedMetaDeclaration(method, outputIndex, methodIndex));
        }

        return true;
    }

    private bool TryCreateComptimeValue(
        ComptimeMetaObjectValue declaration,
        out LetDecl let,
        out string reason)
    {
        let = new LetDecl();
        if (!TryGetString(declaration, "name", out var name, out reason) ||
            !ValidateGeneratedComptimeName(name, out reason) ||
            !TryGetType(declaration, "type", out var type, out reason) ||
            !TryGetObject(declaration, "value", out var valueObject, out reason) ||
            !TryCreateExpression(valueObject, new Dictionary<string, string>(StringComparer.Ordinal), out var expression, out reason))
        {
            return false;
        }

        var pattern = new VarPattern();
        pattern.SetSpan(_attributeSpan);
        pattern.SetName(name);
        let.SetSpan(_attributeSpan);
        let.SetPattern(pattern);
        let.SetTypeAnnotation(CreateTypeNode(type));
        let.SetComptime(true);
        let.SetValue(expression);
        return true;
    }

    private bool TryCreateAttributeAttachment(
        ComptimeMetaObjectValue declaration,
        int outputIndex,
        out MetaAttributeAttachment attachment,
        out string reason)
    {
        attachment = null!;
        if (!TryGetDecl(declaration, "target", out var target, out reason) ||
            !TryGetString(declaration, "name", out var name, out reason) ||
            !TryGetSequence(declaration, "arguments", out var argumentValues, out reason))
        {
            return false;
        }

        if (string.IsNullOrWhiteSpace(name) || name.StartsWith("__", StringComparison.Ordinal))
        {
            reason = $"invalid generated attribute name '{name}'";
            return false;
        }

        var arguments = new List<string>(argumentValues.Count);
        foreach (var argument in argumentValues)
        {
            if (argument is not ComptimeStringValue stringValue)
            {
                reason = "generated attribute arguments must be strings";
                return false;
            }

            arguments.Add(stringValue.Value);
        }

        attachment = new MetaAttributeAttachment(target, name, arguments, outputIndex);
        return true;
    }

    private bool TryCreateTest(
        ComptimeMetaObjectValue declaration,
        out FuncDef test,
        out string reason)
    {
        test = new FuncDef();
        if (!TryGetString(declaration, "name", out var name, out reason) ||
            !ValidateGeneratedValueName(name, out reason) ||
            !TryGetObject(declaration, "body", out var bodyObject, out reason) ||
            !TryCreateExpression(bodyObject, new Dictionary<string, string>(StringComparer.Ordinal), out var body, out reason))
        {
            return false;
        }

        var branch = new PatternBranch();
        branch.SetSpan(_attributeSpan);
        branch.SetPattern(CreateWildcardPattern());
        branch.SetExpression(body);
        var testAttribute = new AstAttribute();
        testAttribute.SetSpan(_attributeSpan);
        testAttribute.SetName("test");
        test.SetSpan(_attributeSpan);
        test.SetName(name);
        test.SetSignature(CreateFunctionType([CreateSimpleTypePath("Unit")], CreateSimpleTypePath("Unit")));
        test.SetBody([branch]);
        test.SetAttributes([testAttribute]);
        return true;
    }

    private bool TryReadDiagnostic(
        ComptimeMetaObjectValue declaration,
        int outputIndex,
        out MetaExpansionDiagnostic diagnostic,
        out string reason)
    {
        diagnostic = null!;
        if (!TryGetString(declaration, "level", out var level, out reason) ||
            !TryGetObject(declaration, "span", out var spanObject, out reason) ||
            !MetaComptimeIntrinsics.TryReadSpan(spanObject, out var span) ||
            !TryGetString(declaration, "message", out var message, out reason))
        {
            reason = string.IsNullOrWhiteSpace(reason) ? "invalid structured diagnostic" : reason;
            return false;
        }

        var normalizedLevel = level.ToLowerInvariant();
        if (normalizedLevel is not ("error" or "warning"))
        {
            reason = $"unsupported structured diagnostic level '{level}'";
            return false;
        }

        diagnostic = new MetaExpansionDiagnostic(normalizedLevel, span, message, outputIndex);
        return true;
    }

    private bool TryCreateExpression(
        ComptimeMetaObjectValue expression,
        Dictionary<string, string> bindings,
        out EidosAstNode result,
        out string reason)
    {
        result = null!;
        reason = string.Empty;
        switch (expression.SchemaKind)
        {
            case "expr.parameter":
                return TryCreateHandleReference(expression, "parameter", bindings, out result, out reason);
            case "expr.binding":
                return TryCreateHandleReference(expression, "binding", bindings, out result, out reason);
            case "expr.decl":
                if (!TryGetDecl(expression, "decl", out var declaration, out reason))
                {
                    return false;
                }

                result = CreateDeclarationReference(declaration);
                return true;
            case "expr.int":
                return TryCreateLiteral<ComptimeIntegerValue>(expression, value => value.Value.ToString(System.Globalization.CultureInfo.InvariantCulture), out result, out reason);
            case "expr.bool":
                return TryCreateLiteral<ComptimeBoolValue>(expression, value => value.Value ? "true" : "false", out result, out reason);
            case "expr.string":
                return TryCreateLiteral<ComptimeStringValue>(expression, value => $"\"{EscapeString(value.Value)}\"", out result, out reason);
            case "expr.unit":
                result = CreateLiteral("()");
                return true;
            case "expr.call":
                return TryCreateCall(expression, bindings, out result, out reason);
            case "expr.constructor":
                return TryCreateConstructor(expression, "arguments", bindings, named: false, out result, out reason);
            case "expr.record-constructor":
                return TryCreateConstructor(expression, "fields", bindings, named: true, out result, out reason);
            case "expr.field":
                return TryCreateFieldAccess(expression, bindings, out result, out reason);
            case "expr.binary":
                return TryCreateBinary(expression, bindings, out result, out reason);
            case "expr.tuple":
                return TryCreateSequenceExpression<TupleExpr>(expression, bindings, out result, out reason);
            case "expr.list":
                return TryCreateSequenceExpression<ListExpr>(expression, bindings, out result, out reason);
            case "expr.match":
                return TryCreateMatch(expression, bindings, out result, out reason);
            default:
                reason = $"unsupported structured expression kind '{expression.SchemaKind}'";
                return false;
        }
    }

    private bool TryCreateHandleReference(
        ComptimeMetaObjectValue expression,
        string property,
        IReadOnlyDictionary<string, string> bindings,
        out EidosAstNode result,
        out string reason)
    {
        result = null!;
        if (!TryGetObject(expression, property, out var handle, out reason) ||
            !TryGetString(handle, "identity", out var identity, out reason) ||
            !bindings.TryGetValue(identity, out var name))
        {
            reason = string.IsNullOrWhiteSpace(reason)
                ? "structured expression references a binding outside its hygiene scope"
                : reason;
            return false;
        }

        var identifier = new IdentifierExpr();
        identifier.SetSpan(_attributeSpan);
        identifier.SetName(name);
        result = identifier;
        return true;
    }

    private bool TryCreateLiteral<T>(
        ComptimeMetaObjectValue expression,
        Func<T, string> format,
        out EidosAstNode result,
        out string reason)
        where T : ComptimeValue
    {
        result = null!;
        if (!expression.TryGet("value", out var value) || value is not T typed)
        {
            reason = $"{expression.SchemaKind} contains an invalid literal value";
            return false;
        }

        result = CreateLiteral(format(typed));
        reason = string.Empty;
        return true;
    }

    private bool TryCreateCall(
        ComptimeMetaObjectValue expression,
        Dictionary<string, string> bindings,
        out EidosAstNode result,
        out string reason)
    {
        result = null!;
        if (!TryGetObject(expression, "callee", out var calleeObject, out reason) ||
            !TryCreateExpression(calleeObject, bindings, out var callee, out reason) ||
            !TryGetSequence(expression, "arguments", out var argumentValues, out reason))
        {
            return false;
        }

        var call = new CallExpr();
        call.SetSpan(_attributeSpan);
        call.SetFunction(callee);
        foreach (var argumentValue in argumentValues)
        {
            if (argumentValue is not ComptimeMetaObjectValue argumentObject ||
                !TryCreateExpression(argumentObject, bindings, out var argument, out reason))
            {
                reason = string.IsNullOrWhiteSpace(reason) ? "invalid structured call argument" : reason;
                return false;
            }

            call.AddPositionalArg(argument);
        }

        result = call;
        return true;
    }

    private bool TryCreateConstructor(
        ComptimeMetaObjectValue expression,
        string valuesProperty,
        Dictionary<string, string> bindings,
        bool named,
        out EidosAstNode result,
        out string reason)
    {
        result = null!;
        if (!TryGetDecl(expression, "constructor", out var constructor, out reason) ||
            _symbolTable.GetSymbol<CtorSymbol>(constructor.SymbolId) == null ||
            !TryGetSequence(expression, valuesProperty, out var values, out reason))
        {
            reason = string.IsNullOrWhiteSpace(reason) ? "structured constructor requires a constructor handle" : reason;
            return false;
        }

        var ctor = new CtorExpr();
        ctor.SetSpan(_attributeSpan);
        ctor.SetConstructorPath(CreateDeclarationTypePath(constructor));
        if (!named)
        {
            foreach (var value in values)
            {
                if (value is not ComptimeMetaObjectValue valueObject ||
                    !TryCreateExpression(valueObject, bindings, out var argument, out reason))
                {
                    reason = string.IsNullOrWhiteSpace(reason) ? "invalid constructor argument" : reason;
                    return false;
                }

                ctor.AddPositionalArg(argument);
            }
        }
        else
        {
            foreach (var value in values)
            {
                if (value is not ComptimeMetaObjectValue { SchemaKind: "named-expr" } namedValue ||
                    !TryGetObject(namedValue, "field", out var field, out reason) ||
                    !TryGetString(field, "name", out var fieldName, out reason) ||
                    !TryGetObject(namedValue, "expression", out var fieldExpression, out reason) ||
                    !TryCreateExpression(fieldExpression, bindings, out var fieldValue, out reason))
                {
                    reason = string.IsNullOrWhiteSpace(reason) ? "invalid named constructor argument" : reason;
                    return false;
                }

                var fieldInit = new FieldInit();
                fieldInit.SetSpan(_attributeSpan);
                fieldInit.SetFieldName(fieldName);
                fieldInit.SetValue(fieldValue);
                ctor.AddNamedArg(fieldInit);
            }
        }

        result = ctor;
        return true;
    }

    private bool TryCreateFieldAccess(
        ComptimeMetaObjectValue expression,
        Dictionary<string, string> bindings,
        out EidosAstNode result,
        out string reason)
    {
        result = null!;
        if (!TryGetObject(expression, "subject", out var subjectObject, out reason) ||
            !TryCreateExpression(subjectObject, bindings, out var subject, out reason) ||
            !TryGetObject(expression, "field", out var field, out reason) ||
            !TryGetString(field, "name", out var fieldName, out reason))
        {
            return false;
        }

        var access = new MethodCallExpr();
        access.SetSpan(_attributeSpan);
        access.SetReceiver(subject);
        access.SetMethodName(fieldName);
        result = access;
        return true;
    }

    private bool TryCreateBinary(
        ComptimeMetaObjectValue expression,
        Dictionary<string, string> bindings,
        out EidosAstNode result,
        out string reason)
    {
        result = null!;
        if (!TryGetString(expression, "operator", out var operatorName, out reason) ||
            !TryMapBinaryOperator(operatorName, out var op) ||
            !TryGetObject(expression, "left", out var leftObject, out reason) ||
            !TryGetObject(expression, "right", out var rightObject, out reason) ||
            !TryCreateExpression(leftObject, bindings, out var left, out reason) ||
            !TryCreateExpression(rightObject, bindings, out var right, out reason))
        {
            reason = string.IsNullOrWhiteSpace(reason) ? $"unsupported structured binary operator '{operatorName}'" : reason;
            return false;
        }

        var binary = new BinaryExpr();
        binary.SetSpan(_attributeSpan);
        binary.SetOperator(op);
        binary.SetLeft(left);
        binary.SetRight(right);
        result = binary;
        return true;
    }

    private bool TryCreateSequenceExpression<T>(
        ComptimeMetaObjectValue expression,
        Dictionary<string, string> bindings,
        out EidosAstNode result,
        out string reason)
        where T : EidosAstNode, new()
    {
        result = null!;
        if (!TryGetSequence(expression, "elements", out var values, out reason))
        {
            return false;
        }

        var elements = new List<EidosAstNode>(values.Count);
        foreach (var value in values)
        {
            if (value is not ComptimeMetaObjectValue valueObject ||
                !TryCreateExpression(valueObject, bindings, out var element, out reason))
            {
                reason = string.IsNullOrWhiteSpace(reason) ? "invalid structured sequence element" : reason;
                return false;
            }

            elements.Add(element);
        }

        if (typeof(T) == typeof(TupleExpr))
        {
            result = new TupleExpr { Elements = elements };
        }
        else
        {
            var list = new ListExpr();
            list.SetSpan(_attributeSpan);
            foreach (var element in elements)
            {
                list.AddElement(element);
            }

            result = list;
        }

        return true;
    }

    private bool TryCreateMatch(
        ComptimeMetaObjectValue expression,
        Dictionary<string, string> bindings,
        out EidosAstNode result,
        out string reason)
    {
        result = null!;
        if (!TryGetObject(expression, "subject", out var subjectObject, out reason) ||
            !TryCreateExpression(subjectObject, bindings, out var subject, out reason) ||
            !TryGetSequence(expression, "branches", out var branchValues, out reason))
        {
            return false;
        }

        var match = new MatchExpr();
        match.SetSpan(_attributeSpan);
        match.SetMatchedExpression(subject);
        foreach (var branchValue in branchValues)
        {
            if (branchValue is not ComptimeMetaObjectValue { SchemaKind: "branch" } branchObject ||
                !TryGetObject(branchObject, "pattern", out var patternObject, out reason) ||
                !TryGetObject(branchObject, "expression", out var expressionObject, out reason))
            {
                reason = string.IsNullOrWhiteSpace(reason) ? "invalid structured match branch" : reason;
                return false;
            }

            var branchBindings = new Dictionary<string, string>(bindings, StringComparer.Ordinal);
            if (!TryCreatePattern(patternObject, branchBindings, out var pattern, out reason) ||
                !TryCreateExpression(expressionObject, branchBindings, out var branchExpression, out reason))
            {
                return false;
            }

            var branch = new PatternBranch();
            branch.SetSpan(_attributeSpan);
            branch.SetPattern(pattern);
            branch.SetExpression(branchExpression);
            match.AddBranch(branch);
        }

        result = match;
        return true;
    }

    private bool TryCreatePattern(
        ComptimeMetaObjectValue patternValue,
        Dictionary<string, string> bindings,
        out Pattern pattern,
        out string reason)
    {
        pattern = null!;
        reason = string.Empty;
        switch (patternValue.SchemaKind)
        {
            case "pattern.wildcard":
                pattern = CreateWildcardPattern();
                return true;
            case "pattern.binding":
                if (!TryGetObject(patternValue, "binding", out var binding, out reason) ||
                    !TryGetString(binding, "identity", out var identity, out reason) ||
                    !TryGetString(binding, "name", out var requestedName, out reason))
                {
                    return false;
                }

                var name = CreateHygienicName("b", identity, requestedName);
                if (!bindings.TryAdd(identity, name))
                {
                    reason = $"duplicate generated binding identity '{identity}'";
                    return false;
                }

                var variable = new VarPattern();
                variable.SetSpan(_attributeSpan);
                variable.SetName(name);
                pattern = variable;
                return true;
            case "pattern.constructor":
                return TryCreateConstructorPattern(patternValue, bindings, named: false, out pattern, out reason);
            case "pattern.record-constructor":
                return TryCreateConstructorPattern(patternValue, bindings, named: true, out pattern, out reason);
            default:
                reason = $"unsupported structured pattern kind '{patternValue.SchemaKind}'";
                return false;
        }
    }

    private bool TryCreateConstructorPattern(
        ComptimeMetaObjectValue patternValue,
        Dictionary<string, string> bindings,
        bool named,
        out Pattern pattern,
        out string reason)
    {
        pattern = null!;
        if (!TryGetDecl(patternValue, "constructor", out var constructor, out reason) ||
            _symbolTable.GetSymbol<CtorSymbol>(constructor.SymbolId) == null ||
            !TryGetSequence(patternValue, named ? "fields" : "patterns", out var values, out reason))
        {
            reason = string.IsNullOrWhiteSpace(reason) ? "structured constructor pattern requires a constructor handle" : reason;
            return false;
        }

        var ctorPattern = new CtorPattern();
        ctorPattern.SetSpan(_attributeSpan);
        ApplyDeclarationPath(ctorPattern, constructor);
        if (!named)
        {
            foreach (var value in values)
            {
                if (value is not ComptimeMetaObjectValue valueObject ||
                    !TryCreatePattern(valueObject, bindings, out var childPattern, out reason))
                {
                    reason = string.IsNullOrWhiteSpace(reason) ? "invalid constructor subpattern" : reason;
                    return false;
                }

                ctorPattern.AddPositionalPattern(childPattern);
            }
        }
        else
        {
            foreach (var value in values)
            {
                if (value is not ComptimeMetaObjectValue { SchemaKind: "field-pattern" } fieldPatternValue ||
                    !TryGetObject(fieldPatternValue, "field", out var field, out reason) ||
                    !TryGetString(field, "name", out var fieldName, out reason) ||
                    !TryGetObject(fieldPatternValue, "pattern", out var childValue, out reason) ||
                    !TryCreatePattern(childValue, bindings, out var childPattern, out reason))
                {
                    reason = string.IsNullOrWhiteSpace(reason) ? "invalid record constructor field pattern" : reason;
                    return false;
                }

                var fieldPattern = new FieldPattern();
                fieldPattern.SetSpan(_attributeSpan);
                fieldPattern.SetFieldName(fieldName);
                fieldPattern.SetPattern(childPattern);
                ctorPattern.AddNamedPattern(fieldPattern);
            }
        }

        pattern = ctorPattern;
        return true;
    }

    private EidosAstNode CreateDeclarationReference(ComptimeDeclValue declaration)
    {
        if (_symbolTable.Modules.TryGetOwningModule(declaration.SymbolId, out var module) &&
            module.Path.Count > 0 &&
            !module.Path.SequenceEqual(_symbolTable.Modules.GetModule(_targetModuleId)?.Path ?? []))
        {
            var path = new PathExpr();
            path.SetSpan(_attributeSpan);
            path.SetModulePath(module.Path);
            path.SetName(declaration.Name);
            return path;
        }

        var identifier = new IdentifierExpr();
        identifier.SetSpan(_attributeSpan);
        identifier.SetName(declaration.Name);
        return identifier;
    }

    private TypePath CreateDeclarationTypePath(ComptimeDeclValue declaration)
    {
        var path = new TypePath();
        path.SetSpan(_attributeSpan);
        path.SetTypeName(declaration.Name);
        if (_symbolTable.Modules.TryGetOwningModule(declaration.SymbolId, out var module))
        {
            path.ModulePath = [.. module.Path];
        }

        return path;
    }

    private void ApplyDeclarationPath(CtorPattern pattern, ComptimeDeclValue declaration)
    {
        pattern.SetConstructorName(declaration.Name);
        if (_symbolTable.Modules.TryGetOwningModule(declaration.SymbolId, out var module))
        {
            pattern.SetModulePath(module.Path);
        }
    }

    private AstAttribute CreateImplAttribute(ComptimeDeclValue trait)
    {
        var attribute = new AstAttribute();
        attribute.SetSpan(_attributeSpan);
        attribute.SetName("impl");
        var display = FormatDeclarationPath(trait);
        attribute.AddArgumentText(display);
        return attribute;
    }

    private string FormatDeclarationPath(ComptimeDeclValue declaration)
    {
        return _symbolTable.Modules.TryGetOwningModule(declaration.SymbolId, out var module) && module.Path.Count > 0
            ? string.Join(WellKnownStrings.Separators.Path, module.Path.Concat([declaration.Name]))
            : declaration.Name;
    }

    private List<TypeParam> CloneTargetTypeParameters()
    {
        return _target.TypeParams.Select(CloneTypeParameter).ToList();
    }

    private TypeParam CloneTypeParameter(TypeParam parameter)
    {
        return new TypeParam
        {
            Name = parameter.Name,
            KindAnnotation = parameter.KindAnnotation,
            IsEffectSet = parameter.IsEffectSet,
            IsComptime = parameter.IsComptime,
            ComptimeTypeAnnotation = parameter.ComptimeTypeAnnotation == null ? null : CloneTypeNode(parameter.ComptimeTypeAnnotation),
            TraitConstraints = parameter.TraitConstraints.Select(CloneTraitRef).ToList(),
            Span = parameter.Span
        };
    }

    private TraitRef CloneTraitRef(TraitRef trait)
    {
        var clone = new TraitRef
        {
            ModulePath = [.. trait.ModulePath],
            TypeArgs = trait.TypeArgs.Select(CloneTypeNode).ToList()
        };
        if (trait.GenericArguments.Count > 0)
        {
            clone.GenericArguments = trait.GenericArguments.Select(CloneGenericArgument).ToList();
            clone.TypeArgs = clone.GenericArguments
                .OfType<TypeGenericArgumentNode>()
                .Select(static argument => argument.Type)
                .ToList();
        }

        clone.SetSpan(trait.Span);
        clone.SetTraitName(trait.TraitName);
        return clone;
    }

    private TypeNode CreateTypeNode(ComptimeTypeValue type)
    {
        if (type.TypeRef.InternalSyntax != null)
        {
            return CloneTypeNode(type.TypeRef.InternalSyntax);
        }

        var symbol = type.TypeRef.SymbolId.IsValid
            ? _symbolTable.GetSymbol(type.TypeRef.SymbolId)
            : null;
        var path = CreateSimpleTypePath(symbol?.Name ?? ExtractUnqualifiedTypeName(type.TypeRef.Name));
        path.SymbolId = type.TypeRef.SymbolId;
        if (type.TypeRef.SymbolId.IsValid &&
            _symbolTable.Modules.TryGetOwningModule(type.TypeRef.SymbolId, out var owner))
        {
            path.ModulePath = [.. owner.Path];
        }

        if (type.TypeRef.GenericArguments is { Count: > 0 } genericArguments)
        {
            path.SetGenericArguments(genericArguments.Select(CreateGenericArgumentNode));
        }
        else
        {
            path.TypeArgs = type.TypeRef.Arguments
                .Select(argument => CreateTypeNode(new ComptimeTypeValue(argument)))
                .ToList();
        }

        return path;
    }

    private TypeNode CloneTypeNode(TypeNode type)
    {
        return type switch
        {
            TypePath path => CloneTypePath(path),
            TupleType tuple => new TupleType { Elements = tuple.Elements.Select(CloneTypeNode).ToList(), Span = _attributeSpan },
            ArrowType arrow => CreateArrowType(CloneTypeNode(arrow.ParamType), CloneTypeNode(arrow.ReturnType)),
            EffectfulType effectful => new EffectfulType
            {
                InputType = CloneTypeNode(effectful.InputType),
                OutputType = effectful.OutputType == null ? null : CloneTypeNode(effectful.OutputType),
                EffectPath = [.. effectful.EffectPath],
                EffectPaths = effectful.EffectPaths.Select(static path => path.ToList()).ToList(),
                EffectPathSpans = [.. effectful.EffectPathSpans],
                Span = _attributeSpan
            },
            _ => type
        };
    }

    private TypePath CloneTypePath(TypePath path)
    {
        var clone = new TypePath
        {
            ModulePath = [.. path.ModulePath],
            TypeArgs = path.TypeArgs.Select(CloneTypeNode).ToList()
        };
        clone.SetPackageAlias(path.PackageAlias);
        clone.SetTypeName(path.TypeName);
        clone.SetSpan(_attributeSpan);
        clone.SymbolId = path.SymbolId;
        if (path.GenericArguments.Count > 0)
        {
            clone.SetGenericArguments(path.GenericArguments.Select(CloneGenericArgument));
        }

        return clone;
    }

    private GenericArgumentNode CloneGenericArgument(GenericArgumentNode argument) => argument switch
    {
        TypeGenericArgumentNode typeArgument => new TypeGenericArgumentNode
        {
            Type = CloneTypeNode(typeArgument.Type),
            Span = _attributeSpan
        },
        ValueGenericArgumentNode valueArgument => new ValueGenericArgumentNode
        {
            Expression = CloneGenericValueExpression(valueArgument.Expression),
            Span = _attributeSpan
        },
        EffectGenericArgumentNode effectArgument => new EffectGenericArgumentNode
        {
            EffectRow = CloneTypeNode(effectArgument.EffectRow),
            Span = _attributeSpan
        },
        UnresolvedGenericArgumentNode unresolved => new UnresolvedGenericArgumentNode
        {
            TypeCandidate = unresolved.TypeCandidate == null ? null : CloneTypeNode(unresolved.TypeCandidate),
            ValueCandidate = unresolved.ValueCandidate == null ? null : CloneGenericValueExpression(unresolved.ValueCandidate),
            Span = _attributeSpan
        },
        _ => argument
    };

    private GenericArgumentNode CreateGenericArgumentNode(MetaGenericArgumentRef argument)
    {
        return argument.Domain switch
        {
            "type" when argument.Type != null => new TypeGenericArgumentNode
            {
                Type = CreateTypeNode(new ComptimeTypeValue(argument.Type)),
                Span = _attributeSpan
            },
            "effect-row" when argument.Type != null => new EffectGenericArgumentNode
            {
                EffectRow = CreateTypeNode(new ComptimeTypeValue(argument.Type)),
                Span = _attributeSpan
            },
            "value" => new ValueGenericArgumentNode
            {
                Expression = CreateGenericValueExpression(argument),
                Span = _attributeSpan
            },
            _ => new UnresolvedGenericArgumentNode
            {
                ValueCandidate = CreateGenericValueExpression(argument),
                Span = _attributeSpan
            }
        };
    }

    private EidosAstNode CreateGenericValueExpression(MetaGenericArgumentRef argument)
    {
        if (argument.SymbolId.IsValid)
        {
            var identifier = new IdentifierExpr();
            identifier.SetSpan(_attributeSpan);
            identifier.SetName(argument.Display);
            identifier.SymbolId = argument.SymbolId;
            return identifier;
        }

        return CreateLiteral(argument.Display);
    }

    private EidosAstNode CloneGenericValueExpression(EidosAstNode expression)
    {
        switch (expression)
        {
            case LiteralExpr literal:
                return CreateLiteral(literal.RawText);
            case IdentifierExpr identifier:
                var identifierClone = new IdentifierExpr();
                identifierClone.SetSpan(_attributeSpan);
                identifierClone.SetName(identifier.Name);
                identifierClone.SymbolId = identifier.SymbolId;
                return identifierClone;
            case PathExpr path:
                var pathClone = new PathExpr();
                pathClone.SetSpan(_attributeSpan);
                pathClone.SetPackageAlias(path.PackageAlias);
                pathClone.SetModulePath(path.ModulePath);
                pathClone.SetName(path.Name);
                pathClone.SymbolId = path.SymbolId;
                pathClone.SetTypeArgs(path.TypeArgs.Select(CloneTypeNode).ToList());
                pathClone.SetGenericArguments(path.GenericArguments.Select(CloneGenericArgument));
                return pathClone;
            case UnaryExpr unary when unary.Operand != null:
                var unaryClone = new UnaryExpr();
                unaryClone.SetSpan(_attributeSpan);
                unaryClone.SetOperator(unary.Operator);
                unaryClone.SetOperand(CloneGenericValueExpression(unary.Operand));
                return unaryClone;
            case BinaryExpr binary when binary.Left != null && binary.Right != null:
                var binaryClone = new BinaryExpr();
                binaryClone.SetSpan(_attributeSpan);
                binaryClone.SetOperator(binary.Operator);
                binaryClone.SetLeft(CloneGenericValueExpression(binary.Left));
                binaryClone.SetRight(CloneGenericValueExpression(binary.Right));
                return binaryClone;
            case CallExpr call when call.Function != null:
                var callClone = new CallExpr();
                callClone.SetSpan(_attributeSpan);
                callClone.SetFunction(CloneGenericValueExpression(call.Function));
                foreach (var argument in call.PositionalArgs)
                {
                    callClone.AddPositionalArg(CloneGenericValueExpression(argument));
                }

                return callClone;
            case TupleExpr tuple:
                return new TupleExpr
                {
                    Elements = tuple.Elements.Select(CloneGenericValueExpression).ToList(),
                    Span = _attributeSpan
                };
            case ListExpr list:
                var listClone = new ListExpr();
                listClone.SetSpan(_attributeSpan);
                foreach (var element in list.Elements)
                {
                    listClone.AddElement(CloneGenericValueExpression(element));
                }

                return listClone;
            default:
                return expression with { Span = _attributeSpan };
        }
    }

    private static string ExtractUnqualifiedTypeName(string name)
    {
        var withoutArguments = name.Split('[', 2)[0];
        return withoutArguments.Contains(WellKnownStrings.Separators.Path, StringComparison.Ordinal)
            ? withoutArguments.Split(WellKnownStrings.Separators.Path)[^1]
            : withoutArguments;
    }

    private TypePath CreateSimpleTypePath(string name)
    {
        var path = new TypePath();
        path.SetSpan(_attributeSpan);
        path.SetTypeName(name);
        return path;
    }

    private TypeNode CreateFunctionType(IReadOnlyList<TypeNode> parameters, TypeNode result)
    {
        var current = result;
        var effectiveParameters = parameters.Count == 0 ? [CreateSimpleTypePath("Unit")] : parameters;
        for (var index = effectiveParameters.Count - 1; index >= 0; index--)
        {
            current = CreateArrowType(effectiveParameters[index], current);
        }

        return current;
    }

    private ArrowType CreateArrowType(TypeNode parameter, TypeNode result)
    {
        var arrow = new ArrowType();
        arrow.SetSpan(_attributeSpan);
        arrow.SetParamType(parameter);
        arrow.SetReturnType(result);
        return arrow;
    }

    private static bool TryMapBinaryOperator(string name, out BinaryOp op)
    {
        op = name switch
        {
            "add" or "+" => BinaryOp.Add,
            "subtract" or "-" => BinaryOp.Subtract,
            "multiply" or "*" => BinaryOp.Multiply,
            "divide" or "/" => BinaryOp.Divide,
            "modulo" or "%" => BinaryOp.Modulo,
            "equal" or "==" => BinaryOp.Equal,
            "notEqual" or "!=" => BinaryOp.NotEqual,
            "less" or "<" => BinaryOp.Less,
            "lessEqual" or "<=" => BinaryOp.LessEqual,
            "greater" or ">" => BinaryOp.Greater,
            "greaterEqual" or ">=" => BinaryOp.GreaterEqual,
            "and" or "&&" => BinaryOp.And,
            "or" or "||" => BinaryOp.Or,
            "concat" or "++" => BinaryOp.Concat,
            _ => (BinaryOp)(-1)
        };
        return (int)op >= 0;
    }

    private static LiteralExpr CreateLiteral(string raw)
    {
        var literal = new LiteralExpr();
        literal.SetLiteral(raw);
        return literal;
    }

    private WildcardPattern CreateWildcardPattern()
    {
        var pattern = new WildcardPattern();
        pattern.SetSpan(_attributeSpan);
        return pattern;
    }

    private TuplePattern CreateTuplePattern(IEnumerable<Pattern> patterns)
    {
        var tuple = new TuplePattern();
        tuple.SetSpan(_attributeSpan);
        foreach (var pattern in patterns)
        {
            tuple.AddElement(pattern);
        }

        return tuple;
    }

    private static string CreateHygienicName(string prefix, string identity, string requestedName)
    {
        var hash = identity.Length >= 12 ? identity[..12] : identity;
        var suffix = new string(requestedName.Where(static ch => char.IsLetterOrDigit(ch) || ch == '_').ToArray());
        if (string.IsNullOrWhiteSpace(suffix))
        {
            suffix = prefix;
        }

        return $"meta_{prefix}_{hash}_{suffix}";
    }

    private static string EscapeString(string value) => value
        .Replace("\\", "\\\\", StringComparison.Ordinal)
        .Replace("\"", "\\\"", StringComparison.Ordinal)
        .Replace("\r", "\\r", StringComparison.Ordinal)
        .Replace("\n", "\\n", StringComparison.Ordinal)
        .Replace("\t", "\\t", StringComparison.Ordinal);

    private static bool ValidateGeneratedValueName(string name, out string reason)
    {
        if (string.IsNullOrWhiteSpace(name) ||
            !char.IsLower(name[0]) ||
            name.StartsWith("__", StringComparison.Ordinal) ||
            name.Contains("__spec_", StringComparison.Ordinal) ||
            name.Any(static ch => !(char.IsLetterOrDigit(ch) || ch == '_')))
        {
            reason = $"generated value declaration name '{name}' must be a valid lower-case Eidos identifier outside the reserved namespace";
            return false;
        }

        reason = string.Empty;
        return true;
    }

    private static bool ValidateGeneratedComptimeName(string name, out string reason)
    {
        if (string.IsNullOrWhiteSpace(name) ||
            !char.IsUpper(name[0]) ||
            name.StartsWith("__", StringComparison.Ordinal) ||
            name.Contains("__spec_", StringComparison.Ordinal) ||
            name.Any(static ch => !(char.IsLetterOrDigit(ch) || ch == '_')))
        {
            reason = $"generated comptime declaration name '{name}' must be a valid upper-case Eidos identifier outside the reserved namespace";
            return false;
        }

        reason = string.Empty;
        return true;
    }

    private static bool TryGetString(
        ComptimeMetaObjectValue value,
        string property,
        out string result,
        out string reason)
    {
        result = string.Empty;
        if (!value.TryGet(property, out var propertyValue) || propertyValue is not ComptimeStringValue stringValue)
        {
            reason = $"{value.SchemaKind} requires string property '{property}'";
            return false;
        }

        result = stringValue.Value;
        reason = string.Empty;
        return true;
    }

    private static bool TryGetType(
        ComptimeMetaObjectValue value,
        string property,
        out ComptimeTypeValue result,
        out string reason)
    {
        result = null!;
        if (!value.TryGet(property, out var propertyValue) || propertyValue is not ComptimeTypeValue typeValue)
        {
            reason = $"{value.SchemaKind} requires Type property '{property}'";
            return false;
        }

        result = typeValue;
        reason = string.Empty;
        return true;
    }

    private static bool TryGetDecl(
        ComptimeMetaObjectValue value,
        string property,
        out ComptimeDeclValue result,
        out string reason)
    {
        result = null!;
        if (!value.TryGet(property, out var propertyValue) || propertyValue is not ComptimeDeclValue declValue)
        {
            reason = $"{value.SchemaKind} requires declaration handle property '{property}'";
            return false;
        }

        result = declValue;
        reason = string.Empty;
        return true;
    }

    private static bool TryGetObject(
        ComptimeMetaObjectValue value,
        string property,
        out ComptimeMetaObjectValue result,
        out string reason)
    {
        result = null!;
        if (!value.TryGet(property, out var propertyValue) || propertyValue is not ComptimeMetaObjectValue objectValue)
        {
            reason = $"{value.SchemaKind} requires structured property '{property}'";
            return false;
        }

        result = objectValue;
        reason = string.Empty;
        return true;
    }

    private static bool TryGetSequence(
        ComptimeMetaObjectValue value,
        string property,
        out IReadOnlyList<ComptimeValue> result,
        out string reason)
    {
        result = [];
        if (!value.TryGet(property, out var propertyValue) ||
            propertyValue is not ComptimeSequenceValue { Kind: ComptimeSequenceKind.List } sequence)
        {
            reason = $"{value.SchemaKind} requires list property '{property}'";
            return false;
        }

        result = sequence.Elements;
        reason = string.Empty;
        return true;
    }
}
