namespace Eidosc;

/// <summary>
/// Centralized string constants used across the compiler.
/// All string literals that appear in more than one place or carry semantic meaning
/// should be defined here as <c>const string</c> fields.
/// </summary>
public static class WellKnownStrings
{
    /// <summary>
    /// Path and name separators used in qualified identifiers.
    /// </summary>
    public static class Separators
    {
        public const string Path = "::";
        public const string ModulePath = "/";
    }

    /// <summary>
    /// Builtin primitive type names recognized by the type system and code generator.
    /// </summary>
    public static class BuiltinTypes
    {
        public const string Int = "Int";
        public const string Int64 = "Int64";
        public const string Int32 = "Int32";
        public const string Int16 = "Int16";
        public const string Int8 = "Int8";
        public const string Float = "Float";
        public const string Float64 = "Float64";
        public const string Float32 = "Float32";
        public const string Float16 = "Float16";
        public const string Bool = "Bool";
        public const string String = "String";
        public const string Char = "Char";
        public const string Unit = "Unit";
        public const string Never = "Never";
        public const string Type = "Type";

        /// <summary>Unit type as it appears in surface syntax.</summary>
        public const string UnitSyntax = "()";
        public const string Seq = "Seq";
        public const string Ref = "Ref";
        public const string MRef = "MRef";
        public const string MutRef = "MutRef";
        public const string Shared = "Shared";
        public const string RawPtr = "RawPtr";
        public const string Ptr = "Ptr";
        public const string Cfn = "Cfn";
        public const string TypeEq = "TypeEq";
    }

    /// <summary>
    /// Built-in ability names that are always available without user declaration.
    /// </summary>
    public static class BuiltinAbilities
    {
        public const string FFI = "FFI";
        public const string IO = "IO";
    }

    /// <summary>
    /// Language keywords that appear in AST validation, token rewriting, and name resolution.
    /// </summary>
    public static class Keywords
    {
        public const string Func = "func";
        public const string Fn = "fn";
        public const string Let = "let";
        public const string Type = "type";
        public const string Trait = "trait";
        public const string Proof = "proof";
        public const string Forall = "forall";
        public const string Refl = "refl";
        public const string ReflConstructor = "Refl";
        public const string Rewrite = "rewrite";
        public const string Simp = "simp";
        public const string TodoProof = "todo_proof";
        public const string Apply = "apply";
        public const string Exact = "exact";
        public const string Symm = "symm";
        public const string Trans = "trans";
        public const string Congr = "congr";
        public const string Ext = "ext";
        public const string Have = "have";
        public const string Calc = "calc";
        public const string Trivial = "trivial";
        public const string Intro = "intro";
        public const string Constructor = "constructor";
        public const string Left = "left";
        public const string Right = "right";
        public const string First = "first";
        public const string Second = "second";
        public const string Contradiction = "contradiction";
        public const string Exists = "exists";
        public const string TrueProposition = "True";
        public const string FalseProposition = "False";
        public const string AndProposition = "and";
        public const string OrProposition = "or";
        public const string NotProposition = "not";
        public const string IffProposition = "iff";
        public const string At = "at";
        public const string By = "by";
        public const string Cases = "cases";
        public const string Induction = "induction";
        public const string LegacyAbility = "ability";
        public const string Module = "module";
        public const string Import = "import";
        public const string Export = "export";
        public const string Match = "match";
        public const string When = "when";
        public const string If = "if";
        public const string Then = "then";
        public const string Else = "else";
        public const string Decide = "decide";
        public const string Loop = "loop";
        public const string While = "while";
        public const string Handler = "handler";
        public const string With = "with";
        public const string Return = "return";
        public const string Unreachable = "unreachable";
        public const string Resume = "resume";
        public const string Effect = "effect";
        public const string Effects = "effects";
        public const string Need = "need";
        public const string Where = "where";
        public const string Requires = "requires";
        public const string Signature = "signature";
        public const string Attribute = "attribute";
        public const string AttributeArgs = "attributeArgs";
        public const string Qualifier = "qualifier";
        public const string Generator = "generator";
        public const string ArrowType = "arrowType";
        public const string EffectfulType = "effectfulType";
        public const string TupleType = "tupleType";
        public const string TypePath = "typePath";
        public const string PrimaryType = "primaryType";
        public const string Ffi = "ffi";
        public const string Self = "Self";
        public const string Mut = "mut";
        public const string Comptime = "comptime";
        public const string Do = "do";
        public const string Derive = "derive";
    }

