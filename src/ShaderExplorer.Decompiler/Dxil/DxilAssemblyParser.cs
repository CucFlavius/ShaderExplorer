namespace ShaderExplorer.Decompiler.Dxil;

/// <summary>
///     Parses DXC disassembly text (LLVM IR) into a structured <see cref="DxilModule" />.
///     Line-by-line regex-based parser.
/// </summary>
public partial class DxilAssemblyParser
{
    // ═══ Regex patterns ═══

    // define void @main() #0 { ...
    // define <return_type> @name(<params>) ...
    [GeneratedRegex(@"^define\s+(.+?)\s+@""?([^""(\s]+)""?\s*\(([^)]*)\)")]
    private static partial Regex DefineRegex();

    // declare <return_type> @name(<params>)
    [GeneratedRegex(@"^declare\s+(.+?)\s+@""?([^""(\s]+)""?\s*\(")]
    private static partial Regex DeclareRegex();

    // %name = ...  or  %123 = ...
    [GeneratedRegex(@"^%([^\s=]+)\s*=\s*(.+)$")]
    private static partial Regex AssignmentRegex();

    // call <rettype> @dx.op.<name>(...)  or  call <rettype> @<name>(...)
    [GeneratedRegex(@"call\s+(.+?)\s+@""?([^""(\s]+)""?\s*\((.*)$")]
    private static partial Regex CallRegex();

    // DXIL opcode: first argument i32 <N> to dx.op calls
    [GeneratedRegex(@"i32\s+(\d+)")]
    private static partial Regex DxilOpcodeRegex();

    // Binary ops: fadd, fmul, fsub, fdiv, frem, add, sub, mul, udiv, sdiv, urem, srem
    [GeneratedRegex(
        @"(fadd|fmul|fsub|fdiv|frem|add|sub|mul|udiv|sdiv|urem|srem)\s+(fast\s+|nnan\s+|ninf\s+|nsz\s+|arcp\s+|contract\s+|afn\s+|reassoc\s+)*(.+?),\s*(.+)$")]
    private static partial Regex BinaryOpRegex();

    // Compare ops: fcmp [fast-math-flags] <pred> <type> <op1>, <op2>
    [GeneratedRegex(@"(fcmp|icmp)\s+(?:(?:fast|nnan|ninf|nsz|arcp|contract|afn|reassoc)\s+)*(\w+)\s+(.+?),\s*(.+)$")]
    private static partial Regex CompareOpRegex();

    // phi <type> [ <val>, %<block> ], ...
    [GeneratedRegex(@"phi\s+(.+?)\s+(.+)$")]
    private static partial Regex PhiRegex();

    // select i1 %cond, <type> %true, <type> %false
    [GeneratedRegex(@"select\s+i1\s+(.+?),\s+\S+\s+(.+?),\s+\S+\s+(.+)$")]
    private static partial Regex SelectRegex();

    // extractvalue { ... } %val, <idx>
    [GeneratedRegex(@"extractvalue\s+.+?\s+(%\S+),\s*(\d+)")]
    private static partial Regex ExtractValueRegex();

    // Cast instructions: bitcast, fptoui, fptosi, uitofp, sitofp, fptrunc, fpext, trunc, zext, sext, ptrtoint, inttoptr, addrspacecast
    [GeneratedRegex(
        @"(bitcast|fptoui|fptosi|uitofp|sitofp|fptrunc|fpext|trunc|zext|sext|ptrtoint|inttoptr|addrspacecast)\s+(.+?)\s+(.+?)\s+to\s+(.+)$")]
    private static partial Regex CastRegex();

    // br label %target
    [GeneratedRegex(@"^br\s+label\s+%(.+)$")]
    private static partial Regex BranchRegex();

    // br i1 %cond, label %true, label %false
    [GeneratedRegex(@"^br\s+i1\s+(.+?),\s*label\s+%(.+?),\s*label\s+%(.+)$")]
    private static partial Regex CondBranchRegex();

    // ret <type> <val>  or  ret void
    [GeneratedRegex(@"^ret\s+(.+)$")]
    private static partial Regex RetRegex();

    // switch <type> <val>, label %default [ <type> <val>, label %label ... ]
    [GeneratedRegex(@"^switch\s+\S+\s+(.+?),\s*label\s+%(\S+)\s*\[(.+)\]$")]
    private static partial Regex SwitchRegex();

    // target triple = "..."
    [GeneratedRegex(@"^target\s+triple\s*=\s*""(.+)""")]
    private static partial Regex TargetTripleRegex();

    // target datalayout = "..."
    [GeneratedRegex(@"^target\s+datalayout\s*=\s*""(.+)""")]
    private static partial Regex TargetDatalayoutRegex();

    // <label>:  ; preds = ...
    [GeneratedRegex(@"^([^;:\s]+):\s*(;.*)?$")]
    private static partial Regex LabelRegex();

    // Operand parsing helpers
    [GeneratedRegex(@"^\s*(\S+)\s+(%\S+|@\S+|-?[\d.]+e?[+-]?\d*|true|false|undef|zeroinitializer|null|poison)")]
    private static partial Regex TypedOperandRegex();

    // load <type>, <ptrtype> %ptr [, align N] [, !metadata !N]*
    [GeneratedRegex(@"load\s+(.+?),\s+\S+\s+(.+?)(?:,\s*align\s+\d+)?(?:,\s*!\w+\s*!\d+)*\s*$")]
    private static partial Regex LoadRegex();

    // store <type> <val>, <ptrtype> %ptr [, align N] [, !metadata !N]*
    [GeneratedRegex(@"store\s+\S+\s+(.+?),\s+\S+\s+(.+?)(?:,\s*align\s+\d+)?(?:,\s*!\w+\s*!\d+)*\s*$")]
    private static partial Regex StoreRegex();

    // alloca <type>
    [GeneratedRegex(@"alloca\s+(.+?)(?:,\s*align\s+\d+)?$")]
    private static partial Regex AllocaRegex();

