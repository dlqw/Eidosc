using System.Xml;
using Eidosc.Ast.Expressions;
using Eidosc.Ast.Types;

namespace Eidosc.Ast.Declarations;

/// <summary>
/// 类型定义（代数数据类型、积类型、类型别名）
/// </summary>
/// <example>
/// type Option[T]
/// {
///     None |
///     Some[T]{value: T}
/// }
///
/// type Person
/// {
///     name: String,
///     age: Int
/// }
///
/// type None = Unit
/// </example>
public record AdtDef : Declaration
{
    /// <summary>
    /// 类型名称
    /// </summary>
    public string Name { get; private set; } = "";

    /// <summary>
    /// 类型参数
    /// </summary>
    public List<TypeParam> TypeParams { get; private set; } = [];

    /// <summary>
    /// 是否是类型别名
    /// </summary>
    public bool IsTypeAlias { get; private set; }

    /// <summary>
    /// 类型别名目标（仅当 IsTypeAlias 为 true 时有效）
    /// </summary>
    public TypeNode? AliasTarget { get; private set; }

    /// <summary>
    /// 构造器列表（仅当 IsTypeAlias 为 false 时有效）
    /// </summary>
    public List<Constructor> Constructors { get; private set; } = [];

    /// <summary>
    /// 字段列表（积类型）
    /// </summary>
    public List<Field> Fields { get; private set; } = [];

    /// <summary>
    /// Closed nominal case types declared directly in this type body.
    /// Constructors are a lowering projection; this list is the authoritative
    /// syntax model for the 0.7 <c>Case :: type</c> surface.
    /// </summary>
    public List<CaseTypeDef> Cases { get; private set; } = [];

    /// <summary>
    /// Lexical type-body member order, including pending member expansion sites.
    /// </summary>
    public List<EidosAstNode> Members { get; private set; } = [];

    public override void BuildFromCst(AstContext context, ConcreteSyntaxNode node)
    {
        Span = node.Span;
        ExtractAttributes(context, node);
        ExtractExportModifier(node);

        if (node is NonTerminalCstNode ntNode)
        {
            Name = TryExtractDeclaredTypeName(ntNode) ?? "";
            foreach (var child in ntNode.Children)
            {
                if (child is NonTerminalCstNode childNt)
                {
                    // 递归收集所有子节点中的 AST 节点
                    CollectFromNode(childNt);
                }
            }

            // 只有真正的 alias 形态才保留 AliasTarget。
            // 普通 ADT / 积类型里的字段或构造器参数同样会包含 TypeNode，
            // 不能因此把整个声明误判成 type alias。
            if (Constructors.Count > 0 || Fields.Count > 0)
            {
                IsTypeAlias = false;
                AliasTarget = null;
            }
        }
    }

    private string? TryExtractDeclaredTypeName(NonTerminalCstNode node)
    {
        var keywordSeen = false;
        return TryExtractDeclaredTypeNameCore(node, ref keywordSeen);
    }

    private string? TryExtractDeclaredTypeNameCore(ConcreteSyntaxNode node, ref bool keywordSeen)
    {
        if (node is TerminalCstNode terminal)
        {
            var text = GetTokenText(terminal);
            if (string.IsNullOrWhiteSpace(text) || IsPunctuation(text))
            {
                return null;
            }

            if (IsKeyword(text))
            {
                keywordSeen = true;
                return null;
            }

            return keywordSeen ? text : null;
        }

        if (node is not NonTerminalCstNode nonTerminal)
        {
            return null;
        }

        foreach (var child in nonTerminal.Children)
        {
            var name = TryExtractDeclaredTypeNameCore(child, ref keywordSeen);
            if (!string.IsNullOrWhiteSpace(name))
            {
                return name;
            }
        }

        return null;
    }

    private void CollectFromNode(NonTerminalCstNode node)
    {
        if (string.Equals(node.NonTerminal?.DebugName, "typeAlias", StringComparison.Ordinal) &&
            TryExtractAliasTargetFromTypeAlias(node, out var extractedAliasTarget))
        {
            IsTypeAlias = true;
            AliasTarget = extractedAliasTarget;
        }

        if (node.AstNode is TypeParam typeParam)
        {
            TypeParams.Add(typeParam);
        }
        else if (node.AstNode is TypeNode typeNode)
        {
            // 如果遇到类型节点，可能是类型别名
            IsTypeAlias = true;
            AliasTarget = typeNode;
        }
        else if (node.AstNode is Constructor ctor)
        {
            Constructors.Add(ctor);
        }
        else if (node.AstNode is Field field)
        {
            Fields.Add(field);
        }

        // 递归遍历子节点
        foreach (var child in node.Children)
        {
            if (child is NonTerminalCstNode childNt)
            {
                CollectFromNode(childNt);
            }
        }
    }