    /// <summary>
    /// Runtime function names emitted by the LLVM backend.
    /// These correspond to functions in the C runtime library.
    /// </summary>
    public static class Runtime
    {
        // Memory management
        public const string Alloc = "eidos_alloc";
        public const string AllocReuse = "eidos_alloc_reuse";
        public const string DropReuse = "eidos_drop_reuse";
        public const string IncRef = "eidos_incref";
        public const string DecRef = "eidos_decref";
        public const string IncRefLocal = "eidos_incref_local";
        public const string DecRefLocal = "eidos_decref_local";
        public const string IncRefShared = "eidos_incref_shared";
        public const string DecRefShared = "eidos_decref_shared";

        // Entry points
        public const string Main = "eidos_main";
        public const string ModuleInit = "eidos_module_init";

        // Print / IO
        public const string PrintInt = "eidos_print_int";
        public const string PrintFloat = "eidos_print_float";
        public const string PrintChar = "eidos_print_char";
        public const string PrintString = "eidos_print_string";
        public const string PrintNewline = "eidos_print_newline";
        public const string ReadChar = "eidos_read_char";
        public const string ReadLine = "eidos_read_line";
        public const string IoLastSuccess = "eidos_io_last_success";
        public const string IoLastError = "eidos_io_last_error";

        // String operations
        public const string StringConcat = "eidos_string_concat";
        public const string StringLength = "eidos_string_length";
        public const string StringCharAt = "eidos_string_char_at";
        public const string StringSlice = "eidos_string_slice";
        public const string StringEquals = "eidos_string_equals";
        public const string StringFromChar = "eidos_string_from_char";
        public const string StringFromCstr = "eidos_string_from_cstr";
        public const string StringIntern = "eidos_string_intern";
        public const string StringFromCstrRaw = "eidos_string_from_cstr_raw";
        public const string StringToCstr = "eidos_string_to_cstr";
        public const string StringToFloat = "eidos_string_to_float";
        public const string IntToString = "eidos_int_to_string";
        public const string IntToFloat = "eidos_int_to_float";

        // Show / type display
        public const string Show = "eidos_show";
        public const string ShowBool = "eidos_builtin_show_bool";

        // Array operations
        public const string ArrayNew = "eidos_array_new";
        public const string ArrayNewWithPolicy = "eidos_array_new_with_policy";
        public const string ArrayPush = "eidos_array_push";
        public const string ArrayExtend = "eidos_array_extend";
        public const string ArrayPop = "eidos_array_pop";
        public const string ArraySwap = "eidos_array_swap";
        public const string ArrayGet = "eidos_array_get";
        public const string ArraySet = "eidos_array_set";
        public const string ArrayLength = "eidos_array_length";

        // File IO
        public const string FileReadAllText = "eidos_file_read_all_text";
        public const string FileWriteAllText = "eidos_file_write_all_text";
        public const string FileExists = "eidos_file_exists";

        // HTTP
        public const string HttpGetText = "eidos_http_get_text";
        public const string HttpRequestText = "eidos_http_request_text";
        public const string HttpRequestTextWithOptions = "eidos_http_request_text_with_options";
        public const string HttpRequestTextWithHeaders = "eidos_http_request_text_with_headers";
        public const string HttpRequestTextWithBinaryBodyOptions = "eidos_http_request_text_with_binary_body_options";
        public const string HttpRequestBodyHexWithOptions = "eidos_http_request_body_hex_with_options";
        public const string HttpRequestBodyHexWithBinaryBodyOptions = "eidos_http_request_body_hex_with_binary_body_options";
        public const string HttpLastStatusCode = "eidos_http_last_status_code";
        public const string HttpLastEffectiveUrl = "eidos_http_last_effective_url";
        public const string HttpLastHeaders = "eidos_http_last_headers";
        public const string HttpLastContentType = "eidos_http_last_content_type";