    // getelementptr [inbounds] <base_type>, <ptr_type> <base_ptr>, <idx_type> <idx> [, ...]
    [GeneratedRegex(@"getelementptr\s+(?:inbounds\s+)?(.+?),\s+(.+?)\s+(%\S+|@\S+|null),?\s*(.*)$")]
    private static partial Regex GepRegex();

    // insertvalue <type> <agg>, <type> <val>, <idx>
    [GeneratedRegex(@"insertvalue\s+(.+?)\s+(%\S+|undef|zeroinitializer|poison),\s+\S+\s+(.+?),\s+(\d+)")]
    private static partial Regex InsertValueRegex();

    // extractelement <vec_type> <vec>, <idx_type> <idx>
    [GeneratedRegex(@"extractelement\s+.+?\s+(%\S+),\s+\S+\s+(.+)$")]
    private static partial Regex ExtractElementRegex();

    // insertelement <vec_type> <vec>, <scalar_type> <val>, <idx_type> <idx>
    [GeneratedRegex(@"insertelement\s+.+?\s+(%\S+),\s+\S+\s+(.+?),\s+\S+\s+(.+)$")]
    private static partial Regex InsertElementRegex();

    // shufflevector <vec_type> <v1>, <vec_type> <v2>, <mask>
    [GeneratedRegex(@"shufflevector\s+.+?\s+(%\S+),\s+.+?\s+(%\S+|undef|poison),\s+<(.+?)>\s+(.+)$")]
    private static partial Regex ShuffleVectorRegex();

    // Struct type definition: %name = type { ... }
    [GeneratedRegex(@"^(%[\w.]+)\s*=\s*type\s+\{(.+)\}")]
    private static partial Regex StructTypeRegex();

    public DxilModule Parse(string assemblyText)
    {
        var module = new DxilModule();
        var lines = assemblyText.Split('\n');

        // First pass: parse resource metadata from comments
        ParseResourceMetadata(lines, module);

        var i = 0;
        while (i < lines.Length)
        {
            var line = lines[i].TrimEnd('\r');
            var trimmed = line.Trim();

            if (string.IsNullOrEmpty(trimmed) || trimmed.StartsWith(';') || trimmed.StartsWith('!') ||
                trimmed.StartsWith("source_filename") || trimmed.StartsWith("attributes"))
            {
                i++;
                continue;
            }

            var tripleMatch = TargetTripleRegex().Match(trimmed);
            if (tripleMatch.Success)
            {
                module.TargetTriple = tripleMatch.Groups[1].Value;
                i++;
                continue;
            }

            var dlMatch = TargetDatalayoutRegex().Match(trimmed);
            if (dlMatch.Success)
            {
                module.TargetDatalayout = dlMatch.Groups[1].Value;
                i++;
                continue;
            }

            var declareMatch = DeclareRegex().Match(trimmed);
            if (declareMatch.Success)
            {
                var func = new DxilFunction
                {
                    Name = declareMatch.Groups[2].Value,
                    IsDeclaration = true
                };
                module.Functions.Add(func);
                i++;
                continue;
            }

            var defineMatch = DefineRegex().Match(trimmed);
            if (defineMatch.Success)
            {
                var func = ParseFunction(defineMatch, lines, ref i);
                module.Functions.Add(func);
                continue;
            }

            // Struct type definitions: %struct.Foo = type { float, <4 x float> }
            // Also handle packed structs: %struct.Foo = type <{ float, i32 }>
            var structLine = trimmed;
            if (structLine.Contains("type <{"))
                structLine = structLine.Replace("type <{", "type {").Replace("}>", "}");
            var structMatch = StructTypeRegex().Match(structLine);
            if (structMatch.Success)
            {
                var structName = structMatch.Groups[1].Value.TrimStart('%');
                var fieldsText = structMatch.Groups[2].Value.Trim();
                var structType = new DxilStructType { Name = structName };
                var fieldIndex = 0;
                foreach (var fieldText in SplitTypeList(fieldsText))
                {
                    var ft = fieldText.Trim();
                    if (!string.IsNullOrEmpty(ft))
                    {
                        structType.Fields.Add(new DxilStructField
                        {
                            Type = ParseType(ft),
                            Name = $"field{fieldIndex}",
                            Offset = fieldIndex
                        });
                        fieldIndex++;
                    }
                }
                module.StructTypes.Add(structType);
                i++;
                continue;
            }

            if (trimmed.StartsWith("@"))
            {
                module.Globals.Add(new DxilGlobalVariable { RawText = trimmed });
                i++;
                continue;
            }

            i++;
        }

        return module;
    }