    private static bool TryExtractAliasTargetFromTypeAlias(NonTerminalCstNode node, out TypeNode aliasTarget)
    {
        aliasTarget = null!;
        var seenEquals = false;

        foreach (var child in node.Children)
        {
            if (child is TerminalCstNode terminal)
            {
                var text = GetTokenText(terminal);
                if (text == WellKnownStrings.Punctuation.Equals)
                {
                    seenEquals = true;
                    continue;
                }

                if (!seenEquals || string.IsNullOrWhiteSpace(text) || IsPunctuation(text))
                {
                    continue;
                }

                var simpleType = new TypePath();
                simpleType.SetTypeName(text);
                simpleType.SetSpan(terminal.Span);
                aliasTarget = simpleType;
                return true;
            }

            if (!seenEquals || child is not NonTerminalCstNode childNt)
            {
                continue;
            }

            if (childNt.AstNode is TypeNode typeNode)
            {
                aliasTarget = typeNode;
                return true;
            }

            if (TryExtractAliasTargetFromNestedNode(childNt, out var nestedTypeNode))
            {
                aliasTarget = nestedTypeNode;
                return true;
            }
        }

        return false;
    }

    private static bool TryExtractAliasTargetFromNestedNode(NonTerminalCstNode node, out TypeNode aliasTarget)
    {
        aliasTarget = null!;

        if (node.AstNode is TypeNode typeNode)
        {
            aliasTarget = typeNode;
            return true;
        }

        foreach (var child in node.Children)
        {
            if (child is NonTerminalCstNode childNt &&
                TryExtractAliasTargetFromNestedNode(childNt, out aliasTarget))
            {
                return true;
            }

            if (child is TerminalCstNode terminal)
            {
                var text = GetTokenText(terminal);
                if (string.IsNullOrWhiteSpace(text) || IsPunctuation(text))
                {
                    continue;
                }

                var simpleType = new TypePath();
                simpleType.SetTypeName(text);
                simpleType.SetSpan(terminal.Span);
                aliasTarget = simpleType;
                return true;
            }
        }

        return false;
    }

    private static bool IsKeyword(string text)
    {
        return text is WellKnownStrings.Keywords.Export or WellKnownStrings.XmlAttributes.Type;
    }

    internal void SetName(string name) => Name = name;
    internal void SetTypeParams(List<Eidosc.Ast.Types.TypeParam> typeParams) => TypeParams = typeParams;
    internal void SetTypeAlias(Eidosc.Ast.Types.TypeNode target) { IsTypeAlias = true; AliasTarget = target; }
    internal void SetConstructors(List<Constructor> ctors) => Constructors = ctors;
    internal void SetFields(List<Field> fields) => Fields = fields;
    internal void SetCases(List<CaseTypeDef> cases) => Cases = cases;
    internal void SetMembers(List<EidosAstNode> members) => Members = members;
    internal void AppendMember(EidosAstNode member) => Members.Add(member);
    internal bool ReplaceMemberExpansion(ExpandDeclaration expansion, IReadOnlyList<EidosAstNode> members) =>
        ReplaceMemberExpansionCore(Members, expansion, members);

    private static bool ReplaceMemberExpansionCore(
        List<EidosAstNode> owner,
        ExpandDeclaration expansion,
        IReadOnlyList<EidosAstNode> members)
    {
        var index = owner.FindIndex(member => ReferenceEquals(member, expansion));
        if (index < 0)
        {
            return false;
        }

        owner.RemoveAt(index);
        owner.InsertRange(index, members);
        return true;
    }