        // Regex
        public const string RegexCompile = "eidos_regex_compile";
        public const string RegexIsMatch = "eidos_regex_is_match";
        public const string RegexFind = "eidos_regex_find";
        public const string RegexFindString = "eidos_regex_find_string";
        public const string RegexFree = "eidos_regex_free";

        // Pointer
        public const string PtrNull = "eidos_ptr_null";
        public const string PtrIsNull = "eidos_ptr_is_null";
        public const string PtrEquals = "eidos_ptr_equals";

        // Closure
        public const string ClosureNew = "eidos_closure_new";

        // Destructor
        public const string RegisterDestructor = "eidos_register_destructor";

        // Type ID
        public const string TypeId = "eidos_type_id";

        // Terminal
        public const string TerminalSetRaw = "eidos_terminal_set_raw";
        public const string TerminalRestore = "eidos_terminal_restore";

        // Time
        public const string TimeNow = "eidos_time_now";
        public const string TimeNowMs = "eidos_time_now_ms";
        public const string TimeYear = "eidos_time_year";
        public const string TimeMonth = "eidos_time_month";
        public const string TimeDay = "eidos_time_day";
        public const string TimeHour = "eidos_time_hour";
        public const string TimeMinute = "eidos_time_minute";
        public const string TimeSecond = "eidos_time_second";
        public const string TimeFormat = "eidos_time_format";

        // Misc
        public const string SleepMs = "eidos_sleep_ms";

        // Intrinsics (used in function name checks, not full eidos_ prefixed)
        public const string IncRefShort = "incref";
        public const string DecRefShort = "decref";
    }

    /// <summary>
    /// Name mangling prefixes used by the code generator.
    /// </summary>
    public static class Mangling
    {
        public const string Prefix = "eidos_";
        public const string GlobalPrefix = "eidos_g_";
        public const string TempPrefix = "eidos_t_";
    }

    /// <summary>
    /// Environment variable names used to configure the toolchain.
    /// </summary>
    public static class EnvVars
    {
        public const string RuntimePath = "EIDOS_RUNTIME_PATH";
        public const string TargetsPath = "EIDOS_TARGETS_PATH";
        public const string ExtraLdFlags = "EIDOS_RUNTIME_EXTRA_LDFLAGS";
        public const string ExtraCFlags = "EIDOS_RUNTIME_EXTRA_CFLAGS";
    }

    /// <summary>
    /// Special entry point and well-known function names.
    /// </summary>
    public static class SpecialNames
    {
        public const string Main = "main";
        public const string Intrinsic = "intrinsic";
        public const string Effects = "effects";
        public const string LlvmAbi = "llvm_abi";
        public const string DestructorPrefix = "eidos_destructor_";
        public const string MemoryRuntimeFile = "eidos_memory.c";
    }

    /// <summary>
    /// Punctuation tokens recognized by the AST layer during CST→AST conversion.
    /// </summary>
    public static class Punctuation
    {
        public const string Dot = ".";
        public const string DotDot = "..";
        public const string Colon = ":";
        public const string OpenParen = "(";
        public const string CloseParen = ")";
        public const string OpenBrace = "{";
        public const string CloseBrace = "}";
        public const string OpenBracket = "[";
        public const string CloseBracket = "]";
        public const string Comma = ",";
        public const string Semicolon = ";";
        public const string Pipe = "|";
        public const string FatArrow = "=>";
        public const string RightArrow = "->";
        public const string LeftArrow = "<-";
        public new const string Equals = "=";
        public const string Underscore = "_";
        public const string At = "@";
    }

    /// <summary>
    /// Operator symbols used in binary/unary expression parsing and token classification.
    /// </summary>
    public static class Operators
    {
        // Binary operators
        public const string PipeForward = "|>";
        public const string Bind = ">>=";
        public const string ComposeRight = ">>>";
        public const string ComposeLeft = "<<<";
        public const string Fmap = "<$>";
        public const string Ap = "<*>";
        public const string Append = "<>";
        public const string Add = "+";
        public const string Subtract = "-";
        public const string Multiply = "*";
        public const string Divide = "/";
        public const string Modulo = "%";
        public const string Concat = "++";
        public const string Prepend = "+:";
        public const string AppendLast = ":+";
        public const string Less = "<";
        public const string Greater = ">";
        public const string LessEqual = "<=";
        public const string GreaterEqual = ">=";
        public const string Equal = "==";
        public const string NotEqual = "!=";
        public const string And = "&&";
        public const string Or = "||";
        public const string Coalesce = "??";
        public const string OptionSuffix = "?";