    private DxilFunction ParseFunction(Match defineMatch, string[] lines, ref int i)
    {
        var func = new DxilFunction
        {
            ReturnType = ParseType(defineMatch.Groups[1].Value.Trim()),
            Name = defineMatch.Groups[2].Value,
            IsDeclaration = false
        };

        // Parse parameters from the define line
        var paramString = defineMatch.Groups[3].Value.Trim();
        if (!string.IsNullOrEmpty(paramString))
            ParseFunctionParameters(paramString, func);

        // Find opening brace
        while (i < lines.Length && !lines[i].Contains('{'))
            i++;
        i++; // skip opening brace line

        DxilBasicBlock? currentBlock = null;

        while (i < lines.Length)
        {
            var line = lines[i].TrimEnd('\r');
            var trimmed = line.Trim();

            // Closing brace ends the function
            if (trimmed == "}")
            {
                if (currentBlock != null)
                    func.BasicBlocks.Add(currentBlock);
                i++;
                break;
            }

            // Empty or comment lines
            if (string.IsNullOrEmpty(trimmed) || trimmed.StartsWith(';'))
            {
                i++;
                continue;
            }

            // Label (block start)
            var labelMatch = LabelRegex().Match(trimmed);
            if (labelMatch.Success)
            {
                if (currentBlock != null)
                    func.BasicBlocks.Add(currentBlock);
                currentBlock = new DxilBasicBlock { Label = labelMatch.Groups[1].Value };
                i++;
                continue;
            }

            // If no block started yet, create an implicit entry block
            if (currentBlock == null)
                currentBlock = new DxilBasicBlock { Label = "entry" };

            // Strip trailing comments and metadata from instruction lines
            trimmed = StripMetadata(StripComment(trimmed));

            // Handle multi-line switch: join continuation lines until ']'
            if (trimmed.StartsWith("switch ") && trimmed.Contains('[') && !trimmed.Contains(']'))
            {
                var sb = new StringBuilder(trimmed);
                while (++i < lines.Length)
                {
                    var cont = StripMetadata(StripComment(lines[i].TrimEnd('\r').Trim()));
                    sb.Append(' ').Append(cont);
                    if (cont.Contains(']'))
                        break;
                }

                trimmed = sb.ToString();
            }

            // Try to parse as a terminator
            if (TryParseTerminator(trimmed, out var terminator))
            {
                currentBlock.Terminator = terminator;
                i++;
                continue;
            }

            // Try to parse as an instruction
            var instr = ParseInstruction(trimmed);
            if (instr != null)
                currentBlock.Instructions.Add(instr);

            i++;
        }

        // Compute successors/predecessors
        ComputeCfgEdges(func);

        return func;
    }

    private DxilInstruction? ParseInstruction(string line)
    {
        // Assignment: %name = <rhs>
        var assignMatch = AssignmentRegex().Match(line);
        if (assignMatch.Success)
        {
            var resultName = assignMatch.Groups[1].Value;
            var rhs = assignMatch.Groups[2].Value.Trim();

            var instr = ParseRhs(rhs);
            if (instr != null)
            {
                instr.ResultName = resultName;
                instr.RawText = line;
                return instr;
            }
        }

        // Non-assignment instructions (store, call with void return)
        if (line.TrimStart().StartsWith("store "))
        {
            var storeMatch = StoreRegex().Match(line.Trim());
            if (storeMatch.Success)
                return new DxilInstruction
                {
                    Kind = DxilInstructionKind.Store,
                    StoreValue = ParseOperand(storeMatch.Groups[1].Value.Trim()),
                    LoadStorePointer = ParseOperand(storeMatch.Groups[2].Value.Trim()),
                    RawText = line
                };
        }

        // Handle tail/musttail/notail call prefixes
        var callLine = line.TrimStart();
        if (callLine.StartsWith("tail ") || callLine.StartsWith("musttail ") || callLine.StartsWith("notail "))
        {
            var callIdx = callLine.IndexOf("call ", StringComparison.Ordinal);
            if (callIdx >= 0)
                callLine = callLine[callIdx..];
        }

        if (callLine.StartsWith("call "))
        {
            var callMatch = CallRegex().Match(callLine);
            if (callMatch.Success) return ParseCallInstruction(callMatch, line);
        }

        return new DxilInstruction { Kind = DxilInstructionKind.Unknown, RawText = line };
    }