    public override XmlElement ToXmlElement(XmlDocument doc)
    {
        var element = CreateDeclarationElement(doc, WellKnownStrings.XmlElements.AdtDef);
        element.SetAttribute(WellKnownStrings.XmlAttributes.Name, Name);
        element.SetAttribute(WellKnownStrings.XmlAttributes.IsTypeAlias, IsTypeAlias.ToString());

        if (TypeParams.Count > 0)
        {
            var typeParamsElement = doc.CreateElement(WellKnownStrings.XmlElements.TypeParams);
            foreach (var param in TypeParams)
            {
                typeParamsElement.AppendChild(param.ToXmlElement(doc));
            }
            element.AppendChild(typeParamsElement);
        }

        if (IsTypeAlias && AliasTarget != null)
        {
            var aliasElement = doc.CreateElement(WellKnownStrings.XmlElements.AliasTarget);
            aliasElement.AppendChild(AliasTarget.ToXmlElement(doc));
            element.AppendChild(aliasElement);
        }

        if (Constructors.Count > 0)
        {
            var ctorsElement = doc.CreateElement(WellKnownStrings.XmlElements.Constructors);
            foreach (var ctor in Constructors)
            {
                ctorsElement.AppendChild(ctor.ToXmlElement(doc));
            }
            element.AppendChild(ctorsElement);
        }

        if (Fields.Count > 0)
        {
            var fieldsElement = doc.CreateElement(WellKnownStrings.XmlElements.Fields);
            foreach (var field in Fields)
            {
                fieldsElement.AppendChild(field.ToXmlElement(doc));
            }
            element.AppendChild(fieldsElement);
        }

        if (Cases.Count > 0)
        {
            var casesElement = doc.CreateElement("Cases");
            foreach (var caseType in Cases)
            {
                casesElement.AppendChild(caseType.ToXmlElement(doc));
            }
            element.AppendChild(casesElement);
        }

        return element;
    }
}

/// <summary>
/// A lexical, sealed case type. It has an exact nominal identity distinct from
/// both its parent type and its same-named value constructor.
/// </summary>
public record CaseTypeDef : Declaration
{
    public string Name { get; private set; } = "";

    public List<TypeParam> TypeParams { get; private set; } = [];

    public List<TypeNode> PositionalFields { get; private set; } = [];

    public List<Field> Fields { get; private set; } = [];

    public List<CaseTypeDef> Cases { get; private set; } = [];

    public List<EidosAstNode> Members { get; private set; } = [];

    /// <summary>
    /// Explicit parent specialization from the <c>case Parent[Args]</c> clause.
    /// Null means the lexical parent with its unchanged generic arguments.
    /// </summary>
    public TypeNode? ParentSpecialization { get; private set; }

    public SymbolId ConstructorSymbolId { get; internal set; } = SymbolId.None;

    public bool IsLeaf => Cases.Count == 0;

    public override void BuildFromCst(AstContext context, ConcreteSyntaxNode node)
    {
        Span = node.Span;
    }

    internal void SetName(string name) => Name = name;
    internal void SetTypeParams(List<TypeParam> typeParams) => TypeParams = typeParams;
    internal void AddPositionalField(TypeNode type) => PositionalFields.Add(type);
    internal void AddField(Field field)
    {
        Fields.Add(field);
        Members.Add(field);
    }
    internal void AddCase(CaseTypeDef caseType)
    {
        Cases.Add(caseType);
        Members.Add(caseType);
    }
    internal void AddMemberExpansion(ExpandDeclaration expansion) => Members.Add(expansion);
    internal void SetFields(List<Field> fields) => Fields = fields;
    internal void SetCases(List<CaseTypeDef> cases) => Cases = cases;
    internal void AppendMember(EidosAstNode member) => Members.Add(member);
    internal bool ReplaceMemberExpansion(ExpandDeclaration expansion, IReadOnlyList<EidosAstNode> members)
    {
        var index = Members.FindIndex(member => ReferenceEquals(member, expansion));
        if (index < 0)
        {
            return false;
        }

        Members.RemoveAt(index);
        Members.InsertRange(index, members);
        return true;
    }
    internal void SetParentSpecialization(TypeNode? parent) => ParentSpecialization = parent;

    public override XmlElement ToXmlElement(XmlDocument doc)
    {
        var element = CreateElement(doc, "CaseType");
        element.SetAttribute(WellKnownStrings.XmlAttributes.Name, Name);

        if (TypeParams.Count > 0)
        {
            var parameters = doc.CreateElement(WellKnownStrings.XmlElements.TypeParams);
            foreach (var parameter in TypeParams)
            {
                parameters.AppendChild(parameter.ToXmlElement(doc));
            }
            element.AppendChild(parameters);
        }

        if (PositionalFields.Count > 0)
        {
            var positional = doc.CreateElement(WellKnownStrings.XmlElements.PositionalArgs);
            foreach (var field in PositionalFields)
            {
                positional.AppendChild(field.ToXmlElement(doc));
            }
            element.AppendChild(positional);
        }

        if (Fields.Count > 0)
        {
            var fields = doc.CreateElement(WellKnownStrings.XmlElements.Fields);
            foreach (var field in Fields)
            {
                fields.AppendChild(field.ToXmlElement(doc));
            }
            element.AppendChild(fields);
        }

        if (Cases.Count > 0)
        {
            var cases = doc.CreateElement("Cases");
            foreach (var child in Cases)
            {
                cases.AppendChild(child.ToXmlElement(doc));
            }
            element.AppendChild(cases);
        }

        if (ParentSpecialization != null)
        {
            var parent = doc.CreateElement("ParentSpecialization");
            parent.AppendChild(ParentSpecialization.ToXmlElement(doc));
            element.AppendChild(parent);
        }

        return element;
    }
}