        // Unary operators
        public const string Negate = "-";
        public const string Not = "!";
        public const string Deref = "*";
        public const string AddressOf = "&";
        public const string Ref = "ref";
        public const string MRef = "mref";
    }

    /// <summary>
    /// CST terminal names used to classify tokens during AST construction.
    /// </summary>
    public static class Terminals
    {
        public const string Number = "numberLiteral";
        public const string String = "stringLiteral";
        public const string Char = "charLiteral";
        public const string Boolean = "booleanLiteral";
        public const string Identifier = "identifier";
        public const string TypeIdentifier = "typeIdentifier";
        public const string OperatorIdentifier = "operatorIdentifier";
    }

    /// <summary>
    /// Additional keywords that appear only in stop-condition checks (not declaration contexts).
    /// </summary>
    public static class AdditionalKeywords
    {
        public const string Break = "break";
        public const string Continue = "continue";
        public const string True = "true";
        public const string False = "false";
        public const string As = "as";
    }

    /// <summary>
    /// Internal names used across MIR and LLVM for effect dispatch, array ops, etc.
    /// </summary>
    public static class InternalNames
    {
        public const string Show = "show";
        public const string ArrayPush = "array_push";
        public const string ArrayNew = "array_new";
        public const string ArrayExtend = "array_extend";
        public const string ArrayPop = "array_pop";
        public const string ArraySwap = "array_swap";
        public const string Entry = "entry";
        public const string Bitcast = "bitcast";
        public const string Closure = "closure";

        // Array
        public const string ArrayLength = "array_length";
        public const string ArrayGet = "array_get";
        public const string ArraySet = "array_set";

        // Pointer intrinsics (MIR-level names, not eidos_ runtime names)
        public const string PtrNull = "ptr_null";
        public const string PtrIsNull = "ptr_is_null";
        public const string PtrEquals = "ptr_equals";
        public const string PtrLoadInt = "ptr_load_int";
        public const string PtrLoadFloat = "ptr_load_float";
        public const string PtrLoadPtr = "ptr_load_ptr";
        public const string PtrLoadI32 = "ptr_load_i32";
        public const string PtrLoadI8 = "ptr_load_i8";
        public const string PtrLoadBool = "ptr_load_bool";
        public const string PtrStoreInt = "ptr_store_int";
        public const string PtrStoreFloat = "ptr_store_float";
        public const string PtrStorePtr = "ptr_store_ptr";
        public const string PtrStoreI32 = "ptr_store_i32";
        public const string PtrStoreI8 = "ptr_store_i8";
        public const string PtrStoreBool = "ptr_store_bool";
        public const string PtrLoadAs = "ptr_load_as";
        public const string PtrStoreAs = "ptr_store_as";
        public const string ValueBox = "value_box";
        public const string ValueUnbox = "value_unbox";
        public const string ValueBoxFree = "value_box_free";
        public const string SharedNew = "shared_new";
        public const string SharedBorrow = "shared_borrow";
        public const string SharedClone = "shared_clone";
        public const string SharedPtrEq = "shared_ptr_eq";

        // Internal name prefixes
        public const string ModuleValueGetterPrefix = "__module_val__";
        public const string ResumeValuePrefix = "__resume_value_";
        public const string LambdaPrefix = "__lambda_";
        public const string LetQuestionErrorPrefix = "__let_question_error_";
        public const string HandlerPrefix = "__handler_";
        public const string OperationPrefix = "__op";
        public const string ClosureEnvPrefix = "__ClosureEnv_";
        public const string ClosurePrefix = "__closure_";
        public const string ContinuationArgPrefix = "__karg";
        public const string SpecCapturePrefix = "__spec_capture_";
        public const string InlinePrefix = "__inl.";
        public const string SpecializationMarker = "__spec_";
        public const string ModuleSeparator = "__";
    }