    private DxilInstruction? ParseRhs(string rhs)
    {
        // Strip tail/musttail/notail call qualifiers
        var callRhs = rhs;
        if (callRhs.StartsWith("tail ") || callRhs.StartsWith("musttail ") || callRhs.StartsWith("notail "))
        {
            var callIdx = callRhs.IndexOf("call ", StringComparison.Ordinal);
            if (callIdx >= 0)
                callRhs = callRhs[callIdx..];
        }

        // Call
        var callMatch = CallRegex().Match(callRhs);
        if (callMatch.Success)
            return ParseCallInstruction(callMatch, rhs);

        // Binary op
        var binMatch = BinaryOpRegex().Match(rhs);
        if (binMatch.Success)
            return new DxilInstruction
            {
                Kind = DxilInstructionKind.BinaryOp,
                Operator = binMatch.Groups[1].Value,
                Operand1 = ParseOperand(binMatch.Groups[3].Value.Trim()),
                Operand2 = ParseOperand(binMatch.Groups[4].Value.Trim())
            };

        // Compare op
        var cmpMatch = CompareOpRegex().Match(rhs);
        if (cmpMatch.Success)
        {
            // Split the first operand from type+value combined
            var operandsPart = cmpMatch.Groups[3].Value.Trim();
            var operand1 = ParseOperand(operandsPart);
            return new DxilInstruction
            {
                Kind = DxilInstructionKind.CompareOp,
                Operator = cmpMatch.Groups[1].Value,
                Predicate = cmpMatch.Groups[2].Value,
                Operand1 = operand1,
                Operand2 = ParseOperand(cmpMatch.Groups[4].Value.Trim())
            };
        }

        // Phi
        var phiMatch = PhiRegex().Match(rhs);
        if (phiMatch.Success)
        {
            var instr = new DxilInstruction
            {
                Kind = DxilInstructionKind.Phi,
                PhiType = ParseType(phiMatch.Groups[1].Value.Trim())
            };
            ParsePhiIncoming(phiMatch.Groups[2].Value, instr);
            return instr;
        }

        // Select
        var selectMatch = SelectRegex().Match(rhs);
        if (selectMatch.Success)
            return new DxilInstruction
            {
                Kind = DxilInstructionKind.Select,
                SelectCondition = ParseOperand(selectMatch.Groups[1].Value.Trim()),
                SelectTrue = ParseOperand(selectMatch.Groups[2].Value.Trim()),
                SelectFalse = ParseOperand(selectMatch.Groups[3].Value.Trim())
            };

        // ExtractValue
        var evMatch = ExtractValueRegex().Match(rhs);
        if (evMatch.Success)
        {
            var instr = new DxilInstruction
            {
                Kind = DxilInstructionKind.ExtractValue,
                AggregateOperand = ParseOperand(evMatch.Groups[1].Value.Trim())
            };
            if (int.TryParse(evMatch.Groups[2].Value, out var idx))
                instr.Indices.Add(idx);
            return instr;
        }

        // Cast
        var castMatch = CastRegex().Match(rhs);
        if (castMatch.Success)
            return new DxilInstruction
            {
                Kind = DxilInstructionKind.Cast,
                Operator = castMatch.Groups[1].Value,
                CastSource = ParseOperand(castMatch.Groups[3].Value.Trim()),
                CastDestType = ParseType(castMatch.Groups[4].Value.Trim())
            };

        // Load
        var loadMatch = LoadRegex().Match(rhs);
        if (loadMatch.Success)
            return new DxilInstruction
            {
                Kind = DxilInstructionKind.Load,
                LoadStorePointer = ParseOperand(loadMatch.Groups[2].Value.Trim())
            };

        // Alloca
        var allocaMatch = AllocaRegex().Match(rhs);
        if (allocaMatch.Success)
            return new DxilInstruction
            {
                Kind = DxilInstructionKind.Alloca,
                AllocaType = ParseType(allocaMatch.Groups[1].Value.Trim())
            };

        // GEP: getelementptr [inbounds] <base_type>, <ptr_type> <base>, <indices...>
        var gepMatch = GepRegex().Match(rhs);
        if (gepMatch.Success)
        {
            var baseType = ParseType(gepMatch.Groups[1].Value.Trim());
            var basePtr = ParseOperand(gepMatch.Groups[3].Value.Trim());
            var indicesText = gepMatch.Groups[4].Value.Trim();
            var gepIndices = new List<DxilOperand>();
            if (!string.IsNullOrEmpty(indicesText))
            {
                foreach (var idxText in SplitTypeList(indicesText))
                {
                    var trimIdx = idxText.Trim();
                    if (!string.IsNullOrEmpty(trimIdx))
                        gepIndices.Add(ParseTypedOperand(trimIdx));
                }
            }
            return new DxilInstruction
            {
                Kind = DxilInstructionKind.GetElementPtr,
                GepBase = basePtr,
                GepIndices = gepIndices,
                GepBaseType = baseType
            };
        }

        // InsertValue: insertvalue <type> <agg>, <type> <val>, <idx>
        var ivMatch = InsertValueRegex().Match(rhs);
        if (ivMatch.Success)
        {
            var instr = new DxilInstruction
            {
                Kind = DxilInstructionKind.InsertValue,
                AggregateOperand = ParseOperand(ivMatch.Groups[2].Value.Trim()),
                InsertedValue = ParseOperand(ivMatch.Groups[3].Value.Trim())
            };
            if (int.TryParse(ivMatch.Groups[4].Value, out var ivIdx))
                instr.Indices.Add(ivIdx);
            return instr;
        }

        // ExtractElement: extractelement <vec_type> <vec>, <idx_type> <idx>
        var eeMatch = ExtractElementRegex().Match(rhs);
        if (eeMatch.Success)
            return new DxilInstruction
            {
                Kind = DxilInstructionKind.ExtractElement,
                VectorOperand = ParseOperand(eeMatch.Groups[1].Value.Trim()),
                VectorIndex = ParseOperand(eeMatch.Groups[2].Value.Trim())
            };

        // InsertElement: insertelement <vec_type> <vec>, <scalar_type> <val>, <idx_type> <idx>
        var ieMatch = InsertElementRegex().Match(rhs);
        if (ieMatch.Success)
            return new DxilInstruction
            {
                Kind = DxilInstructionKind.InsertElement,
                VectorOperand = ParseOperand(ieMatch.Groups[1].Value.Trim()),
                InsertScalar = ParseOperand(ieMatch.Groups[2].Value.Trim()),
                VectorIndex = ParseOperand(ieMatch.Groups[3].Value.Trim())
            };

        // ShuffleVector: shufflevector <vec_type> <v1>, <vec_type> <v2>, <mask_type> <mask>
        var svMatch = ShuffleVectorRegex().Match(rhs);
        if (svMatch.Success)
        {
            var instr = new DxilInstruction
            {
                Kind = DxilInstructionKind.ShuffleVector,
                VectorOperand = ParseOperand(svMatch.Groups[1].Value.Trim()),
                ShuffleVector2 = ParseOperand(svMatch.Groups[2].Value.Trim())
            };
            // Parse mask: "<i32 0, i32 1>" or "zeroinitializer"
            var maskText = svMatch.Groups[4].Value.Trim();
            // Strip outer angle brackets: <i32 0, i32 1> → i32 0, i32 1
            if (maskText.StartsWith('<') && maskText.EndsWith('>'))
                maskText = maskText[1..^1].Trim();
            if (maskText is "zeroinitializer" or "poison" or "undef")
            {
                // All-zero or undefined mask — try to get count from mask type
                var maskTypeText = svMatch.Groups[3].Value.Trim();
                var xIdx = maskTypeText.IndexOf('x');
                var count = 4;
                if (xIdx > 0 && int.TryParse(maskTypeText[..xIdx].Trim(), out var c))
                    count = c;
                for (var j = 0; j < count; j++)
                    instr.ShuffleMask.Add(maskText == "zeroinitializer" ? 0 : -1);
            }
            else foreach (var maskPart in SplitTypeList(maskText))
            {
                var trimPart = maskPart.Trim();
                // Extract numeric value from "i32 N" or just "N"
                var lastSp = trimPart.LastIndexOf(' ');
                var numText = lastSp >= 0 ? trimPart[(lastSp + 1)..] : trimPart;
                if (numText == "undef" || numText == "poison")
                    instr.ShuffleMask.Add(-1); // undefined lane
                else if (int.TryParse(numText, out var maskVal))
                    instr.ShuffleMask.Add(maskVal);
            }
            return instr;
        }

        return new DxilInstruction { Kind = DxilInstructionKind.Unknown };
    }