/// <summary>
/// 构造器
/// </summary>
public record Constructor : EidosAstNode
{
    /// <summary>
    /// 构造器名称
    /// </summary>
    public string Name { get; private set; } = "";

    /// <summary>
    /// 类型参数
    /// </summary>
    public List<TypeParam> TypeParams { get; private set; } = [];

    /// <summary>
    /// 位置参数类型
    /// </summary>
    public List<TypeNode> PositionalArgs { get; private set; } = [];

    /// <summary>
    /// 命名字段
    /// </summary>
    public List<Field> NamedArgs { get; private set; } = [];

    /// <summary>
    /// GADT-style constructor result type. When omitted, the result is the enclosing ADT applied to its type parameters.
    /// </summary>
    public TypeNode? ReturnType { get; private set; }

    public override void BuildFromCst(AstContext context, ConcreteSyntaxNode node)
    {
        Span = node.Span;
        Name = "";
        TypeParams.Clear();
        PositionalArgs.Clear();
        NamedArgs.Clear();

        if (node is not NonTerminalCstNode ntNode)
        {
            return;
        }

        foreach (var child in ntNode.Children)
        {
            if (child is TerminalCstNode term && TryAssignConstructorName(term))
            {
                continue;
            }

            if (child is not NonTerminalCstNode childNt)
            {
                continue;
            }

            CollectTypeParams(childNt);

            if (IsCtorArgsNode(childNt))
            {
                CollectCtorArgs(context, childNt);
                continue;
            }

            if (IsConstructorReturnTypeNode(childNt))
            {
                ReturnType = ExtractFirstTypeNode(childNt);
            }
        }
    }

    private bool TryAssignConstructorName(TerminalCstNode term)
    {
        if (Name.Length > 0 || !IsTypeIdentifierTerminal(term))
        {
            return false;
        }

        Name = GetTokenText(term);
        return Name.Length > 0;
    }

    private void CollectTypeParams(NonTerminalCstNode node)
    {
        if (node.AstNode is TypeParam typeParam)
        {
            TypeParams.Add(typeParam);
            return;
        }

        foreach (var child in node.Children)
        {
            if (child is NonTerminalCstNode childNt)
            {
                CollectTypeParams(childNt);
            }
        }
    }

    private void CollectCtorArgs(AstContext context, NonTerminalCstNode ctorArgsNode)
    {
        var namedFields = new List<Field>();
        var seenNamedKeys = new HashSet<string>(StringComparer.Ordinal);
        CollectNamedFields(context, ctorArgsNode, namedFields, seenNamedKeys);

        if (namedFields.Count > 0)
        {
            NamedArgs.AddRange(namedFields);
            return;
        }

        CollectPositionalTypes(ctorArgsNode);
    }

    private void CollectNamedFields(
        AstContext context,
        NonTerminalCstNode node,
        List<Field> namedFields,
        HashSet<string> seenNamedKeys)
    {
        if (node.AstNode is Field field)
        {
            AddNamedFieldIfUnique(field, namedFields, seenNamedKeys);
            return;
        }

        if (IsFieldNode(node))
        {
            var parsedField = new Field();
            parsedField.BuildFromCst(context, node);
            AddNamedFieldIfUnique(parsedField, namedFields, seenNamedKeys);
            return;
        }

        foreach (var child in node.Children)
        {
            if (child is NonTerminalCstNode childNt)
            {
                CollectNamedFields(context, childNt, namedFields, seenNamedKeys);
            }
        }
    }

    private static void AddNamedFieldIfUnique(
        Field field,
        List<Field> namedFields,
        HashSet<string> seenNamedKeys)
    {
        var key = $"{field.Name}@{field.Span.Position}:{field.Span.Length}";
        if (!seenNamedKeys.Add(key))
        {
            return;
        }

        namedFields.Add(field);
    }