    /// <summary>
    /// XML element names used in AST serialization (ToXmlElement).
    /// </summary>
    public static class XmlElements
    {
        // Declarations
        public const string EffectDef = "EffectDef";
        public const string EffectRequirement = "EffectRequirement";
        public const string AdtDef = "AdtDef";
        public const string Assignment = "Assignment";
        public const string Attribute = "Attribute";
        public const string Constructor = "Constructor";
        public const string Field = "Field";
        public const string FieldInit = "FieldInit";
        public const string FuncDecl = "FuncDecl";
        public const string FuncDef = "FuncDef";
        public const string ImportDecl = "ImportDecl";
        public const string LetDecl = "LetDecl";
        public const string LetQuestionDecl = "LetQuestionDecl";
        public const string ModuleDecl = "ModuleDecl";
        public const string ProofDecl = "ProofDecl";
        public const string TraitDef = "TraitDef";

        // Declaration sub-elements
        public const string AliasTarget = "AliasTarget";
        public const string Arguments = "Arguments";
        public const string Attributes = "Attributes";
        public const string Body = "Body";
        public const string Constructors = "Constructors";
        public const string Declarations = "Declarations";
        public const string Fields = "Fields";
        public const string NamedArgs = "NamedArgs";
        public const string NamedArg = "NamedArg";
        public const string Operations = "Operations";
        public const string Pattern = "Pattern";
        public const string PositionalArgs = "PositionalArgs";
        public const string ProofParameter = "ProofParameter";
        public const string ProofParameters = "ProofParameters";
        public const string ProofCase = "ProofCase";
        public const string ProofCases = "ProofCases";
        public const string ProofCaseExpression = "ProofCaseExpression";
        public const string ProofTerm = "ProofTerm";
        public const string Proofs = "Proofs";
        public const string RequiredAbilities = "RequiredAbilities";
        public const string SelectiveImport = "SelectiveImport";
        public const string Signature = "Signature";
        public const string TypeParams = "TypeParams";
        public const string Value = "Value";

        // Expressions
        public const string BinaryExpr = "BinaryExpr";
        public const string BlockExpr = "BlockExpr";
        public const string BreakExpr = "BreakExpr";
        public const string CallExpr = "CallExpr";
        public const string ContinueExpr = "ContinueExpr";
        public const string CtorExpr = "CtorExpr";
        public const string DoBinding = "DoBinding";
        public const string DoExpr = "DoExpr";
        public const string HandlerBranch = "HandlerBranch";
        public const string HandlerExpr = "HandlerExpr";
        public const string IdentifierExpr = "IdentifierExpr";
        public const string IfExpr = "IfExpr";
        public const string IfLetExpr = "IfLetExpr";
        public const string IndexExpr = "IndexExpr";
        public const string InfixCallExpr = "InfixCallExpr";
        public const string LambdaExpr = "LambdaExpr";
        public const string ListComprehension = "ListComprehension";
        public const string ListExpr = "ListExpr";
        public const string LiteralExpr = "LiteralExpr";
        public const string LoopExpr = "LoopExpr";
        public const string MatchExpr = "MatchExpr";
        public const string MethodCallExpr = "MethodCallExpr";
        public const string PathExpr = "PathExpr";
        public const string PatternBranch = "PatternBranch";
        public const string PatternGuardExpr = "PatternGuardExpr";
        public const string Qualifier = "Qualifier";
        public const string ReturnExpr = "ReturnExpr";
        public const string SequentialGuardExpr = "SequentialGuardExpr";
        public const string TupleExpr = "TupleExpr";
        public const string UnaryExpr = "UnaryExpr";
        public const string UnreachableExpr = "UnreachableExpr";
        public const string WhileLetExpr = "WhileLetExpr";
        public const string WithClause = "WithClause";