    private DxilInstruction ParseCallInstruction(Match callMatch, string rawText)
    {
        var returnType = callMatch.Groups[1].Value.Trim();
        var funcName = callMatch.Groups[2].Value;
        var argsText = callMatch.Groups[3].Value;

        // Strip trailing )
        var lastParen = argsText.LastIndexOf(')');
        if (lastParen >= 0)
            argsText = argsText[..lastParen];

        var args = ParseCallArguments(argsText);

        var instr = new DxilInstruction
        {
            Kind = DxilInstructionKind.Call,
            CallReturnType = ParseType(returnType),
            CalledFunction = funcName,
            Arguments = args,
            RawText = rawText
        };

        // Extract DXIL opcode from dx.op calls (first i32 argument)
        if (funcName.StartsWith("dx.op.") && args.Count > 0)
            if (args[0].Kind == DxilOperandKind.IntConstant && args[0].IntValue is { } opVal)
                instr.DxilOpCode = (int)opVal;

        return instr;
    }

    private List<DxilOperand> ParseCallArguments(string argsText)
    {
        var args = new List<DxilOperand>();
        if (string.IsNullOrWhiteSpace(argsText))
            return args;

        // Split on commas, but respect nested parentheses/braces
        var depth = 0;
        var start = 0;
        for (var i = 0; i <= argsText.Length; i++)
            if (i == argsText.Length || (argsText[i] == ',' && depth == 0))
            {
                var arg = argsText[start..i].Trim();
                if (!string.IsNullOrEmpty(arg))
                {
                    // Arguments are "type value" — extract value part
                    var operand = ParseTypedOperand(arg);
                    args.Add(operand);
                }

                start = i + 1;
            }
            else if (argsText[i] is '(' or '{' or '<' or '[')
            {
                depth++;
            }
            else if (argsText[i] is ')' or '}' or '>' or ']')
            {
                depth--;
            }

        return args;
    }

    private DxilOperand ParseTypedOperand(string text)
    {
        text = text.Trim();

        // Handle constant vector: <N x type> <elem_type val, elem_type val, ...>
        // Pattern: "> <" separates the vector type from the vector value
        var vecSplit = text.IndexOf("> <", StringComparison.Ordinal);
        if (text.StartsWith('<') && vecSplit >= 0)
        {
            var typePart = text[..(vecSplit + 1)].Trim(); // e.g., "<2 x float>"
            var valuePart = text[(vecSplit + 2)..].Trim(); // e.g., "<float 0x..., float 0x...>"
            valuePart = valuePart.Trim('<', '>', ' ');

            // Parse vector type for element count and type
            var vecInner = typePart.Trim('<', '>', ' ');
            var xPos = vecInner.IndexOf('x');
            if (xPos > 0 && int.TryParse(vecInner[..xPos].Trim(), out var count))
            {
                var elemTypeStr = vecInner[(xPos + 1)..].Trim();
                var elements = SplitTypeList(valuePart);
                var parsedVals = new List<string>();
                foreach (var elem in elements)
                {
                    var parsed = ParseTypedOperand(elem.Trim());
                    if (parsed.FloatValue.HasValue)
                        parsedVals.Add(parsed.FloatValue.Value.ToString("G9", CultureInfo.InvariantCulture));
                    else if (parsed.IntValue.HasValue)
                        parsedVals.Add(parsed.IntValue.Value.ToString(CultureInfo.InvariantCulture));
                    else
                        parsedVals.Add(parsed.RawText);
                }

                var baseType = elemTypeStr switch
                {
                    "float" => "float",
                    "double" => "float",
                    "half" => "half",
                    "i32" => "int",
                    "i16" => "short",
                    _ => "float"
                };
                return new DxilOperand
                {
                    Kind = DxilOperandKind.FloatConstant,
                    RawText = $"{baseType}{count}({string.Join(", ", parsedVals)})",
                    Type = ParseType(typePart)
                };
            }
        }

        // Handle "type value" format
        // First, try to split off the type prefix
        // Common patterns: "i32 5", "float 0.0", "%dx.types.Handle %1", etc.
        var match = TypedOperandRegex().Match(text);
        if (match.Success) return ParseOperand(match.Groups[2].Value);

        // The whole text might be a struct type followed by a value
        // e.g., "%dx.types.CBufRet.f32 %3"
        var lastSpace = text.LastIndexOf(' ');
        if (lastSpace > 0)
        {
            var valuePart = text[(lastSpace + 1)..].Trim();
            if (valuePart.StartsWith('%') || valuePart.StartsWith('@') ||
                valuePart == "undef" || valuePart == "zeroinitializer" || valuePart == "null" ||
                valuePart == "poison" ||
                valuePart == "true" || valuePart == "false" ||
                double.TryParse(valuePart, NumberStyles.Float, CultureInfo.InvariantCulture, out _))
                return ParseOperand(valuePart);
        }

        return ParseOperand(text);
    }