    private void CollectPositionalTypes(NonTerminalCstNode ctorArgsNode)
    {
        foreach (var child in ctorArgsNode.Children)
        {
            if (child is TerminalCstNode term && TryCreateSimpleTypePath(term, out var terminalType))
            {
                PositionalArgs.Add(terminalType);
                continue;
            }

            if (child is not NonTerminalCstNode childNt)
            {
                continue;
            }

            if (childNt.AstNode is TypeNode typeNode)
            {
                PositionalArgs.Add(typeNode);
                continue;
            }

            if (childNt.AstNode is Field || IsFieldNode(childNt))
            {
                continue;
            }

            if (TryExtractTypeFromNode(childNt, out var extractedType))
            {
                PositionalArgs.Add(extractedType);
            }
        }
    }

    private static bool TryExtractTypeFromNode(ConcreteSyntaxNode node, out TypeNode typeNode)
    {
        typeNode = null!;

        if (node is TerminalCstNode term)
        {
            return TryCreateSimpleTypePath(term, out typeNode);
        }

        if (node is not NonTerminalCstNode ntNode)
        {
            return false;
        }

        if (ntNode.AstNode is TypeNode typedNode)
        {
            typeNode = typedNode;
            return true;
        }

        if (ntNode.AstNode is Field || IsFieldNode(ntNode))
        {
            return false;
        }

        foreach (var child in ntNode.Children)
        {
            if (TryExtractTypeFromNode(child, out typeNode))
            {
                return true;
            }
        }

        return false;
    }

    private static bool IsCtorArgsNode(NonTerminalCstNode node)
    {
        return string.Equals(node.NonTerminal?.DebugName, "ctorArgs", StringComparison.Ordinal);
    }

    private static bool IsConstructorReturnTypeNode(NonTerminalCstNode node)
    {
        return string.Equals(node.NonTerminal?.DebugName, "constructorReturnType", StringComparison.Ordinal);
    }

    private static TypeNode? ExtractFirstTypeNode(ConcreteSyntaxNode node)
    {
        if (node is NonTerminalCstNode { AstNode: TypeNode typeNode })
        {
            return typeNode;
        }

        if (node is not NonTerminalCstNode ntNode)
        {
            return null;
        }

        foreach (var child in ntNode.Children)
        {
            if (ExtractFirstTypeNode(child) is { } childType)
            {
                return childType;
            }
        }

        return null;
    }

    private static bool IsFieldNode(NonTerminalCstNode node)
    {
        return string.Equals(node.NonTerminal?.DebugName, "field", StringComparison.Ordinal);
    }

    private static bool TryCreateSimpleTypePath(TerminalCstNode term, out TypeNode typeNode)
    {
        typeNode = null!;
        if (!IsTypeIdentifierTerminal(term))
        {
            return false;
        }

        var typePath = new TypePath();
        typePath.SetTypeName(GetTokenText(term));
        typePath.SetSpan(term.Span);
        typeNode = typePath;
        return true;
    }

    private static bool IsTypeIdentifierTerminal(TerminalCstNode term)
    {
        return term.Terminal?.ToString() == WellKnownStrings.Terminals.Identifier;
    }

    internal void SetName(string name) => Name = name;
    internal void SetTypeParams(List<TypeParam> typeParams) => TypeParams = typeParams;
    internal void SetSpan(Utils.SourceSpan span) => Span = span;
    internal void AddPositionalArg(Eidosc.Ast.Types.TypeNode type) => PositionalArgs.Add(type);
    internal void AddNamedArg(Field field) => NamedArgs.Add(field);
    internal void InsertNamedArg(int index, Field field) => NamedArgs.Insert(index, field);
    internal void SetReturnType(Eidosc.Ast.Types.TypeNode? returnType) => ReturnType = returnType;

    public override XmlElement ToXmlElement(XmlDocument doc)
    {
        var element = CreateElement(doc, WellKnownStrings.XmlElements.Constructor);
        element.SetAttribute(WellKnownStrings.XmlAttributes.Name, Name);

        if (TypeParams.Count > 0)
        {
            var typeParamsElement = doc.CreateElement(WellKnownStrings.XmlElements.TypeParams);
            foreach (var param in TypeParams)
            {
                typeParamsElement.AppendChild(param.ToXmlElement(doc));
            }
            element.AppendChild(typeParamsElement);
        }

        if (PositionalArgs.Count > 0)
        {
            var argsElement = doc.CreateElement(WellKnownStrings.XmlElements.PositionalArgs);
            foreach (var arg in PositionalArgs)
            {
                argsElement.AppendChild(arg.ToXmlElement(doc));
            }
            element.AppendChild(argsElement);
        }

        if (NamedArgs.Count > 0)
        {
            var argsElement = doc.CreateElement(WellKnownStrings.XmlElements.NamedArgs);
            foreach (var field in NamedArgs)
            {
                argsElement.AppendChild(field.ToXmlElement(doc));
            }
            element.AppendChild(argsElement);
        }

        if (ReturnType != null)
        {
            var returnElement = doc.CreateElement("ReturnType");
            returnElement.AppendChild(ReturnType.ToXmlElement(doc));
            element.AppendChild(returnElement);
        }

        return element;
    }
}