        // Patterns
        public const string AndPattern = "AndPattern";
        public const string AsPattern = "AsPattern";
        public const string CtorPattern = "CtorPattern";
        public const string FieldPattern = "FieldPattern";
        public const string ListPattern = "ListPattern";
        public const string LiteralPattern = "LiteralPattern";
        public const string NotPattern = "NotPattern";
        public const string OrPattern = "OrPattern";
        public const string RangePattern = "RangePattern";
        public const string TuplePattern = "TuplePattern";
        public const string VarPattern = "VarPattern";
        public const string ViewPattern = "ViewPattern";
        public const string WildcardPattern = "WildcardPattern";

        // Types
        public const string ArrowType = "ArrowType";
        public const string EffectfulType = "EffectfulType";
        public const string Kind = "Kind";
        public const string TraitRef = "TraitRef";
        public const string TupleType = "TupleType";
        public const string Type = "Type";
        public const string TypePath = "TypePath";
        public const string WildcardType = "WildcardType";

        // Sub-elements used as child wrappers in ToXmlElement
        public const string Alternative = "Alternative";
        public const string Branches = "Branches";
        public const string Condition = "Condition";
        public const string Conjunct = "Conjunct";
        public const string ConstructorPath = "ConstructorPath";
        public const string ElseBranch = "ElseBranch";
        public const string End = "End";
        public const string Expression = "Expression";
        public const string Function = "Function";
        public const string GeneratorExpression = "GeneratorExpression";
        public const string GeneratorPattern = "GeneratorPattern";
        public const string Guard = "Guard";
        public const string GuardExpression = "GuardExpression";
        public const string Handler = "Handler";
        public const string Index = "Index";
        public const string InnerPattern = "InnerPattern";
        public const string InputType = "InputType";
        public const string Left = "Left";
        public const string MatchedExpression = "MatchedExpression";
        public const string Methods = "Methods";
        public const string NamedPatterns = "NamedPatterns";
        public const string Object = "Object";
        public const string Operand = "Operand";
        public const string SuperTraits = "SuperTraits";
        public const string Output = "Output";
        public const string OutputType = "OutputType";
        public const string ParamType = "ParamType";
        public const string Parameter = "Parameter";
        public const string Parameters = "Parameters";
        public const string PositionalPatterns = "PositionalPatterns";
        public const string Receiver = "Receiver";
        public const string Rest = "Rest";
        public const string ResumePattern = "ResumePattern";
        public const string ReturnType = "ReturnType";
        public const string Right = "Right";
        public const string SourceExpression = "SourceExpression";
        public const string Start = "Start";
        public const string Statement = "Statement";
        public const string ThenBranch = "ThenBranch";
        public const string TraitConstraints = "TraitConstraints";
        public const string TypeArgs = "TypeArgs";
        public const string Element = "Element";
        public const string ParseTree = "ParseTree";
        public const string ViewExpression = "ViewExpression";
    }

    /// <summary>
    /// XML attribute names used in AST serialization (SetAttribute).
    /// </summary>
    public static class XmlAttributes
    {
        public const string Alias = "alias";
        public const string BindingMode = "bindingMode";
        public const string BindingName = "bindingName";
        public const string CstructGetterName = "cstructGetterName";
        public const string Ctor = "ctor";
        public const string EffectPath = "effectPath";
        public const string EffectPaths = "effectPaths";
        public const string FieldName = "fieldName";
        public const string Function = "function";
        public const string HandlerName = "handlerName";
        public const string HasExplicitCallSyntax = "hasExplicitCallSyntax";
        public const string HasRest = "hasRest";
        public const string IsDeclarationOnly = "isDeclarationOnly";
        public const string IsRecovered = "isRecovered";
        public const string IsShorthand = "isShorthand";
        public const string IsStar = "isStar";
        public const string IsTypeAlias = "isTypeAlias";
        public const string IsTypePath = "isTypePath";
        public const string Kind = "kind";
        public const string Library = "library";
        public const string MethodName = "methodName";
        public const string ModulePath = "modulePath";
        public const string Name = "name";
        public const string Operation = "operation";
        public const string Operator = "operator";
        public const string Path = "path";
        public const string RawText = "rawText";
        public const string RecoveryReason = "recoveryReason";
        public const string ResolvedAsFieldAccess = "resolvedAsFieldAccess";
        public const string Span = "span";
        public const string Target = "target";
        public const string Text = "text";
        public const string Type = "type";
        public const string Value = "value";
    }
}