    private static DxilOperand ParseOperand(string text)
    {
        text = text.Trim();

        // Strip leading type if present (e.g., "float %3" → "%3")
        // Simple heuristic: if text contains space and last token starts with % or @ or is a literal
        var lastSpace = text.LastIndexOf(' ');
        if (lastSpace > 0)
        {
            var candidate = text[(lastSpace + 1)..].Trim();
            if (candidate.StartsWith('%') || candidate.StartsWith('@') ||
                candidate is "undef" or "zeroinitializer" or "null" or "poison" or "true" or "false")
                text = candidate;
            else if (double.TryParse(candidate, NumberStyles.Float, CultureInfo.InvariantCulture, out _))
                text = candidate;
            // Also handle negative numbers like "-1.000000e+00"
            else if (candidate.StartsWith('-') && candidate.Length > 1 &&
                     double.TryParse(candidate, NumberStyles.Float, CultureInfo.InvariantCulture, out _))
                text = candidate;
        }

        if (text.StartsWith('%'))
            return new DxilOperand
            {
                Kind = DxilOperandKind.SsaRef,
                Name = text[1..],
                RawText = text
            };

        if (text.StartsWith('@'))
            return new DxilOperand
            {
                Kind = DxilOperandKind.Global,
                Name = text[1..],
                RawText = text
            };

        if (text == "undef" || text == "poison")
            return new DxilOperand
            {
                Kind = DxilOperandKind.Undef,
                RawText = text
            };

        if (text == "zeroinitializer")
            return new DxilOperand
            {
                Kind = DxilOperandKind.ZeroInit,
                RawText = text
            };

        if (text is "null" or "nullptr")
            return new DxilOperand
            {
                Kind = DxilOperandKind.Null,
                RawText = text
            };

        if (text == "true")
            return new DxilOperand
            {
                Kind = DxilOperandKind.BoolConstant,
                BoolValue = true,
                RawText = text
            };

        if (text == "false")
            return new DxilOperand
            {
                Kind = DxilOperandKind.BoolConstant,
                BoolValue = false,
                RawText = text
            };

        // Hex values: 0x prefix — must check before decimal integer/float
        // LLVM IR encodes doubles as 0x + 16 hex digits (IEEE 754 bits)
        if (text.StartsWith("0x", StringComparison.OrdinalIgnoreCase))
        {
            var hexPart = text.AsSpan(2);
            // 16 hex digits = IEEE 754 double (LLVM IR hex float encoding)
            if (hexPart.Length == 16 &&
                ulong.TryParse(hexPart, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var bits))
            {
                var val = BitConverter.Int64BitsToDouble(unchecked((long)bits));
                return new DxilOperand
                {
                    Kind = DxilOperandKind.FloatConstant,
                    FloatValue = val,
                    RawText = text
                };
            }

            // Shorter hex = integer
            if (long.TryParse(hexPart, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var hexVal))
                return new DxilOperand
                {
                    Kind = DxilOperandKind.IntConstant,
                    IntValue = hexVal,
                    RawText = text
                };
            // Fallback: treat as float constant (e.g., LLVM half/fp128 encodings)
            return new DxilOperand
            {
                Kind = DxilOperandKind.FloatConstant,
                RawText = text
            };
        }

        // Try integer
        if (long.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var intVal))
            return new DxilOperand
            {
                Kind = DxilOperandKind.IntConstant,
                IntValue = intVal,
                RawText = text
            };