/// <summary>
/// Compile-time constructor bridge fact entry. These values belong to named instance bridges, not ADT layout.
/// </summary>
public record ConstructorConstant : EidosAstNode
{
    public string Name { get; private set; } = "";

    public EidosAstNode? Value { get; private set; }

    public override void BuildFromCst(AstContext context, ConcreteSyntaxNode node)
    {
        Span = node.Span;
    }

    internal void SetName(string name) => Name = name;
    internal void SetValue(EidosAstNode value) => Value = value;
    internal void SetSpan(Utils.SourceSpan span) => Span = span;

    public override XmlElement ToXmlElement(XmlDocument doc)
    {
        var element = CreateElement(doc, "ConstructorConstant");
        element.SetAttribute(WellKnownStrings.XmlAttributes.Name, Name);

        if (Value != null)
        {
            var valueElement = doc.CreateElement("Value");
            valueElement.AppendChild(Value.ToXmlElement(doc));
            element.AppendChild(valueElement);
        }

        return element;
    }
}

/// <summary>
/// 字段定义
/// </summary>
public record Field : EidosAstNode
{
    /// <summary>
    /// 字段名称
    /// </summary>
    public string Name { get; private set; } = "";

    /// <summary>
    /// 字段类型
    /// </summary>
    public TypeNode? Type { get; private set; }

    public override void BuildFromCst(AstContext context, ConcreteSyntaxNode node)
    {
        Span = node.Span;
        Name = "";
        Type = null;

        if (node is not NonTerminalCstNode ntNode)
        {
            return;
        }

        var foundName = false;
        ExtractFieldData(ntNode, ref foundName);

        if (Type == null && TryExtractTypeFromTerminal(ntNode, out var fallbackType))
        {
            Type = fallbackType;
        }
    }

    private void ExtractFieldData(ConcreteSyntaxNode node, ref bool foundName)
    {
        if (node is TerminalCstNode term)
        {
            var text = GetTokenText(term);
            if (!IsPunctuation(text) && !foundName)
            {
                Name = text;
                foundName = true;
            }

            return;
        }

        if (node is not NonTerminalCstNode ntNode)
        {
            return;
        }

        if (ntNode.AstNode is TypeNode typeNode)
        {
            Type = typeNode;
            return;
        }

        foreach (var child in ntNode.Children)
        {
            ExtractFieldData(child, ref foundName);
        }
    }

    private static bool TryExtractTypeFromTerminal(ConcreteSyntaxNode node, out TypeNode typeNode)
    {
        typeNode = null!;

        if (node is TerminalCstNode term && term.Terminal?.ToString() == WellKnownStrings.Terminals.Identifier)
        {
            var typePath = new TypePath();
            typePath.SetTypeName(GetTokenText(term));
            typePath.SetSpan(term.Span);
            typeNode = typePath;
            return true;
        }

        if (node is not NonTerminalCstNode ntNode)
        {
            return false;
        }

        foreach (var child in ntNode.Children)
        {
            if (TryExtractTypeFromTerminal(child, out typeNode))
            {
                return true;
            }
        }

        return false;
    }

    internal void SetName(string name) => Name = name;
    internal void SetType(Eidosc.Ast.Types.TypeNode? type) => Type = type;
    internal void SetSpan(Utils.SourceSpan span) => Span = span;

    public override XmlElement ToXmlElement(XmlDocument doc)
    {
        var element = CreateElement(doc, WellKnownStrings.XmlElements.Field);
        element.SetAttribute(WellKnownStrings.XmlAttributes.Name, Name);

        if (Type != null)
        {
            var typeElement = doc.CreateElement(WellKnownStrings.XmlElements.Type);
            typeElement.AppendChild(Type.ToXmlElement(doc));
            element.AppendChild(typeElement);
        }

        return element;
    }
}