        // Try float (handles scientific notation like 1.000000e+00)
        if (double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out var floatVal))
            return new DxilOperand
            {
                Kind = DxilOperandKind.FloatConstant,
                FloatValue = floatVal,
                RawText = text
            };

        return new DxilOperand
        {
            Kind = DxilOperandKind.SsaRef,
            Name = text,
            RawText = text
        };
    }

    private static void ParsePhiIncoming(string text, DxilInstruction instr)
    {
        // Parse [ %val, %block ], [ %val, %block ], ...
        var regex = new Regex(@"\[\s*(.+?),\s*%(\S+?)\s*\]");
        foreach (Match m in regex.Matches(text))
        {
            var value = ParseOperand(m.Groups[1].Value.Trim());
            var block = m.Groups[2].Value;
            instr.PhiIncoming.Add((value, block));
        }
    }

    private bool TryParseTerminator(string line, out DxilTerminator? terminator)
    {
        terminator = null;

        // Unconditional branch
        var brMatch = BranchRegex().Match(line);
        if (brMatch.Success)
        {
            terminator = new DxilTerminator
            {
                Kind = DxilTerminatorKind.Branch,
                TargetLabel = brMatch.Groups[1].Value,
                RawText = line
            };
            return true;
        }

        // Conditional branch
        var condBrMatch = CondBranchRegex().Match(line);
        if (condBrMatch.Success)
        {
            terminator = new DxilTerminator
            {
                Kind = DxilTerminatorKind.ConditionalBranch,
                Condition = ParseOperand(condBrMatch.Groups[1].Value.Trim()),
                TrueLabel = condBrMatch.Groups[2].Value,
                FalseLabel = condBrMatch.Groups[3].Value,
                RawText = line
            };
            return true;
        }

        // Return
        var retMatch = RetRegex().Match(line);
        if (retMatch.Success)
        {
            var retText = retMatch.Groups[1].Value.Trim();
            terminator = new DxilTerminator
            {
                Kind = DxilTerminatorKind.Return,
                ReturnValue = retText == "void" ? null : ParseOperand(retText),
                RawText = line
            };
            return true;
        }

        // Switch
        if (line.StartsWith("switch "))
        {
            // Multi-line switch statements are collapsed by DXC into one line
            var switchMatch = SwitchRegex().Match(line);
            if (switchMatch.Success)
            {
                var sw = new DxilTerminator
                {
                    Kind = DxilTerminatorKind.Switch,
                    SwitchValue = ParseOperand(switchMatch.Groups[1].Value.Trim()),
                    DefaultLabel = switchMatch.Groups[2].Value,
                    RawText = line
                };

                var caseRegex = new Regex(@"(\S+)\s+(-?\d+),\s*label\s+%(\S+)");
                foreach (Match cm in caseRegex.Matches(switchMatch.Groups[3].Value))
                {
                    var val = ParseOperand(cm.Groups[2].Value);
                    sw.SwitchCases.Add((val, cm.Groups[3].Value));
                }

                terminator = sw;
                return true;
            }
        }

        // Unreachable
        if (line == "unreachable")
        {
            terminator = new DxilTerminator
            {
                Kind = DxilTerminatorKind.Unreachable,
                RawText = line
            };
            return true;
        }

        return false;
    }

    private static DxilType ParseType(string typeText)
    {
        typeText = typeText.Trim();

        // Strip fast-math flags and other qualifiers
        foreach (var flag in new[] { "fast ", "nnan ", "ninf ", "nsz ", "arcp ", "contract ", "afn ", "reassoc " })
            if (typeText.StartsWith(flag))
                typeText = typeText[flag.Length..].Trim();

        return typeText switch
        {
            "void" => DxilType.Void,
            "float" => DxilType.Float,
            "double" => DxilType.Double,
            "half" => DxilType.Half,
            "i1" => DxilType.I1,
            "i8" => DxilType.I8,
            "i16" => DxilType.I16,
            "i32" => DxilType.I32,
            "i64" => DxilType.I64,
            _ => new DxilType { Name = typeText }
        };
    }

    /// <summary>
    ///     Strips trailing LLVM IR comments ('; ...') from an instruction line.
    /// </summary>
    private static string StripComment(string line)
    {
        var inQuotes = false;
        for (var i = 0; i < line.Length; i++)
        {
            if (line[i] == '"') inQuotes = !inQuotes;
            if (line[i] == ';' && !inQuotes)
                return line[..i].TrimEnd();
        }

        return line;
    }

    /// <summary>
    ///     Strips LLVM metadata annotations (!, !name !N) and attribute group references (#N)
    ///     from the end of an instruction line.
    /// </summary>
    private static string StripMetadata(string line)
    {
        // Strip LLVM metadata annotations: , !name !N
        line = Regex.Replace(line, @",\s*!\w+\s*!\d+", "").TrimEnd();
        // Strip attribute group references: #N at end of line
        line = Regex.Replace(line, @"\s*#\d+\s*$", "").TrimEnd();
        return line;
    }

    /// <summary>
    ///     Parses resource binding tables and cbuffer definitions from DXC disassembly comments.
    ///     These appear as ';'-prefixed comment blocks in the disassembly output and provide
    ///     metadata that SM6.0 DXIL containers often lack (no RDEF chunk).
    /// </summary>
    private static void ParseResourceMetadata(string[] lines, DxilModule module)
    {
        var i = 0;
        var cbufferOrdinal = 0;
        while (i < lines.Length)
        {
            var line = lines[i].TrimEnd('\r').TrimStart();

            // Look for resource binding table
            if (line.StartsWith(";") && line.Contains("Resource Bindings:"))
            {
                i++;
                // Skip to the dashes separator line
                while (i < lines.Length)
                {
                    var l = lines[i].TrimEnd('\r').TrimStart();
                    if (l.StartsWith(";") && l.Contains("------"))
                        break;
                    i++;
                }

                i++; // skip dashes line

                // Parse resource entries until we hit an empty comment line or non-comment
                while (i < lines.Length)
                {
                    var l = lines[i].TrimEnd('\r').TrimStart();
                    if (!l.StartsWith(";"))
                        break;
                    var content = l.TrimStart(';').Trim();
                    if (string.IsNullOrWhiteSpace(content))
                        break;

                    ParseResourceEntry(content, module);
                    i++;
                }

                continue;
            }

            // Look for cbuffer definition blocks: "; cbuffer <name>" or "; cbuffer " (unnamed)
            if (line.StartsWith("; cbuffer"))
            {
                var cbName = line.Length > "; cbuffer ".Length
                    ? line.Substring("; cbuffer ".Length).Trim()
                    : "";
                // Use ordinal index as fallback key when name is blank
                if (string.IsNullOrEmpty(cbName))
                    cbName = $"__cb_ordinal_{cbufferOrdinal++}";
                i++;
                var depth = 0;
                while (i < lines.Length)
                {
                    var l = lines[i].TrimEnd('\r').TrimStart();
                    if (!l.StartsWith(";"))
                    {
                        i++;
                        continue;
                    }

                    var content = l.TrimStart(';').Trim();

                    if (content == "{")
                    {
                        depth++;
                        i++;
                        continue;
                    }

                    if (content.StartsWith("}"))
                    {
                        depth--;
                        if (depth <= 0)
                        {
                            i++;
                            break;
                        }

                        i++;
                        continue;
                    }

                    // Only parse variable lines (have "Offset:" but not "Size:" which marks struct closings)
                    if (content.Contains("Offset:") && !content.Contains("Size:"))
                        ParseCBufferVariable(content, cbName, module);

                    // Parse byte-array size: "[N x i8] (type annotation not present)"
                    var sizeMatch = Regex.Match(content, @"\[(\d+)\s+x\s+i8\]");
                    if (sizeMatch.Success)
                    {
                        var byteSize = int.Parse(sizeMatch.Groups[1].Value, CultureInfo.InvariantCulture);
                        module.CBufferVariables.Add(new DxilCBufferVariable
                        {
                            Name = "__raw_data",
                            Type = $"[{byteSize} x i8]",
                            Offset = 0,
                            CBufferName = cbName
                        });
                    }

                    i++;
                }

                continue;
            }

            i++;
        }
    }

    /// <summary>
    ///     Parses a single resource entry line from the resource binding table.
    ///     Format: "Name  Type  Format  Dim  ID  HLSLBind  Count"
    ///     Name may be blank (some DXIL shaders), giving only 6 columns.
    /// </summary>
    private static void ParseResourceEntry(string content, DxilModule module)
    {
        var parts = content.Split((char[])[' '], StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length < 6) return;

        // Fixed columns from the end: Count, HLSLBind, ID, Dim, Format, Type
        // Name may be multiple words or blank (6 parts = no name)
        string name;
        int typeIndex;
        if (parts.Length >= 7)
        {
            typeIndex = parts.Length - 6;
            name = string.Join(" ", parts.Take(typeIndex));
        }
        else
        {
            // No name column — 6 parts: Type, Format, Dim, ID, HLSLBind, Count
            typeIndex = 0;
            name = "";
        }

        var binding = new DxilResourceBinding
        {
            Name = name,
            Type = parts[typeIndex],
            Format = parts[typeIndex + 1],
            Dim = parts[typeIndex + 2],
            HlslBind = parts[typeIndex + 4]
        };

        // Parse HLSL Bind (e.g., s0, t0, cb0, u0)
        var bind = binding.HlslBind;
        if (bind.StartsWith("cb") && int.TryParse(bind.AsSpan(2), out var cbp))
        {
            binding.BindClass = 'b';
            binding.BindPoint = cbp;
        }
        else if (bind.Length >= 2 && bind[0] is 't' or 's' or 'u' &&
                 int.TryParse(bind.AsSpan(1), out var bp))
        {
            binding.BindClass = bind[0];
            binding.BindPoint = bp;
        }

        module.ResourceBindings.Add(binding);
    }

    /// <summary>
    ///     Parses a cbuffer variable definition from DXC disassembly comments.
    ///     Format: "float4 color;    ; Offset:    0"
    /// </summary>
    private static void ParseCBufferVariable(string content, string cbName, DxilModule module)
    {
        var offsetMatch = Regex.Match(content, @"Offset:\s*(\d+)");
        if (!offsetMatch.Success) return;

        var offset = int.Parse(offsetMatch.Groups[1].Value, CultureInfo.InvariantCulture);

        // Extract "type name" from before the first semicolon
        var semiPos = content.IndexOf(';');
        if (semiPos <= 0) return;

        var varDecl = content[..semiPos].Trim();
        var lastSpace = varDecl.LastIndexOf(' ');
        if (lastSpace <= 0) return;

        var type = varDecl[..lastSpace].Trim();
        var name = varDecl[(lastSpace + 1)..].Trim();

        module.CBufferVariables.Add(new DxilCBufferVariable
        {
            Name = name,
            Type = type,
            Offset = offset,
            CBufferName = cbName
        });
    }

    private static void ParseFunctionParameters(string paramString, DxilFunction func)
    {
        // Split on commas at depth 0 (respecting nested parentheses/braces/angles)
        var depth = 0;
        var start = 0;
        var unnamedIdx = 0;
        var paramTexts = new List<string>();
        for (var i = 0; i <= paramString.Length; i++)
        {
            if (i == paramString.Length || (paramString[i] == ',' && depth == 0))
            {
                var paramText = paramString[start..i].Trim();
                if (!string.IsNullOrEmpty(paramText) && paramText != "...")
                    paramTexts.Add(paramText);
                start = i + 1;
            }
            else if (paramString[i] is '(' or '{' or '<' or '[')
            {
                depth++;
            }
            else if (paramString[i] is ')' or '}' or '>' or ']')
            {
                depth--;
            }
        }

        foreach (var paramText in paramTexts)
        {
            var param = ParseSingleParameter(paramText);
            // Unnamed params are referenced as %0, %1, etc. in LLVM IR
            if (string.IsNullOrEmpty(param.Name))
                param.Name = (unnamedIdx++).ToString();
            else
                unnamedIdx++; // Named params still consume an index
            func.Parameters.Add(param);
        }
    }

    private static DxilParameter ParseSingleParameter(string text)
    {
        text = text.Trim();
        var rawTypeText = text;

        // Extract address space: addrspace(N)
        var addrSpace = -1;
        var addrMatch = Regex.Match(text, @"addrspace\((\d+)\)");
        if (addrMatch.Success)
            addrSpace = int.Parse(addrMatch.Groups[1].Value, CultureInfo.InvariantCulture);

        // The parameter is "type %name" — find the last %name token
        var name = "";
        var lastPercent = text.LastIndexOf('%');
        if (lastPercent >= 0)
        {
            name = text[(lastPercent + 1)..].Trim();
            text = text[..lastPercent].Trim();
        }

        // Strip attributes like nocapture, readonly, etc.
        text = Regex.Replace(text, @"\b(nocapture|readonly|readnone|writeonly|nonnull|dereferenceable\(\d+\)|align\s+\d+)\b", "").Trim();
        // Clean up extra spaces
        text = Regex.Replace(text, @"\s+", " ").Trim();

        return new DxilParameter
        {
            Type = ParseType(text),
            Name = name,
            AddressSpace = addrSpace,
            RawTypeText = rawTypeText
        };
    }

    /// <summary>
    ///     Splits a comma-separated type list respecting nested angle brackets and braces.
    /// </summary>
    private static List<string> SplitTypeList(string text)
    {
        var result = new List<string>();
        var depth = 0;
        var start = 0;
        for (var i = 0; i <= text.Length; i++)
        {
            if (i == text.Length || (text[i] == ',' && depth == 0))
            {
                var part = text[start..i].Trim();
                if (!string.IsNullOrEmpty(part))
                    result.Add(part);
                start = i + 1;
            }
            else if (text[i] is '<' or '{' or '(')
            {
                depth++;
            }
            else if (text[i] is '>' or '}' or ')')
            {
                depth--;
            }
        }
        return result;
    }

    private static void ComputeCfgEdges(DxilFunction func)
    {
        var blockMap = new Dictionary<string, DxilBasicBlock>();
        foreach (var bb in func.BasicBlocks)
            blockMap[bb.Label] = bb;

        foreach (var bb in func.BasicBlocks)
        {
            if (bb.Terminator == null) continue;

            var targets = new List<string>();
            switch (bb.Terminator.Kind)
            {
                case DxilTerminatorKind.Branch:
                    if (bb.Terminator.TargetLabel != null)
                        targets.Add(bb.Terminator.TargetLabel);
                    break;
                case DxilTerminatorKind.ConditionalBranch:
                    if (bb.Terminator.TrueLabel != null)
                        targets.Add(bb.Terminator.TrueLabel);
                    if (bb.Terminator.FalseLabel != null)
                        targets.Add(bb.Terminator.FalseLabel);
                    break;
                case DxilTerminatorKind.Switch:
                    if (bb.Terminator.DefaultLabel != null)
                        targets.Add(bb.Terminator.DefaultLabel);
                    foreach (var (_, label) in bb.Terminator.SwitchCases)
                        targets.Add(label);
                    break;
            }

            foreach (var target in targets)
            {
                bb.Successors.Add(target);
                if (blockMap.TryGetValue(target, out var targetBb))
                    targetBb.Predecessors.Add(bb.Label);
            }
        }
    }
}